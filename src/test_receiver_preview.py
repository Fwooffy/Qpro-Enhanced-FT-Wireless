"""HTTP previews should not consume camera-processing time when unused."""

import unittest
from unittest import mock
from types import SimpleNamespace

import numpy as np

from receiver import MjpegHandler, SharedPreview, format_tracking_health


class ReceiverPreviewTests(unittest.TestCase):
    def setUp(self) -> None:
        self.strip = np.hstack([
            np.full((400, 400), value, dtype=np.uint8)
            for value in (10, 20, 30, 40, 50)
        ])

    @staticmethod
    def encode(_extension, image, _parameters):
        return True, np.asarray([image[0, 0]], dtype=np.uint8)

    def test_no_http_clients_do_not_encode_jpegs(self) -> None:
        shared = SharedPreview()
        with mock.patch("receiver.cv2.imencode") as encode:
            shared.update(self.strip, [0, 1, 2, 3, 4])
        encode.assert_not_called()
        self.assertEqual(shared.available, [0, 1, 2, 3, 4])
        self.assertEqual(shared.jpegs, {})

    def test_selected_client_follows_camera_changes(self) -> None:
        shared = SharedPreview()
        shared.subscribe("selected")
        with mock.patch("receiver.cv2.imencode", side_effect=self.encode) as encode:
            shared.update(self.strip, [0, 1, 2, 3, 4])
            self.assertEqual(shared.jpegs, {"camera3": bytes([40])})
            self.assertTrue(shared.select(1))
            shared.update(self.strip, [0, 1, 2, 3, 4])
        self.assertEqual(encode.call_count, 2)
        self.assertEqual(shared.jpegs, {"camera1": bytes([20])})

    def test_requested_streams_are_encoded_once(self) -> None:
        shared = SharedPreview()
        for requested in ("selected", "camera3", "camera3", "strip"):
            shared.subscribe(requested)
        with mock.patch("receiver.cv2.imencode", side_effect=self.encode) as encode:
            shared.update(self.strip, [0, 1, 2, 3, 4])
        self.assertEqual(encode.call_count, 2)
        self.assertEqual(set(shared.jpegs), {"camera3", "strip"})
        shared.unsubscribe("camera3")
        self.assertEqual(shared.subscribers["camera3"], 1)
        shared.unsubscribe("camera3")
        self.assertNotIn("camera3", shared.subscribers)

    def test_disconnect_releases_subscription(self) -> None:
        shared = SharedPreview(available=[3], jpegs={"camera3": b"jpeg"})
        handler = MjpegHandler.__new__(MjpegHandler)
        handler.shared = shared
        handler.path = "/selected.mjpg"
        handler.send_response = mock.Mock()
        handler.send_header = mock.Mock()
        handler.end_headers = mock.Mock()
        handler.wfile = mock.Mock()
        handler.wfile.write.side_effect = BrokenPipeError()
        handler.do_GET()
        self.assertEqual(shared.subscribers, {})
        with mock.patch("receiver.cv2.imencode") as encode:
            shared.update(self.strip, [0, 1, 2, 3, 4])
        encode.assert_not_called()

    def test_selected_strip_and_missing_camera_need_no_extra_encodes(self) -> None:
        shared = SharedPreview(selected="strip")
        shared.subscribe("selected")
        shared.subscribe("camera4")
        with mock.patch("receiver.cv2.imencode", side_effect=self.encode) as encode:
            shared.update(self.strip[:, :800], [0, 1])
        self.assertEqual(encode.call_count, 1)
        self.assertEqual(set(shared.jpegs), {"strip"})

    def test_health_snapshot_expires_outputs_independently_of_camera_fps(self) -> None:
        prediction = SimpleNamespace(pipeline_ms=80.0, age_ms=240.0, visible=True,
                                     inference_ms=50.0, dropped_frames=7)
        pupil = SimpleNamespace(age_ms=20.0, completed_at=9.9, processing_ms=15.0,
                                dropped_frames=2, values=(5.0, 5.0))
        text = format_tracking_health(camera_fps=30.0, inference_fps=20.0,
            frame_age_ms=3.0, decode_ms=.01, prediction=prediction,
            tongue_enabled=True, cheek_enabled=True, pupil_result=pupil,
            pupil_enabled=True, now=10.0)
        self.assertIn("tongue=stale cheek=valid pupil=valid", text)
        self.assertIn("pupil_age_ms=120.0", text)
        prediction.age_ms = 440.0
        text = format_tracking_health(camera_fps=30.0, inference_fps=0.0,
            frame_age_ms=3.0, decode_ms=.01, prediction=prediction,
            tongue_enabled=True, cheek_enabled=True, pupil_result=pupil,
            pupil_enabled=True, now=11.0)
        self.assertIn("tongue=stale cheek=stale pupil=stale", text)

    def test_health_neutral_pose_is_fresh_and_missing_results_are_explicit(self) -> None:
        prediction = SimpleNamespace(pipeline_ms=20.0, age_ms=10.0, visible=False)
        text = format_tracking_health(camera_fps=24.0, inference_fps=24.0,
            frame_age_ms=1.0, decode_ms=.01, prediction=prediction, tongue_enabled=True)
        self.assertIn("tongue=hidden cheek=off pupil=off", text)
        text = format_tracking_health(camera_fps=24.0, inference_fps=0.0,
            frame_age_ms=1.0, decode_ms=.01, tongue_enabled=True, pupil_enabled=True,
            pupil_seen=True)
        self.assertIn("inference_ms=-1.0", text)
        self.assertIn("tongue=warming cheek=off pupil=stale", text)

    def test_pupil_only_and_first_frame_health_keep_camera_metrics(self) -> None:
        pupil = SimpleNamespace(age_ms=13.0, completed_at=9.99, processing_ms=11.0,
                                dropped_frames=1, values=(5.1, 5.0))
        text = format_tracking_health(camera_fps=30, inference_fps=28,
            frame_age_ms=.2, decode_ms=.004, pupil_result=pupil, pupil_enabled=True, now=10)
        self.assertEqual(text,
            "TRACKING_HEALTH camera_fps=30.0 inference_fps=28.0 frame_age_ms=0.2 "
            "decode_ms=0.004 preprocess_ms=-1.000 inference_ms=-1.0 transfer_ms=-1.000 "
            "pipeline_ms=-1.0 result_age_ms=-1.0 dropped_frames=0 tongue=off cheek=off "
            "pupil=valid pupil_ms=11.0 pupil_age_ms=23.0 pupil_dropped_frames=1")
        text = format_tracking_health(camera_fps=30, inference_fps=0,
            frame_age_ms=.2, decode_ms=.004, pupil_enabled=True, now=10)
        self.assertEqual(text,
            "TRACKING_HEALTH camera_fps=30.0 inference_fps=0.0 frame_age_ms=0.2 "
            "decode_ms=0.004 preprocess_ms=-1.000 inference_ms=-1.0 transfer_ms=-1.000 "
            "pipeline_ms=-1.0 result_age_ms=-1.0 dropped_frames=0 tongue=off cheek=off "
            "pupil=warming pupil_ms=-1.0 pupil_age_ms=-1.0 pupil_dropped_frames=0")


if __name__ == "__main__":
    unittest.main()
