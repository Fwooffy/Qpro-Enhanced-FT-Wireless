import json
import socket
import tempfile
import time
import unittest
from pathlib import Path

from label_capture import (
    LabelSidecarRecorder, NATIVE_TONGUE_MAPPING, inspect_sidecar,
    native_tongue_out, require_current_tongue_cache,
)


class LabelCaptureTests(unittest.TestCase):
    def test_records_schema_and_timestamped_sample(self) -> None:
        probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
        probe.close()

        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "labels.qplabel.jsonl"
            recorder = LabelSidecarRecorder(path, port=port)
            sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
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
            recorder.close()
            sender.close()

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
