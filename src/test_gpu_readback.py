"""Readback ownership, completion, cancellation, and package integration checks."""

import math
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

from gpu_readback import GPUReadbackCancelled, copy_to_cpu


class GpuReadbackTests(unittest.TestCase):
    def fixture(self, ready=(False, False, True)):
        calls = []
        device = SimpleNamespace(type="cuda", index=1)
        source = SimpleNamespace(device=device, shape=(2, 7), dtype="uint8", cpu=mock.Mock())
        event = mock.Mock()
        event.record.side_effect = lambda stream: calls.append(("record", stream))
        event.query.side_effect = lambda: (calls.append(("query", None)), next(queries))[1]
        queries = iter(ready)
        targets = []

        def allocate(*args, **kwargs):
            target = mock.Mock()
            target.copy_.side_effect = lambda tensor, **kw: calls.append(("copy", tensor))
            targets.append(target)
            calls.append(("allocate", None))
            return target

        selected = mock.MagicMock()
        selected.__enter__.side_effect = lambda: calls.append(("device", device))
        torch = SimpleNamespace(
            empty=mock.Mock(side_effect=allocate),
            cuda=SimpleNamespace(device=mock.Mock(return_value=selected),
                                 current_stream=mock.Mock(return_value="cuda1-stream"),
                                 Event=mock.Mock(return_value=event)),
        )
        return source, torch, event, calls, targets

    def test_cpu_path_does_not_import_torch_or_initialize_events(self):
        cpu = mock.Mock(device=SimpleNamespace(type="cpu"))
        with mock.patch.dict("sys.modules", {"torch": None}):
            result = copy_to_cpu(cpu)
        self.assertIs(result, cpu.cpu.return_value)
        cpu.cpu.assert_called_once_with()

    def test_current_device_stream_is_recorded_after_owned_async_copy(self):
        source, torch, event, calls, targets = self.fixture()
        with (mock.patch.dict("sys.modules", {"torch": torch}),
              mock.patch("gpu_readback.time.perf_counter", side_effect=(0, .1, .2)),
              mock.patch("gpu_readback.time.sleep") as sleep):
            result = copy_to_cpu(source)
        self.assertIs(result, targets[0])
        self.assertEqual([entry[0] for entry in calls],
                         ["device", "allocate", "copy", "record", "query", "query", "query"])
        torch.cuda.device.assert_called_once_with(source.device)
        torch.cuda.current_stream.assert_called_once_with(source.device)
        torch.empty.assert_called_once_with((2, 7), dtype="uint8", device="cpu", pin_memory=True)
        targets[0].copy_.assert_called_once_with(source, non_blocking=True)
        event.record.assert_called_once_with("cuda1-stream")
        self.assertEqual(sleep.call_args_list, [mock.call(.001), mock.call(.001)])
        source.cpu.assert_not_called()
        targets[0].numpy.assert_not_called()

    def test_each_transfer_owns_its_buffer_and_cannot_overwrite_previous_result(self):
        source, torch, _event, _calls, targets = self.fixture(ready=(True, True))
        with mock.patch.dict("sys.modules", {"torch": torch}):
            first = copy_to_cpu(source)
            second = copy_to_cpu(source)
        self.assertIsNot(first, second)
        self.assertEqual(targets, [first, second])

    def test_cancelled_before_start_does_not_allocate_or_copy(self):
        source, torch, _event, _calls, _targets = self.fixture()
        with (mock.patch.dict("sys.modules", {"torch": torch}),
              self.assertRaises(GPUReadbackCancelled)):
            copy_to_cpu(source, cancelled=lambda: True)
        torch.empty.assert_not_called()
        source.cpu.assert_not_called()

    def test_stop_during_wait_never_reads_incomplete_result_or_blocks_in_cpu_copy(self):
        source, torch, event, _calls, targets = self.fixture(ready=(False,))
        cancelled = mock.Mock(side_effect=(False, False, True))
        with (mock.patch.dict("sys.modules", {"torch": torch}),
              mock.patch("gpu_readback.time.sleep"), self.assertRaises(GPUReadbackCancelled)):
            copy_to_cpu(source, cancelled=cancelled)
        self.assertEqual(event.query.call_count, 1)
        source.cpu.assert_not_called()
        targets[0].numpy.assert_not_called()

    def test_stalled_event_has_a_finite_failure_without_reading_partial_output(self):
        source, torch, event, _calls, targets = self.fixture(ready=(False, False))
        with (mock.patch.dict("sys.modules", {"torch": torch}),
              mock.patch("gpu_readback.time.perf_counter", side_effect=(0, .2, 1)),
              mock.patch("gpu_readback.time.sleep") as sleep,
              self.assertRaisesRegex(TimeoutError, "readback timeout")):
            copy_to_cpu(source, timeout_seconds=1)
        self.assertEqual(event.query.call_count, 2)
        sleep.assert_called_once_with(.001)
        source.cpu.assert_not_called()
        targets[0].numpy.assert_not_called()

    def test_invalid_gpu_timeouts_do_not_allocate(self):
        for invalid in (0, -1, math.inf, math.nan):
            source, torch, _event, _calls, _targets = self.fixture()
            with (self.subTest(timeout=invalid), mock.patch.dict("sys.modules", {"torch": torch}),
                  self.assertRaises(ValueError)):
                copy_to_cpu(source, timeout_seconds=invalid)
            torch.empty.assert_not_called()

    def test_gpu_errors_propagate_for_existing_backend_failure_handling(self):
        source, torch, event, _calls, _targets = self.fixture()
        event.query.side_effect = RuntimeError("GPU disconnected")
        with (mock.patch.dict("sys.modules", {"torch": torch}),
              self.assertRaisesRegex(RuntimeError, "GPU disconnected")):
            copy_to_cpu(source)

    def test_helper_is_in_runtime_and_source_release_inventories(self):
        root = Path(__file__).resolve().parent
        for file in ("build-release.ps1", "build-github-source.ps1"):
            with self.subTest(file=file):
                self.assertIn('"gpu_readback.py"', (root/file).read_text(encoding="utf-8-sig"))


if __name__ == "__main__":
    unittest.main()
