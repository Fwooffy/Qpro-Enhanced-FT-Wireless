import builtins
import io
import tempfile
import unittest
import warnings
from collections import OrderedDict
from pathlib import Path
from unittest import mock

import torch

import developer_tongue_training
import model_preview
import train_tongue_model
import tongue_model_preview
import test_train_tongue_model
from model_checkpoint import ModelCheckpointError, load_model_checkpoint
from tongue_calibration import TONGUE_TARGET_NAMES


class _ExecutableMetadata:
    def __init__(self, marker: Path) -> None:
        self.marker = marker

    def __reduce__(self):
        # Saving records this instruction without executing it. A restricted
        # load must reject it before the marker can be written.
        return builtins.eval, (
            f"__import__('pathlib').Path({str(self.marker)!r}).write_text('executed')",
        )


class ModelCheckpointTests(unittest.TestCase):
    def test_plain_metadata_and_tensor_state_are_preserved(self):
        checkpoint = {
            "modelState": OrderedDict(weight=torch.arange(6).reshape(2, 3)),
            "targetNames": list(TONGUE_TARGET_NAMES),
            "imageSize": 224,
            "inputPreprocessing": "raw-v1",
            "parentCheckpoint": "gate.pt",
            "parentCheckpointSha256": "ab" * 32,
            "validation": {"perPrompt": [{"activeMae": .05, "samples": 12}],
                           "unused": None},
            "visibilityGate": {"cameraWeight": 1.0, "threshold": .5},
        }
        stream = io.BytesIO()
        torch.save(checkpoint, stream)
        stream.seek(0)
        loaded = load_model_checkpoint(stream)
        self.assertIsInstance(loaded["modelState"], OrderedDict)
        self.assertTrue(torch.equal(loaded["modelState"]["weight"],
                                    checkpoint["modelState"]["weight"]))
        for key in checkpoint.keys() - {"modelState"}:
            self.assertEqual(loaded[key], checkpoint[key])

    def test_executable_metadata_is_rejected_without_unsafe_retry(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            marker = root / "executed.txt"
            checkpoint_path = root / "untrusted.pt"
            torch.save({"modelState": {}, "metadata": _ExecutableMetadata(marker)},
                       checkpoint_path)
            with mock.patch("model_checkpoint.torch.load", wraps=torch.load) as read:
                with self.assertRaisesRegex(ModelCheckpointError, "loaded safely"):
                    load_model_checkpoint(checkpoint_path)
            self.assertFalse(marker.exists())
            self.assertEqual(read.call_count, 1)
            self.assertTrue(read.call_args.kwargs["weights_only"])
            self.assertEqual(read.call_args.kwargs["map_location"], "cpu")

    def test_torchscript_program_is_rejected_without_jit_loading(self):
        with tempfile.TemporaryDirectory() as directory:
            archive = Path(directory) / "program.pt"
            module = torch.jit.trace(torch.nn.Linear(2, 1), torch.zeros(1, 2))
            module.save(str(archive))
            with (mock.patch("model_checkpoint.torch.jit.load") as jit_load,
                  warnings.catch_warnings(record=True) as notices):
                with self.assertRaisesRegex(ModelCheckpointError, "TorchScript program"):
                    load_model_checkpoint(archive)
            jit_load.assert_not_called()
            self.assertFalse(any("dispatching" in str(item.message) for item in notices))

    def test_truncated_checkpoints_report_damaged_data(self):
        stream = io.BytesIO()
        torch.save({"modelState": {}}, stream)
        for contents in (b"", stream.getvalue()[:100]):
            with self.subTest(length=len(contents)):
                with self.assertRaisesRegex(ModelCheckpointError, "incomplete or damaged"):
                    load_model_checkpoint(io.BytesIO(contents))

    def test_checkpoint_root_must_be_a_metadata_dictionary(self):
        stream = io.BytesIO()
        torch.save(torch.ones(2), stream)
        stream.seek(0)
        with self.assertRaisesRegex(ModelCheckpointError, "dictionary"):
            load_model_checkpoint(stream)

    def test_missing_checkpoint_keeps_the_file_error(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(FileNotFoundError):
                load_model_checkpoint(Path(directory) / "missing.pt")

    def test_preview_and_developer_entrypoints_reject_executable_metadata(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            marker = root / "executed.txt"
            untrusted = root / "untrusted.pt"
            good = root / "plain.pt"
            torch.save({"modelState": {}, "metadata": _ExecutableMetadata(marker)},
                       untrusted)
            torch.save({
                "modelState": {}, "targetNames": list(TONGUE_TARGET_NAMES),
                "expressionNames": ["JawOpen"], "imageSize": 128,
                "architecture": "legacy-late-fusion-v1",
            }, good)
            actions = {
                "tongue gate": lambda: tongue_model_preview.LiveTongueModelPreview(
                    untrusted, device_name="cpu"),
                "tongue direction": lambda: tongue_model_preview.LiveTongueModelPreview(
                    good, device_name="cpu", direction_checkpoint_path=untrusted),
                "five-camera preview": lambda: model_preview.LiveModelPreview(
                    untrusted, device_name="cpu"),
                "developer gate": lambda: developer_tongue_training.ModelPair(
                    untrusted, good, torch.device("cpu")),
                "developer direction": lambda: developer_tongue_training.ModelPair(
                    good, untrusted, torch.device("cpu")),
                "public metadata": lambda:
                    developer_tongue_training.validate_public_checkpoint_metadata(untrusted),
                "parent metadata": lambda: developer_tongue_training.validate_refinement_parent(
                    untrusted, good, "ab" * 32, "legacy-late-fusion-v1", 128),
            }
            with (
                mock.patch.object(tongue_model_preview, "create_model",
                                  side_effect=lambda *args: torch.nn.Identity()),
                mock.patch.object(tongue_model_preview, "_prepare_inference_model",
                                  side_effect=lambda model, device: model),
                mock.patch.object(developer_tongue_training, "create_model",
                                  side_effect=lambda *args: torch.nn.Identity()),
            ):
                for name, action in actions.items():
                    with self.subTest(entrypoint=name):
                        with self.assertRaises(ModelCheckpointError):
                            action()
                        self.assertFalse(marker.exists())

    def test_refinement_rejects_executable_parent_before_training_or_output(self):
        with tempfile.TemporaryDirectory() as directory:
            cache = Path(directory)
            test_train_tongue_model.TongueTrainingTests.make_preprocessing_cache(cache)
            marker = cache / "executed.txt"
            parent = cache / "untrusted.pt"
            output = cache / "candidate.pt"
            torch.save({"modelState": {}, "metadata": _ExecutableMetadata(marker)}, parent)
            with (
                mock.patch("sys.argv", ["train", str(cache), "--initial-checkpoint",
                                        str(parent), "--output", str(output)]),
                mock.patch.object(train_tongue_model, "create_model") as create,
                mock.patch.object(train_tongue_model, "run_training_epoch") as train,
            ):
                with self.assertRaises(ModelCheckpointError):
                    train_tongue_model.main()
            create.assert_not_called()
            train.assert_not_called()
            self.assertFalse(marker.exists())
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
