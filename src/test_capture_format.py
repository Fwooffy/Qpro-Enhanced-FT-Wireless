import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

from capture_format import (
    CaptureWriter, TRANSPORT_HEADER, inspect_capture, scan_stereo_mouth_stills,
)


class CaptureFormatTests(unittest.TestCase):
    def test_close_failure_closes_handle_and_preserves_first_error(self):
        for stage in ("flush", "header"):
            with self.subTest(stage=stage):
                writer = CaptureWriter.__new__(CaptureWriter)
                writer._file = mock.Mock(closed=False)
                error = OSError("disk write failed")
                writer._write_file_header = mock.Mock()
                if stage == "flush":
                    writer._file.flush.side_effect = error
                else:
                    writer._write_file_header.side_effect = error
                writer._file.close.side_effect = OSError("later close error")
                with self.assertRaises(OSError) as failed:
                    writer.close()
                self.assertIs(failed.exception, error)
                writer._file.close.assert_called_once()
                if stage == "flush":
                    writer._write_file_header.assert_not_called()

    def test_constructor_header_failure_does_not_leak_file(self):
        handle = mock.Mock()
        with (tempfile.TemporaryDirectory() as directory,
              mock.patch.object(Path, "open", return_value=handle),
              mock.patch.object(CaptureWriter, "_write_file_header", side_effect=OSError("header failed")),
              self.assertRaisesRegex(OSError, "header failed")):
            CaptureWriter(Path(directory) / "fixture.qpcap")
        handle.close.assert_called_once()
    def test_stereo_still_scanner_supports_old_face_and_new_mouth_capture(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            for name, width, mask in (("old-face", 1200, 0x1C), ("mouth", 800, 0x0C)):
                path = Path(directory) / f"{name}.qpcap"
                writer = CaptureWriter(path)
                payload = bytes(width * 400)
                header = TRANSPORT_HEADER.pack(
                    b"QPLIVE3\0", 3, TRANSPORT_HEADER.size, 1, 100,
                    width, 400, width, 1, len(payload), mask, 0,
                )
                writer.write(header, payload, 123_000_000, time.time_ns())
                writer.close()
                entries, actual_width = scan_stereo_mouth_stills(path)
                self.assertEqual(actual_width, width)
                self.assertEqual(len(entries), 1)
                self.assertEqual(entries[0][1], 123_000_000)

    def test_round_trip_preserves_face_layout(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "face.qpcap"
            writer = CaptureWriter(path)
            payload = memoryview(bytearray(1200 * 400)).toreadonly()
            for sequence in (10, 12):
                source_header = TRANSPORT_HEADER.pack(
                    b"QPLIVE3\0", 3, TRANSPORT_HEADER.size, sequence,
                    123_000_000 + sequence, 1200, 400, 1200, 1,
                    len(payload), 0b11100, 7,
                )
                writer.write(
                    source_header, payload,
                    1_000_000_000 + sequence * 1_000_000,
                    time.time_ns(),
                )
            writer.close()

            summary = inspect_capture(path)
            self.assertTrue(summary["completed"])
            self.assertFalse(summary["truncated"])
            self.assertEqual(summary["declared_frames"], 2)
            self.assertEqual(summary["scanned_frames"], 2)
            self.assertEqual(summary["camera_masks"], {0b11100: 2})
            self.assertEqual(summary["dimensions"], {(1200, 400): 2})
            self.assertEqual(summary["first_sequence"], 10)
            self.assertEqual(summary["last_sequence"], 12)
            self.assertEqual(summary["source_skips"], 1)
            self.assertEqual(summary["exact_nonconsecutive_replays"], 0)
            self.assertEqual(summary["rejected_torn_delta"], 0)
            self.assertGreater(summary["duration_seconds"], 0)

    def test_incomplete_capture_remains_scannable(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "interrupted.qpcap"
            writer = CaptureWriter(path)
            payload = bytes(400 * 400)
            source_header = TRANSPORT_HEADER.pack(
                b"QPLIVE3\0", 3, TRANSPORT_HEADER.size, 1, 100, 400, 400,
                400, 1, len(payload), 0b00001, 0,
            )
            writer.write(source_header, payload, time.monotonic_ns(), time.time_ns())
            writer.close(completed=False)

            summary = inspect_capture(path)
            self.assertFalse(summary["completed"])
            self.assertFalse(summary["truncated"])
            self.assertEqual(summary["scanned_frames"], 1)

    def test_inspector_counts_only_nonconsecutive_exact_replays(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "replay.qpcap"
            writer = CaptureWriter(path)
            payloads = [bytes([1]) * 100, bytes([2]) * 100,
                        bytes([1]) * 100, bytes([1]) * 100]
            for sequence, payload in enumerate(payloads, start=1):
                source_header = TRANSPORT_HEADER.pack(
                    b"QPLIVE3\0", 3, TRANSPORT_HEADER.size, sequence,
                    100 + sequence, 100, 1, 100, 1, len(payload), 1, 0,
                )
                writer.write(
                    source_header, payload, time.monotonic_ns(), time.time_ns()
                )
            writer.close()
            summary = inspect_capture(path)
            self.assertEqual(summary["exact_nonconsecutive_replays"], 1)


if __name__ == "__main__":
    unittest.main()
