#!/usr/bin/env python3
"""Merge multiple exact-still tongue caches without losing prompt boundaries."""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import cv2
import numpy as np
from label_capture import NATIVE_TONGUE_MAPPING, require_current_tongue_cache


ARRAY_FILES = (
    "images.npy",
    "targets.npy",
    "native_tongue_out.npy",
    "native_expressions.npy",
    "timestamps.npy",
    "step_ids.npy",
    "trainable.npy",
)


def load_cache(path: Path) -> dict[str, object]:
    metadata = json.loads((path / "metadata.json").read_text(encoding="utf-8"))
    if metadata.get("datasetType") != "manual-stereo-stills":
        raise ValueError(f"Not an exact-still tongue cache: {path}")
    require_current_tongue_cache(metadata)
    values: dict[str, object] = {"path": path, "metadata": metadata}
    for filename in ARRAY_FILES:
        values[filename[:-4]] = np.load(path / filename, mmap_mode="r")
    return values


def main() -> int:
    parser = argparse.ArgumentParser(description="Merge manual tongue still caches")
    parser.add_argument("inputs", nargs="+")
    parser.add_argument("--output", required=True)
    parser.add_argument("--size", type=int, default=224)
    arguments = parser.parse_args()
    if not 128 <= arguments.size <= 320:
        parser.error("--size must be between 128 and 320")

    caches = [load_cache(Path(value).resolve()) for value in arguments.inputs]
    target_names = list(caches[0]["metadata"]["targetNames"])
    factory_names = list(caches[0]["metadata"]["factoryExpressionNames"])
    for cache in caches[1:]:
        if list(cache["metadata"]["targetNames"]) != target_names:
            raise ValueError("Manual caches use different target schemas")
        if list(cache["metadata"]["factoryExpressionNames"]) != factory_names:
            raise ValueError("Manual caches use different factory-label schemas")

    selections = [np.flatnonzero(cache["trainable"]) for cache in caches]
    total = sum(len(value) for value in selections)
    output = Path(arguments.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    images = np.lib.format.open_memmap(
        output / "images.npy",
        mode="w+",
        dtype=np.uint8,
        shape=(total, 2, arguments.size, arguments.size),
    )
    targets = np.empty((total, len(target_names)), dtype=np.float32)
    native = np.empty(total, dtype=np.float32)
    native_expressions = np.empty((total, len(factory_names)), dtype=np.float32)
    timestamps = np.empty(total, dtype=np.int64)
    step_ids = np.empty(total, dtype=np.int16)
    source_ids = np.empty(total, dtype=np.uint8)

    cursor = 0
    next_step = 0
    started = time.monotonic()
    for source_id, (cache, selected) in enumerate(zip(caches, selections)):
        source_steps = cache["step_ids"]
        unique_steps = sorted(int(value) for value in np.unique(source_steps[selected]))
        step_map = {value: next_step + index for index, value in enumerate(unique_steps)}
        next_step += len(unique_steps)
        for source_index in selected:
            source_image = np.asarray(cache["images"][source_index])
            for view in range(2):
                if source_image.shape[-1] == arguments.size:
                    images[cursor, view] = source_image[view]
                else:
                    images[cursor, view] = cv2.resize(
                        source_image[view],
                        (arguments.size, arguments.size),
                        interpolation=cv2.INTER_AREA,
                    )
            targets[cursor] = cache["targets"][source_index]
            native[cursor] = cache["native_tongue_out"][source_index]
            native_expressions[cursor] = cache["native_expressions"][source_index]
            timestamps[cursor] = cache["timestamps"][source_index]
            step_ids[cursor] = step_map[int(source_steps[source_index])]
            source_ids[cursor] = source_id
            cursor += 1
            if cursor % 250 == 0 or cursor == total:
                rate = cursor / max(0.001, time.monotonic() - started)
                print(f"Merged {cursor}/{total} manual stills ({rate:.1f}/s)")

    images.flush()
    np.save(output / "targets.npy", targets)
    np.save(output / "native_tongue_out.npy", native)
    np.save(output / "native_expressions.npy", native_expressions)
    np.save(output / "timestamps.npy", timestamps)
    np.save(output / "step_ids.npy", step_ids)
    np.save(output / "source_ids.npy", source_ids)
    np.save(output / "trainable.npy", np.ones(total, dtype=np.bool_))
    metadata = {
        "version": 1,
        "nativeTongueMapping": NATIVE_TONGUE_MAPPING,
        "datasetType": "manual-stereo-stills",
        "sessionType": "merged-personal-corrections-v1",
        "complete": True,
        "frames": total,
        "trainableFrames": total,
        "excludedFrames": 0,
        "imageSize": arguments.size,
        "cameraOrder": ["left_face_camera2", "right_face_camera3"],
        "targetNames": target_names,
        "factoryExpressionNames": factory_names,
        "sources": [str(cache["path"]) for cache in caches],
        "sourceFrameCounts": [len(value) for value in selections],
        "capturePolicy": "merged exact synchronized manual stills",
    }
    (output / "metadata.json").write_text(
        json.dumps(metadata, indent=2), encoding="utf-8"
    )
    print(f"Merged manual tongue cache complete: {output}")
    print(f"Frames: {total}; prompt groups: {next_step}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
