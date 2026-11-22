import collections
import json
import random
import socket
import tempfile
import threading
import time
import unittest
from contextlib import ExitStack
from pathlib import Path
from unittest import mock

from label_capture import (
    LabelSidecarRecorder, NATIVE_TONGUE_MAPPING, inspect_sidecar,
    native_tongue_out, require_current_tongue_cache,
)


class LabelFailureTests(unittest.TestCase):
    def recorder(self):
        recorder = LabelSidecarRecorder.__new__(LabelSidecarRecorder)
        recorder._lifecycle_lock = threading.Lock()
        recorder._receiver_finished = True
        recorder._deferred_file_close = False
        return recorder

    def test_healthy_close_flushes_samples_before_end_and_is_idempotent(self):
        recorder = self.recorder()
        recorder._running, recorder._receive_error = True, None
        recorder.sample_count, recorder.invalid_count = 4, 1
        recorder._socket, recorder._thread, recorder._file = mock.Mock(), mock.Mock(), mock.Mock()
        events = []
        recorder._file.flush.side_effect = lambda: events.append("flush")
        recorder._write = mock.Mock(side_effect=lambda value: events.append(value["type"]))
        recorder.close()
        recorder.close()
        self.assertEqual(events, ["flush", "end", "flush"])
        recorder._file.close.assert_called_once()
        recorder._socket.close.assert_called_once()

    def test_bind_failure_closes_exclusive_sidecar_and_socket(self):
        handle, sock = mock.Mock(), mock.Mock()
        sock.bind.side_effect = OSError("address occupied")
        with (tempfile.TemporaryDirectory() as directory,
              mock.patch.object(Path, "open", return_value=handle),
              mock.patch("label_capture.socket.socket", return_value=sock),
              self.assertRaisesRegex(OSError, "address occupied")):
            LabelSidecarRecorder(Path(directory) / "fixture.jsonl")
        sock.close.assert_called_once()
        handle.close.assert_called_once()

    def test_ingest_disk_failure_is_reported_without_successful_end(self):
        recorder = self.recorder()
        recorder._running = True
        recorder._receive_error = None
        recorder._socket, recorder._thread, recorder._file = mock.Mock(), mock.Mock(), mock.Mock()
        recorder._write = mock.Mock()
        error = OSError("disk full")
        recorder._receive_loop = mock.Mock(side_effect=error)
        recorder._run_receive_loop()
        self.assertIsNone(recorder.label_age_seconds())
        with self.assertRaises(OSError) as failed:
            recorder.close()
        self.assertIs(failed.exception, error)
        recorder._write.assert_not_called()
        recorder._file.close.assert_called_once()
        recorder._socket.close.assert_called_once()

    def test_unexpected_socket_error_invalidates_freshness_and_preflight(self):
        recorder = self.recorder()
        recorder._running, recorder._receive_error = True, None
        recorder._receiver_finished = False
        recorder._socket, recorder._thread, recorder._file = mock.Mock(), mock.Mock(), mock.Mock()
        error = OSError("receiver socket failed")
        recorder._socket.recvfrom.side_effect = error
        recorder._write = mock.Mock()
        recorder.last_sample_monotonic_ns = time.monotonic_ns()
        recorder.sample_count = 20
        recorder.schema_names = ["TongueOut"]
        recorder.source_change_sequence, recorder.source_unchanged_ms = 10, 0.0
        self.assertTrue(recorder.manual_tongue_reference_ready())
        self.assertTrue(recorder.source_is_live())
        recorder._run_receive_loop()
        self.assertIsNone(recorder.label_age_seconds())
        self.assertFalse(recorder.manual_tongue_reference_ready())
        self.assertFalse(recorder.source_is_live())
        with self.assertRaises(OSError) as failed:
            recorder.close()
        self.assertIs(failed.exception, error)
        recorder._write.assert_not_called()
        recorder._file.close.assert_called_once()

    def test_socket_error_from_deliberate_stop_is_normal_cleanup(self):
        recorder = self.recorder()
        recorder._running, recorder._receive_error = True, None
        recorder._receiver_finished = False
        recorder._socket, recorder._file = mock.Mock(), mock.Mock()
        recorder.sample_count, recorder.invalid_count = 2, 0
        recorder._write = mock.Mock()

        receiving, closed = threading.Event(), threading.Event()

        def stopped_socket(_size):
            receiving.set()
            if not closed.wait(5):
                raise AssertionError("test socket was not closed")
            raise OSError("socket closed by Stop")

        recorder._socket.recvfrom.side_effect = stopped_socket
        recorder._socket.close.side_effect = closed.set
        recorder._thread = threading.Thread(target=recorder._run_receive_loop)
        recorder._thread.start()
        try:
            self.assertTrue(receiving.wait(2))
            recorder.close()
        finally:
            closed.set()
            recorder._thread.join(5)
        self.assertIsNone(recorder._receive_error)
        self.assertEqual(recorder._write.call_args.args[0]["type"], "end")
        recorder._file.close.assert_called_once()

    def test_final_flush_failure_closes_file_without_completion_marker(self):
        recorder = self.recorder()
        recorder._running, recorder._receive_error = True, None
        recorder._socket, recorder._thread, recorder._file = mock.Mock(), mock.Mock(), mock.Mock()
        recorder._file.flush.side_effect = OSError("disk full")
        recorder._write = mock.Mock()
        with self.assertRaisesRegex(OSError, "disk full"):
            recorder.close()
        recorder._write.assert_not_called()
        recorder._file.close.assert_called_once()

    def test_join_timeout_does_not_race_file_write_and_defers_close_to_receiver(self):
        recorder = self.recorder()
        recorder._running, recorder._receive_error = True, None
        recorder._receiver_finished = False
        recorder._socket, recorder._file = mock.Mock(), mock.Mock()
        recorder._write = mock.Mock()
        writing, resume = threading.Event(), threading.Event()

        def blocked_write():
            writing.set()
            if not resume.wait(5):
                raise AssertionError("test write never released")

        recorder._receive_loop = blocked_write
        recorder._thread = threading.Thread(target=recorder._run_receive_loop)
        recorder._thread.start()
        try:
            self.assertTrue(writing.wait(2))
            with mock.patch.object(recorder._thread, "join"), self.assertRaisesRegex(TimeoutError, "unconfirmed"):
                recorder.close()
            recorder._write.assert_not_called()
            recorder._file.flush.assert_not_called()
            recorder._file.close.assert_not_called()
        finally:
            resume.set()
            recorder._thread.join(5)
        self.assertFalse(recorder._thread.is_alive())
        recorder._file.close.assert_called_once()
        recorder._write.assert_not_called()


class LabelCaptureTests(unittest.TestCase):
    def test_records_schema_and_timestamped_sample(self) -> None:
        probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
        probe.close()

        with tempfile.TemporaryDirectory() as directory, ExitStack() as resources:
            path = Path(directory) / "labels.qplabel.jsonl"
            recorder = LabelSidecarRecorder(path, port=port)
            resources.callback(recorder.close)
            sender = resources.enter_context(socket.socket(socket.AF_INET, socket.SOCK_DGRAM))
            sender.sendto(
                json.dumps(
                    {"v": 1, "type": "schema", "names": ["JawOpen", "BrowInnerUpLeft"],
                     "trackingSource": "VirtualDesktop"}
                ).encode(),
                ("127.0.0.1", port),
            )
            sender.sendto(
                json.dumps(
                    {
                        "v": 1,
                        "type": "sample",
                        "trackingSource": "VirtualDesktop",
                        "sequence": 1,
                        "qpc": 123456,
                        "qpcFrequency": 10_000_000,
                        "utcUnixMs": 1_700_000_000_000,
                        "sourceChangeSequence": 4,
                        "sourceUnchangedMs": 25.0,
                        "values": [0.25, 0.75],
                    }
                ).encode(),
                ("127.0.0.1", port),
            )
            deadline = time.monotonic() + 1.0
            while recorder.sample_count < 1 and time.monotonic() < deadline:
                time.sleep(0.01)
            self.assertTrue(recorder.source_is_live())
            nearest = recorder.nearest_sample(time.monotonic_ns())
            self.assertIsNotNone(nearest)
            self.assertEqual(nearest["values"], [0.25, 0.75])
            self.assertEqual(nearest["trackingSource"], "VirtualDesktop")
            resources.close()

            summary = inspect_sidecar(path)
            self.assertTrue(summary["completed"])
            self.assertEqual(summary["schemas"], 1)
            self.assertEqual(summary["samples"], 1)
            self.assertEqual(summary["expressions"], 2)
            self.assertEqual(summary["invalid_lines"], 0)
            records = [json.loads(line) for line in path.read_text().splitlines()]
            self.assertEqual([record["trackingSource"] for record in records
                              if record["type"] in ("schema", "sample")],
                             ["VirtualDesktop", "VirtualDesktop"])

    def test_steam_source_is_retained_and_conflicting_sample_is_rejected(self):
        probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
        probe.close()
        with tempfile.TemporaryDirectory() as directory:
            recorder = LabelSidecarRecorder(Path(directory) / "steam.jsonl", port=port)
            sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try:
                values = [0.0] * 70
                values[63], values[68] = 0.15, 0.85
                schema = {"v": 1, "type": "schema", "trackingSource": "SteamLink",
                          "names": NativeTongueLayoutTests.names}
                sample = {"v": 1, "type": "sample", "trackingSource": "SteamLink",
                          "sequence": 1, "qpc": 1, "qpcFrequency": 10_000_000,
                          "utcUnixMs": 1, "faceFlags": 3, "values": values}
                for message in (schema, sample, {**sample, "trackingSource": "VirtualDesktop"}):
                    sender.sendto(json.dumps(message).encode(), ("127.0.0.1", port))
                deadline = time.monotonic() + 1.0
                while recorder.invalid_count < 1 and time.monotonic() < deadline:
                    time.sleep(0.01)
                self.assertEqual(recorder.sample_count, 1)
                self.assertEqual(recorder.invalid_count, 1)
                nearest = recorder.nearest_sample(time.monotonic_ns())
                self.assertEqual(nearest["trackingSource"], "SteamLink")
                self.assertEqual(native_tongue_out(nearest, recorder.schema_names), 0.85)
                untagged = {key: value for key, value in sample.items() if key != "trackingSource"}
                sender.sendto(json.dumps(untagged).encode(), ("127.0.0.1", port))
                deadline = time.monotonic() + 1.0
                while recorder.sample_count < 2 and time.monotonic() < deadline:
                    time.sleep(0.01)
                nearest = recorder.nearest_sample(time.monotonic_ns())
                self.assertNotIn("trackingSource", nearest)
                with self.assertRaisesRegex(ValueError, "TongueOut layout is ambiguous"):
                    native_tongue_out(nearest, recorder.schema_names)
            finally:
                recorder.close()
                sender.close()


class NearestLabelSampleTests(unittest.TestCase):
    @staticmethod
    def recorder(times):
        # Exercise lookup without starting its UDP receiver, writing a file,
        # or depending on an installed tracking process.
        recorder = LabelSidecarRecorder.__new__(LabelSidecarRecorder)
        recorder._recent_lock = threading.Lock()
        recorder._recent_samples = collections.deque(
            ({"arrivalMonotonicNs": timestamp, "sequence": index, "values": [index]}
             for index, timestamp in enumerate(times)), maxlen=512,
        )
        return recorder

    def test_endpoints_duplicates_and_equal_distance_keep_earliest_copy(self):
        empty = self.recorder([])
        self.assertIsNone(empty.nearest_sample(0))
        for times, target, expected_sequence in (
            ([10], 0, 0), ([10], 20, 0),
            ([10, 10, 20, 20], 0, 0),
            ([10, 10, 20, 20], 10, 0),
            ([10, 10, 20, 20], 15, 0),
            ([10, 10, 20, 20], 16, 2),
            ([10, 10, 20, 20], 20, 2),
            ([10, 10, 20, 20], 30, 2),
            ([10, 10, 10], 30, 0),
            ([2**70, 2**70 + 2], 2**70 + 1, 0),
        ):
            with self.subTest(times=times, target=target):
                recorder = self.recorder(times)
                result = recorder.nearest_sample(target)
                original = recorder._recent_samples[expected_sequence]
                self.assertEqual(result, original)
                self.assertIsNot(result, original)
                self.assertIs(result["values"], original["values"])
                result["sequence"] = "changed copy"
                self.assertEqual(original["sequence"], expected_sequence)

    def test_seeded_monotonic_deques_match_min_including_eviction(self):
        rng = random.Random(20261120)
        for case in range(200):
            timestamp = 10**15
            times = []
            for _ in range(rng.randrange(1, 700)):
                timestamp += rng.randrange(0, 8)
                times.append(timestamp)
            recorder = self.recorder(times)
            retained = recorder._recent_samples
            queries = [times[0] - 100, times[-1] + 100]
            queries.extend(rng.randrange(times[0] - 5, times[-1] + 6) for _ in range(20))
            queries.extend(times[index] for index in (0, len(times) // 2, len(times) - 1))
            for target in queries:
                with self.subTest(case=case, target=target):
                    expected = min(retained, key=lambda value: abs(int(value["arrivalMonotonicNs"]) - target))
                    result = recorder.nearest_sample(target)
                    self.assertEqual(result, expected)
                    self.assertIsNot(result, expected)
                    self.assertIs(result["values"], expected["values"])


class NativeTongueLayoutTests(unittest.TestCase):
    names = [
        "BrowLowererL", "BrowLowererR", "CheekPuffL", "CheekPuffR",
        "CheekRaiserL", "CheekRaiserR", "CheekSuckL", "CheekSuckR",
        "ChinRaiserB", "ChinRaiserT", "DimplerL", "DimplerR",
        "EyesClosedL", "EyesClosedR", "EyesLookDownL", "EyesLookDownR",
        "EyesLookLeftL", "EyesLookLeftR", "EyesLookRightL", "EyesLookRightR",
        "EyesLookUpL", "EyesLookUpR", "InnerBrowRaiserL", "InnerBrowRaiserR",
        "JawDrop", "JawSidewaysLeft", "JawSidewaysRight", "JawThrust",
        "LidTightenerL", "LidTightenerR", "LipCornerDepressorL", "LipCornerDepressorR",
        "LipCornerPullerL", "LipCornerPullerR", "LipFunnelerLb", "LipFunnelerLt",
        "LipFunnelerRb", "LipFunnelerRt", "LipPressorL", "LipPressorR",
        "LipPuckerL", "LipPuckerR", "LipStretcherL", "LipStretcherR",
        "LipSuckLb", "LipSuckLt", "LipSuckRb", "LipSuckRt", "LipTightenerL", "LipTightenerR",
        "LipsToward", "LowerLipDepressorL", "LowerLipDepressorR", "MouthLeft", "MouthRight",
        "NoseWrinklerL", "NoseWrinklerR", "OuterBrowRaiserL", "OuterBrowRaiserR",
        "UpperLidRaiserL", "UpperLidRaiserR", "UpperLipRaiserL", "UpperLipRaiserR",
        "TongueTipInterdental", "TongueTipAlveolar", "TongueFrontDorsalPalate",
        "TongueMidDorsalPalate", "TongueBackDorsalVelar", "TongueOut", "TongueRetreat"]

    def test_source_flags_choose_correct_scalar_without_remapping_expressions(self):
        values = [0.0] * 70
        values[63], values[68] = 0.15, 0.85
        for flags, expected in ((1, 0.85), (3, 0.15), (0, None), (2, None)):
            sample = {"faceFlags": flags, "values": list(values),
                      "trackingSource": "VirtualDesktop"}
            original = json.dumps(sample)
            self.assertEqual(native_tongue_out(sample, self.names), expected)
            self.assertEqual(json.dumps(sample), original)
        self.assertEqual(native_tongue_out({"values": values}, self.names), 0.85)
        # Old Steam Link producers also set flags=3, but never used VD's
        # alternate tongue slots. Source identity takes precedence over bit 2.
        self.assertEqual(native_tongue_out({"faceFlags": 3, "values": values,
                                           "trackingSource": "SteamLink"}, self.names), 0.85)

    def test_unknown_source_bit2_requires_explicit_recording_source(self):
        values = [0.0] * 70
        values[63], values[68] = 0.15, 0.85
        sample = {"faceFlags": 3, "values": values}
        original = json.dumps(sample)
        with self.assertRaisesRegex(ValueError, "TongueOut layout is ambiguous"):
            native_tongue_out(sample, self.names)
        self.assertEqual(native_tongue_out(sample, self.names,
                                          tracking_source="VirtualDesktop"), 0.15)
        self.assertEqual(native_tongue_out(sample, self.names,
                                          tracking_source="SteamLink"), 0.85)
        self.assertEqual(json.dumps(sample), original)
        with self.assertRaisesRegex(ValueError, "conflicts with the recorded"):
            native_tongue_out({**sample, "trackingSource": "SteamLink"}, self.names,
                             tracking_source="VirtualDesktop")
        with self.assertRaisesRegex(ValueError, "unsupported trackingSource"):
            native_tongue_out({**sample, "trackingSource": "unknown"}, self.names)

    def test_alternate_requires_known_full_tongue_tail_and_matching_values(self):
        values = [0.0] * 70
        invalid = [self.names[:69], self.names[1:] + self.names[:1],
                   self.names[:63] + list(reversed(self.names[63:])),
                   self.names[:62] + [self.names[0]] + self.names[63:]]
        for names in invalid:
            with self.subTest(names=names), self.assertRaisesRegex(ValueError, "full 70-channel"):
                native_tongue_out({"faceFlags": 3, "values": values,
                                  "trackingSource": "VirtualDesktop"}, names)
        with self.assertRaisesRegex(ValueError, "match its channel schema"):
            native_tongue_out({"faceFlags": 3, "values": values[:69],
                              "trackingSource": "VirtualDesktop"}, self.names)

    def test_old_cache_requires_regeneration_not_model_replacement(self):
        for metadata in ({}, {"nativeTongueMapping": "legacy"}):
            with self.subTest(metadata=metadata), self.assertRaisesRegex(ValueError, "Regenerate the cache"):
                require_current_tongue_cache(metadata)
        require_current_tongue_cache({"nativeTongueMapping": NATIVE_TONGUE_MAPPING})


if __name__ == "__main__":
    unittest.main()
