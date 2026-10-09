"""GPU preprocessing math and detection parity; no camera or UDP access."""

import unittest
from unittest import mock

import cv2
import numpy as np

from pupil_dilation import RelativePupilTracker, _cpu_masks
from pupil_gpu import TorchPupilPreprocessor
from gpu_readback import GPUReadbackCancelled
from test_pupil_dilation import eye


class PupilGpuParityTests(unittest.TestCase):
    def test_readback_cancellation_does_not_switch_to_cpu(self) -> None:
        tracker = RelativePupilTracker(backend="cpu")
        strip = np.hstack((eye(24), eye(24)))
        for _ in range(12):
            tracker.update(strip, [0, 1])
        baselines = tuple(value._baseline for value in tracker.eyes)
        tracker._gpu = mock.Mock()
        tracker._gpu.median.side_effect = GPUReadbackCancelled("Tracking stopped")
        tracker.backend, tracker.device = "nvidia-cuda", "cuda:1"
        tracker.device_name = "Discrete GPU"
        tracker.backend_notice = ""
        with (mock.patch("pupil_dilation.cv2.medianBlur") as cpu_median,
              self.assertRaises(GPUReadbackCancelled)):
            tracker.update(strip, [0, 1])
        cpu_median.assert_not_called()
        self.assertEqual(tracker.backend, "nvidia-cuda")
        self.assertEqual(tracker.device, "cuda:1")
        self.assertEqual(tracker.backend_notice, "")
        self.assertIsNotNone(tracker._gpu)
        self.assertEqual(tuple(value._baseline for value in tracker.eyes), baselines)

    def test_readback_timeout_keeps_existing_cpu_fallback_and_baselines(self) -> None:
        tracker = RelativePupilTracker(backend="cpu")
        strip = np.hstack((eye(24), eye(24)))
        for _ in range(12):
            expected = tracker.update(strip, [0, 1])
        baselines = tuple(value._baseline for value in tracker.eyes)
        tracker._gpu = mock.Mock()
        tracker._gpu.median.side_effect = TimeoutError("GPU readback timeout")
        tracker.backend, tracker.device = "nvidia-cuda", "cuda:1"
        self.assertEqual(tracker.update(strip, [0, 1]), expected)
        self.assertIsNone(tracker._gpu)
        self.assertEqual((tracker.backend, tracker.device), ("cpu", "cpu"))
        self.assertEqual(tuple(value._baseline for value in tracker.eyes), baselines)
        self.assertIn("GPU readback timeout", tracker.backend_notice)

    def test_median_and_elliptical_morphology_match_opencv(self) -> None:
        try:
            import torch
        except ImportError:
            self.skipTest("Torch is not installed in this test environment")
        # CPU tensor execution also checks these algorithms on test machines
        # without GPU hardware. Only production construction selects a GPU.
        processor = object.__new__(TorchPupilPreprocessor)
        processor.torch, processor.device = torch, "cpu"
        processor._kernel_rows = {
            size: tuple(int(row.sum()) for row in cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (size, size)))
            for size in (3, 7)
        }
        rois = [np.random.default_rng(seed).integers(0, 256, (29, 37), dtype=np.uint8) for seed in (9, 11)]
        rois[0][:8, :8] = 0
        rois[1][-8:, -8:] = 255
        clean, gpu_clean = processor.median(rois)
        np.testing.assert_array_equal(clean, np.stack([cv2.medianBlur(roi, 7) for roi in rois]))
        thresholds = [[0, 64, 128, 200, 275], [125, 130, 140]]
        gpu_masks = processor.masks(gpu_clean, thresholds)
        for index, cutoffs in enumerate(thresholds):
            for actual, expected in zip(gpu_masks[index], _cpu_masks(clean[index], cutoffs)):
                np.testing.assert_array_equal(actual, expected)

    def test_available_discrete_gpu_matches_cpu_detector(self) -> None:
        gpu = RelativePupilTracker()
        if gpu.backend == "cpu":
            self.skipTest("A validated discrete CUDA/ROCm GPU is unavailable")
        cpu = RelativePupilTracker(backend="cpu")
        for size, center in ((18, (110, 130)), (25, (245, 180)), (35, (340, 300))):
            strip = np.hstack((eye(size, center), eye(24)))
            for _ in range(12):
                cpu_values = cpu.update(strip, [0, 1])
                gpu_values = gpu.update(strip, [0, 1])
                self.assertEqual(gpu.detections, cpu.detections)
                self.assertEqual(gpu_values, cpu_values)
        self.assertNotEqual(gpu.backend, "cpu", gpu.backend_notice)


if __name__ == "__main__":
    unittest.main()
