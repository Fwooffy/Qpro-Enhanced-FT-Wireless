"""Training orchestration safety checks; no headset, downloads or GPU required."""

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import torch

import lower_face_training as lower

def plain():
    return {"architecture": "spatial-stereo-resnet-v2", "imageSize": 224,
            "targetNames": ["visible", "horizontal", "vertical"],
            "inputPreprocessing": "raw-v1", "modelState": {"encoder.weight": torch.tensor([1., 2.])}}

def combined(parent, changed=False):
    return {**parent, "architecture": lower.CHEEK_ARCHITECTURE_PREFIX + parent["architecture"],
            "baseArchitecture": parent["architecture"],
            "targetNames": parent["targetNames"] + list(lower.CHEEK_TARGET_NAMES),
            "modelState": {"parent." + key: value + int(changed) for key, value in parent["modelState"].items()},
            "experimental": True, "cheekTraining": {"frozenTongueParent": True}}

class TrainingSafetyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.stage = self.root / "training" / "lower-face-run-test"
        (self.stage / "models").mkdir(parents=True)
        self.direction = self.stage / "models" / "qpro-stereo-tongue-v7-direction.pt"
        self.gate = self.stage / "models" / "qpro-stereo-tongue-v7-gate.pt"
        torch.save(plain(), self.direction)
        torch.save(plain(), self.gate)

    def tearDown(self):
        self.temporary.cleanup()

    def test_unwrap_plain_and_combined_keeps_original_tensors(self):
        parent = plain()
        self.assertIs(lower.tongue_parent(parent), parent)
        checkpoint = combined(parent)
        restored = lower.tongue_parent(checkpoint)
        self.assertEqual(restored["targetNames"], parent["targetNames"])
        self.assertTrue(torch.equal(restored["modelState"]["encoder.weight"], parent["modelState"]["encoder.weight"]))
        self.assertNotIn("cheekTraining", restored)
        self.assertNotIn("experimental", restored)
        self.assertIn("cheekTraining", checkpoint)

    def test_unwrap_rejects_missing_parent_and_ambiguous_schema(self):
        for broken in ({**combined(plain()), "modelState": {}},
                       {**combined(plain()), "baseArchitecture": "other"},
                       {**combined(plain()), "targetNames": ["visible", "cheekPuffRight", "cheekPuffLeft"]}):
            with self.assertRaises(ValueError):
                lower.tongue_parent(broken)


    def test_publish_plain_pair_keeps_matching_scripts(self):
        script = self.gate.with_suffix(".torchscript.pt")
        script.write_bytes(b"gate script")
        lower.publish_pair(self.stage, self.root, 7)
        self.assertEqual((self.root / "models" / script.name).read_bytes(), b"gate script")

    def test_publish_rejects_new_combined_models(self):
        torch.save(combined(plain()), self.direction)
        with self.assertRaisesRegex(ValueError, "tongue outputs only"):
            lower.publish_pair(self.stage, self.root, 7)
        self.assertFalse((self.root / "models").exists())

    def test_publish_preserves_any_existing_version_artifact(self):
        models = self.root / "models"
        models.mkdir()
        occupied = models / "qpro-stereo-tongue-v7-direction.torchscript.pt"
        occupied.write_bytes(b"keep")
        with self.assertRaisesRegex(ValueError, "already occupied"):
            lower.publish_pair(self.stage, self.root, 7)
        self.assertEqual(occupied.read_bytes(), b"keep")
        self.assertEqual(len(list(models.iterdir())), 1)

    def test_publish_failure_rolls_back_only_our_files(self):
        import os
        real_move = os.rename if os.name == "nt" else os.link
        calls = 0

        def interrupted(source, destination):
            nonlocal calls
            calls += 1
            if calls == 2:
                Path(destination).write_bytes(b"another trainer won")
                raise FileExistsError("race")
            return real_move(source, destination)

        operation = "rename" if os.name == "nt" else "link"
        with patch.object(lower.os, operation, side_effect=interrupted):
            with self.assertRaises(FileExistsError):
                lower.publish_pair(self.stage, self.root, 7)
        models = self.root / "models"
        self.assertEqual([path.name for path in models.iterdir()], [self.direction.name])
        self.assertEqual((models / self.direction.name).read_bytes(), b"another trainer won")


if __name__ == "__main__":
    unittest.main()
