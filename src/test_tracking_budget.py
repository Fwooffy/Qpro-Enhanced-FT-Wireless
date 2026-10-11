"""Inference caps retain newest frames, frame age, and prompt stop behavior."""

import time
import unittest
from unittest import mock

import numpy as np

from tracking_budget import InferenceCadence, resolve_tracking_budget
from pupil_inference import PupilInferenceWorker
from test_pupil_inference import Broadcaster, Tracker, wait_until
from tongue_model_preview import TongueInferenceWorker, TonguePrediction


class TrackingBudgetTests(unittest.TestCase):
    def test_all_presets_keep_tongue_and_default_camera_cadence(self):
        self.assertEqual(resolve_tracking_budget().tongue_fps, 0)
        self.assertEqual(resolve_tracking_budget().pupil_fps, 0)
        for profile, expected in (("responsive", 0), ("balanced", 12), ("low-cpu", 8)):
            budget = resolve_tracking_budget(profile)
            self.assertEqual(budget.tongue_fps, 0)
            self.assertEqual(budget.pupil_fps, expected)
        self.assertEqual(resolve_tracking_budget("balanced", pupil_fps=0).pupil_fps, 0)
        self.assertEqual(resolve_tracking_budget(tongue_fps=20).tongue_fps, 20)

    def test_invalid_budgets_fail_before_starting_worker(self):
        for value in (-1, 1, 4.9, 120.1, float("nan"), float("inf")):
            with self.subTest(value=value), self.assertRaises(ValueError):
                resolve_tracking_budget(pupil_fps=value)
        with self.assertRaises(ValueError):
            resolve_tracking_budget("unknown")

    def test_cadence_uses_monotonic_worker_starts_and_no_catch_up_burst(self):
        cadence = InferenceCadence(8)
        self.assertEqual(cadence.delay(10), 0)
        cadence.started(10)
        self.assertAlmostEqual(cadence.delay(10.05), .075)
        self.assertEqual(cadence.delay(11), 0)
        cadence.started(11)
        self.assertAlmostEqual(cadence.delay(11), .125)
        unlimited = InferenceCadence()
        unlimited.started(10)
        self.assertEqual(unlimited.delay(10), 0)

    def test_pupil_cap_sleeps_and_uses_latest_frame_with_original_timestamp(self):
        tracker, broadcaster = Tracker(), Broadcaster()
        worker = PupilInferenceWorker(tracker, broadcaster, maximum_fps=5)
        try:
            worker.submit(np.full((1, 1), 1, np.uint8), [0, 1])
            wait_until(lambda: worker.latest() is not None)
            worker.submit(np.full((1, 1), 2, np.uint8), [0, 1])
            time.sleep(.04)
            worker.submit(np.full((1, 1), 3, np.uint8), [0, 1])
            wait_until(lambda: worker.latest() is not None and worker.latest().values == (3.0, 3.0))
            result = worker.latest()
            self.assertEqual(tracker.inputs, [1, 3])
            self.assertEqual(result.dropped_frames, 1)
            # The scheduling wait remains part of frame age, never hidden by
            # replacing its timestamp with processing start time.
            self.assertGreater(result.age_ms, 100)
            self.assertLess(result.worker_cpu_ms, 20)
            self.assertLess(result.processing_ms, 50)
        finally:
            worker.close()

    def test_pupil_stale_pending_frame_is_not_processed_or_published(self):
        tracker, broadcaster = Tracker(), Broadcaster()
        worker = PupilInferenceWorker(tracker, broadcaster, maximum_fps=5)
        worker.maximum_age_seconds = .03
        try:
            worker.submit(np.full((1, 1), 1, np.uint8), [0, 1])
            wait_until(lambda: worker.latest() is not None)
            worker.submit(np.full((1, 1), 2, np.uint8), [0, 1])
            wait_until(lambda: broadcaster.sent[-1] == (None, None))
            self.assertEqual(tracker.inputs, [1])
            self.assertIsNone(worker.latest())
        finally:
            worker.close()

    def test_stop_interrupts_pupil_budget_wait_without_pending_publication(self):
        tracker, broadcaster = Tracker(), Broadcaster()
        worker = PupilInferenceWorker(tracker, broadcaster, maximum_fps=5)
        worker.submit(np.zeros((1, 1), np.uint8), [0, 1])
        wait_until(lambda: worker.latest() is not None)
        worker.submit(np.ones((1, 1), np.uint8), [0, 1])
        started = time.perf_counter()
        worker.close()
        self.assertLess(time.perf_counter() - started, .1)
        self.assertEqual(tracker.inputs, [0])
        self.assertFalse(worker._thread.is_alive())
        self.assertEqual(broadcaster.sent[-1], (None, None))

    def test_explicit_tongue_cap_keeps_newest_frame_and_unmodified_values(self):
        inputs = []

        class Preview:
            target_names = ["extension"]

            def predict(self, strip, _sample, _names):
                value = float(strip[0, 0])
                inputs.append(value)
                return TonguePrediction(np.asarray([value]), 0, 1, True, 1)

        broadcaster = mock.Mock(enabled=True)
        worker = TongueInferenceWorker(Preview(), broadcaster, render_preview=False, maximum_fps=5)
        try:
            worker.submit(np.zeros((1, 1)), None, [])
            wait_until(lambda: worker.latest()[0] is not None)
            worker.submit(np.full((1, 1), .2), None, [])
            time.sleep(.04)
            worker.submit(np.full((1, 1), .7), None, [])
            wait_until(lambda: worker.latest()[0] is not None and worker.latest()[0].values[0] == .7)
            result, _ = worker.latest()
            self.assertEqual(inputs, [0, .7])
            self.assertEqual(result.dropped_frames, 1)
            self.assertGreater(result.pipeline_ms, 100)
            self.assertLess(result.pipeline_ms, 300)
        finally:
            worker.close()

    def test_stop_interrupts_tongue_budget_wait(self):
        class Preview:
            target_names = ["extension"]

            def predict(self, *_):
                return TonguePrediction(np.asarray([.4]), 0, 1, True, 1)

        broadcaster = mock.Mock(enabled=True)
        worker = TongueInferenceWorker(Preview(), broadcaster, render_preview=False, maximum_fps=5)
        worker.submit(np.zeros((1, 1)), None, [])
        wait_until(lambda: worker.latest()[0] is not None)
        worker.submit(np.ones((1, 1)), None, [])
        before = broadcaster.send_prediction.call_count
        started = time.perf_counter()
        worker.close()
        self.assertLess(time.perf_counter() - started, .1)
        self.assertFalse(worker._thread.is_alive())
        self.assertEqual(broadcaster.send_prediction.call_count, before)


if __name__ == "__main__":
    unittest.main()
