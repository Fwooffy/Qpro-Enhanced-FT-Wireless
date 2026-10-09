"""Fresh native evidence must not outlive its streaming source."""

import unittest
from unittest import mock

import numpy as np
import torch
import test_label_capture

from tongue_model_preview import (
    LiveTongueModelPreview, TonguePrediction, _native_tongue_reference,
    format_tongue_status, tongue_output_state,
)


class NativeTongueReferenceTests(unittest.TestCase):
    now = 2_000_000_000
    names = ["JawDrop", "TongueOut"]

    def sample(self, age_ms=0, value=0.75, flags=1):
        return {"arrivalMonotonicNs": self.now - int(age_ms * 1_000_000),
                "values": [0.3, value], "faceFlags": flags}

    def test_recent_valid_sample_includes_unchanged_held_pose(self):
        for age in (0, 20, 299.9, 300):
            sample = self.sample(age)
            sample["sourceUnchangedMs"] = 5_000
            self.assertEqual(_native_tongue_reference(sample, self.names, self.now),
                             (0.75, "available"))

    def test_old_or_future_packet_is_not_factory_evidence(self):
        for age in (300.001, 500, 60_000, -1):
            self.assertEqual(_native_tongue_reference(self.sample(age), self.names, self.now),
                             (0, "stale"))

    def test_missing_schema_or_sample_is_unavailable(self):
        self.assertEqual(_native_tongue_reference(None, self.names, self.now),
                         (0, "unavailable"))
        self.assertEqual(_native_tongue_reference(self.sample(), ["JawDrop"], self.now),
                         (0, "unavailable"))

    def test_invalid_lower_face_and_values_are_rejected(self):
        samples = [self.sample(flags=0), self.sample(flags=2), self.sample(flags=True),
                   self.sample(value=float("nan")), self.sample(value=float("inf")),
                   self.sample(value=-0.1), self.sample(value=1.1), {},
                   {**self.sample(), "values": [0.1]},
                   {**self.sample(), "arrivalMonotonicNs": "2000000000"}]
        for sample in samples:
            with self.subTest(sample=sample):
                self.assertEqual(_native_tongue_reference(sample, self.names, self.now),
                                 (0, "invalid"))

    def test_both_virtual_desktop_tongue_layouts_have_valid_lower_face_flag(self):
        names = test_label_capture.NativeTongueLayoutTests.names
        for flags in (1, 3):
            values = [0.0] * 70
            values[63], values[68] = 0.25, 0.75
            sample = {**self.sample(flags=flags), "values": values,
                      "trackingSource": "VirtualDesktop"}
            self.assertEqual(_native_tongue_reference(sample, names, self.now),
                             (0.75 if flags == 1 else 0.25, "available"))

    def test_alternate_layout_cannot_be_applied_to_partial_schema(self):
        self.assertEqual(_native_tongue_reference({**self.sample(flags=3),
                         "trackingSource": "VirtualDesktop"}, self.names, self.now),
                         (0, "invalid"))

    def test_historical_steam_flag2_does_not_select_alternate_vd_slot(self):
        names = test_label_capture.NativeTongueLayoutTests.names
        values = [0.0] * 70
        values[63], values[68] = 0.25, 0.75
        sample = {**self.sample(flags=3), "values": values}
        self.assertEqual(_native_tongue_reference(sample, names, self.now),
                         (0, "source-unknown"))
        sample["trackingSource"] = "SteamLink"
        self.assertEqual(_native_tongue_reference(sample, names, self.now),
                         (0.75, "available"))

    def test_visibility_modes_do_not_reuse_stale_or_invalid_native_value(self):
        class Model(torch.nn.Module):
            def forward(self, inputs):
                return torch.tensor([[0.5, 0.7]], device=inputs.device)

        checkpoint = {"targetNames": ["visibility", "extension"], "imageSize": 32,
                      "modelState": {}, "visibilityGate": {"cameraWeight": 0.5, "threshold": 0.6}}
        for mode in ("weighted", "native", "agreement", "camera"):
            for sample in (self.sample(age_ms=500, value=1), self.sample(value=1, flags=0)):
                with (
                    self.subTest(mode=mode, sample=sample),
                    mock.patch("tongue_model_preview.torch.load", return_value=checkpoint),
                    mock.patch("tongue_model_preview.create_model", return_value=Model()),
                    mock.patch("tongue_model_preview.validated_torch_device_name", return_value="cpu"),
                    mock.patch("tongue_model_preview.time.monotonic_ns", return_value=self.now),
                ):
                    preview = LiveTongueModelPreview("gate.pt", visibility_mode=mode, smoothing=1)
                    prediction = preview.predict(np.zeros((400, 800), np.uint8), sample, self.names)
                    expected = {"weighted": 0.25, "native": 0, "agreement": 0, "camera": 0.5}[mode]
                    self.assertAlmostEqual(prediction.fused_visibility, expected)
                    self.assertEqual(prediction.native_tongue_out, 0)
                    self.assertFalse(prediction.visible)
                    self.assertEqual(preview.threshold, 0.6)
                    self.assertEqual(preview.camera_weight, 0.5)

    def test_fresh_native_sample_preserves_calibrated_weighted_formula(self):
        sample = self.sample(value=0.8)
        native, status = _native_tongue_reference(sample, self.names, self.now)
        self.assertEqual(status, "available")
        self.assertAlmostEqual(0.95 * 0.9 + 0.05 * native, 0.895)


class TongueOutputStateTests(unittest.TestCase):
    def prediction(self, *, visible=True, pipeline=20):
        return TonguePrediction(np.asarray([0.8], np.float32), 0, 0.9,
                                visible, 4, pipeline_ms=pipeline)

    def test_toggle_and_effective_output_are_distinct(self):
        self.assertEqual(tongue_output_state(self.prediction(), False), "off")
        self.assertEqual(tongue_output_state(self.prediction(), True), "visible")
        self.assertEqual(tongue_output_state(self.prediction(visible=False), True), "hidden")
        self.assertEqual(tongue_output_state(self.prediction(pipeline=300), True), "visible")
        for pipeline in (300.001, 500, -1, float("nan"), float("inf")):
            with self.subTest(pipeline=pipeline):
                self.assertEqual(tongue_output_state(self.prediction(pipeline=pipeline), True), "stale")
                self.assertEqual(tongue_output_state(self.prediction(pipeline=pipeline), False), "off")

    def test_periodic_log_reports_slow_and_hidden_results_with_toggle_still_on(self):
        for prediction, state in ((self.prediction(pipeline=500), "stale"),
                                  (self.prediction(visible=False), "hidden")):
            with self.subTest(state=state):
                status = format_tongue_status(prediction, camera_fps=24,
                                              inference_fps=12, enabled=True)
                self.assertIn("requested_output=on", status)
                self.assertIn("output_state=" + state, status)
                self.assertIn("native_status=unavailable", status)
                self.assertIn("pipeline_ms=" + f"{prediction.pipeline_ms:.1f}", status)


if __name__ == "__main__":
    unittest.main()
