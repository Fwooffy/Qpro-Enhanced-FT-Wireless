#!/usr/bin/env python3
"""Build a compact stereo tongue cache from a prompted face-mode capture."""

from __future__ import annotations

import argparse
import bisect
import json
import time
from pathlib import Path
from typing import Any

import cv2
import numpy as np

from calibration import CalibrationStep, target_activation
from calibration_inspect import step_intervals
from capture_format import FILE_HEADER, FILE_MAGIC, FRAME_HEADER, FRAME_MAGIC, TRANSPORT_HEADER
from dataset_inspect import load_labels
from label_capture import (
    NATIVE_TONGUE_MAPPING, TRACKING_SOURCES,
    native_tongue_out as read_native_tongue_out,
)
from prepare_training import nearest_label_indices
from tongue_calibration import TONGUE_TARGET_NAMES


FACE_MASK = 0x1C
FACE_WIDTH = 1200
FACE_HEIGHT = 400


def scan_face_frames(path: Path) -> tuple[list[tuple[int, int]], int]:
    entries: list[tuple[int, int]] = []
    with path.open("rb") as capture:
        header = capture.read(FILE_HEADER.size)
        if len(header) != FILE_HEADER.size or FILE_HEADER.unpack(header)[0] != FILE_MAGIC:
            raise ValueError("Unrecognized capture file")
        declared = int(FILE_HEADER.unpack(header)[5])
        while True:
            frame_header = capture.read(FRAME_HEADER.size)
            if not frame_header:
                break
            if len(frame_header) != FRAME_HEADER.size:
                raise ValueError("Truncated capture frame header")
            magic, _record_size, _source_size, timestamp, _wall, transport_raw = (
                FRAME_HEADER.unpack(frame_header)
            )
            if magic != FRAME_MAGIC:
                raise ValueError("Invalid frame record")
            transport = TRANSPORT_HEADER.unpack(transport_raw)
            width, height, stride, payload_size, mask = map(
                int, (transport[5], transport[6], transport[7], transport[9], transport[10])
            )
            if (mask, width, height) != (FACE_MASK, FACE_WIDTH, FACE_HEIGHT):
                raise ValueError(
                    "Tongue training requires face mode (cameras 2, 3, and 4)"
                )
            if stride != width or payload_size != width * height:
                raise ValueError("Unsupported face-frame layout")
            entries.append((capture.tell(), int(timestamp)))
            capture.seek(payload_size, 1)
    if len(entries) != declared:
        raise ValueError(f"Capture declares {declared} frames but scanned {len(entries)}")
    return entries, declared


def step_from_json(payload: dict[str, Any]) -> CalibrationStep:
    return CalibrationStep(
        name=str(payload["name"]),
        instruction=str(payload["instruction"]),
        seconds=float(payload["seconds"]),
        targets={key: float(value) for key, value in payload.get("targets", {}).items()},
        tags=tuple(str(value) for value in payload.get("tags", [])),
        pattern=str(payload.get("pattern", "free")),
    )


def target_vector(
    step: CalibrationStep, elapsed_s: float, *, invert_activation: bool = False
) -> np.ndarray:
    activation = target_activation(step, elapsed_s)
    if invert_activation and step.pattern == "pulse":
        activation = 1.0 - activation
    values = np.zeros(len(TONGUE_TARGET_NAMES), dtype=np.float32)
    for index, name in enumerate(TONGUE_TARGET_NAMES):
        values[index] = float(step.targets.get(name, 0.0)) * activation
    return values


def load_label_overrides(path: Path | None, session: dict[str, Any]) -> dict[str, Any]:
    if path is None:
        return {
            "invertedStepIds": [],
            "excludedStepIds": [],
            "stepEdits": {},
            "source": "none",
        }
    payload = json.loads(path.read_text(encoding="utf-8"))
    if payload.get("format") != "qpro-tongue-label-overrides-v1":
        raise ValueError("Unrecognized tongue label-override file")
    session_type = payload.get("sessionType")
    if session_type and session_type != session.get("sessionType"):
        raise ValueError("Tongue label overrides target a different session type")
    inverted = [int(value) for value in payload.get("invertedStepIds", [])]
    excluded = [
        int(value) for value in (
            list(payload.get("excludedStepIds", []))
            + list(payload.get("skippedStepIds", []))
        )
    ]
    step_count = len(session["steps"])
    if any(value < 0 or value >= step_count for value in inverted + excluded):
        raise ValueError("Tongue label overrides contain an invalid step ID")
    edits: dict[str, dict[str, Any]] = {}
    for raw_step_id, raw_edit in payload.get("stepEdits", {}).items():
        step_id = int(raw_step_id)
        if step_id < 0 or step_id >= step_count or not isinstance(raw_edit, dict):
            raise ValueError("Tongue label overrides contain an invalid step edit")
        mode = str(raw_edit.get("mode", "normal"))
        if mode not in ("normal", "inverted", "exclude"):
            raise ValueError(f"Invalid tongue-review mode for step {step_id}: {mode}")
        trim_start = max(0.0, float(raw_edit.get("trimStartSeconds", 0.0)))
        trim_end = max(0.0, float(raw_edit.get("trimEndSeconds", 0.0)))
        edits[str(step_id)] = {
            "mode": mode,
            "trimStartSeconds": trim_start,
            "trimEndSeconds": trim_end,
            "note": str(raw_edit.get("note", "")),
        }
        if mode == "inverted":
            inverted.append(step_id)
        elif mode == "normal":
            inverted = [value for value in inverted if value != step_id]
        elif mode == "exclude":
            excluded.append(step_id)
    payload["invertedStepIds"] = sorted(set(inverted))
    payload["excludedStepIds"] = sorted(set(excluded))
    payload["stepEdits"] = edits
    payload["source"] = str(path)
    return payload


def apply_training_overrides(
    step_ids: np.ndarray,
    elapsed: np.ndarray,
    trainable: np.ndarray,
    steps: list[CalibrationStep],
    overrides: dict[str, Any],
) -> np.ndarray:
    """Apply non-destructive review decisions to the trainable-frame mask."""
    result = np.asarray(trainable, dtype=np.bool_).copy()
    for step_id in overrides.get("excludedStepIds", []):
        result[step_ids == int(step_id)] = False
    for raw_step_id, edit in overrides.get("stepEdits", {}).items():
        step_id = int(raw_step_id)
        if edit.get("mode") == "exclude":
            result[step_ids == step_id] = False
            continue
        selected = step_ids == step_id
        trim_start = float(edit.get("trimStartSeconds", 0.0))
        trim_end = float(edit.get("trimEndSeconds", 0.0))
        if trim_start > 0:
            result[selected & (elapsed < trim_start)] = False
        if trim_end > 0:
            cutoff = max(0.0, float(steps[step_id].seconds) - trim_end)
            result[selected & (elapsed > cutoff)] = False
    return result


def assign_steps(
    frame_times: list[int], session: dict[str, Any]
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    steps = [step_from_json(value) for value in session["steps"]]
    intervals = step_intervals(session)
    skipped = {int(value) for value in session.get("skippedSteps", [])}
    step_ids = np.full(len(frame_times), -1, dtype=np.int16)
    elapsed = np.zeros(len(frame_times), dtype=np.float32)
    trainable = np.zeros(len(frame_times), dtype=np.bool_)
    for step_id, step_ranges in enumerate(intervals):
        is_validation = "validation" in steps[step_id].tags or step_id in skipped
        for start, finish in step_ranges:
            first = bisect.bisect_left(frame_times, start)
            last = bisect.bisect_right(frame_times, finish)
            step_ids[first:last] = step_id
            elapsed[first:last] = (
                np.asarray(frame_times[first:last], dtype=np.float64) - start
            ) / 1e9
            trainable[first:last] = not is_validation
    if np.any(step_ids < 0):
        raise ValueError(f"{int(np.sum(step_ids < 0))} frames are outside completed steps")
    return step_ids, elapsed, trainable


def main() -> int:
    parser = argparse.ArgumentParser(description="Prepare stereo tongue training data")
    parser.add_argument("capture")
    parser.add_argument("--labels")
    parser.add_argument("--session")
    parser.add_argument("--label-overrides")
    parser.add_argument("--output")
    parser.add_argument("--size", type=int, default=160)
    parser.add_argument("--tracking-source", choices=TRACKING_SOURCES,
                        help="App used to record a legacy sidecar without source identity")
    arguments = parser.parse_args()
    if not 96 <= arguments.size <= 256:
        parser.error("--size must be between 96 and 256")

    capture_path = Path(arguments.capture).resolve()
    label_path = Path(arguments.labels).resolve() if arguments.labels else capture_path.with_suffix(".qplabel.jsonl")
    session_path = Path(arguments.session).resolve() if arguments.session else capture_path.with_suffix(".qpsession.json")
    output = Path(arguments.output).resolve() if arguments.output else Path("training") / f"{capture_path.stem}-tongue-{arguments.size}px"
    output.mkdir(parents=True, exist_ok=True)

    session = json.loads(session_path.read_text(encoding="utf-8"))
    if not session.get("completed") or session.get("sessionType") != "tongue-stereo-v1":
        raise ValueError("A completed tongue-stereo-v1 session is required")
    default_overrides = capture_path.with_suffix(".qptongueoverride.json")
    override_path = (
        Path(arguments.label_overrides).resolve()
        if arguments.label_overrides
        else default_overrides if default_overrides.exists() else None
    )
    label_overrides = load_label_overrides(override_path, session)
    inverted_steps = set(label_overrides["invertedStepIds"])
    entries, frame_count = scan_face_frames(capture_path)
    frame_times = [timestamp for _offset, timestamp in entries]
    step_ids, step_elapsed, trainable = assign_steps(frame_times, session)
    steps = [step_from_json(value) for value in session["steps"]]
    trainable = apply_training_overrides(
        step_ids, step_elapsed, trainable, steps, label_overrides
    )

    names, labels = load_labels(label_path)
    if "TongueOut" not in names:
        raise ValueError("Factory label stream has no TongueOut channel")
    label_times = [int(value["arrivalMonotonicNs"]) for value in labels]
    label_indices, errors_ms = nearest_label_indices(frame_times, label_times)
    native_references = np.asarray([
        read_native_tongue_out(labels[int(index)], names,
                              tracking_source=arguments.tracking_source) or 0.0
        for index in label_indices
    ], dtype=np.float32)
    if float(np.max(errors_ms)) > 20.0:
        raise ValueError(f"Worst camera/label alignment is {float(np.max(errors_ms)):.2f} ms")

    images = np.lib.format.open_memmap(
        output / "images.npy", mode="w+", dtype=np.uint8,
        shape=(frame_count, 2, arguments.size, arguments.size),
    )
    targets = np.lib.format.open_memmap(
        output / "targets.npy", mode="w+", dtype=np.float32,
        shape=(frame_count, len(TONGUE_TARGET_NAMES)),
    )
    native_tongue_out = np.lib.format.open_memmap(
        output / "native_tongue_out.npy", mode="w+", dtype=np.float32,
        shape=(frame_count,),
    )
    native_expressions = np.lib.format.open_memmap(
        output / "native_expressions.npy", mode="w+", dtype=np.float32,
        shape=(frame_count, len(names)),
    )
    np.save(output / "timestamps.npy", np.asarray(frame_times, dtype=np.int64))
    np.save(output / "step_ids.npy", step_ids)
    np.save(output / "trainable.npy", trainable)

    started = time.monotonic()
    with capture_path.open("rb", buffering=4 * 1024 * 1024) as capture:
        for index, ((payload_offset, _timestamp), label_index) in enumerate(zip(entries, label_indices)):
            capture.seek(payload_offset)
            raw = capture.read(FACE_WIDTH * FACE_HEIGHT)
            if len(raw) != FACE_WIDTH * FACE_HEIGHT:
                raise ValueError(f"Frame {index} is truncated")
            strip = np.frombuffer(raw, dtype=np.uint8).reshape(FACE_HEIGHT, FACE_WIDTH)
            # Face-mode order is camera 2, camera 3, camera 4.  Keep the two
            # lower-face views separate and intentionally omit camera 4 here.
            for view in range(2):
                panel = strip[:, view * 400:(view + 1) * 400]
                images[index, view] = cv2.resize(
                    panel, (arguments.size, arguments.size), interpolation=cv2.INTER_AREA
                )
            step_id = int(step_ids[index])
            targets[index] = target_vector(
                steps[step_id],
                float(step_elapsed[index]),
                invert_activation=step_id in inverted_steps,
            )
            factory = np.asarray(labels[int(label_index)]["values"], dtype=np.float32)
            native_expressions[index] = factory
            native_tongue_out[index] = native_references[index]
            if (index + 1) % 500 == 0 or index + 1 == frame_count:
                rate = (index + 1) / max(0.001, time.monotonic() - started)
                print(f"Prepared {index + 1}/{frame_count} frames ({rate:.1f}/s)")

    for array in (images, targets, native_tongue_out, native_expressions):
        array.flush()
    metadata = {
        "version": 2,
        "nativeTongueMapping": NATIVE_TONGUE_MAPPING,
        "nativeTongueSourceOverride": arguments.tracking_source,
        "complete": True,
        "capture": str(capture_path),
        "labels": str(label_path),
        "session": str(session_path),
        "frames": frame_count,
        "trainableFrames": int(np.count_nonzero(trainable)),
        "validationFrames": int(np.count_nonzero(~trainable)),
        "imageSize": arguments.size,
        "cameraOrder": ["left_face_camera2", "right_face_camera3"],
        "targetNames": list(TONGUE_TARGET_NAMES),
        "factoryExpressionNames": names,
        "nativeGate": "initial runtime gate = 0.5 * camera_visibility + 0.5 * native_TongueOut; tune on held-out prompts",
        "labelOverrides": label_overrides,
        "medianLabelErrorMs": float(np.median(errors_ms)),
        "maximumLabelErrorMs": float(np.max(errors_ms)),
    }
    (output / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    size_bytes = sum(path.stat().st_size for path in output.iterdir() if path.is_file())
    print(f"Tongue cache complete: {output.resolve()}")
    print(f"Frames: {frame_count}; cache: {size_bytes / 1_000_000:.1f} MB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
