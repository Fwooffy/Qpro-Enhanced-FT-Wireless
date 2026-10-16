import sys
import io
import json
import tempfile
import unittest
from collections import deque
from contextlib import redirect_stdout
from dataclasses import asdict
from pathlib import Path
from unittest import mock

import receiver
import tongue_model_preview
import torch
from tongue_still_capture import (
    TONGUE_ARC_PROMPTS, TONGUE_CORRECTION_PROMPTS, TONGUE_REFINEMENT_PROMPTS,
    TONGUE_STILL_PROMPTS, TongueStillCaptureSession,
)


class _FakeServer:
    def shutdown(self):
        pass

    def server_close(self):
        pass


class _FakeLabels:
    def __init__(self, *_args, **_kwargs):
        self.sample_count = 0
        self.schema_names = []
        self.path = None

    def close(self):
        pass


class _FakeTonguePreview:
    def __init__(self, checkpoint_path, **_kwargs):
        self.device = torch.device("cpu")
        self.checkpoint_path = Path(checkpoint_path)
        self.direction_checkpoint_path = None


class ReceiverTongueStartupTests(unittest.TestCase):
    def capture_startup_journal(self, flag):
        # No frame is received. Startup creates and finalizes only an empty
        # temporary capture and its journal, with all UI and sockets replaced.
        with tempfile.TemporaryDirectory() as directory:
            capture = Path(directory) / "capture.qpcap"
            arguments = ["receiver.py", "--record", str(capture), "--no-labels", flag]
            original_save = TongueStillCaptureSession._save
            with (
                redirect_stdout(io.StringIO()),
                mock.patch.object(sys, "argv", arguments),
                mock.patch.object(receiver, "start_mjpeg_server", return_value=_FakeServer()),
                mock.patch.object(receiver.threading.Thread, "start", return_value=None),
                mock.patch.object(receiver.cv2, "setNumThreads"),
                mock.patch.object(receiver.cv2, "namedWindow"),
                mock.patch.object(receiver.cv2, "resizeWindow"),
                mock.patch.object(receiver.cv2, "setWindowProperty"),
                mock.patch.object(receiver.cv2, "setMouseCallback"),
                mock.patch.object(receiver.cv2, "destroyAllWindows"),
                mock.patch.object(receiver.socket, "create_connection", side_effect=KeyboardInterrupt) as connect,
                mock.patch.object(TongueStillCaptureSession, "_save", autospec=True,
                                  side_effect=original_save) as save,
            ):
                with self.assertRaises(KeyboardInterrupt):
                    receiver.main()
            connect.assert_called_once()
            # A selected constructor writes once, then finish records the
            # interrupted session. No transient legacy journal is created.
            self.assertEqual(save.call_count, 2)
            journal = json.loads(capture.with_suffix(".qpsession.json").read_text(encoding="utf-8"))
            self.assertFalse(journal["completed"])
            self.assertEqual(journal["samples"], [])
            return journal

    def test_quick_and_full_capture_use_only_tongue_curricula(self):
        cases = (
            ("--tongue-refinement-calibration", "tongue-stereo-refinement-v2", TONGUE_REFINEMENT_PROMPTS, 16),
            ("--tongue-still-calibration", "tongue-stereo-stills-v1", TONGUE_STILL_PROMPTS, 58),
        )
        for flag, session_type, expected, count in cases:
            with self.subTest(flag=flag):
                journal = self.capture_startup_journal(flag)
                self.assertEqual(journal["sessionType"], session_type)
                self.assertEqual(journal["prompts"], [asdict(card) for card in expected])
                self.assertEqual(len(journal["prompts"]), count)
                self.assertFalse(any("cheekPuffLeft" in card["targets"] for card in journal["prompts"]))

    def test_correction_and_arc_keep_their_existing_curricula(self):
        cases = (
            ("--tongue-correction-calibration", "tongue-stereo-corrections-v1", TONGUE_CORRECTION_PROMPTS),
            ("--tongue-arc-calibration", "tongue-stereo-arc-v3", TONGUE_ARC_PROMPTS),
        )
        for flag, session_type, prompts in cases:
            with self.subTest(flag=flag):
                journal = self.capture_startup_journal(flag)
                self.assertEqual(journal["sessionType"], session_type)
                self.assertEqual(journal["prompts"], [asdict(card) for card in prompts])

    def test_removed_camera_cheek_options_are_rejected_before_startup(self):
        for option in ("--cheek-output", "--cheek-still-calibration"):
            with self.subTest(option=option), mock.patch.object(sys, "argv", ["receiver.py", option]), \
                    mock.patch.object(receiver, "start_mjpeg_server") as server, \
                    mock.patch.object(receiver.cv2, "setNumThreads"), \
                    mock.patch.object(sys, "stderr", io.StringIO()):
                with self.assertRaises(SystemExit) as error:
                    receiver.main()
                self.assertEqual(error.exception.code, 2)
                server.assert_not_called()

    def test_stream_gap_stats_separates_headset_and_pc_receive_pauses(self):
        stats = receiver.StreamGapStats(
            source_ms=deque(maxlen=240), arrival_ms=deque(maxlen=240)
        )
        stats.add(1_000_000_000, 2_000_000_000)
        stats.add(1_050_000_000, 2_050_000_000)
        stats.add(1_100_000_000, 2_180_000_000)
        source, arrival = stats.summaries()
        self.assertEqual(source, (50.0, 50.0))
        self.assertGreater(arrival[0], 120.0)
        self.assertEqual(arrival[1], 130.0)

    def test_frame_replay_stats_detects_nonconsecutive_exact_payload(self):
        stats = receiver.FrameReplayStats()
        self.assertFalse(stats.add(b"frame-a"))
        self.assertFalse(stats.add(b"frame-b"))
        self.assertFalse(stats.add(b"frame-c"))
        self.assertTrue(stats.add(b"frame-a"))
        self.assertEqual(stats.suspected_replays, 1)
        # A normal duplicate adjacent frame is not classified as ring replay.
        self.assertFalse(stats.add(b"frame-a"))
        self.assertEqual(stats.suspected_replays, 1)

    def test_tongue_preview_does_not_access_unused_pilot_model(self):
        model = Path("models/qpro-stereo-tongue-v1.pt").resolve()
        arguments = [
            "receiver.py",
            "--tongue-model",
            str(model),
            "--tongue-model-device",
            "cpu",
        ]
        with (
            mock.patch.object(sys, "argv", arguments),
            mock.patch.object(receiver, "start_mjpeg_server", return_value=_FakeServer()),
            mock.patch.object(receiver, "LabelSidecarRecorder", _FakeLabels),
            mock.patch.object(tongue_model_preview, "LiveTongueModelPreview", _FakeTonguePreview),
            mock.patch.object(receiver.threading.Thread, "start", return_value=None),
            mock.patch.object(receiver.cv2, "namedWindow"),
            mock.patch.object(receiver.cv2, "resizeWindow"),
            mock.patch.object(receiver.cv2, "setMouseCallback"),
            mock.patch.object(receiver.cv2, "destroyAllWindows"),
            mock.patch.object(receiver.socket, "create_connection", side_effect=KeyboardInterrupt),
        ):
            with self.assertRaises(KeyboardInterrupt):
                receiver.main()


if __name__ == "__main__":
    unittest.main()
