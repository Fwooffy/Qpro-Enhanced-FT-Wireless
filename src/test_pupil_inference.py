"""Pupil scheduling checks use controlled detectors and no real sockets."""

import threading
import time
import unittest
from types import SimpleNamespace

import numpy as np

from pupil_inference import PupilInferenceWorker
from gpu_readback import GPUReadbackCancelled


class Broadcaster:
    def __init__(self):
        self.sent = []
        self.closed = threading.Event()
        self.closes = 0

    def send(self, values):
        if self.closed.is_set():
            raise AssertionError("Output after broadcaster closed")
        self.sent.append(values)

    def close(self):
        self.sent.append((None, None))
        self.closes += 1
        self.closed.set()


class Tracker:
    def __init__(self, delay_first=False):
        self.eyes = (SimpleNamespace(status="tracking"), SimpleNamespace(status="tracking"))
        self.detections = (None, None)
        self.backend, self.device, self.backend_notice = "cpu", "cpu", ""
        self.inputs = []
        self.entered, self.release = threading.Event(), threading.Event()
        self.delay_first = delay_first

    def update(self, strip, camera_ids):
        value = int(strip[0, 0])
        self.inputs.append(value)
        if self.delay_first and len(self.inputs) == 1:
            self.entered.set()
            if not self.release.wait(3):
                raise RuntimeError("Test failed to release detector")
        return float(value), float(value)


def wait_until(predicate, timeout=1.0):
    deadline = time.perf_counter() + timeout
    while time.perf_counter() < deadline:
        if predicate():
            return
        time.sleep(0.005)
    raise AssertionError("Timed out waiting for worker")


class PupilWorkerTests(unittest.TestCase):
    def test_stop_cancels_gpu_wait_without_cpu_fallback_or_worker_error(self):
        class CancelledTracker(Tracker):
            def set_readback_cancelled(self, cancelled):
                self.cancelled = cancelled

            def update(self, *_):
                self.entered.set()
                while not self.cancelled():
                    time.sleep(.001)
                raise GPUReadbackCancelled("Stopped GPU wait")

        tracker, broadcaster = CancelledTracker(), Broadcaster()
        worker = PupilInferenceWorker(tracker, broadcaster)
        worker.submit(np.zeros((1, 1), np.uint8), [0, 1])
        self.assertTrue(tracker.entered.wait(1))
        worker.close()
        self.assertFalse(worker._thread.is_alive())
        self.assertIsNone(worker.latest())
        self.assertIsNone(tracker.cancelled)
        self.assertEqual(broadcaster.sent, [(None, None)])

    def test_completed_result_reports_worker_cpu_separately_from_wall_time(self):
        from unittest import mock

        tracker, broadcaster = Tracker(delay_first=True), Broadcaster()
        with mock.patch("pupil_inference.time.thread_time", side_effect=(1, 1.004)):
            worker = PupilInferenceWorker(tracker, broadcaster)
            try:
                worker.submit(np.zeros((1, 1), np.uint8), [0, 1])
                self.assertTrue(tracker.entered.wait(1))
                time.sleep(.025)
                tracker.release.set()
                wait_until(lambda: worker.latest() is not None)
                result = worker.latest()
                self.assertAlmostEqual(result.worker_cpu_ms, 4.0)
                self.assertGreater(result.processing_ms, result.worker_cpu_ms)
            finally:
                tracker.release.set()
                worker.close()

    def test_only_newest_pending_pair_is_processed(self):
        tracker, broadcaster = Tracker(delay_first=True), Broadcaster()
        worker = PupilInferenceWorker(tracker, broadcaster)
        try:
            worker.submit(np.full((1, 1), 1, np.uint8), [0, 1])
            self.assertTrue(tracker.entered.wait(1))
            worker.submit(np.full((1, 1), 2, np.uint8), [0, 1])
            worker.submit(np.full((1, 1), 3, np.uint8), [0, 1])
            tracker.release.set()
            wait_until(lambda: worker.latest() is not None and worker.latest().values == (3.0, 3.0))
            self.assertEqual(tracker.inputs, [1, 3])
            self.assertEqual(worker.latest().dropped_frames, 1)
        finally:
            tracker.release.set()
            worker.close()

    def test_slow_result_is_invalid_and_feed_stall_expires_output(self):
        tracker, broadcaster = Tracker(delay_first=True), Broadcaster()
        worker = PupilInferenceWorker(tracker, broadcaster)
        worker.maximum_age_seconds = 0.03
        try:
            worker.submit(np.full((1, 1), 1, np.uint8), [0, 1])
            self.assertTrue(tracker.entered.wait(1))
            time.sleep(0.04)
            tracker.release.set()
            wait_until(lambda: len(broadcaster.sent) > 0)
            self.assertEqual(broadcaster.sent[0], (None, None))
            self.assertIsNone(worker.latest())
            worker.submit(np.full((1, 1), 2, np.uint8), [0, 1])
            wait_until(lambda: (2.0, 2.0) in broadcaster.sent)
            wait_until(lambda: broadcaster.sent[-1] == (None, None))
        finally:
            tracker.release.set()
            worker.close()

    def test_close_clears_output_before_blocked_detector_finishes(self):
        tracker, broadcaster = Tracker(delay_first=True), Broadcaster()
        worker = PupilInferenceWorker(tracker, broadcaster)
        worker.submit(np.full((1, 1), 1, np.uint8), [0, 1])
        self.assertTrue(tracker.entered.wait(1))
        closing = threading.Thread(target=worker.close)
        closing.start()
        try:
            self.assertTrue(broadcaster.closed.wait(0.5))
            tracker.release.set()
            closing.join(1)
            self.assertFalse(closing.is_alive())
            self.assertEqual(broadcaster.sent, [(None, None)])
            self.assertEqual(broadcaster.closes, 1)
            worker.close()
            self.assertEqual(broadcaster.closes, 1)
        finally:
            tracker.release.set()
            closing.join(3)
            worker.close()

    def test_latest_expires_from_frame_age_not_just_completion_time(self):
        from pupil_inference import PupilResult
        tracker, broadcaster = Tracker(), Broadcaster()
        worker = PupilInferenceWorker(tracker, broadcaster)
        try:
            # This frame completed now but already spent 490ms being processed.
            now = time.perf_counter()
            worker._latest = PupilResult((5.0, 5.0), (None, None), ("tracking", "tracking"),
                                         "cpu", "cpu", "", 490, 490, 0, now)
            self.assertIsNotNone(worker.latest())
            time.sleep(0.02)
            self.assertIsNone(worker.latest())
        finally:
            worker.close()


if __name__ == "__main__":
    unittest.main()
