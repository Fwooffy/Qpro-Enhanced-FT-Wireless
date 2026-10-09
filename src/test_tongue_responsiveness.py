"""Behavioral checks for tongue delay, steady poses, and preview overhead."""

import threading
import time
import unittest
from dataclasses import replace
from unittest import mock

import numpy as np
import torch

from tongue_model_preview import (
    LiveTongueModelPreview, TongueBroadcaster, TongueInferenceWorker,
    TongueMotionFilter, TonguePrediction, smooth_tongue_output,
)
from gpu_readback import GPUReadbackCancelled


class TongueResponsivenessTests(unittest.TestCase):
    names = ["visibility", "extension", "horizontal", "cheekPuffLeft", "cheekPuffRight"]
    default_alpha = 1.0 - 0.88 * 0.55

    def test_stop_cancels_gpu_wait_without_publishing_or_worker_error(self):
        entered = threading.Event()

        class Preview:
            target_names = ["extension"]

            def set_readback_cancelled(self, cancelled):
                self.cancelled = cancelled

            def predict(self, *_):
                entered.set()
                while not self.cancelled():
                    time.sleep(.001)
                raise GPUReadbackCancelled("Stopped GPU wait")

        preview, broadcaster = Preview(), mock.Mock(enabled=True)
        worker = TongueInferenceWorker(preview, broadcaster, render_preview=False)
        worker.submit(np.zeros((1, 1)), None, [])
        self.assertTrue(entered.wait(1))
        worker.close()
        self.assertFalse(worker._thread.is_alive())
        self.assertEqual(worker.latest(), (None, None))
        self.assertIsNone(preview.cancelled)
        broadcaster.send_prediction.assert_not_called()

    def test_completed_result_records_actual_worker_cpu_time(self):
        class Preview:
            target_names = ["extension"]

            def predict(self, *_):
                time.sleep(.02)
                return TonguePrediction(np.asarray([0.7]), 0, 1, True, 1)

        broadcaster = mock.Mock(enabled=True)
        with mock.patch("tongue_model_preview.time.thread_time", side_effect=(1, 1.002)):
            worker = TongueInferenceWorker(Preview(), broadcaster, render_preview=False)
            try:
                worker.submit(np.zeros((1, 1)), None, [])
                deadline = time.monotonic() + 1
                while worker.latest()[0] is None and time.monotonic() < deadline:
                    time.sleep(.001)
                result, _ = worker.latest()
                self.assertIsNotNone(result)
                self.assertAlmostEqual(result.worker_cpu_ms, 2.0)
                self.assertGreater(result.pipeline_ms, result.worker_cpu_ms)
            finally:
                worker.close()

    def test_large_movement_reaches_target_sooner_without_overshooting(self):
        for fps in (12, 24, 48, 72):
            with self.subTest(fps=fps):
                motion = TongueMotionFilter(self.default_alpha, self.names)
                initial = np.zeros(5, np.float32)
                target = np.asarray([0.0, 0.8, -0.8, 0.0, 0.0], np.float32)
                motion.update(initial, 0.0)
                previous = initial
                reached_at = None
                for frame in range(1, fps + 1):
                    current = motion.update(target, frame / fps)
                    self.assertGreaterEqual(current[1], previous[1])
                    self.assertLessEqual(current[1], target[1])
                    self.assertLessEqual(current[2], previous[2])
                    self.assertGreaterEqual(current[2], target[2])
                    if reached_at is None and current[1] >= 0.95 * target[1]:
                        reached_at = frame / fps
                    previous = current
                self.assertIsNotNone(reached_at)
                self.assertLessEqual(reached_at, 0.15)

    def test_small_steady_pose_noise_is_filtered(self):
        motion = TongueMotionFilter(self.default_alpha, self.names)
        motion.update(np.full(5, 0.4, np.float32), 0.0)
        outputs = []
        for frame in range(1, 61):
            values = np.full(5, 0.4 + (0.02 if frame % 2 else -0.02), np.float32)
            outputs.append(motion.update(values, frame / 24)[1])
        self.assertLess(np.ptp(outputs[10:]), 0.02)

    def test_visibility_and_cheeks_keep_ordinary_smoothing(self):
        motion = TongueMotionFilter(self.default_alpha, self.names)
        motion.update(np.zeros(5, np.float32), 0.0)
        values = motion.update(np.ones(5, np.float32), 1 / 24)
        np.testing.assert_allclose(values[[0, 3, 4]], self.default_alpha)
        self.assertGreater(values[1], values[0])
        self.assertGreater(values[2], values[0])

    def test_small_response_is_consistent_across_camera_rates(self):
        results = []
        for fps in (12, 24, 48, 72):
            motion = TongueMotionFilter(self.default_alpha, self.names)
            motion.update(np.zeros(5, np.float32), 0.0)
            for frame in range(1, fps + 1):
                values = motion.update(np.full(5, 0.04, np.float32), frame / fps)
            results.append(values)
        np.testing.assert_allclose(results, np.full((4, 5), 0.04), atol=1e-6)

    def test_no_smoothing_is_exact_and_invalid_model_outputs_fail(self):
        motion = TongueMotionFilter(1.0, self.names)
        for frame in range(3):
            values = np.asarray([0.1, 0.9, -0.7, 0.8, 0.1], np.float32) / (frame + 1)
            np.testing.assert_array_equal(motion.update(values, frame / 24), values)
        for invalid in (np.zeros(4), np.full(5, np.nan), np.full(5, np.inf)):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                motion.update(invalid, 1.0)

    def test_output_follows_72hz_and_preserves_small_changes(self):
        target_names = ["extension"]
        socket = mock.Mock()
        with mock.patch("tongue_model_preview.socket.socket", return_value=socket):
            broadcaster = TongueBroadcaster(enabled=True)
        packets = []
        broadcaster._send = lambda values, *, enabled: packets.append(values.copy())
        try:
            for frame in range(72):
                prediction = TonguePrediction(np.asarray([frame / 100], np.float32),
                                              0, 1, True, 1)
                with mock.patch("tongue_model_preview.time.perf_counter", return_value=10 + frame / 72):
                    broadcaster.send_prediction(prediction, target_names)
            self.assertEqual(len(packets), 72)
            self.assertAlmostEqual(packets[-1][0], 0.71, places=6)
        finally:
            broadcaster.close()

    def test_output_rise_and_fall_are_short_but_analog(self):
        for fps in (24, 48, 72):
            with self.subTest(fps=fps):
                values = np.zeros(12, np.float32)
                target = np.ones(12, np.float32)
                values = smooth_tongue_output(values, target, 1 / fps)
                self.assertTrue(np.all((values > 0) & (values < 1)))
                for _ in range(round(fps * 0.15)):
                    values = smooth_tongue_output(values, target, 1 / fps)
                np.testing.assert_allclose(values, target, atol=1e-6)
                values = smooth_tongue_output(values, np.zeros(12), 1 / fps)
                self.assertTrue(np.all((values > 0) & (values < 1)))
                for _ in range(round(fps * 0.15)):
                    values = smooth_tongue_output(values, np.zeros(12), 1 / fps)
                np.testing.assert_allclose(values, 0, atol=1e-6)

    def test_disabling_output_waits_for_publication_and_sends_off_last(self):
        publishing = threading.Event()
        release = threading.Event()
        disabled = threading.Event()
        flags = []
        with mock.patch("tongue_model_preview.socket.socket"):
            broadcaster = TongueBroadcaster(enabled=True)

        def send(_values, *, enabled):
            if enabled:
                publishing.set()
                release.wait(1)
            flags.append(enabled)

        def disable():
            broadcaster.toggle()
            disabled.set()

        broadcaster._send = send
        prediction = TonguePrediction(np.asarray([0.7]), 0, 1, True, 1)
        publisher = threading.Thread(target=broadcaster.send_prediction,
                                     args=(prediction, ["extension"]))
        toggler = threading.Thread(target=disable)
        try:
            publisher.start()
            self.assertTrue(publishing.wait(1))
            toggler.start()
            self.assertFalse(disabled.wait(0.02))
            release.set()
            self.assertTrue(disabled.wait(1))
            publisher.join(1)
            toggler.join(1)
            broadcaster.send_prediction(prediction, ["extension"])
            self.assertEqual(flags, [True, False])
        finally:
            release.set()
            publisher.join(1)
            if toggler.ident is not None:
                toggler.join(1)
            broadcaster.close()
            broadcaster.close()
            self.assertFalse(broadcaster.toggle())

    def test_stale_result_restores_native_output_and_fresh_results_can_resume(self):
        with mock.patch("tongue_model_preview.socket.socket"):
            broadcaster = TongueBroadcaster(enabled=True)
        flags = []
        broadcaster._send = lambda _values, *, enabled: flags.append(enabled)
        fresh = TonguePrediction(np.asarray([0.7]), 0, 1, True, 1, pipeline_ms=20)
        try:
            broadcaster.send_prediction(fresh, ["extension"])
            for age in (301, 400, 500, 501, 800, float("nan"), float("inf"), -1):
                broadcaster.send_prediction(replace(fresh, pipeline_ms=age), ["extension"])
            self.assertEqual(flags, [True, False])
            broadcaster.send_prediction(fresh, ["extension"])
            self.assertEqual(flags, [True, False, True])
        finally:
            broadcaster.close()

    def test_tongue_freshness_boundary_matches_bridge_timeout(self):
        with mock.patch("tongue_model_preview.socket.socket"):
            broadcaster = TongueBroadcaster(enabled=True)
        flags = []
        broadcaster._send = lambda _values, *, enabled: flags.append(enabled)
        prediction = TonguePrediction(np.asarray([0.7]), 0, 1, True, 1, pipeline_ms=300)
        try:
            broadcaster.send_prediction(prediction, ["extension"])
            broadcaster.send_prediction(replace(prediction, pipeline_ms=300.001), ["extension"])
            self.assertEqual(flags, [True, False])
        finally:
            broadcaster.close()

    def test_rejected_stale_pose_cannot_seed_a_fresh_hidden_pose(self):
        for smoothing in (0.35, 1.0):
            with self.subTest(smoothing=smoothing):
                clock = [10.0]

                class Model(torch.nn.Module):
                    def __init__(self):
                        super().__init__()
                        self.frames = iter(((0.6, [0.9, 0.8, -0.7]), (0.01, [0, 0, 0])) * 3)

                    def forward(self, _inputs):
                        delay, values = next(self.frames)
                        clock[0] += delay
                        return torch.tensor([values], dtype=torch.float32)

                checkpoint = {"targetNames": ["visibility", "extension", "horizontal"],
                              "imageSize": 32, "modelState": {}}
                with (
                    mock.patch("tongue_model_preview.torch.load", return_value=checkpoint),
                    mock.patch("tongue_model_preview.create_model", return_value=Model()),
                    mock.patch("tongue_model_preview.validated_torch_device_name", return_value="cpu"),
                ):
                    preview = LiveTongueModelPreview("gate.pt", smoothing=smoothing,
                                                    visibility_mode="camera")
                with mock.patch("tongue_model_preview.socket.socket"):
                    broadcaster = TongueBroadcaster(enabled=True)
                sent = []
                broadcaster._send = lambda values, *, enabled: sent.append((enabled, values.copy()))
                with (mock.patch("tongue_model_preview.time.perf_counter", side_effect=lambda: clock[0]),
                      mock.patch("tongue_model_preview.print") as status_log):
                    worker = TongueInferenceWorker(preview, broadcaster, render_preview=False)
                    try:
                        for frame in range(1, 7):
                            worker.submit(np.zeros((400, 800), np.uint8), None, [])
                            deadline = time.monotonic() + 1
                            while time.monotonic() < deadline:
                                prediction, _ = worker.latest()
                                if prediction is not None and prediction.completed_frames == frame:
                                    break
                                time.sleep(0.001)
                            else:
                                self.fail("Inference did not finish")
                        self.assertEqual([enabled for enabled, _ in sent], [False, True] * 3)
                        self.assertFalse(prediction.visible)
                        np.testing.assert_array_equal(prediction.values, [0, 0, 0])
                        np.testing.assert_array_equal(sent[-1][1], np.zeros(12))
                        messages = [str(call.args[0]) for call in status_log.call_args_list]
                        self.assertEqual(sum("TONGUE_OUTPUT_STALE" in value for value in messages), 1)
                        self.assertEqual(sum("TONGUE_OUTPUT_RECOVERED" in value for value in messages), 1)
                        # Repeated slow/fresh alternation within five seconds
                        # must not flood Activity with warning/recovery pairs.
                        self.assertTrue(any("native tongue values" in value for value in messages))
                    finally:
                        worker.close()
                        broadcaster.close()

    def test_avatar_output_and_status_publish_before_slow_preview_render(self):
        rendering = threading.Event()
        release = threading.Event()

        class Preview:
            target_names = ["extension"]

            def predict(self, *_):
                return TonguePrediction(np.asarray([0.7]), 0, 1, True, 1)

            def render(self, *_args, **_kwargs):
                rendering.set()
                release.wait(1)
                return np.zeros((1, 1))

        broadcaster = mock.Mock(enabled=True)
        worker = TongueInferenceWorker(Preview(), broadcaster)
        try:
            worker.submit(np.zeros((1, 1)), None, [])
            self.assertTrue(rendering.wait(1))
            broadcaster.send_prediction.assert_called_once()
            prediction, image = worker.latest()
            self.assertIsNotNone(prediction)
            self.assertIsNone(image)
            self.assertEqual(prediction.completed_frames, 1)
            first_age = prediction.age_ms
            time.sleep(0.01)
            self.assertGreater(worker.latest()[0].age_ms, first_age)
        finally:
            release.set()
            worker.close()

    def test_ambiguous_native_source_warns_once_only_when_tongue_output_is_enabled(self):
        class Preview:
            target_names = ["extension"]

            def predict(self, *_):
                return TonguePrediction(np.asarray([0.7]), 0, 1, True, 1,
                                        native_status="source-unknown")

        broadcaster = mock.Mock(enabled=False)
        with mock.patch("tongue_model_preview.print") as status_log:
            worker = TongueInferenceWorker(Preview(), broadcaster, render_preview=False)
            try:
                for frame in range(1, 5):
                    broadcaster.enabled = frame > 1
                    worker.submit(np.zeros((1, 1)), None, [])
                    deadline = time.monotonic() + 1
                    while time.monotonic() < deadline:
                        prediction, _ = worker.latest()
                        if prediction is not None and prediction.completed_frames == frame:
                            break
                        time.sleep(0.001)
                    else:
                        self.fail("Inference did not finish")
                    if frame == 1:
                        status_log.assert_not_called()
                messages = [str(call.args[0]) for call in status_log.call_args_list]
                self.assertEqual(sum("TONGUE_NATIVE_SOURCE_UNKNOWN" in value for value in messages), 1)
                self.assertTrue(any("native TongueOut reference is omitted" in value for value in messages))
            finally:
                worker.close()

    def test_preview_throttling_keeps_every_completed_prediction(self):
        class Preview:
            target_names = ["extension"]

            def __init__(self):
                self.render_count = 0

            def predict(self, *_):
                return TonguePrediction(np.asarray([0.7]), 0, 1, True, 1)

            def render(self, *_args, **_kwargs):
                self.render_count += 1
                return np.zeros((1, 1))

        preview = Preview()
        broadcaster = mock.Mock(enabled=True)
        clock = [10.0]
        with mock.patch("tongue_model_preview.time.perf_counter", side_effect=lambda: clock[0]):
            worker = TongueInferenceWorker(preview, broadcaster)
            try:
                for frame in range(72):
                    clock[0] = 10.0 + frame / 72
                    worker.submit(np.zeros((1, 1)), None, [])
                    deadline = time.monotonic() + 1
                    while time.monotonic() < deadline:
                        prediction, image = worker.latest()
                        if prediction is not None and prediction.completed_frames == frame + 1:
                            break
                        time.sleep(0.001)
                    else:
                        self.fail("Inference did not finish the current frame")
                self.assertEqual(broadcaster.send_prediction.call_count, 72)
                self.assertLessEqual(preview.render_count, 12)
                self.assertGreater(preview.render_count, 1)
                self.assertIsNotNone(image)
            finally:
                worker.close()


if __name__ == "__main__":
    unittest.main()
