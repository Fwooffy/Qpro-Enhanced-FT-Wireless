"""Synthetic checks for the opt-in pupil path; live camera QA is still needed."""

import struct
import unittest
from unittest.mock import patch

import cv2
import numpy as np

from pupil_dilation import (
    PupilDetection,
    RelativePupilEye,
    RelativePupilTracker,
    detect_pupil,
    encode_pupil_packet,
    _thresholds,
)
from receiver import stereo_mouth_strip


def eye(radius: int, center: tuple[int, int] = (200, 200)) -> np.ndarray:
    image = np.full((400, 400), 155, dtype=np.uint8)
    cv2.circle(image, center, 95, 105, -1)
    cv2.circle(image, center, radius, 18, -1)
    cv2.circle(image, (center[0] - 6, center[1] - 6), 3, 245, -1)
    return image


class PupilDilationTests(unittest.TestCase):
    def test_histogram_thresholds_keep_linear_percentile_cutoffs(self) -> None:
        def reference(clean):
            low = int(np.min(clean))
            return sorted(set([low + 10, low + 20] + [int(value) for value in np.percentile(clean, (5, 10, 15, 20, 25, 30))]))

        for size in (1, 2, 3, 19, 20, 21, 97, 93800):
            for seed in range(12):
                frame = np.random.default_rng(seed).integers(0, 256, (1, size), dtype=np.uint8)
                with self.subTest(size=size, seed=seed):
                    self.assertEqual(_thresholds(frame), reference(frame))
        for value in (0, 18, 250, 255):
            frame = np.full((280, 335), value, dtype=np.uint8)
            self.assertEqual(_thresholds(frame), reference(frame))
        # Strided camera/ROI views must retain the same intensity counts.
        frame = eye(24)[75:355, 55:390]
        self.assertEqual(_thresholds(frame), reference(frame))

    def test_dark_pupil_changes_size_and_position(self) -> None:
        for radius, center in ((24, (200, 200)), (35, (245, 175))):
            detection = detect_pupil(eye(radius, center))
            self.assertIsNotNone(detection)
            self.assertAlmostEqual(detection.diameter_px, 2 * radius, delta=10)
            self.assertAlmostEqual(detection.center[0], center[0], delta=6)
            self.assertAlmostEqual(detection.center[1], center[1], delta=6)

    def test_invalid_eye_frames_do_not_produce_dilation(self) -> None:
        self.assertIsNone(detect_pupil(np.zeros((400, 400), np.uint8)))
        self.assertIsNone(detect_pupil(np.full((400, 400), 140, np.uint8)))

    def test_reflections_inside_off_center_pupil(self) -> None:
        center = (300, 220)
        image = eye(30, center)
        for offset in ((-9, -7), (8, 3), (4, 12)):
            cv2.circle(image, (center[0] + offset[0], center[1] + offset[1]), 4, 255, -1)
        detection = detect_pupil(image)
        self.assertIsNotNone(detection)
        self.assertAlmostEqual(detection.center[0], center[0], delta=6)
        self.assertAlmostEqual(detection.center[1], center[1], delta=6)
        self.assertAlmostEqual(detection.diameter_px, 60, delta=10)

    def test_baseline_waits_for_steady_gaze_and_rejects_large_shift(self) -> None:
        tracker = RelativePupilEye()
        def sample(center: tuple[float, float], diameter: float = 38.0) -> PupilDetection:
            return PupilDetection(center, diameter, 0.9)

        for index in range(12):
            self.assertIsNone(tracker.update(sample((200.0 + index % 2 * 60, 200.0))))
        for _ in range(11):
            self.assertIsNone(tracker.update(sample((200.0, 200.0))))
        self.assertAlmostEqual(tracker.update(sample((200.0, 200.0))), 5.0, delta=0.01)
        self.assertAlmostEqual(tracker.update(sample((260.0, 200.0), 90.0)), 5.0, delta=0.01)
        self.assertIn("gaze outside calibrated area", tracker.status)
        self.assertAlmostEqual(tracker.update(sample((200.0, 200.0))), 5.0, delta=0.01)

    def test_relative_dilation_is_independent_per_eye(self) -> None:
        tracker = RelativePupilTracker(backend="cpu")
        baseline = np.hstack((eye(24), eye(24)))
        for _ in range(12):
            left, right = tracker.update(baseline, [0, 1])
        self.assertAlmostEqual(left, 5.0, delta=0.3)
        self.assertAlmostEqual(right, 5.0, delta=0.3)
        grown = np.hstack((eye(35), eye(24)))
        for _ in range(8):
            left, right = tracker.update(grown, [0, 1])
        self.assertGreater(left, 5.5)
        self.assertAlmostEqual(right, 5.0, delta=0.3)
        held, _right = tracker.update(np.hstack((np.zeros((400, 400), np.uint8), eye(24))), [0, 1])
        self.assertAlmostEqual(held, left, delta=0.01)
        self.assertIn("holding last estimate", tracker.eyes[0].status)

    def test_response_gain_makes_both_directions_more_visible(self) -> None:
        normal = RelativePupilEye(sensitivity=1.0)
        balanced = RelativePupilEye(sensitivity=1.4)
        def sample(diameter: float) -> PupilDetection:
            return PupilDetection((200.0, 200.0), diameter, 0.9)
        for _ in range(12):
            self.assertEqual(normal.update(sample(40.0)), balanced.update(sample(40.0)))
        for _ in range(10):
            normal_larger = normal.update(sample(44.0))
            balanced_larger = balanced.update(sample(44.0))
        self.assertGreater(balanced_larger, normal_larger)
        for _ in range(20):
            normal_smaller = normal.update(sample(36.0))
            balanced_smaller = balanced.update(sample(36.0))
        self.assertLess(balanced_smaller, normal_smaller)

    def test_high_response_eases_change_and_ignores_one_bad_size(self) -> None:
        tracker = RelativePupilEye(sensitivity=3.0)
        def sample(diameter: float) -> PupilDetection:
            return PupilDetection((200.0, 200.0), diameter, 0.9)
        for _ in range(12):
            value = tracker.update(sample(40.0))
        self.assertAlmostEqual(value, 5.0, delta=0.01)
        self.assertAlmostEqual(tracker.update(sample(60.0)), 5.0, delta=0.01)
        self.assertAlmostEqual(tracker.update(sample(40.0)), 5.0, delta=0.01)
        previous = value
        for _ in range(12):
            value = tracker.update(sample(44.0))
            self.assertLessEqual(abs(value - previous), 0.28)
            previous = value
        self.assertGreater(value, 6.1)
        self.assertLessEqual(value, 8.5)
        with self.assertRaises(ValueError):
            RelativePupilEye(sensitivity=3.1)

    def test_high_response_rejects_still_gaze_jitter_and_short_occlusion(self) -> None:
        tracker = RelativePupilEye(sensitivity=3.0)
        def sample(diameter: float) -> PupilDetection:
            return PupilDetection((200.0, 200.0), diameter, 0.9)
        for _ in range(12):
            tracker.update(sample(40.0))
        values = [tracker.update(sample(float(size))) for size in (39, 41, 40) * 12]
        self.assertLess(max(values) - min(values), 0.3)
        for _ in range(20):
            enlarged = tracker.update(sample(52.0))
        self.assertGreater(enlarged, 7.0)
        held = tracker.update(None)
        self.assertAlmostEqual(held, enlarged, delta=0.01)
        self.assertIn("holding last estimate", tracker.status)
        for _ in range(35):
            eased = tracker.update(None)
        self.assertTrue(eased is None or abs(eased - 5.0) < 0.1)

    def test_high_response_keeps_range_near_maximum(self) -> None:
        tracker = RelativePupilEye(sensitivity=3.0)
        def sample(diameter: float) -> PupilDetection:
            return PupilDetection((200.0, 200.0), diameter, 0.9)
        for _ in range(12):
            tracker.update(sample(40.0))
        for _ in range(20):
            first = tracker.update(sample(52.0))
        for _ in range(20):
            second = tracker.update(sample(56.0))
        self.assertGreater(first, 7.5)
        self.assertGreater(second, first + 0.10)
        self.assertLess(second, 8.0)

    def test_warmup_rejects_pupil_size_trend(self) -> None:
        tracker = RelativePupilEye()
        def sample(diameter: float) -> PupilDetection:
            return PupilDetection((200.0, 200.0), diameter, 0.9)
        for diameter in (40, 40, 41, 42, 43, 44, 45, 46, 47, 48, 48, 48):
            result = tracker.update(sample(float(diameter)))
        self.assertIsNone(result)
        self.assertEqual(tracker.status, "warming: hold gaze steady")
        for _ in range(12):
            result = tracker.update(sample(48.0))
        self.assertAlmostEqual(result, 5.0, delta=0.1)

    def test_packet_flags_and_length(self) -> None:
        packet = encode_pupil_packet((5.2, None))
        self.assertEqual(len(packet), 16)
        magic, version, flags, reserved, left, right = struct.unpack("<4sBBHff", packet)
        self.assertEqual((magic, version, flags, reserved), (b"QPDI", 1, 1, 0))
        self.assertAlmostEqual(left, 5.2, places=5)
        self.assertEqual(right, 0.0)

    def test_combined_eye_and_face_stream_routes_mouth_cameras(self) -> None:
        strip = np.hstack((
            eye(24), eye(24),
            np.full((400, 400), 42, np.uint8),
            np.full((400, 400), 83, np.uint8),
            np.full((400, 400), 120, np.uint8),
        ))
        tracker = RelativePupilTracker(backend="cpu")
        for _ in range(12):
            left, right = tracker.update(strip, [0, 1, 2, 3, 4])
        self.assertAlmostEqual(left, 5.0, delta=0.3)
        self.assertAlmostEqual(right, 5.0, delta=0.3)
        mouth = stereo_mouth_strip(strip, [0, 1, 2, 3, 4])
        self.assertEqual(mouth.shape, (400, 800))
        self.assertTrue(np.all(mouth[:, :400] == 42))
        self.assertTrue(np.all(mouth[:, 400:] == 83))
        self.assertTrue(np.shares_memory(mouth, strip))
        # Unordered/non-adjacent caller layouts still preserve left/right.
        reordered = stereo_mouth_strip(strip, [3, 0, 1, 2, 4])
        self.assertEqual(reordered[0, 0], strip[0, 1200])
        self.assertEqual(reordered[0, 400], strip[0, 0])

    def test_gpu_initialization_failure_uses_cpu(self) -> None:
        with patch("pupil_gpu.TorchPupilPreprocessor", side_effect=RuntimeError("driver unavailable")):
            tracker = RelativePupilTracker()
        self.assertEqual((tracker.backend, tracker.device), ("cpu", "cpu"))
        self.assertEqual(tracker.device_name, "CPU")
        self.assertIn("driver unavailable", tracker.backend_notice)
        strip = np.hstack((eye(24), eye(24)))
        for _ in range(12):
            values = tracker.update(strip, [0, 1])
        self.assertAlmostEqual(values[0], 5.0, delta=0.3)

    def test_gpu_runtime_failure_preserves_eye_baselines(self) -> None:
        tracker = RelativePupilTracker(backend="cpu")
        strip = np.hstack((eye(24), eye(24)))
        for _ in range(12):
            values = tracker.update(strip, [0, 1])
        baselines = tuple(eye_tracker._baseline for eye_tracker in tracker.eyes)
        class BrokenGpu:
            def median(self, images):
                raise RuntimeError()
        tracker._gpu = BrokenGpu()
        tracker.backend, tracker.device = "amd-rocm", "cuda:0"
        self.assertEqual(tracker.update(strip, [0, 1]), values)
        self.assertEqual(tuple(eye_tracker._baseline for eye_tracker in tracker.eyes), baselines)
        self.assertEqual((tracker.backend, tracker.device), ("cpu", "cpu"))
        self.assertEqual(tracker.device_name, "CPU")
        self.assertIsNone(tracker._gpu)
        self.assertIn("switched to CPU", tracker.backend_notice)

    def test_missing_eye_invalidates_only_that_eye(self) -> None:
        tracker = RelativePupilTracker(backend="cpu")
        for _ in range(12):
            values = tracker.update(np.hstack((eye(24), eye(24))), [0, 1])
        left, right = tracker.update(eye(24), [1])
        self.assertEqual((left, right), values)
        self.assertIn("holding last estimate", tracker.eyes[0].status)
        self.assertEqual(tracker.eyes[1].status, "tracking")


if __name__ == "__main__":
    unittest.main()
