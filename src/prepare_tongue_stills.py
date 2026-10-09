#!/usr/bin/env python3
"""Prepare manually selected stereo tongue stills for model training."""

from __future__ import annotations

import argparse
import json
import time
from collections import Counter
from pathlib import Path

import cv2
import numpy as np

from capture_format import inspect_capture, scan_stereo_mouth_stills
from cheek_still_capture import CHEEK_TARGET_NAMES, LOWER_FACE_SESSION_TYPES
from dataset_inspect import load_labels
from label_capture import NATIVE_TONGUE_MAPPING, TRACKING_SOURCES, native_tongue_out
from prepare_training import nearest_label_indices
from prepare_tongue_training import FACE_HEIGHT
from prepare_cheek_stills import validate_cheek_session
from tongue_calibration import TONGUE_TARGET_NAMES


def validate_focused_arc_session(session: dict[str, object]) -> None:
    """Require the new focus cards before treating an arc capture as training data.

    Old arc-v3 journals contain only the original ten cards and remain usable.
    The presence of any new card identifies the expanded curriculum, whose
    facial-hair pairs and mid-diagonal corners must all have usable stills.
    """
    if session.get("sessionType") != "tongue-stereo-arc-v3":
        return
    prompts = session.get("prompts", [])
    hair = [
        (index, card) for index, card in enumerate(prompts)
        if str(card.get("context", "")).startswith("facial hair / ")
    ]
    mid = [
        (index, card) for index, card in enumerate(prompts)
        if str(card.get("name", "")).startswith("Mid diagonal ")
    ]
    if not hair and not mid:
        return

    expected_contexts = {
        "facial hair / neutral", "facial hair / smile",
        "facial hair / open jaw", "facial hair / fit variation",
    }
    contexts = {str(card["context"]) for _, card in hair}
    if contexts != expected_contexts or len(hair) != 8:
        raise ValueError("Focused arc session is missing a facial-hair hidden/visible pair")
    for context in expected_contexts:
        pair = [card for _, card in hair if card["context"] == context]
        if len(pair) != 2 or sorted(
            float(card.get("targets", {}).get("visibility", 0.0)) for card in pair
        ) != [0.0, 1.0]:
            raise ValueError(f"Focused arc session has an invalid facial-hair pair: {context}")

    corners = {
        (
            float(card.get("targets", {}).get("horizontal", 0.0)),
            float(card.get("targets", {}).get("vertical", 0.0)),
        )
        for _, card in mid
    }
    if len(mid) != 4 or corners != {
        (-0.5, 0.5), (0.5, 0.5), (-0.5, -0.5), (0.5, -0.5)
    }:
        raise ValueError("Focused arc session is missing a mid-diagonal corner")

    active_samples = [sample for sample in session.get("samples", []) if not sample.get("excluded")]
    counts = Counter(int(sample["promptIndex"]) for sample in active_samples)
    for index, card in hair + mid:
        minimum = max(4, int(card.get("minimum_captures", 4)))
        if counts[index] < minimum:
            raise ValueError(
                f"Focused arc card '{card['name']}' has {counts[index]} usable stills; "
                f"at least {minimum} are required. Recapture this card before training."
            )
        if any(
            sample.get("targets", {}) != card.get("targets", {})
            for sample in active_samples if int(sample["promptIndex"]) == index
        ):
            raise ValueError(f"Focused arc card '{card['name']}' has mismatched target labels")


def main() -> int:
    parser = argparse.ArgumentParser(description="Prepare manual stereo tongue stills")
    parser.add_argument("capture")
    parser.add_argument("--labels")
    parser.add_argument("--session")
    parser.add_argument("--output")
    parser.add_argument("--size", type=int, default=224)
    parser.add_argument("--tracking-source", choices=TRACKING_SOURCES,
                        help="App used to record a legacy sidecar without source identity")
    arguments = parser.parse_args()
    if not 128 <= arguments.size <= 320:
        parser.error("--size must be between 128 and 320")

    capture_path = Path(arguments.capture).resolve()
    label_path = (
        Path(arguments.labels).resolve()
        if arguments.labels else capture_path.with_suffix(".qplabel.jsonl")
    )
    session_path = (
        Path(arguments.session).resolve()
        if arguments.session else capture_path.with_suffix(".qpsession.json")
    )
    output = (
        Path(arguments.output).resolve()
        if arguments.output
        else Path("training") / f"{capture_path.stem}-tongue-stills-{arguments.size}px"
    )
    session = json.loads(session_path.read_text(encoding="utf-8"))
    allowed_session_types = {
        "tongue-stereo-stills-v1",
        "tongue-stereo-corrections-v1",
        "tongue-stereo-refinement-v2",
        "tongue-stereo-arc-v3",
    } | LOWER_FACE_SESSION_TYPES
    if session.get("sessionType") not in allowed_session_types:
        raise ValueError("A manual lower-face or tongue still session is required")
    validate_focused_arc_session(session)
    lower_face = session.get("sessionType") in LOWER_FACE_SESSION_TYPES
    if lower_face:
        validate_cheek_session(session)
        summary = inspect_capture(capture_path)
        if not summary["completed"] or summary["truncated"]:
            raise ValueError("The lower-face camera recording is incomplete")
    entries, frame_width = scan_stereo_mouth_stills(capture_path)
    frame_count = len(entries)
    frame_times = [timestamp for _offset, timestamp in entries]
    sample_by_frame = {
        int(sample["frameIndex"]): sample
        for sample in session.get("samples", [])
    }
    if set(sample_by_frame) != set(range(frame_count)):
        missing = sorted(set(range(frame_count)) - set(sample_by_frame))
        raise ValueError(
            f"Session journal does not describe every captured still; missing {missing[:8]}"
        )

    cheek_offset = frame_count  # Unused for legacy tongue-only captures.
    if lower_face:
        _cheek_samples, cheek_offset = validate_cheek_session(session, frame_count)
    cheek_frames = np.asarray([
        lower_face and sample_by_frame[index]["promptIndex"] >= cheek_offset
        for index in range(frame_count)
    ], dtype=np.bool_)
    if lower_face and not any(
        not cheek_frames[index] and not sample_by_frame[index].get("excluded", False)
        for index in range(frame_count)
    ):
        raise ValueError("The lower-face capture has no usable tongue stills")

    names, labels = load_labels(label_path)
    if "TongueOut" not in names:
        raise ValueError("Factory label stream has no TongueOut channel")
    if not labels:
        raise ValueError("Factory label stream has no samples")
    label_times = [int(value["arrivalMonotonicNs"]) for value in labels]
    label_indices, errors_ms = nearest_label_indices(frame_times, label_times)
    # Cheek pose cards supply their own labels. Native factory references are
    # required for tongue rows only, including if the label feed stops during
    # the cheek portion of a combined capture.
    tongue_errors_ms = errors_ms[~cheek_frames]
    if float(np.max(tongue_errors_ms)) > 35.0:
        raise ValueError(
            f"Worst tongue still/factory-label alignment is {float(np.max(tongue_errors_ms)):.2f} ms"
        )

    # Validate the source-dependent scalar before replacing any cache arrays.
    native_references = np.asarray([
        (native_tongue_out(labels[int(index)], names,
                           tracking_source=arguments.tracking_source) or 0.0)
        if not cheek_frames[frame] else 0.0
        for frame, index in enumerate(label_indices)
    ], dtype=np.float32)

    output.mkdir(parents=True, exist_ok=True)
    shape = (frame_count, 2, arguments.size, arguments.size)
    images = np.lib.format.open_memmap(
        output / "images.npy", mode="w+", dtype=np.uint8, shape=shape
    )
    targets = np.zeros((frame_count, len(TONGUE_TARGET_NAMES)), dtype=np.float32)
    native = np.zeros(frame_count, dtype=np.float32)
    native_expressions = np.zeros((frame_count, len(names)), dtype=np.float32)
    step_ids = np.zeros(frame_count, dtype=np.int16)
    trainable = np.ones(frame_count, dtype=np.bool_)
    cheek_targets = np.zeros((frame_count, len(CHEEK_TARGET_NAMES)), dtype=np.float32) if lower_face else None
    cheek_trainable = np.zeros(frame_count, dtype=np.bool_) if lower_face else None

    started = time.monotonic()
    with capture_path.open("rb", buffering=4 * 1024 * 1024) as capture:
        for index, ((payload_offset, _timestamp), label_index) in enumerate(
            zip(entries, label_indices)
        ):
            sample = sample_by_frame[index]
            capture.seek(payload_offset)
            raw = capture.read(frame_width * FACE_HEIGHT)
            if len(raw) != frame_width * FACE_HEIGHT:
                raise ValueError(f"Still {index} is truncated")
            strip = np.frombuffer(raw, dtype=np.uint8).reshape(FACE_HEIGHT, frame_width)
            for view in range(2):
                panel = strip[:, view * 400:(view + 1) * 400]
                images[index, view] = cv2.resize(
                    panel,
                    (arguments.size, arguments.size),
                    interpolation=cv2.INTER_AREA,
                )
            for target_index, name in enumerate(TONGUE_TARGET_NAMES):
                targets[index, target_index] = float(sample.get("targets", {}).get(name, 0.0))
            factory = np.asarray(labels[int(label_index)]["values"], dtype=np.float32)
            native_expressions[index] = factory
            native[index] = native_references[index]
            step_ids[index] = int(sample["promptIndex"])
            permitted = not bool(sample.get("excluded", False))
            trainable[index] = permitted and not cheek_frames[index]
            if cheek_frames[index]:
                cheek_targets[index] = [sample["targets"][name] for name in CHEEK_TARGET_NAMES]
                cheek_trainable[index] = permitted
            if (index + 1) % 50 == 0 or index + 1 == frame_count:
                rate = (index + 1) / max(0.001, time.monotonic() - started)
                print(f"Prepared {index + 1}/{frame_count} stills ({rate:.1f}/s)")

    images.flush()
    np.save(output / "targets.npy", targets)
    np.save(output / "native_tongue_out.npy", native)
    np.save(output / "native_expressions.npy", native_expressions)
    np.save(output / "timestamps.npy", np.asarray(frame_times, dtype=np.int64))
    np.save(output / "step_ids.npy", step_ids)
    np.save(output / "trainable.npy", trainable)
    if lower_face:
        np.save(output / "cheek_targets.npy", cheek_targets)
        np.save(output / "cheek_trainable.npy", cheek_trainable)
    metadata = {
        "version": 3,
        "nativeTongueMapping": NATIVE_TONGUE_MAPPING,
        "nativeTongueSourceOverride": arguments.tracking_source,
        "datasetType": "manual-stereo-stills",
        "sessionType": session.get("sessionType"),
        "complete": bool(session.get("completed")),
        "capture": str(capture_path),
        "labels": str(label_path),
        "session": str(session_path),
        "frames": frame_count,
        "trainableFrames": int(np.count_nonzero(trainable)),
        "excludedFrames": int(np.count_nonzero(~trainable)),
        "imageSize": arguments.size,
        "cameraOrder": ["left_face_camera2", "right_face_camera3"],
        "captureCameraMode": "mouth" if frame_width == 800 else "face",
        "targetNames": list(TONGUE_TARGET_NAMES),
        "factoryExpressionNames": names,
        "capturePolicy": "manually triggered exact synchronized stereo frames",
        "medianLabelErrorMs": float(np.median(tongue_errors_ms)),
        "maximumLabelErrorMs": float(np.max(tongue_errors_ms)),
    }
    if lower_face:
        metadata.update({
            "hasCheekCards": True,
            "cheekTargetNames": list(CHEEK_TARGET_NAMES),
            "cheekTargetSource": "prompted-cheek-poses",
            "cheekTrainableFrames": int(np.count_nonzero(cheek_trainable)),
            "cheekStrengthLabels": "wearer-performed approximate fractions, not measured pressure",
        })
    (output / "metadata.json").write_text(
        json.dumps(metadata, indent=2), encoding="utf-8"
    )
    size_bytes = sum(path.stat().st_size for path in output.iterdir() if path.is_file())
    print(f"Manual tongue cache complete: {output.resolve()}")
    print(
        f"Usable stills: {int(np.count_nonzero(trainable))}/{frame_count}; "
        f"cache: {size_bytes / 1_000_000:.1f} MB"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
