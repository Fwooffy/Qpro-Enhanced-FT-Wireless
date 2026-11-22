import unittest
from unittest.mock import patch

from pupil_dilation import PUPIL_PACKET, PupilBroadcaster


class PupilBroadcastTests(unittest.TestCase):
    def send_at(self, timestamps):
        with patch("pupil_dilation.socket.socket") as make_socket, patch(
            "pupil_dilation.time.perf_counter", side_effect=timestamps
        ):
            output = PupilBroadcaster()
            for frame in range(len(timestamps)):
                output.send((4.0 + frame / 1000, None))
            output.close()
        return [PUPIL_PACKET.unpack(call.args[0])
                for call in make_socket.return_value.sendto.call_args_list]

    def test_jitter_at_camera_rate_does_not_drop_alternate_updates(self):
        times = [10 + i / 24 + (0.002 if i % 2 == 0 else -0.002) for i in range(240)]
        packets = self.send_at(times)
        self.assertEqual(len(packets), len(times) + 1)
        self.assertEqual([round(packet[4], 3) for packet in packets[:-1]],
                         [round(4.0 + i / 1000, 3) for i in range(len(times))])
        self.assertEqual(packets[-1][2], 0)  # Explicit Stop always clears both eyes.

    def test_fast_input_is_capped_and_a_pause_cannot_build_up_credit(self):
        first = [10 + i / 120 for i in range(1200)]
        after_pause = [50 + i / 120 for i in range(5)]
        packets = self.send_at(first + after_pause)
        initial = [p for p in packets[:-1] if p[4] < 5.1995]
        resumed = [p for p in packets[:-1] if p[4] >= 5.1995]
        self.assertTrue(239 <= len(initial) <= 241)
        self.assertTrue(1 <= len(resumed) <= 2)
        self.assertAlmostEqual(resumed[0][4], 5.2, places=4)
