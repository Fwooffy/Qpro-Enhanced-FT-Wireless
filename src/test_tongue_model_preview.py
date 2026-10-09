import unittest
from unittest import mock

import cv2
import numpy as np
import torch

from tongue_image_processing import preprocess_stereo_images
from tongue_model_preview import LiveTongueModelPreview
from tongue_calibration import TONGUE_TARGET_NAMES


class _RecordingModel(torch.nn.Module):
    def __init__(self):
        super().__init__()
        self.inputs = []

    def forward(self, inputs):
        self.inputs.append(inputs)
        return torch.tensor([[0.9, 0.4, 0.2]], device=inputs.device)


class TongueModelPreviewInputTests(unittest.TestCase):
    def setUp(self):
        # Distinct low-contrast panels catch a swapped view or processing the
        # 800-pixel strip before each branch's resize.
        rows, columns = np.indices((400, 400))
        left = (70 + (rows // 7 + columns // 11) % 35).astype(np.uint8)
        right = (120 + (rows // 13 + columns // 5) % 20).astype(np.uint8)
        self.strip = np.concatenate((left, right), axis=1)

    def checkpoint(self, size=64, mode=None):
        result = {
            "targetNames": ["visibility", "extension", "horizontal"],
            "imageSize": size,
            "modelState": {},
        }
        if mode is not None:
            result["inputPreprocessing"] = mode
        return result

    def load_preview(self, gate, direction=None):
        checkpoints = [gate] if direction is None else [gate, direction]
        models = [_RecordingModel() for _ in checkpoints]
        with (
            mock.patch("tongue_model_preview.torch.load", side_effect=checkpoints),
            mock.patch("tongue_model_preview.create_model", side_effect=models),
            mock.patch("tongue_model_preview.validated_torch_device_name", return_value="cpu"),
        ):
            preview = LiveTongueModelPreview(
                "gate.pt", device_name="cpu",
                direction_checkpoint_path="direction.pt" if direction is not None else None,
            )
        return preview, models

    def expected_inputs(self, size, mode):
        resized = np.stack([
            cv2.resize(
                self.strip[:, view * 400:(view + 1) * 400], (size, size),
                interpolation=cv2.INTER_AREA,
            )
            for view in range(2)
        ])
        processed = preprocess_stereo_images(resized, mode)
        return processed.astype(np.float32)[None] / 255.0

    def test_legacy_checkpoint_matches_original_raw_pipeline_exactly(self):
        preview, models = self.load_preview(self.checkpoint())
        preview.predict(self.strip, None, [])
        original = np.empty((1, 2, 64, 64), dtype=np.float32)
        for view in range(2):
            original[0, view] = cv2.resize(
                self.strip[:, view * 400:(view + 1) * 400], (64, 64),
                interpolation=cv2.INTER_AREA,
            ).astype(np.float32) / 255.0
        self.assertEqual(preview.input_preprocessing, "raw-v1")
        np.testing.assert_array_equal(models[0].inputs[0].numpy(), original)

    def test_each_branch_uses_its_own_preprocessing_contract(self):
        for gate_mode, direction_mode in (("raw-v1", "clahe-v1"), ("clahe-v1", "raw-v1")):
            with self.subTest(gate=gate_mode, direction=direction_mode):
                preview, models = self.load_preview(
                    self.checkpoint(mode=gate_mode), self.checkpoint(mode=direction_mode)
                )
                preview.predict(self.strip, None, [])
                np.testing.assert_array_equal(
                    models[0].inputs[0].numpy(), self.expected_inputs(64, gate_mode)
                )
                np.testing.assert_array_equal(
                    models[1].inputs[0].numpy(), self.expected_inputs(64, direction_mode)
                )
                self.assertIsNot(models[0].inputs[0], models[1].inputs[0])

    def test_matching_branch_contract_reuses_inputs(self):
        preview, models = self.load_preview(
            self.checkpoint(mode="clahe-v1"), self.checkpoint(mode="clahe-v1")
        )
        preview.predict(self.strip, None, [])
        self.assertIs(models[0].inputs[0], models[1].inputs[0])

    def test_mixed_precision_direction_head_cannot_truncate_gate_visibility(self):
        preview, _models = self.load_preview(self.checkpoint(), self.checkpoint())
        gate = torch.tensor([[0.9123456, 0.1, 0.2]], dtype=torch.float32)
        direction = torch.tensor([[0.1, 0.41, -0.22]], dtype=torch.float16)
        preview.model.forward = mock.Mock(return_value=gate)
        preview.direction_model.forward = mock.Mock(return_value=direction)
        result = preview.predict(self.strip, None, [])
        expected = direction[0].float().numpy().copy()
        expected[0] = gate[0, 0].item()
        np.testing.assert_array_equal(result.values, expected)
        self.assertEqual(result.values[0], gate[0, 0].item())
        self.assertNotEqual(result.values[0], gate[0, 0].half().item())
        self.assertEqual(direction[0, 0].item(), torch.tensor(0.1, dtype=torch.float16).item())

    def test_different_branch_sizes_resize_before_processing(self):
        preview, models = self.load_preview(
            self.checkpoint(size=64, mode="clahe-v1"),
            self.checkpoint(size=48, mode="clahe-v1"),
        )
        preview.predict(self.strip, None, [])
        self.assertIsNot(models[0].inputs[0], models[1].inputs[0])
        for model, size in zip(models, (64, 48)):
            np.testing.assert_array_equal(
                model.inputs[0].numpy(), self.expected_inputs(size, "clahe-v1")
            )

    def test_legacy_direction_does_not_inherit_contrast_gate_tag(self):
        preview, models = self.load_preview(
            self.checkpoint(mode="clahe-v1"), self.checkpoint()
        )
        self.assertEqual(preview.direction_input_preprocessing, "raw-v1")
        preview.predict(self.strip, None, [])
        np.testing.assert_array_equal(
            models[1].inputs[0].numpy(), self.expected_inputs(64, "raw-v1")
        )

    def test_unknown_tag_fails_for_either_branch(self):
        for gate, direction in (
            (self.checkpoint(mode="unknown-v1"), None),
            (self.checkpoint(), self.checkpoint(mode="unknown-v1")),
        ):
            with self.subTest(direction=direction is not None):
                with self.assertRaises(ValueError):
                    self.load_preview(gate, direction)


class _SequenceModel(torch.nn.Module):
    def __init__(self, rows):
        super().__init__()
        self.rows = iter(rows)

    def forward(self, inputs):
        return torch.tensor([next(self.rows)], device=inputs.device)


class CombinedTongueCheekPreviewTests(unittest.TestCase):
    tongue_names = list(TONGUE_TARGET_NAMES)
    combined_names = tongue_names + ["cheekPuffLeft", "cheekPuffRight"]

    def load_preview(self, gate_rows, direction_rows, direction_names=None):
        names = self.combined_names if direction_names is None else direction_names
        checkpoints = [
            {"targetNames": self.tongue_names, "imageSize": 32, "modelState": {}},
            {"targetNames": names, "imageSize": 32, "modelState": {}},
        ]
        models = [_SequenceModel(gate_rows), _SequenceModel(direction_rows)]
        with (
            mock.patch("tongue_model_preview.torch.load", side_effect=checkpoints),
            mock.patch("tongue_model_preview.create_model", side_effect=models),
            mock.patch("tongue_model_preview.validated_torch_device_name", return_value="cpu"),
        ):
            return LiveTongueModelPreview(
                "gate.pt", device_name="cpu", direction_checkpoint_path="direction.pt",
                smoothing=1.0, visibility_mode="camera",
            )

    def row(self, visibility=0.9, extension=0.7, horizontal=0.6, cheeks=None):
        row = np.zeros(len(self.tongue_names), dtype=np.float32)
        for name, value in (("visibility", visibility), ("extension", extension),
                            ("horizontal", horizontal)):
            row[self.tongue_names.index(name)] = value
        return row.tolist() + ([] if cheeks is None else list(cheeks))

    def test_ten_head_visibility_gate_and_twelve_head_direction_route_separately(self):
        preview = self.load_preview(
            [self.row(visibility=0.9, extension=0.1)],
            [self.row(visibility=0.0, extension=0.7, cheeks=(0.25, 0.75))],
        )
        prediction = preview.predict(np.zeros((400, 800), np.uint8), None, [])
        self.assertEqual(preview.target_names, self.combined_names)
        self.assertEqual(prediction.values.shape, (12,))
        self.assertTrue(prediction.visible)
        self.assertAlmostEqual(prediction.values[self.tongue_names.index("visibility")], 0.9)
        self.assertAlmostEqual(prediction.values[self.tongue_names.index("extension")], 0.7)
        np.testing.assert_allclose(prediction.values[-2:], [0.25, 0.75])

    def test_mismatched_or_reordered_combined_schema_is_rejected(self):
        for names in (
            self.combined_names[:-1],
            self.tongue_names + ["cheekPuffRight", "cheekPuffLeft"],
            self.combined_names[1:] + self.combined_names[:1],
        ):
            with self.subTest(names=names), self.assertRaisesRegex(ValueError, "different target schemas"):
                self.load_preview([], [], names)

    def test_tongue_visibility_hold_does_not_hold_cheek_strengths(self):
        preview = self.load_preview(
            [self.row(visibility=0.9), self.row(visibility=0), self.row(visibility=0)],
            [self.row(cheeks=(0.25, 0.75)),
             self.row(extension=0.0, horizontal=-0.6, cheeks=(0.8, 0.1)),
             self.row(extension=0.0, horizontal=-0.6, cheeks=(0.2, 0.9))],
        )
        strip = np.zeros((400, 800), np.uint8)
        with mock.patch("tongue_model_preview.time.perf_counter", return_value=1.0):
            first = preview.predict(strip, None, [])
        with mock.patch("tongue_model_preview.time.perf_counter", return_value=1.1):
            held = preview.predict(strip, None, [])
        self.assertTrue(held.visible)
        np.testing.assert_array_equal(held.values[:10], first.values[:10])
        np.testing.assert_allclose(held.values[-2:], [0.8, 0.1])
        with mock.patch("tongue_model_preview.time.perf_counter", return_value=1.4):
            hidden = preview.predict(strip, None, [])
        self.assertFalse(hidden.visible)
        np.testing.assert_allclose(hidden.values[-2:], [0.2, 0.9])

    def test_combined_preview_keeps_both_cheek_bars_and_footer_visible(self):
        preview = self.load_preview(
            [self.row()], [self.row(cheeks=(0.25, 0.75))],
        )
        prediction = preview.predict(np.zeros((400, 800), np.uint8), None, [])
        with mock.patch("tongue_model_preview.cv2.putText", wraps=cv2.putText) as put_text:
            image = preview.render(prediction)
        labels = {call.args[1]: call for call in put_text.call_args_list}
        for name in ("cheekPuffLeft", "cheekPuffRight"):
            call = labels[name]
            x, baseline = call.args[2]
            self.assertLess(baseline, image.shape[0])
            self.assertTrue(np.any(image[baseline - 20:baseline + 1, x:x + 200]))
        last_baseline = labels["cheekPuffRight"].args[2][1]
        footer = next(call for text, call in labels.items() if text.startswith("T toggles"))
        (width, text_height), descent = cv2.getTextSize(
            footer.args[1], footer.args[3], footer.args[4], footer.args[6],
        )
        footer_x, footer_baseline = footer.args[2]
        # There must be real image space between the last expression's bar
        # and the footer, rather than drawing the footer over the cheek row.
        footer_top = footer_baseline - text_height
        self.assertGreater(footer_top, last_baseline + 16)
        self.assertLess(footer_baseline + descent, image.shape[0])
        self.assertTrue(np.any(image[footer_top:footer_baseline + descent,
                                     footer_x:footer_x + width]))


if __name__ == "__main__":
    unittest.main()
