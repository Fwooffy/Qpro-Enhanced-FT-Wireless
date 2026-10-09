#!/usr/bin/env python3
"""Train and audit a distributable tongue candidate from consented stills.

This offline tool never edits the bundled model. Its independent test sessions
are not used for checkpoint selection or visibility-threshold tuning.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
import torch
from torch.nn import functional as F

from capture_format import scan_stereo_mouth_stills
from dataset_inspect import load_labels
from label_capture import native_tongue_out, require_current_tongue_cache
from prepare_training import nearest_label_indices
from prepare_tongue_training import FACE_HEIGHT
from qpro_gpu import validated_torch_device_name
from tongue_calibration import TONGUE_TARGET_NAMES
from tongue_still_capture import TONGUE_ARC_PROMPTS, TONGUE_STILL_PROMPTS
from tongue_image_processing import (
    MODES, preprocess_stereo_images, resolve_input_preprocessing,
)
from train_tongue_model import SIGNED_TARGETS, create_model


ALLOWED_SESSION_TYPES = {
    "tongue-stereo-stills-v1",
    "tongue-stereo-corrections-v1",
    "tongue-stereo-arc-v3",
}
ARRAY_NAMES = (
    "images", "targets", "native_tongue_out", "native_expressions",
    "timestamps", "step_ids", "trainable",
)


@dataclass(frozen=True)
class Session:
    cache: Path
    role: str
    session_id: str
    wearer_id: str
    capture_sha256: str
    source_sha256: dict[str, str]
    metadata: dict[str, object]
    prompt_names: tuple[str, ...]
    trainable_frames: int
    complete_curriculum: bool


def _read_json(path: Path) -> dict[str, object]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path}")
    return value


def _resolve(base: Path, value: object) -> Path:
    if not isinstance(value, str) or not value.strip():
        raise ValueError("A non-empty path is required")
    path = Path(value)
    return (path if path.is_absolute() else base / path).resolve(strict=True)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _arrays(cache: Path) -> dict[str, np.ndarray]:
    return {
        name: np.load(cache / f"{name}.npy", mmap_mode="r", allow_pickle=False)
        for name in ARRAY_NAMES
    }


def _read_raw_stereo(
    capture, entries: list[tuple[int, int]], frame_width: int,
    indices: np.ndarray,
) -> np.ndarray:
    images = np.empty((len(indices), 2, FACE_HEIGHT, 400), dtype=np.uint8)
    for destination, index in enumerate(indices):
        capture.seek(entries[int(index)][0])
        raw = capture.read(frame_width * FACE_HEIGHT)
        if len(raw) != frame_width * FACE_HEIGHT:
            raise ValueError(f"Raw still {int(index)} is truncated")
        strip = np.frombuffer(raw, dtype=np.uint8).reshape(FACE_HEIGHT, frame_width)
        images[destination, 0] = strip[:, :400]
        images[destination, 1] = strip[:, 400:800]
    return images


def _verify_source_frames(
    capture_path: Path, label_path: Path, metadata: dict[str, object],
    arrays: dict[str, np.ndarray],
) -> None:
    """Rebuild the model inputs and native labels from the source sidecars."""
    entries, frame_width = scan_stereo_mouth_stills(capture_path)
    if len(entries) != len(arrays["images"]):
        raise ValueError(f"Capture frame count differs from its prepared cache: {capture_path}")
    names, labels = load_labels(label_path)
    if names != list(metadata.get("factoryExpressionNames", [])) or "TongueOut" not in names:
        raise ValueError(f"Factory label schema differs from the prepared cache: {label_path}")
    label_times = [int(label["arrivalMonotonicNs"]) for label in labels]
    frame_times = [timestamp for _offset, timestamp in entries]
    label_indices, errors_ms = nearest_label_indices(frame_times, label_times)
    if float(np.max(errors_ms)) > 35.0:
        raise ValueError(f"Capture/label alignment exceeds 35 ms: {label_path}")
    if not np.array_equal(arrays["timestamps"], np.asarray(frame_times, dtype=np.int64)):
        raise ValueError(f"Prepared timestamps differ from the raw capture: {capture_path}")
    image_size = int(metadata["imageSize"])
    with capture_path.open("rb", buffering=4 * 1024 * 1024) as capture:
        for index, ((payload_offset, _timestamp), label_index) in enumerate(zip(entries, label_indices)):
            capture.seek(payload_offset)
            raw = capture.read(frame_width * FACE_HEIGHT)
            if len(raw) != frame_width * FACE_HEIGHT:
                raise ValueError(f"Raw still {index} is truncated: {capture_path}")
            strip = np.frombuffer(raw, dtype=np.uint8).reshape(FACE_HEIGHT, frame_width)
            for view in range(2):
                panel = strip[:, view * 400:(view + 1) * 400]
                expected = cv2.resize(panel, (image_size, image_size), interpolation=cv2.INTER_AREA)
                if not np.array_equal(arrays["images"][index, view], expected):
                    raise ValueError(f"Prepared image {index} differs from the raw capture: {capture_path}")
            factory = np.asarray(labels[int(label_index)]["values"], dtype=np.float32)
            native = native_tongue_out(
                labels[int(label_index)], names,
                tracking_source=metadata.get("nativeTongueSourceOverride"),
            ) or 0.0
            if (len(factory) != len(names)
                    or not np.array_equal(arrays["native_expressions"][index], factory)
                    or not np.isclose(arrays["native_tongue_out"][index], native, atol=1e-6)):
                raise ValueError(f"Prepared native label {index} differs from the sidecar: {label_path}")


def _check_cache(cache: Path) -> tuple[dict[str, object], tuple[str, ...], int, dict[str, str]]:
    metadata = _read_json(cache / "metadata.json")
    if metadata.get("datasetType") != "manual-stereo-stills" or not metadata.get("complete"):
        raise ValueError(f"Only completed exact-still caches are eligible: {cache}")
    require_current_tongue_cache(metadata)
    if metadata.get("sessionType") not in ALLOWED_SESSION_TYPES:
        raise ValueError(f"Unsupported capture session type in {cache}")
    if list(metadata.get("targetNames", [])) != list(TONGUE_TARGET_NAMES):
        raise ValueError(f"Target schema differs from the runtime model: {cache}")
    if list(metadata.get("cameraOrder", [])) != [
        "left_face_camera2", "right_face_camera3"
    ]:
        raise ValueError(f"Unexpected camera order: {cache}")
    arrays = _arrays(cache)
    images, targets = arrays["images"], arrays["targets"]
    native, native_all, timestamps, steps, trainable = (
        arrays["native_tongue_out"], arrays["native_expressions"],
        arrays["timestamps"], arrays["step_ids"], arrays["trainable"]
    )
    frames = len(images)
    size = int(metadata.get("imageSize", 0))
    if (
        images.dtype != np.uint8 or images.shape != (frames, 2, size, size)
        or targets.shape != (frames, len(TONGUE_TARGET_NAMES))
        or targets.dtype != np.float32
        or native.shape != (frames,) or native.dtype != np.float32
        or native_all.shape != (frames, len(metadata.get("factoryExpressionNames", [])))
        or native_all.dtype != np.float32
        or timestamps.shape != (frames,) or timestamps.dtype != np.int64
        or steps.shape != (frames,)
        or trainable.shape != (frames,) or trainable.dtype != np.bool_
        or frames != int(metadata.get("frames", -1))
        or not 128 <= size <= 320
    ):
        raise ValueError(f"Cache arrays have an invalid shape or type: {cache}")
    if not np.all(np.isfinite(targets)) or not np.all(np.isfinite(native)):
        raise ValueError(f"Cache has non-finite labels: {cache}")
    for column, name in enumerate(TONGUE_TARGET_NAMES):
        low = -1.0 if name in SIGNED_TARGETS else 0.0
        if np.any(targets[:, column] < low) or np.any(targets[:, column] > 1.0):
            raise ValueError(f"{name} labels are outside their output range: {cache}")
    if np.any(native < 0.0) or np.any(native > 1.0):
        raise ValueError(f"Native TongueOut labels are outside 0..1: {cache}")

    session_path = _resolve(cache, metadata.get("session"))
    capture_path = _resolve(cache, metadata.get("capture"))
    label_path = _resolve(cache, metadata.get("labels"))
    journal = _read_json(session_path)
    if not journal.get("completed") or journal.get("sessionType") != metadata["sessionType"]:
        raise ValueError(f"Capture journal is incomplete or mismatched: {session_path}")
    prompts = journal.get("prompts")
    samples = journal.get("samples")
    if not isinstance(prompts, list) or not isinstance(samples, list) or len(samples) != frames:
        raise ValueError(f"Capture journal does not match its cache: {session_path}")
    by_frame = {int(item["frameIndex"]): item for item in samples}
    if set(by_frame) != set(range(frames)):
        raise ValueError(f"Capture journal has duplicate or missing frames: {session_path}")
    for index in range(frames):
        sample = by_frame[index]
        prompt_id = int(sample["promptIndex"])
        if prompt_id < 0 or prompt_id >= len(prompts) or prompt_id != int(steps[index]):
            raise ValueError(f"Prompt index differs between journal and cache: {session_path}")
        if bool(sample.get("excluded", False)) == bool(trainable[index]):
            raise ValueError(f"Excluded-frame flag differs from cache: {session_path}")
        expected = [float(sample.get("targets", {}).get(name, 0.0)) for name in TONGUE_TARGET_NAMES]
        if not np.allclose(targets[index], expected, atol=1e-6):
            raise ValueError(f"Prompt targets differ between journal and cache: {session_path}")
    selected = int(np.count_nonzero(trainable))
    if selected != int(metadata.get("trainableFrames", -1)) or selected == 0:
        raise ValueError(f"Trainable frame count is invalid: {cache}")
    _verify_source_frames(capture_path, label_path, metadata, arrays)
    names = tuple(str(item.get("name", f"Card {index + 1}")) for index, item in enumerate(prompts))
    hashes = {
        "capture": _sha256(capture_path),
        "labels": _sha256(label_path),
        "session": _sha256(session_path),
        "images": _sha256(cache / "images.npy"),
        "nativeTongueOut": _sha256(cache / "native_tongue_out.npy"),
    }
    return metadata, names, selected, hashes


def _complete_curriculum(cache: Path, metadata: dict[str, object]) -> bool:
    """Require every current Full/Focused card, including matched hair cards."""
    session_type = metadata["sessionType"]
    reference = (
        TONGUE_STILL_PROMPTS if session_type == "tongue-stereo-stills-v1"
        else TONGUE_ARC_PROMPTS if session_type == "tongue-stereo-arc-v3" else None
    )
    if reference is None:
        return False
    journal = _read_json(_resolve(cache, metadata["session"]))
    prompts = journal["prompts"]
    if len(prompts) != len(reference) or journal.get("skippedPrompts"):
        return False
    step_ids = np.load(cache / "step_ids.npy", mmap_mode="r", allow_pickle=False)
    trainable = np.load(cache / "trainable.npy", mmap_mode="r", allow_pickle=False)
    for index, card in enumerate(reference):
        recorded = prompts[index]
        if (recorded.get("name") != card.name
                or recorded.get("targets", {}) != card.targets
                or int(np.count_nonzero((step_ids == index) & trainable)) < card.minimum_captures):
            return False
    return True


def curriculum_issues(sessions: list[Session]) -> list[str]:
    problems = []
    for role in ("train", "holdout"):
        included = [item for item in sessions if item.role == role]
        for session_type, label in (
            ("tongue-stereo-stills-v1", "all 58 current Full cards"),
            ("tongue-stereo-arc-v3", "all current Focused diagonal and facial-hair cards"),
        ):
            if not any(item.metadata["sessionType"] == session_type
                       and item.complete_curriculum for item in included):
                problems.append(f"{role} needs a completed capture with {label}")
    return problems


def load_manifest(path: Path) -> list[Session]:
    manifest = _read_json(path)
    if manifest.get("schemaVersion") != 1 or manifest.get("purpose") != "developer-tongue-model":
        raise ValueError("Expected a version 1 developer-tongue-model consent manifest")
    entries = manifest.get("sessions")
    if not isinstance(entries, list) or not entries:
        raise ValueError("The consent manifest must list capture sessions")
    sessions = []
    cache_paths: set[Path] = set()
    session_ids: set[str] = set()
    captures: set[str] = set()
    image_arrays: set[str] = set()
    for entry in entries:
        if not isinstance(entry, dict):
            raise ValueError("Every manifest session must be an object")
        role = entry.get("role")
        if role not in {"train", "holdout"}:
            raise ValueError("Session role must be train or holdout")
        session_id = str(entry.get("sessionId", "")).strip()
        wearer_id = str(entry.get("wearerId", "")).strip()
        if not session_id or not wearer_id:
            raise ValueError("Each session needs a pseudonymous sessionId and wearerId")
        consent = entry.get("consent")
        if not isinstance(consent, dict) or consent.get("manualPoseReview") is not True:
            raise ValueError(f"Manual pose review must be recorded for {session_id}")
        needed = (
            ("training", "redistributeCheckpoint") if role == "train"
            else ("evaluation",)
        )
        if any(consent.get(item) is not True for item in needed):
            raise ValueError(f"Required consent is missing for {session_id}")
        cache = _resolve(path.parent, entry.get("cache"))
        metadata, prompt_names, count, hashes = _check_cache(cache)
        capture_hash = hashes["capture"]
        if (cache in cache_paths or session_id in session_ids
                or capture_hash in captures or hashes["images"] in image_arrays):
            raise ValueError("The manifest repeats a cache, session ID, capture, or identical image array across train/holdout")
        cache_paths.add(cache)
        session_ids.add(session_id)
        captures.add(capture_hash)
        image_arrays.add(hashes["images"])
        sessions.append(Session(
            cache, role, session_id, wearer_id, capture_hash, hashes, metadata,
            prompt_names, count, _complete_curriculum(cache, metadata),
        ))
    if not any(item.role == "train" for item in sessions):
        raise ValueError("At least one training session is required")
    if not any(item.role == "holdout" for item in sessions):
        raise ValueError("At least one separate holdout session is required")
    train_wearers = {item.wearer_id for item in sessions if item.role == "train"}
    holdout_wearers = {item.wearer_id for item in sessions if item.role == "holdout"}
    if train_wearers & holdout_wearers:
        raise ValueError("Every holdout wearer must be absent from all training sessions")
    sizes = {int(item.metadata["imageSize"]) for item in sessions}
    if len(sizes) != 1:
        raise ValueError("Prepare every developer session at the same image size")
    return sessions


class ModelPair:
    def __init__(self, gate_path: Path, direction_path: Path, device: torch.device) -> None:
        self.device = device
        self.models = []
        self.architectures = []
        self.image_sizes = []
        self.input_preprocessing = []
        checkpoints = []
        for path in (gate_path, direction_path):
            checkpoint = torch.load(path, map_location="cpu", weights_only=False)
            names = list(checkpoint["targetNames"])
            if names != list(TONGUE_TARGET_NAMES):
                raise ValueError(f"Checkpoint target schema is incompatible: {path}")
            size = int(checkpoint["imageSize"])
            if not 128 <= size <= 320:
                raise ValueError(f"Checkpoint image size is invalid: {path}")
            architecture = str(checkpoint.get("architecture", "legacy-late-fusion-v1"))
            input_preprocessing = resolve_input_preprocessing(checkpoint)
            model = create_model(architecture, names)
            model.load_state_dict(checkpoint["modelState"])
            self.models.append((model.to(device).eval(), size))
            self.architectures.append(architecture)
            self.image_sizes.append(size)
            self.input_preprocessing.append(input_preprocessing)
            checkpoints.append(checkpoint)
        gate = checkpoints[0].get("visibilityGate", {})
        self.camera_weight = float(gate.get("cameraWeight", 0.5))
        self.threshold = float(gate.get("threshold", 0.44))
        if not 0.0 <= self.camera_weight <= 1.0 or not 0.0 < self.threshold < 1.0:
            raise ValueError("The gate checkpoint has invalid frozen visibility settings")

    def predict(
        self, images: np.ndarray, raw_images: np.ndarray | None = None,
    ) -> np.ndarray:
        if any(mode != "raw-v1" for mode in self.input_preprocessing):
            if (raw_images is None or raw_images.dtype != np.uint8
                    or raw_images.shape != (len(images), 2, FACE_HEIGHT, 400)):
                raise ValueError("Contrast evaluation needs the original uint8 400px stereo panes")
        inputs = torch.from_numpy(np.asarray(images, dtype=np.float32) / 255.0).to(self.device)
        outputs = []
        with torch.inference_mode():
            for (model, size), mode in zip(self.models, self.input_preprocessing):
                if mode == "raw-v1":
                    # Retain the baseline's original float AREA resize exactly.
                    resized = (
                        inputs if inputs.shape[-1] == size
                        else F.interpolate(inputs, size=(size, size), mode="area")
                    )
                else:
                    # Replay the original panes, avoiding a second resize from
                    # a prepared cache that differs from live camera inputs.
                    processed = np.empty((len(images), 2, size, size), dtype=np.uint8)
                    for index, stereo in enumerate(raw_images):
                        pixels = stereo if stereo.shape[1:] == (size, size) else np.stack([
                            cv2.resize(view, (size, size), interpolation=cv2.INTER_AREA)
                            for view in stereo
                        ])
                        processed[index] = preprocess_stereo_images(pixels, mode)
                    resized = torch.from_numpy(processed).float().div_(255).to(self.device)
                outputs.append(model(resized).float().cpu().numpy())
        combined = outputs[1]
        combined[:, TONGUE_TARGET_NAMES.index("visibility")] = outputs[0][:, TONGUE_TARGET_NAMES.index("visibility")]
        return combined


def _rates(prediction: np.ndarray, target: np.ndarray, native: np.ndarray,
           camera_weight: float, threshold: float, mask: np.ndarray) -> dict[str, object]:
    visibility = TONGUE_TARGET_NAMES.index("visibility")
    horizontal = TONGUE_TARGET_NAMES.index("horizontal")
    vertical = TONGUE_TARGET_NAMES.index("vertical")
    expected = target[:, visibility] >= 0.5
    score = camera_weight * prediction[:, visibility] + (1.0 - camera_weight) * native
    observed = score >= threshold
    hidden = mask & ~expected
    visible = mask & expected
    true_positive = int(np.count_nonzero(visible & observed))
    false_positive = int(np.count_nonzero(hidden & observed))
    missed = int(np.count_nonzero(visible & ~observed))
    precision = true_positive / (true_positive + false_positive) if true_positive + false_positive else 0.0
    recall = true_positive / int(np.count_nonzero(visible)) if np.any(visible) else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    direction = visible & (
        (np.abs(target[:, horizontal]) >= 0.25) | (np.abs(target[:, vertical]) >= 0.25)
    )
    direction_error = np.abs(
        prediction[:, [horizontal, vertical]] - target[:, [horizontal, vertical]]
    ).mean(axis=1)
    return {
        "samples": int(np.count_nonzero(mask)),
        "hidden": int(np.count_nonzero(hidden)),
        "visible": int(np.count_nonzero(visible)),
        "falsePositive": false_positive,
        "missedVisible": missed,
        "precision": precision,
        "recall": recall,
        "f1": f1,
        "falsePositiveRate": false_positive / int(np.count_nonzero(hidden)) if np.any(hidden) else None,
        "falseNegativeRate": missed / int(np.count_nonzero(visible)) if np.any(visible) else None,
        "directionSamples": int(np.count_nonzero(direction)),
        "directionMae": float(np.mean(direction_error[direction])) if np.any(direction) else None,
    }


def summarize(prediction: np.ndarray, target: np.ndarray, native: np.ndarray,
              camera_weight: float, threshold: float, session_ids: np.ndarray,
              wearer_ids: np.ndarray, step_ids: np.ndarray,
              prompt_names: dict[str, tuple[str, ...]]) -> dict[str, object]:
    mask = np.ones(len(target), dtype=np.bool_)
    result = _rates(prediction, target, native, camera_weight, threshold, mask)
    result["frozenGate"] = {"cameraWeight": camera_weight, "threshold": threshold}
    result["perWearer"] = {
        str(wearer): _rates(prediction, target, native, camera_weight, threshold, wearer_ids == wearer)
        for wearer in np.unique(wearer_ids)
    }
    per_prompt = []
    for session_id in np.unique(session_ids):
        for step in np.unique(step_ids[session_ids == session_id]):
            item = _rates(prediction, target, native, camera_weight, threshold,
                          (session_ids == session_id) & (step_ids == step))
            item.update({
                "sessionId": str(session_id), "promptId": int(step),
                "prompt": prompt_names[str(session_id)][int(step)],
            })
            per_prompt.append(item)
    result["perPrompt"] = per_prompt
    x = target[:, TONGUE_TARGET_NAMES.index("horizontal")]
    y = target[:, TONGUE_TARGET_NAMES.index("vertical")]
    visible = target[:, TONGUE_TARGET_NAMES.index("visibility")] >= 0.5
    result["diagonalCorners"] = {}
    for name, xsign, ysign in (
        ("upper-left", -1, 1), ("upper-right", 1, 1),
        ("lower-left", -1, -1), ("lower-right", 1, -1),
    ):
        corner = visible & (x * xsign >= 0.4) & (y * ysign >= 0.4)
        result["diagonalCorners"][name] = _rates(
            prediction, target, native, camera_weight, threshold, corner
        )
    return result


def promotion_gate(baseline: dict[str, object], candidate: dict[str, object],
                   train_wearers: set[str], holdout_wearers: set[str]) -> list[str]:
    """Return concrete reasons that a candidate cannot be staged for release."""
    problems = []
    if train_wearers & holdout_wearers or not holdout_wearers:
        problems.append("Every holdout wearer must be absent from the training group")
    if candidate["hidden"] < 24 or candidate["visible"] < 24:
        problems.append("Holdout needs at least 24 hidden and 24 visible stills")
    for corner, values in candidate["diagonalCorners"].items():
        if values["visible"] < 4:
            problems.append(f"Holdout needs at least four visible {corner} diagonal stills")
            continue
        baseline_corner = baseline["diagonalCorners"][corner]
        if values["directionMae"] > baseline_corner["directionMae"] + 0.10:
            problems.append(f"Direction error regressed on {corner} diagonals")
    if candidate["f1"] < 0.80:
        problems.append("Candidate visibility F1 is below 0.80")
    if candidate["falsePositiveRate"] is None or candidate["falsePositiveRate"] > 0.10:
        problems.append("Candidate hidden false-positive rate exceeds 0.10")
    if candidate["falseNegativeRate"] is None or candidate["falseNegativeRate"] > 0.25:
        problems.append("Candidate visible false-negative rate exceeds 0.25")
    if candidate["directionMae"] is None or candidate["directionMae"] > 0.35:
        problems.append("Candidate X/Y direction MAE exceeds 0.35")
    if candidate["f1"] < baseline["f1"] - 0.01:
        problems.append("Visibility F1 regressed against the bundled model")
    if candidate["falsePositiveRate"] is not None and baseline["falsePositiveRate"] is not None:
        if candidate["falsePositiveRate"] > baseline["falsePositiveRate"] + 0.02:
            problems.append("Hidden false-positive rate regressed against the bundled model")
    if candidate["directionMae"] is not None and baseline["directionMae"] is not None:
        if candidate["directionMae"] > baseline["directionMae"] + 0.02:
            problems.append("X/Y direction error regressed against the bundled model")
    for wearer, values in candidate.get("perWearer", {}).items():
        previous = baseline["perWearer"][wearer]
        if values["visible"] >= 24 and values["hidden"] >= 24:
            if values["f1"] < previous["f1"] - 0.05:
                problems.append(f"Visibility regressed for holdout wearer {wearer}")
            if values["falsePositiveRate"] > previous["falsePositiveRate"] + 0.05:
                problems.append(f"Hidden false positives regressed for holdout wearer {wearer}")
            if (values["directionMae"] is not None and previous["directionMae"] is not None
                    and values["directionMae"] > previous["directionMae"] + 0.08):
                problems.append(f"X/Y direction regressed for holdout wearer {wearer}")
    materially_better = (
        candidate["f1"] >= baseline["f1"] + 0.02
        or (baseline["directionMae"] is not None
            and candidate["directionMae"] is not None
            and candidate["directionMae"] <= baseline["directionMae"] * 0.85)
    )
    if not materially_better:
        problems.append("Neither F1 improved by 0.02 nor X/Y direction MAE improved by 15%")
    return problems


def evaluate_pair(pair: ModelPair, sessions: list[Session], batch_size: int) -> dict[str, object]:
    predictions = []
    targets = []
    natives = []
    session_ids = []
    wearer_ids = []
    steps = []
    needs_raw = any(mode != "raw-v1" for mode in pair.input_preprocessing)
    for session in sessions:
        arrays = _arrays(session.cache)
        selected = np.flatnonzero(arrays["trainable"])
        raw_capture = None
        try:
            if needs_raw:
                capture_path = _resolve(session.cache, session.metadata["capture"])
                if _sha256(capture_path) != session.capture_sha256:
                    raise ValueError("Raw evaluation capture changed after provenance verification")
                entries, frame_width = scan_stereo_mouth_stills(capture_path)
                raw_capture = capture_path.open("rb", buffering=4 * 1024 * 1024)
            for start in range(0, len(selected), batch_size):
                indices = selected[start:start + batch_size]
                raw_images = (
                    _read_raw_stereo(raw_capture, entries, frame_width, indices)
                    if raw_capture is not None else None
                )
                predictions.append(pair.predict(arrays["images"][indices], raw_images))
        finally:
            if raw_capture is not None:
                raw_capture.close()
        targets.append(np.asarray(arrays["targets"][selected]))
        natives.append(np.asarray(arrays["native_tongue_out"][selected]))
        steps.append(np.asarray(arrays["step_ids"][selected]))
        session_ids.extend([session.session_id] * len(selected))
        wearer_ids.extend([session.wearer_id] * len(selected))
    return summarize(
        np.concatenate(predictions), np.concatenate(targets), np.concatenate(natives),
        pair.camera_weight, pair.threshold, np.asarray(session_ids),
        np.asarray(wearer_ids), np.concatenate(steps),
        {session.session_id: session.prompt_names for session in sessions},
    )


def _run(command: list[str]) -> None:
    print("Running:", " ".join(command), flush=True)
    subprocess.run(command, check=True)


def resize_training_cache(source: Path, destination: Path, size: int) -> None:
    """Keep an initialized branch at its original v8 input resolution."""
    destination.mkdir(parents=True, exist_ok=False)
    metadata = _read_json(source / "metadata.json")
    images = np.load(source / "images.npy", mmap_mode="r", allow_pickle=False)
    output = np.lib.format.open_memmap(
        destination / "images.npy", mode="w+", dtype=np.uint8,
        shape=(len(images), 2, size, size),
    )
    for index in range(len(images)):
        for view in range(2):
            output[index, view] = cv2.resize(
                images[index, view], (size, size), interpolation=cv2.INTER_AREA,
            )
    output.flush()
    for name in ("targets.npy", "native_tongue_out.npy", "step_ids.npy", "trainable.npy"):
        shutil.copy2(source / name, destination / name)
    metadata["imageSize"] = size
    metadata["derivedFor"] = "developer checkpoint fine tuning at the parent resolution"
    (destination / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")


def rebuild_training_cache_from_raw(source: Path, destination: Path, size: int) -> None:
    """Derive unenhanced branch inputs directly from verified original panes.

    This only prepares a private cache. Consent and manual review are still
    required by the caller before model training; release gates are unchanged.
    """
    if not 128 <= size <= 320:
        raise ValueError("Branch image size must be between 128 and 320")
    metadata, _prompts, _count, _hashes = _check_cache(source)
    capture_path = _resolve(source, metadata["capture"])
    entries, frame_width = scan_stereo_mouth_stills(capture_path)
    destination.mkdir(parents=True, exist_ok=False)
    output = np.lib.format.open_memmap(
        destination / "images.npy", mode="w+", dtype=np.uint8,
        shape=(len(entries), 2, size, size),
    )
    try:
        with capture_path.open("rb", buffering=4 * 1024 * 1024) as capture:
            for start in range(0, len(entries), 16):
                indices = np.arange(start, min(start + 16, len(entries)))
                raw = _read_raw_stereo(capture, entries, frame_width, indices)
                for index, stereo in zip(indices, raw):
                    for view in range(2):
                        output[index, view] = cv2.resize(
                            stereo[view], (size, size), interpolation=cv2.INTER_AREA,
                        )
        output.flush()
    finally:
        output._mmap.close()
    for name in ARRAY_NAMES:
        if name != "images":
            shutil.copy2(source / f"{name}.npy", destination / f"{name}.npy")
    for key in ("capture", "labels", "session"):
        metadata[key] = str(_resolve(source, metadata[key]))
    metadata["imageSize"] = size
    metadata["derivedFor"] = "direct original stereo panes at the branch resolution"
    metadata["inputSampling"] = "raw-capture-direct-area"
    (destination / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")


def refinement_command(
    script: Path, cache: Path, initial_checkpoint: Path, output: Path,
    architecture: str, focus: str, epochs: int, batch_size: int, device: str,
    input_preprocessing: str | None = None,
) -> list[str]:
    """Fine tune a copy of one compatible v8 checkpoint, preserving its file."""
    command = [
        sys.executable, str(script), str(cache),
        "--initial-checkpoint", str(initial_checkpoint),
        "--architecture", architecture,
        "--learning-rate", "0.00005",
        "--checkpoint-focus", focus,
        "--epochs", str(epochs), "--batch-size", str(batch_size),
        "--device", device, "--output", str(output),
    ]
    if input_preprocessing is not None:
        mode = resolve_input_preprocessing({"inputPreprocessing": input_preprocessing})
        command.extend(["--input-preprocessing", mode])
    return command


def validate_public_checkpoint_metadata(path: Path) -> None:
    """Reject private absolute paths embedded in a distributable .pt file."""
    checkpoint = torch.load(path, map_location="cpu", weights_only=False)
    if not isinstance(checkpoint, dict):
        raise ValueError(f"Expected checkpoint metadata dictionary: {path}")
    parent = checkpoint.get("parentCheckpoint")
    if parent is not None and (not isinstance(parent, str) or Path(parent).name != parent):
        raise ValueError(f"Checkpoint parent reference contains a path: {path}")

    def scan(value: object) -> None:
        if isinstance(value, str):
            if re.search(r"(?i)(?:[a-z]:[\\/]|\\\\[^\\]|/home/|/Users/)", value):
                raise ValueError(f"Checkpoint metadata contains a private absolute path: {path}")
        elif isinstance(value, dict):
            for key, item in value.items():
                if key != "modelState":
                    scan(item)
        elif isinstance(value, (list, tuple)):
            for item in value:
                scan(item)

    scan(checkpoint)


def validate_refinement_parent(
    candidate: Path, parent: Path, expected_sha256: str,
    expected_architecture: str, expected_image_size: int,
) -> int:
    """Confirm the saved branch really started from the intended v8 weights."""
    checkpoint = torch.load(candidate, map_location="cpu", weights_only=False)
    if (checkpoint.get("parentCheckpoint") != parent.name
            or checkpoint.get("parentCheckpointSha256") != expected_sha256
            or checkpoint.get("architecture") != expected_architecture
            or int(checkpoint.get("imageSize", 0)) != expected_image_size):
        raise ValueError(f"Candidate checkpoint does not match its v8 parent: {candidate}")
    return int(checkpoint.get("checkpointEpoch", 0))


def fresh_output_dir(path: Path) -> None:
    if path.exists() and (not path.is_dir() or any(path.iterdir())):
        raise FileExistsError("Output directory is not empty; use a fresh run directory to preserve earlier reports and checkpoints")
    path.mkdir(parents=True, exist_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--check-only", action="store_true", help="validate consent and cache provenance without training")
    parser.add_argument("--baseline-gate", type=Path)
    parser.add_argument("--baseline-direction", type=Path)
    parser.add_argument("--candidate-gate", type=Path, help="evaluate an already trained pair instead of training")
    parser.add_argument("--candidate-direction", type=Path)
    parser.add_argument("--output-dir", type=Path, default=Path("training/developer-candidate"))
    parser.add_argument("--epochs", type=int, default=24)
    parser.add_argument("--batch-size", type=int, default=16)
    parser.add_argument("--device", default="auto")
    parser.add_argument(
        "--input-preprocessing", choices=MODES,
        help="optionally change candidate processing; otherwise each branch inherits its parent",
    )
    parser.add_argument("--stage", action="store_true", help="copy a passing pair to a candidate staging folder")
    args = parser.parse_args()
    if args.epochs < 1 or args.batch_size < 1:
        parser.error("epochs and batch size must be positive")
    if bool(args.candidate_gate) != bool(args.candidate_direction):
        parser.error("provide both candidate checkpoint paths or neither")
    if args.stage and args.candidate_gate:
        parser.error("external candidate checkpoints may be audited but cannot be staged without training provenance")
    if args.input_preprocessing and args.candidate_gate:
        parser.error("external candidates use their saved input processing and cannot be overridden")
    sessions = load_manifest(args.manifest.resolve(strict=True))
    train = [item for item in sessions if item.role == "train"]
    holdout = [item for item in sessions if item.role == "holdout"]
    coverage_problems = curriculum_issues(sessions)
    print(f"Approved sessions: {len(train)} train, {len(holdout)} holdout; "
          f"stills: {sum(item.trainable_frames for item in train)} train, "
          f"{sum(item.trainable_frames for item in holdout)} holdout", flush=True)
    for problem in coverage_problems:
        print(f"COVERAGE_BLOCKED: {problem}")
    if coverage_problems:
        return 2
    if args.check_only:
        return 0
    if args.baseline_gate is None or args.baseline_direction is None:
        parser.error("baseline gate and direction checkpoints are required")
    baseline_gate = args.baseline_gate.resolve(strict=True)
    baseline_direction = args.baseline_direction.resolve(strict=True)
    baseline_hashes = (_sha256(baseline_gate), _sha256(baseline_direction))
    baseline_pair = ModelPair(baseline_gate, baseline_direction, torch.device("cpu"))
    architectures = tuple(baseline_pair.architectures)
    baseline_sizes = tuple(baseline_pair.image_sizes)
    baseline_preprocessing = tuple(baseline_pair.input_preprocessing)
    del baseline_pair
    output = args.output_dir.resolve()
    fresh_output_dir(output)
    if args.candidate_gate:
        candidate_gate = args.candidate_gate.resolve(strict=True)
        candidate_direction = args.candidate_direction.resolve(strict=True)
    else:
        candidate_gate = output / "candidate-gate.pt"
        candidate_direction = output / "candidate-direction.pt"
        if candidate_gate.exists() or candidate_direction.exists():
            raise FileExistsError("Candidate checkpoint already exists; choose a fresh output directory")
        script_dir = Path(__file__).resolve().parent
        if len(train) == 1:
            train_cache = train[0].cache
        else:
            train_cache = output / "merged-training-cache"
            if train_cache.exists():
                raise FileExistsError("Merged cache already exists; choose a fresh output directory")
            _run([sys.executable, str(script_dir / "merge_manual_tongue_caches.py"),
                  *(str(item.cache) for item in train), "--output", str(train_cache),
                  "--size", str(train[0].metadata["imageSize"])])
        for focus, checkpoint in (("visibility", candidate_gate), ("direction", candidate_direction)):
            source = baseline_gate if focus == "visibility" else baseline_direction
            branch_index = 0 if focus == "visibility" else 1
            architecture = architectures[branch_index]
            branch_cache = train_cache
            mode = args.input_preprocessing or baseline_preprocessing[branch_index]
            if mode != "raw-v1":
                direct_caches = []
                for index, session in enumerate(train):
                    direct = output / f"{focus}-raw-session-{index}"
                    rebuild_training_cache_from_raw(session.cache, direct, baseline_sizes[branch_index])
                    direct_caches.append(direct)
                branch_cache = direct_caches[0]
                if len(direct_caches) > 1:
                    branch_cache = output / f"{focus}-training-cache"
                    _run([sys.executable, str(script_dir / "merge_manual_tongue_caches.py"),
                          *(str(cache) for cache in direct_caches), "--output", str(branch_cache),
                          "--size", str(baseline_sizes[branch_index])])
            elif baseline_sizes[branch_index] != int(train[0].metadata["imageSize"]):
                branch_cache = output / f"{focus}-training-cache"
                resize_training_cache(train_cache, branch_cache, baseline_sizes[branch_index])
            _run(refinement_command(
                script_dir / "train_tongue_model.py", branch_cache, source, checkpoint,
                architecture, focus, args.epochs, args.batch_size, args.device,
                args.input_preprocessing,
            ))
    validate_public_checkpoint_metadata(candidate_gate)
    validate_public_checkpoint_metadata(candidate_direction)
    trained_epochs = None
    if not args.candidate_gate:
        trained_epochs = (
            validate_refinement_parent(
                candidate_gate, baseline_gate, baseline_hashes[0], architectures[0], baseline_sizes[0],
            ),
            validate_refinement_parent(
                candidate_direction, baseline_direction, baseline_hashes[1], architectures[1], baseline_sizes[1],
            ),
        )
        if (_sha256(baseline_gate), _sha256(baseline_direction)) != baseline_hashes:
            raise RuntimeError("The original v8 checkpoint files changed during this run")
    device = torch.device(validated_torch_device_name(torch, args.device))
    baseline = evaluate_pair(ModelPair(baseline_gate, baseline_direction, device),
                             holdout, args.batch_size)
    candidate_pair = ModelPair(candidate_gate, candidate_direction, device)
    candidate_preprocessing = tuple(candidate_pair.input_preprocessing)
    candidate = evaluate_pair(candidate_pair, holdout, args.batch_size)
    problems = coverage_problems + promotion_gate(
        baseline, candidate, {item.wearer_id for item in train},
        {item.wearer_id for item in holdout},
    )
    if args.candidate_gate:
        problems.append("External candidate training provenance is unverified by this run")
    elif trained_epochs == (0, 0):
        problems.append("Neither checkpoint improved after fine tuning; both retained their v8 weights")
    report = {
        "schemaVersion": 1,
        "interpretation": "Prompt targets are intended poses, not verified physical ground truth. This raw-still audit does not model live EMA, visibility hysteresis, or VRChat behavior; manual camera review and live tracking QA remain required.",
        "split": (
            "Entire held-out capture sessions were excluded from this run's training, checkpoint selection, and visibility-threshold tuning."
            if not args.candidate_gate else
            "External checkpoint provenance is unknown; holdout exclusion from its training cannot be verified."
        ),
        "sessions": [{
            "sessionId": item.session_id, "wearerId": item.wearer_id,
            "role": item.role, "captureSha256": item.capture_sha256,
            "sourceSha256": item.source_sha256,
            "trainableFrames": item.trainable_frames,
            "completeCurrentCurriculum": item.complete_curriculum,
        } for item in sessions],
        "checkpointSha256": {
            "baselineGate": baseline_hashes[0],
            "baselineDirection": baseline_hashes[1],
            "candidateGate": _sha256(candidate_gate),
            "candidateDirection": _sha256(candidate_direction),
        },
        "inputPreprocessing": {
            "baselineGate": baseline_preprocessing[0],
            "baselineDirection": baseline_preprocessing[1],
            "candidateGate": candidate_preprocessing[0],
            "candidateDirection": candidate_preprocessing[1],
        },
        "inputSampling": {
            name: "raw-capture-direct-area" if mode != "raw-v1" else "prepared-cache-float-area"
            for name, mode in zip(
                ("baselineGate", "baselineDirection", "candidateGate", "candidateDirection"),
                (*baseline_preprocessing, *candidate_preprocessing),
            )
        },
        "fineTunedEpochs": (
            {"gate": trained_epochs[0], "direction": trained_epochs[1]}
            if trained_epochs is not None else None
        ),
        "baseline": baseline,
        "candidate": candidate,
        "offlineGatePassed": not problems,
        "liveTrackingValidated": False,
        "promotionFailures": problems,
    }
    report_path = output / "holdout-report.json"
    report_path.write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
    print(f"Held-out report: {report_path}")
    print(f"Baseline F1 {baseline['f1']:.3f}, X/Y MAE {baseline['directionMae']}; "
          f"candidate F1 {candidate['f1']:.3f}, X/Y MAE {candidate['directionMae']}")
    if problems:
        for problem in problems:
            print(f"PROMOTION_BLOCKED: {problem}")
        return 2
    if args.stage:
        stage = output / "offline-gate-pair"
        if stage.exists():
            raise FileExistsError("Offline-gate folder already exists; choose a fresh output directory")
        stage.mkdir()
        shutil.copy2(candidate_gate, stage / "developer-candidate-gate.pt")
        shutil.copy2(candidate_direction, stage / "developer-candidate-direction.pt")
        shutil.copy2(report_path, stage / "holdout-report.json")
        print(f"Offline candidate staged for manual live release review: {stage}")
    else:
        print("OFFLINE_GATE_PASSED; live tracking QA remains required before release")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
