import time
import unittest

from label_capture import LabelSidecarRecorder


class ManualTongueReferenceTests(unittest.TestCase):
    def recorder(self) -> LabelSidecarRecorder:
        recorder = LabelSidecarRecorder.__new__(LabelSidecarRecorder)
        recorder._receive_error = None
        recorder.sample_count = 10
        recorder.schema_names = ["JawOpen", "TongueOut"]
        recorder.source_change_sequence = 0
        recorder.source_unchanged_ms = 60_000.0
        recorder.last_sample_monotonic_ns = time.monotonic_ns()
        return recorder

    def test_fresh_packets_allow_manual_pose_capture_with_static_face_values(self):
        recorder = self.recorder()
        self.assertFalse(recorder.source_is_live())
        self.assertTrue(recorder.manual_tongue_reference_ready())

    def test_missing_schema_or_stale_packets_still_block_capture(self):
        recorder = self.recorder()
        recorder.schema_names = ["JawOpen"]
        self.assertFalse(recorder.manual_tongue_reference_ready())
        recorder.schema_names.append("TongueOut")
        recorder.last_sample_monotonic_ns = time.monotonic_ns() - 2_000_000_000
        self.assertFalse(recorder.manual_tongue_reference_ready())


if __name__ == "__main__":
    unittest.main()
