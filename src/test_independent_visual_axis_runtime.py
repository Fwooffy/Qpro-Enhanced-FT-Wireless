import math
import queue
import struct
import sys
import unittest
from contextlib import ExitStack
from unittest import mock

import numpy as np

import independent_visual_axis_runtime as gaze_runtime
from independent_visual_axis_runtime import (
    PACKET_FORMAT,
    PACKET_MAGIC,
    PACKET_SIZE,
    PACKET_VERSION,
    calibrated_angles,
    encode_packet,
    vrcft_angles,
)
from native_raw_eye_probe import RawEyeSample


class RuntimeContractTests(unittest.TestCase):
    def test_v2_calibration_maps_detector_tags_to_physical_eyes(self):
        identity = {"coefficients": [[0.0, 0.0], [1.0, 0.0], [0.0, 1.0]]}
        calibration = {
            "left": identity,
            "right": identity,
            "detector_tag_mapping": {
                "physical_left": "trace_tag_1",
                "physical_right": "trace_tag_0",
            },
        }
        sample = RawEyeSample(
            pc_monotonic_ns=1,
            kernel_time_s=1.0,
            left_valid=True,
            right_valid=True,
            left_vector=(0.1, -0.2, 1.0),
            right_vector=(-0.3, 0.4, 1.0),
        )
        left, right = calibrated_angles(calibration, sample)
        self.assertTrue(np.allclose(left, sample.right_angles))
        self.assertTrue(np.allclose(right, sample.left_angles))

    def test_vrcft_conversion_does_not_mirror_calibrated_yaw(self):
        x, y = vrcft_angles(np.asarray([-30.0, 15.0]))
        self.assertTrue(math.isclose(x, math.radians(-30.0)))
        self.assertTrue(math.isclose(y, math.radians(15.0)))

    def test_packet_contract_and_validity_bits(self):
        packet = encode_packet(np.asarray([10.0, -5.0]), np.asarray([-20.0, 7.0]))
        self.assertEqual(len(packet), PACKET_SIZE)
        magic, version, flags, reserved, lx, ly, rx, ry = struct.unpack(
            PACKET_FORMAT, packet
        )
        self.assertEqual(magic, PACKET_MAGIC)
        self.assertEqual(version, PACKET_VERSION)
        self.assertEqual(flags, 3)
        self.assertEqual(reserved, 0)
        # The packet fields are VRCFT channel names, whose physical ownership
        # is reversed by the final VRChat boundary test.
        self.assertTrue(math.isclose(lx, math.radians(-20.0), abs_tol=1e-6))
        self.assertTrue(math.isclose(ly, math.radians(7.0), abs_tol=1e-6))
        self.assertTrue(math.isclose(rx, math.radians(10.0), abs_tol=1e-6))
        self.assertTrue(math.isclose(ry, math.radians(-5.0), abs_tol=1e-6))

    def test_packet_validity_bits_follow_crossed_vrcft_channels(self):
        packet = encode_packet(
            np.asarray([1.0, 2.0]), np.asarray([3.0, 4.0]),
            left_valid=True, right_valid=False,
        )
        _magic, _version, flags, *_values = struct.unpack(PACKET_FORMAT, packet)
        self.assertEqual(flags, 2)

    def test_direct_broadcaster_publishes_physical_left_with_invalid_right(self):
        sock = mock.Mock()
        with mock.patch.object(gaze_runtime.socket, "socket", return_value=sock):
            broadcaster = gaze_runtime.GazeBroadcaster(27275, physical_order=True)
        broadcaster.send(np.asarray([10.0, -5.0]), np.asarray([float("nan"), 2.0]),
                         left_valid=True, right_valid=False)
        packet, endpoint = sock.sendto.call_args.args
        self.assertEqual(endpoint, ("127.0.0.1", 27275))
        magic, version, flags, _, lx, ly, rx, ry = struct.unpack(PACKET_FORMAT, packet)
        self.assertEqual((magic, version, flags), (PACKET_MAGIC, PACKET_VERSION, 1))
        self.assertAlmostEqual(lx, math.radians(10.0))
        self.assertAlmostEqual(ly, math.radians(-5.0))
        self.assertEqual((rx, ry), (0.0, 0.0))

    def test_invalid_eye_resets_only_its_own_filter(self):
        eye_filter = gaze_runtime.IndependentEyeFilter(2)
        eye_filter.update(np.asarray([1.0, 2.0]), np.asarray([3.0, 4.0]), 1.0)
        left, right = eye_filter.update(np.asarray([1.0, 2.0]), np.full(2, np.nan), 1.02,
                                        right_valid=False)
        np.testing.assert_array_equal(left, [1.0, 2.0])
        np.testing.assert_array_equal(right, [0.0, 0.0])
        self.assertEqual(len(eye_filter.left_history), 2)
        self.assertEqual(len(eye_filter.right_history), 0)
        _, right = eye_filter.update(np.asarray([1.0, 2.0]), np.asarray([8.0, 9.0]), 1.04)
        np.testing.assert_array_equal(right, [8.0, 9.0])

    def test_headless_runtime_handles_valid_and_idle_loops(self):
        self.assertEqual(self._run_with_one_sample(headless=True), 0)

    def test_visual_runtime_handles_valid_and_idle_loops(self):
        self.assertEqual(self._run_with_one_sample(headless=False), 0)

    def test_indefinite_no_window_outputs_until_stop_and_cleans_up(self):
        for output_switch in ("--output-vrcft", "--direct-vrchat"):
            with self.subTest(output_switch=output_switch):
                self.assertEqual(self._run_with_one_sample(headless=False, no_window=True,
                                                          output_switch=output_switch), 0)

    def test_runtime_remaps_detector_validity_before_direct_output(self):
        self.assertEqual(self._run_with_one_sample(headless=False, no_window=True,
                                                  output_switch="--direct-vrchat",
                                                  physical_left_only=True), 0)

    def test_preview_throttles_rendering_without_waiting_for_another_sample(self):
        self.assertEqual(self._run_with_one_sample(headless=False, check_throttle=True), 0)

    def test_preview_rejects_nonfinite_or_out_of_range_rate(self):
        for rate in ("0", "61", "nan", "inf"):
            with self.subTest(rate=rate), mock.patch.object(sys, "argv", [
                "gaze", "--adb", "test", "--calibration", "test", "--preview-fps", rate
            ]), self.assertRaisesRegex(ValueError, "Preview FPS"):
                gaze_runtime.main()

    def _run_with_one_sample(self, *, headless: bool, no_window: bool = False,
                             check_throttle: bool = False, output_switch: str = "--output-vrcft",
                             physical_left_only: bool = False) -> int:
        sample = RawEyeSample(
            pc_monotonic_ns=1,
            kernel_time_s=1.0,
            left_valid=not physical_left_only,
            right_valid=True,
            left_vector=(0.1, 0.0, 1.0),
            right_vector=(-0.1, 0.0, 1.0),
        )
        reader = mock.Mock()
        reader.samples = queue.Queue()
        reader.samples.put(sample)
        reader.errors = queue.Queue()
        calibration = {"quality_gate": {"convergence_pass": False}, "detector_tag_mapping": {
            "physical_left": "trace_tag_1", "physical_right": "trace_tag_0",
        }}
        arguments = ["gaze", "--adb", "test", "--calibration", "test"]
        if headless:
            arguments.extend(["--headless-seconds", "0.03"])
        if no_window:
            arguments.extend(["--no-window", output_switch, "--stop-file", "test-stop"])
        if check_throttle:
            arguments.extend(["--stop-file", "test-stop"])

        with ExitStack() as patches:
            patches.enter_context(mock.patch.object(sys, "argv", arguments))
            patches.enter_context(mock.patch.object(gaze_runtime, "load_calibration", return_value=calibration))
            patches.enter_context(mock.patch.object(gaze_runtime, "RawTraceEyeReader", return_value=reader))
            patches.enter_context(mock.patch.object(
                gaze_runtime, "calibrated_angles",
                return_value=(np.array([1.0, 2.0]), np.array([-1.0, 2.0])),
            ))
            patches.enter_context(mock.patch.object(
                gaze_runtime, "IndependentEyeFilter",
                return_value=mock.Mock(update=mock.Mock(return_value=(
                    np.array([1.0, 2.0]), np.array([-1.0, 2.0])
                ))),
            ))
            if not headless:
                ui = {name: patches.enter_context(mock.patch.object(gaze_runtime.cv2, name))
                      for name in ("namedWindow", "resizeWindow", "imshow", "destroyWindow", "waitKey")}
                ui["waitKey"].side_effect = [-1, ord("q")]
                patches.enter_context(mock.patch.object(gaze_runtime, "put_text"))
                patches.enter_context(mock.patch.object(gaze_runtime, "gaze_panel"))
                if no_window:
                    patches.enter_context(mock.patch.object(gaze_runtime.Path, "exists", side_effect=[False, False, True]))
                    broadcaster = mock.Mock()
                    create_broadcaster = patches.enter_context(
                        mock.patch.object(gaze_runtime, "GazeBroadcaster", return_value=broadcaster)
                    )
                if check_throttle:
                    patches.enter_context(mock.patch.object(gaze_runtime.Path, "exists", side_effect=[False] * 100 + [True]))
                    # Advance one millisecond per loop, keeping this check
                    # independent of the scheduler and physical display speed.
                    patches.enter_context(mock.patch.object(gaze_runtime.time, "monotonic", side_effect=(i * 0.001 for i in range(110))))
                    patches.enter_context(mock.patch.object(gaze_runtime.time, "sleep"))
                    ui["waitKey"].side_effect = None
                    ui["waitKey"].return_value = -1
            result = gaze_runtime.main()

            if no_window:
                create_broadcaster.assert_called_once_with(
                    27275, physical_order=output_switch == "--direct-vrchat"
                )
                for call in ui.values():
                    call.assert_not_called()
                broadcaster.send.assert_called_once()
                self.assertEqual(broadcaster.send.call_args.args[2:], (True, not physical_left_only))
                broadcaster.close.assert_called_once_with()
            if check_throttle:
                self.assertGreaterEqual(ui["imshow"].call_count, 2)
                self.assertLessEqual(ui["imshow"].call_count, 3)

        reader.start.assert_called_once_with()
        reader.close.assert_called_once_with()
        return result


if __name__ == "__main__":
    unittest.main()
