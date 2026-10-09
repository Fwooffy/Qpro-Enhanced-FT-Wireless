import json
import sys
import tempfile
import unittest
from dataclasses import asdict
from pathlib import Path
from unittest import mock

import cv2
import numpy as np
import torch
import test_label_capture

from capture_format import CaptureWriter, TRANSPORT_HEADER
from developer_tongue_training import (
    ModelPair, _check_cache, _complete_curriculum, curriculum_issues, evaluate_pair,
    fresh_output_dir, load_manifest, promotion_gate, refinement_command,
    rebuild_training_cache_from_raw, resize_training_cache, summarize, validate_public_checkpoint_metadata,
    validate_refinement_parent,
)
from tongue_calibration import TONGUE_TARGET_NAMES
from tongue_still_capture import TONGUE_ARC_PROMPTS, TONGUE_STILL_PROMPTS
from tongue_image_processing import preprocess_stereo_images
from label_capture import NATIVE_TONGUE_MAPPING
from train_tongue_model import StereoTongueModel, parent_checkpoint_metadata


class DeveloperTongueTrainingTests(unittest.TestCase):
    @staticmethod
    def make_cache(root: Path, name: str) -> Path:
        cache = root / f"{name}-cache"
        cache.mkdir()
        capture = root / f"{name}.qpcap"
        labels = root / f"{name}.qplabel.jsonl"
        journal = root / f"{name}.qpsession.json"
        targets = np.zeros((8, len(TONGUE_TARGET_NAMES)), np.float32)
        targets[4:, 0] = 1.0
        targets[4:, 1] = 0.75
        images = np.zeros((8, 2, 128, 128), np.uint8)
        native = np.asarray([0.1] * 4 + [0.7] * 4, np.float32)
        timestamps = np.asarray([1_000_000_000 + index * 100_000_000 for index in range(8)], np.int64)
        samples = []
        label_lines = [json.dumps({"type": "schema", "names": ["TongueOut"]})]
        writer = CaptureWriter(capture)
        try:
            for index in range(8):
                offset = {"first": 10, "second": 20, "train": 30,
                          "holdout": 40, "seen": 50, "unseen": 60}[name]
                pixel = index + 1 + offset
                images[index] = pixel
                payload = bytes([pixel]) * (800 * 400)
                header = TRANSPORT_HEADER.pack(
                    b"QPLIVE3\0", 3, TRANSPORT_HEADER.size, index + 1, 2,
                    800, 400, 800, 1, len(payload), 0x0C, 0,
                )
                writer.write(header, payload, int(timestamps[index]), int(timestamps[index]))
                label_lines.append(json.dumps({
                    "type": "sample", "arrivalMonotonicNs": int(timestamps[index]),
                    "values": [float(native[index])],
                }))
                samples.append({
                    "frameIndex": index,
                    "promptIndex": 0 if index < 4 else 1,
                    "excluded": False,
                    "targets": {} if index < 4 else {"visibility": 1.0, "extension": 0.75},
                })
        finally:
            writer.close(completed=True)
        labels.write_text("\n".join(label_lines) + "\n", encoding="utf-8")
        journal.write_text(json.dumps({
            "sessionType": "tongue-stereo-stills-v1",
            "completed": True,
            "prompts": [{"name": "hidden"}, {"name": "visible"}],
            "samples": samples,
        }), encoding="utf-8")
        (cache / "metadata.json").write_text(json.dumps({
            "datasetType": "manual-stereo-stills",
            "nativeTongueMapping": NATIVE_TONGUE_MAPPING,
            "sessionType": "tongue-stereo-stills-v1",
            "complete": True,
            "imageSize": 128,
            "cameraOrder": ["left_face_camera2", "right_face_camera3"],
            "targetNames": list(TONGUE_TARGET_NAMES),
            "frames": 8,
            "trainableFrames": 8,
            "session": str(journal),
            "capture": str(capture),
            "labels": str(labels),
            "factoryExpressionNames": ["TongueOut"],
        }), encoding="utf-8")
        np.save(cache / "images.npy", images)
        np.save(cache / "targets.npy", targets)
        np.save(cache / "native_tongue_out.npy", native)
        np.save(cache / "native_expressions.npy", native[:, None])
        np.save(cache / "timestamps.npy", timestamps)
        np.save(cache / "step_ids.npy", np.asarray([0] * 4 + [1] * 4, np.int16))
        np.save(cache / "trainable.npy", np.ones(8, np.bool_))
        return cache

    def test_source_dependent_scalar_integrity_preserves_raw_factory_values(self):
        for source, expected in (("VirtualDesktop", 0.15), ("SteamLink", 0.85), (None, 0.85)):
            with self.subTest(source=source), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                cache = self.make_cache(root, "first")
                labels = root / "first.qplabel.jsonl"
                names = test_label_capture.NativeTongueLayoutTests.names
                values = [0.0] * 70
                values[63], values[68] = 0.15, 0.85
                records = [json.loads(line) for line in labels.read_text().splitlines()]
                records[0]["names"] = names
                for record in records[1:]:
                    record.update(values=values, faceFlags=3)
                    if source:
                        record["trackingSource"] = source
                labels.write_text("\n".join(json.dumps(record) for record in records) + "\n")
                metadata_path = cache / "metadata.json"
                metadata = json.loads(metadata_path.read_text())
                metadata["factoryExpressionNames"] = names
                if source is None:
                    metadata["nativeTongueSourceOverride"] = "SteamLink"
                metadata_path.write_text(json.dumps(metadata))
                np.save(cache / "native_expressions.npy", np.tile(np.asarray(values, np.float32), (8, 1)))
                np.save(cache / "native_tongue_out.npy", np.full(8, expected, np.float32))
                _check_cache(cache)
                # The other slot is deliberately different, so a slot68-only
                # integrity check would reject the valid alternate VD cache.
                np.save(cache / "native_tongue_out.npy", np.full(8, 1 - expected, np.float32))
                with self.assertRaisesRegex(ValueError, "differs from the sidecar"):
                    _check_cache(cache)

    def test_old_mapping_cache_requires_repreparation_before_provenance_check(self):
        with tempfile.TemporaryDirectory() as directory:
            cache = self.make_cache(Path(directory), "first")
            metadata_path = cache / "metadata.json"
            metadata = json.loads(metadata_path.read_text())
            metadata.pop("nativeTongueMapping")
            metadata_path.write_text(json.dumps(metadata))
            with self.assertRaisesRegex(ValueError, "Regenerate the cache"):
                _check_cache(cache)

    def test_manifest_requires_explicit_consent_and_distinct_captures(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = self.make_cache(root, "first")
            second = self.make_cache(root, "second")
            manifest = root / "manifest.json"
            entries = [
                {"cache": str(first), "role": "train", "sessionId": "s1", "wearerId": "a",
                 "consent": {"training": True, "redistributeCheckpoint": True, "manualPoseReview": True}},
                {"cache": str(second), "role": "holdout", "sessionId": "s2", "wearerId": "b",
                 "consent": {"evaluation": True, "manualPoseReview": True}},
            ]
            manifest.write_text(json.dumps({
                "schemaVersion": 1, "purpose": "developer-tongue-model", "sessions": entries,
            }), encoding="utf-8")
            self.assertEqual([item.role for item in load_manifest(manifest)], ["train", "holdout"])
            entries[0]["consent"]["redistributeCheckpoint"] = False
            manifest.write_text(json.dumps({
                "schemaVersion": 1, "purpose": "developer-tongue-model", "sessions": entries,
            }), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "consent"):
                load_manifest(manifest)
            entries[0]["consent"]["redistributeCheckpoint"] = True
            (root / "second.qpcap").write_bytes((root / "first.qpcap").read_bytes())
            (second / "images.npy").write_bytes((first / "images.npy").read_bytes())
            manifest.write_text(json.dumps({
                "schemaVersion": 1, "purpose": "developer-tongue-model", "sessions": entries,
            }), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "repeats"):
                load_manifest(manifest)

    def test_source_images_and_labels_must_match_raw_sidecars(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            train = self.make_cache(root, "train")
            holdout = self.make_cache(root, "holdout")
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps({
                "schemaVersion": 1, "purpose": "developer-tongue-model", "sessions": [
                    {"cache": str(train), "role": "train", "sessionId": "t", "wearerId": "a",
                     "consent": {"training": True, "redistributeCheckpoint": True, "manualPoseReview": True}},
                    {"cache": str(holdout), "role": "holdout", "sessionId": "h", "wearerId": "b",
                     "consent": {"evaluation": True, "manualPoseReview": True}},
                ],
            }), encoding="utf-8")
            self.assertEqual(len(load_manifest(manifest)), 2)
            images = np.load(train / "images.npy")
            images[2, 0, 0, 0] += 1
            np.save(train / "images.npy", images)
            with self.assertRaisesRegex(ValueError, "Prepared image"):
                load_manifest(manifest)
            images[2, 0, 0, 0] -= 1
            np.save(train / "images.npy", images)
            native = np.load(train / "native_tongue_out.npy")
            native[1] += 0.2
            np.save(train / "native_tongue_out.npy", native)
            with self.assertRaisesRegex(ValueError, "Prepared native label"):
                load_manifest(manifest)

    def test_all_holdout_wearers_must_be_disjoint_from_training(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            train = self.make_cache(root, "train")
            seen = self.make_cache(root, "seen")
            unseen = self.make_cache(root, "unseen")
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps({
                "schemaVersion": 1, "purpose": "developer-tongue-model", "sessions": [
                    {"cache": str(train), "role": "train", "sessionId": "t", "wearerId": "a",
                     "consent": {"training": True, "redistributeCheckpoint": True, "manualPoseReview": True}},
                    {"cache": str(seen), "role": "holdout", "sessionId": "h1", "wearerId": "a",
                     "consent": {"evaluation": True, "manualPoseReview": True}},
                    {"cache": str(unseen), "role": "holdout", "sessionId": "h2", "wearerId": "b",
                     "consent": {"evaluation": True, "manualPoseReview": True}},
                ],
            }), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Every holdout wearer"):
                load_manifest(manifest)

    def test_summary_uses_frozen_gate_and_reports_prompt_errors(self):
        target = np.zeros((4, len(TONGUE_TARGET_NAMES)), np.float32)
        target[2:, 0] = 1.0
        target[2:, 2] = -0.5
        target[2:, 3] = 0.5
        prediction = target.copy()
        prediction[:, 0] = [0.1, 0.8, 0.4, 0.9]
        report = summarize(
            prediction, target, np.zeros(4, np.float32), 1.0, 0.5,
            np.asarray(["s"] * 4), np.asarray(["w"] * 4), np.asarray([0, 0, 1, 1]),
            {"s": ("hidden", "upper left")},
        )
        self.assertEqual(report["falsePositive"], 1)
        self.assertEqual(report["missedVisible"], 1)
        self.assertEqual(report["perPrompt"][0]["falsePositiveRate"], 0.5)
        self.assertEqual(report["diagonalCorners"]["upper-left"]["visible"], 2)
        self.assertEqual(report["frozenGate"]["threshold"], 0.5)

    def test_model_pair_evaluates_separate_cache_with_checkpoint_gate(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = self.make_cache(root, "first")
            second = self.make_cache(root, "second")
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps({
                "schemaVersion": 1, "purpose": "developer-tongue-model", "sessions": [
                    {"cache": str(first), "role": "train", "sessionId": "s1", "wearerId": "a",
                     "consent": {"training": True, "redistributeCheckpoint": True, "manualPoseReview": True}},
                    {"cache": str(second), "role": "holdout", "sessionId": "s2", "wearerId": "b",
                     "consent": {"evaluation": True, "manualPoseReview": True}},
                ],
            }), encoding="utf-8")
            holdout = [item for item in load_manifest(manifest) if item.role == "holdout"]
            checkpoint = {
                "architecture": "legacy-late-fusion-v1",
                "targetNames": list(TONGUE_TARGET_NAMES),
                "imageSize": 128,
                "modelState": StereoTongueModel(list(TONGUE_TARGET_NAMES)).state_dict(),
                "visibilityGate": {"cameraWeight": 0.9, "threshold": 0.37},
            }
            gate = root / "gate.pt"
            direction = root / "direction.pt"
            torch.save(checkpoint, gate)
            torch.save(checkpoint, direction)
            pair = ModelPair(gate, direction, torch.device("cpu"))
            self.assertEqual(pair.architectures, ["legacy-late-fusion-v1"] * 2)
            self.assertEqual(pair.image_sizes, [128, 128])
            report = evaluate_pair(pair, holdout, 4)
            self.assertEqual(report["samples"], 8)
            self.assertEqual(report["frozenGate"], {"cameraWeight": 0.9, "threshold": 0.37})
            self.assertEqual(len(report["perPrompt"]), 2)
            self.assertEqual(set(report["perWearer"]), {"b"})
            checkpoint["architecture"] = "spatial-stereo-resnet-v2"
            torch.save(checkpoint, direction)
            with self.assertRaises(RuntimeError):
                ModelPair(gate, direction, torch.device("cpu"))

    def test_refinement_command_starts_from_matching_baseline(self):
        script = Path("train_tongue_model.py")
        source = Path("qpro-stereo-tongue-v8-direction.pt")
        output = Path("candidate-direction.pt")
        command = refinement_command(
            script, Path("training-cache"), source, output,
            "legacy-late-fusion-v1", "direction", 12, 16, "cpu",
        )
        self.assertEqual(command[0], sys.executable)
        self.assertEqual(command[command.index("--initial-checkpoint") + 1], str(source))
        self.assertEqual(command[command.index("--architecture") + 1], "legacy-late-fusion-v1")
        self.assertEqual(command[command.index("--learning-rate") + 1], "0.00005")
        self.assertEqual(command[command.index("--output") + 1], str(output))
        self.assertNotEqual(source, output)
        self.assertNotIn("--input-preprocessing", command)
        contrast = refinement_command(
            script, Path("training-cache"), source, output,
            "legacy-late-fusion-v1", "direction", 12, 16, "cpu", "clahe-v1",
        )
        self.assertEqual(contrast[contrast.index("--input-preprocessing") + 1], "clahe-v1")

    def test_each_evaluation_branch_honors_its_saved_processing_and_size(self):
        import developer_tongue_training

        class RecordingModel(torch.nn.Module):
            def forward(self, inputs):
                self.last_inputs = inputs.clone()
                return torch.zeros(len(inputs), len(TONGUE_TARGET_NAMES))

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            common = {"targetNames": list(TONGUE_TARGET_NAMES), "modelState": {},
                      "architecture": "legacy-late-fusion-v1"}
            gate = root / "gate.pt"
            direction = root / "direction.pt"
            torch.save({**common, "imageSize": 128, "inputPreprocessing": "clahe-v1"}, gate)
            torch.save({**common, "imageSize": 192}, direction)
            gate_model, direction_model = RecordingModel(), RecordingModel()
            with mock.patch.object(developer_tongue_training, "create_model",
                                   side_effect=[gate_model, direction_model]):
                pair = ModelPair(gate, direction, torch.device("cpu"))
            pixels = np.tile((np.arange(400) % 256).astype(np.uint8), (400, 1))
            raw_images = np.stack([np.stack([pixels, pixels.T])])
            images = np.stack([np.stack([
                cv2.resize(view, (224, 224), interpolation=cv2.INTER_AREA)
                for view in raw_images[0]
            ])])
            original = images.copy()
            with self.assertRaisesRegex(ValueError, "original uint8 400px"):
                pair.predict(images)
            pair.predict(images, raw_images)
            resized_pixels = np.stack([
                cv2.resize(view, (128, 128), interpolation=cv2.INTER_AREA)
                for view in raw_images[0]
            ])
            expected_gate = torch.from_numpy(
                preprocess_stereo_images(resized_pixels, "clahe-v1")[None]
            ).float().div_(255)
            raw = torch.from_numpy(np.asarray(images, dtype=np.float32) / 255.0)
            expected_direction = torch.nn.functional.interpolate(raw, size=(192, 192), mode="area")
            self.assertTrue(torch.equal(gate_model.last_inputs, expected_gate))
            self.assertTrue(torch.equal(direction_model.last_inputs, expected_direction))
            self.assertEqual(pair.input_preprocessing, ["clahe-v1", "raw-v1"])
            self.assertEqual(pair.image_sizes, [128, 192])
            np.testing.assert_array_equal(images, original)
            torch.save({**common, "imageSize": 128, "inputPreprocessing": "unknown"}, gate)
            with self.assertRaisesRegex(ValueError, "preprocessing"):
                ModelPair(gate, direction, torch.device("cpu"))

    def test_contrast_branch_cache_and_evaluation_resize_original_panes_once(self):
        import developer_tongue_training

        class RecordingModel(torch.nn.Module):
            def __init__(self):
                super().__init__()
                self.inputs = []

            def forward(self, inputs):
                self.inputs.append(inputs.clone())
                return torch.zeros(len(inputs), len(TONGUE_TARGET_NAMES))

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            train = self.make_cache(root, "train")
            holdout = self.make_cache(root, "holdout")
            metadata_path = holdout / "metadata.json"
            metadata = json.loads(metadata_path.read_text())
            raw_images = np.random.default_rng(81).integers(
                0, 256, (8, 2, 400, 400), dtype=np.uint8,
            )
            Path(metadata["capture"]).unlink()
            writer = CaptureWriter(Path(metadata["capture"]))
            timestamps = np.load(holdout / "timestamps.npy")
            try:
                for index, stereo in enumerate(raw_images):
                    payload = np.concatenate(stereo, axis=1).tobytes()
                    header = TRANSPORT_HEADER.pack(
                        b"QPLIVE3\0", 3, TRANSPORT_HEADER.size, index + 1, 2,
                        800, 400, 800, 1, len(payload), 0x0C, 0,
                    )
                    writer.write(header, payload, int(timestamps[index]), int(timestamps[index]))
            finally:
                writer.close(completed=True)
            prepared = np.stack([np.stack([
                cv2.resize(view, (224, 224), interpolation=cv2.INTER_AREA)
                for view in stereo
            ]) for stereo in raw_images])
            np.save(holdout / "images.npy", prepared)
            metadata["imageSize"] = 224
            metadata_path.write_text(json.dumps(metadata), encoding="utf-8")
            # Both sessions have the same prepared resolution for the manifest.
            train_metadata_path = train / "metadata.json"
            train_metadata = json.loads(train_metadata_path.read_text())
            train_metadata["imageSize"] = 224
            train_metadata_path.write_text(json.dumps(train_metadata), encoding="utf-8")
            train_pixels = np.load(train / "images.npy")[:, :, 0, 0]
            np.save(train / "images.npy", np.broadcast_to(
                train_pixels[:, :, None, None], (8, 2, 224, 224),
            ).copy())
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps({
                "schemaVersion": 1, "purpose": "developer-tongue-model", "sessions": [
                    {"cache": str(train), "role": "train", "sessionId": "t", "wearerId": "a",
                     "consent": {"training": True, "redistributeCheckpoint": True, "manualPoseReview": True}},
                    {"cache": str(holdout), "role": "holdout", "sessionId": "h", "wearerId": "b",
                     "consent": {"evaluation": True, "manualPoseReview": True}},
                ],
            }), encoding="utf-8")
            sessions = load_manifest(manifest)
            direct = root / "direction-cache"
            rebuild_training_cache_from_raw(holdout, direct, 192)
            branch = np.load(direct / "images.npy")
            expected = np.stack([np.stack([
                cv2.resize(view, (192, 192), interpolation=cv2.INTER_AREA)
                for view in stereo
            ]) for stereo in raw_images])
            np.testing.assert_array_equal(branch, expected)
            np.testing.assert_array_equal(np.load(holdout / "images.npy"), prepared)
            doubled = np.stack([np.stack([
                cv2.resize(view, (192, 192), interpolation=cv2.INTER_AREA)
                for view in stereo
            ]) for stereo in prepared])
            self.assertFalse(np.array_equal(branch, doubled))
            derived = json.loads((direct / "metadata.json").read_text())
            self.assertEqual(derived["inputSampling"], "raw-capture-direct-area")
            self.assertEqual(derived["imageSize"], 192)
            common = {"targetNames": list(TONGUE_TARGET_NAMES), "modelState": {},
                      "architecture": "legacy-late-fusion-v1", "imageSize": 192,
                      "inputPreprocessing": "clahe-v1"}
            gate, direction = root / "gate.pt", root / "direction.pt"
            torch.save(common, gate)
            torch.save(common, direction)
            gate_model, direction_model = RecordingModel(), RecordingModel()
            with mock.patch.object(developer_tongue_training, "create_model",
                                   side_effect=[gate_model, direction_model]):
                pair = ModelPair(gate, direction, torch.device("cpu"))
            report = evaluate_pair(pair, [sessions[1]], 3)
            self.assertEqual(report["samples"], 8)
            expected_input = torch.from_numpy(np.stack([
                preprocess_stereo_images(stereo, "clahe-v1") for stereo in expected
            ])).float().div_(255)
            self.assertTrue(torch.equal(torch.cat(direction_model.inputs), expected_input))
            # Altering the original capture after review must fail closed.
            with Path(metadata["capture"]).open("ab") as capture:
                capture.write(b"changed")
            with self.assertRaisesRegex(ValueError, "changed after provenance"):
                evaluate_pair(pair, [sessions[1]], 3)

    def test_direction_training_cache_keeps_parent_input_size(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = self.make_cache(root, "train")
            destination = root / "direction-cache"
            resize_training_cache(source, destination, 192)
            self.assertEqual(np.load(destination / "images.npy").shape, (8, 2, 192, 192))
            self.assertEqual(
                int(np.load(destination / "images.npy")[3, 1, 0, 0]),
                int(np.load(source / "images.npy")[3, 1, 0, 0]),
            )
            self.assertEqual(json.loads((destination / "metadata.json").read_text())["imageSize"], 192)
            self.assertTrue(np.array_equal(
                np.load(destination / "targets.npy"), np.load(source / "targets.npy")
            ))

    def test_checkpoint_metadata_has_only_parent_basename_and_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            parent = root / "v8-gate.pt"
            parent.write_bytes(b"trusted parent")
            parent_name, parent_hash = parent_checkpoint_metadata(parent)
            self.assertEqual(parent_name, "v8-gate.pt")
            self.assertEqual(len(parent_hash), 64)
            checkpoint = root / "candidate.pt"
            torch.save({
                "parentCheckpoint": parent_name, "parentCheckpointSha256": parent_hash,
                "architecture": "spatial-stereo-resnet-v2", "imageSize": 224,
                "checkpointEpoch": 3,
            }, checkpoint)
            validate_public_checkpoint_metadata(checkpoint)
            self.assertEqual(validate_refinement_parent(
                checkpoint, parent, parent_hash, "spatial-stereo-resnet-v2", 224,
            ), 3)
            with self.assertRaisesRegex(ValueError, "does not match"):
                validate_refinement_parent(
                    checkpoint, parent, "0" * 64, "spatial-stereo-resnet-v2", 224,
                )
            torch.save({"parentCheckpoint": "C:\\Private\\v8-gate.pt"}, checkpoint)
            with self.assertRaisesRegex(ValueError, "parent reference"):
                validate_public_checkpoint_metadata(checkpoint)
            torch.save({"validation": {"notes": "D:\\private\\capture"}}, checkpoint)
            with self.assertRaisesRegex(ValueError, "private absolute path"):
                validate_public_checkpoint_metadata(checkpoint)

    def test_output_report_cannot_be_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "run"
            fresh_output_dir(output)
            (output / "holdout-report.json").write_text("old report", encoding="utf-8")
            with self.assertRaisesRegex(FileExistsError, "not empty"):
                fresh_output_dir(output)
            self.assertEqual((output / "holdout-report.json").read_text(), "old report")

    def test_external_candidate_cannot_be_staged(self):
        import developer_tongue_training
        with mock.patch.object(sys, "argv", [
            "developer_tongue_training.py", "manifest.json", "--candidate-gate", "gate.pt",
            "--candidate-direction", "direction.pt", "--stage",
        ]):
            with self.assertRaises(SystemExit) as stopped:
                developer_tongue_training.main()
        self.assertEqual(stopped.exception.code, 2)

    def test_external_candidate_processing_cannot_be_overridden(self):
        import developer_tongue_training
        with mock.patch.object(sys, "argv", [
            "developer_tongue_training.py", "manifest.json", "--candidate-gate", "gate.pt",
            "--candidate-direction", "direction.pt", "--input-preprocessing", "clahe-v1",
        ]):
            with self.assertRaises(SystemExit) as stopped:
                developer_tongue_training.main()
        self.assertEqual(stopped.exception.code, 2)

    def test_incomplete_curriculum_blocks_before_model_training(self):
        import developer_tongue_training
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            train = self.make_cache(root, "train")
            holdout = self.make_cache(root, "holdout")
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps({
                "schemaVersion": 1, "purpose": "developer-tongue-model", "sessions": [
                    {"cache": str(train), "role": "train", "sessionId": "t", "wearerId": "a",
                     "consent": {"training": True, "redistributeCheckpoint": True, "manualPoseReview": True}},
                    {"cache": str(holdout), "role": "holdout", "sessionId": "h", "wearerId": "b",
                     "consent": {"evaluation": True, "manualPoseReview": True}},
                ],
            }), encoding="utf-8")
            sessions = load_manifest(manifest)
            self.assertEqual(len(curriculum_issues(sessions)), 4)
            with mock.patch.object(sys, "argv", ["developer_tongue_training.py", str(manifest)]), \
                    mock.patch.object(developer_tongue_training, "_run") as train_call:
                self.assertEqual(developer_tongue_training.main(), 2)
                train_call.assert_not_called()

    def test_current_full_and_focused_coverage_rejects_skipped_or_legacy_cards(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for cards, kind in ((TONGUE_STILL_PROMPTS, "tongue-stereo-stills-v1"),
                                (TONGUE_ARC_PROMPTS, "tongue-stereo-arc-v3")):
                journal = root / f"{kind}.json"
                cache = root / kind
                cache.mkdir()
                steps = np.concatenate([
                    np.repeat(index, card.minimum_captures)
                    for index, card in enumerate(cards)
                ]).astype(np.int16)
                np.save(cache / "step_ids.npy", steps)
                np.save(cache / "trainable.npy", np.ones(len(steps), np.bool_))
                payload = {"prompts": [asdict(card) for card in cards], "skippedPrompts": []}
                journal.write_text(json.dumps(payload), encoding="utf-8")
                metadata = {"sessionType": kind, "session": str(journal)}
                self.assertTrue(_complete_curriculum(cache, metadata))
                payload["skippedPrompts"] = [0]
                journal.write_text(json.dumps(payload), encoding="utf-8")
                self.assertFalse(_complete_curriculum(cache, metadata))
                payload["skippedPrompts"] = []
                payload["prompts"] = payload["prompts"][:10]
                journal.write_text(json.dumps(payload), encoding="utf-8")
                self.assertFalse(_complete_curriculum(cache, metadata))

    def test_promotion_requires_unseen_wearer_and_material_improvement(self):
        corners = {name: {"visible": 6, "directionMae": 0.20}
                   for name in ("upper-left", "upper-right", "lower-left", "lower-right")}
        baseline = {"f1": 0.82, "falsePositiveRate": 0.08, "directionMae": 0.20,
                    "diagonalCorners": corners}
        candidate = {"f1": 0.86, "falsePositiveRate": 0.06, "falseNegativeRate": 0.10,
                     "directionMae": 0.18, "hidden": 48, "visible": 48,
                     "diagonalCorners": corners}
        self.assertEqual(promotion_gate(baseline, candidate, {"a"}, {"b"}), [])
        self.assertTrue(any("wearer" in issue for issue in
                            promotion_gate(baseline, candidate, {"a"}, {"a"})))
        candidate["f1"] = 0.82
        candidate["directionMae"] = 0.19
        self.assertTrue(any("Neither" in issue for issue in
                            promotion_gate(baseline, candidate, {"a"}, {"b"})))

    def test_full_capture_has_fixed_diagonals_and_matched_nuisance_cards(self):
        self.assertEqual(len(TONGUE_STILL_PROMPTS), 58)
        coordinates = {
            (card.targets.get("horizontal"), card.targets.get("vertical"))
            for card in TONGUE_STILL_PROMPTS
            if "horizontal" in card.targets and "vertical" in card.targets
        }
        for x in (-0.5, 0.5):
            for y in (-1.0, -0.5, 0.5, 1.0):
                self.assertIn((x, y), coordinates)
        for x in (-1.0, 1.0):
            for y in (-1.0, -0.5, 0.5, 1.0):
                self.assertIn((x, y), coordinates)
        for context in ("smile + teeth", "jaw half open", "rounded lips", "fit variation"):
            cards = [card for card in TONGUE_STILL_PROMPTS if card.context == context]
            self.assertTrue(any(not card.targets for card in cards), context)
            self.assertTrue(any(card.targets.get("visibility") == 1.0 for card in cards), context)


if __name__ == "__main__":
    unittest.main()
