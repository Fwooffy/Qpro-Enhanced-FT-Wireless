"""Replay source layouts through both real cache preparers using raw stills."""

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import numpy as np
import test_label_capture

import prepare_tongue_stills
import prepare_tongue_training
from capture_format import CaptureWriter, TRANSPORT_HEADER
from label_capture import NATIVE_TONGUE_MAPPING


class NativeTonguePreparationTests(unittest.TestCase):
    @staticmethod
    def make_capture(root: Path, *, manual: bool, legacy_ambiguous: bool = False):
        capture = root / "fixture.qpcap"
        labels = capture.with_suffix(".qplabel.jsonl")
        session = capture.with_suffix(".qpsession.json")
        names = test_label_capture.NativeTongueLayoutTests.names
        raw_values = [0.0] * 70
        raw_values[63], raw_values[68] = 0.15, 0.85
        records = [{"type": "schema", "names": names}]
        samples = []
        timestamps = [1_000_000_000 + index * 100_000_000 for index in range(4)]
        panels, mask = (2, 0x0C) if manual else (3, 0x1C)
        writer = CaptureWriter(capture)
        try:
            for index, (flags, source) in enumerate(((1, "VirtualDesktop"),
                    (3, "VirtualDesktop"), (3, "SteamLink"), (1, None))):
                if legacy_ambiguous:
                    flags, source = 3, None
                strip = np.concatenate([
                    np.full((400, 400), 10 * (view + 1) + index, np.uint8)
                    for view in range(panels)
                ], axis=1)
                header = TRANSPORT_HEADER.pack(
                    b"QPLIVE3\0", 3, TRANSPORT_HEADER.size, index + 1, 2,
                    panels * 400, 400, panels * 400, 1, strip.size, mask, 0,
                )
                writer.write(header, strip.tobytes(), timestamps[index], timestamps[index])
                records.append({"type": "sample", "arrivalMonotonicNs": timestamps[index],
                                "faceFlags": flags, "values": raw_values,
                                **({"trackingSource": source} if source else {})})
                samples.append({"frameIndex": index, "promptIndex": 0,
                                "targets": {"visibility": 1.0, "extension": 0.75}})
        finally:
            writer.close(completed=True)
        labels.write_text("\n".join(json.dumps(record) for record in records) + "\n",
                          encoding="utf-8")
        if manual:
            journal = {"sessionType": "tongue-stereo-corrections-v1", "completed": True,
                       "prompts": [{"name": "Tongue out"}], "samples": samples}
        else:
            journal = {"sessionType": "tongue-stereo-v1", "completed": True,
                       "steps": [{"name": "Tongue out", "instruction": "Hold",
                                  "seconds": 2.0, "targets": {"visibility": 1.0,
                                  "extension": 0.75}, "pattern": "free"}],
                       "events": [{"event": "step_started", "step": 0,
                                   "monotonicNs": timestamps[0] - 10_000_000},
                                  {"event": "step_finished", "step": 0,
                                   "monotonicNs": timestamps[-1] + 10_000_000}]}
        session.write_text(json.dumps(journal), encoding="utf-8")
        return capture, labels, session, np.asarray(raw_values, np.float32)

    @staticmethod
    def prepare(capture: Path, output: Path, *, manual: bool, source: str | None = None):
        module = prepare_tongue_stills if manual else prepare_tongue_training
        arguments = [module.__file__, str(capture), "--output", str(output), "--size", "128"]
        if source:
            arguments += ["--tracking-source", source]
        with mock.patch.object(sys, "argv", arguments), contextlib.redirect_stdout(io.StringIO()):
            return module.main()

    def test_real_preparers_use_source_flags_and_preserve_raw_expressions_and_views(self):
        for manual in (True, False):
            with self.subTest(manual=manual), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                capture, labels, session, raw = self.make_capture(root, manual=manual)
                sources = {path: path.read_bytes() for path in (capture, labels, session)}
                output = root / "prepared"
                self.assertEqual(self.prepare(capture, output, manual=manual), 0)
                np.testing.assert_allclose(np.load(output / "native_tongue_out.npy"),
                                           [0.85, 0.15, 0.85, 0.85])
                np.testing.assert_array_equal(np.load(output / "native_expressions.npy"),
                                              np.tile(raw, (4, 1)))
                images = np.load(output / "images.npy")
                for frame in range(4):
                    self.assertTrue(np.all(images[frame, 0] == 10 + frame))
                    self.assertTrue(np.all(images[frame, 1] == 20 + frame))
                metadata = json.loads((output / "metadata.json").read_text())
                self.assertEqual(metadata["nativeTongueMapping"], NATIVE_TONGUE_MAPPING)
                self.assertIsNone(metadata["nativeTongueSourceOverride"])
                for path, original in sources.items():
                    self.assertEqual(path.read_bytes(), original)

    def test_ambiguous_legacy_data_requires_explicit_source_before_replacing_cache(self):
        for manual in (True, False):
            with self.subTest(manual=manual), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                capture, labels, _session, _raw = self.make_capture(
                    root, manual=manual, legacy_ambiguous=True)
                output = root / "prepared"
                output.mkdir()
                previous = output / "images.npy"
                previous.write_bytes(b"keep previous cache until the new labels validate")
                label_bytes = labels.read_bytes()
                with self.assertRaisesRegex(ValueError, "TongueOut layout is ambiguous"):
                    self.prepare(capture, output, manual=manual)
                self.assertEqual(previous.read_bytes(),
                                 b"keep previous cache until the new labels validate")
                self.assertEqual(self.prepare(capture, output, manual=manual,
                                             source="SteamLink"), 0)
                np.testing.assert_allclose(np.load(output / "native_tongue_out.npy"), [0.85] * 4)
                metadata = json.loads((output / "metadata.json").read_text())
                self.assertEqual(metadata["nativeTongueSourceOverride"], "SteamLink")
                self.assertEqual(labels.read_bytes(), label_bytes)

    def test_invalid_alternate_schema_and_source_conflict_do_not_replace_cache(self):
        for manual in (True, False):
            with self.subTest(manual=manual), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                capture, labels, _session, _raw = self.make_capture(root, manual=manual)
                output = root / "prepared"
                self.prepare(capture, output, manual=manual)
                previous = {path.name: path.read_bytes() for path in output.iterdir()}
                with self.assertRaisesRegex(ValueError, "conflicts with the recorded"):
                    self.prepare(capture, output, manual=manual, source="SteamLink")
                records = [json.loads(line) for line in labels.read_text().splitlines()]
                records[0]["names"][63], records[0]["names"][68] = (
                    records[0]["names"][68], records[0]["names"][63])
                labels.write_text("\n".join(json.dumps(record) for record in records) + "\n")
                with self.assertRaisesRegex(ValueError, "full 70-channel"):
                    self.prepare(capture, output, manual=manual)
                self.assertEqual({path.name: path.read_bytes() for path in output.iterdir()}, previous)


if __name__ == "__main__":
    unittest.main()
