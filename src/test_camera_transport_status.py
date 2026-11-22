"""Offline framing failure classification; no model or headset is started."""
import ast
import __future__
import io
import socket
import struct
import unittest
from pathlib import Path
from types import SimpleNamespace


class Stream:
    def __init__(self, pieces):
        self.pieces = iter(pieces)

    def recv_into(self, target):
        value = next(self.pieces)
        if callable(value):
            value = value()
        if isinstance(value, Exception):
            raise value
        target[:len(value)] = value
        return len(value)


class FakeClock:
    now = 0.0

    def monotonic(self):
        return self.now

    def after(self, elapsed, value):
        def advance():
            self.now += elapsed
            return value
        return advance


class CameraTransportStatusTests(unittest.TestCase):
    def setUp(self):
        self.tree = ast.parse(Path(__file__).with_name('receiver.py').read_text(encoding='utf-8'))
        names = {'StopRequested', 'CameraTransportDisconnected', 'check_stop_requested',
                 'receive_exact', 'camera_ids_from_mask'}
        constants = {'HEADER', 'MAGIC', 'CAMERA_WIDTH', 'CAMERA_HEIGHT', 'LIVE_WIRELESS_IDLE_SECONDS'}
        body = [node for node in self.tree.body
                if getattr(node, 'name', None) in names
                or (isinstance(node, ast.Assign) and ast.unparse(node.targets[0]) in constants)]
        self.clock = FakeClock()
        self.scope = {'socket': socket, 'struct': struct, 'time': self.clock}
        self.execute(body)

    def execute(self, body):
        subset = ast.fix_missing_locations(ast.Module(body=body, type_ignores=[]))
        exec(compile(subset, 'receiver.py', 'exec', flags=__future__.annotations.compiler_flag), self.scope)

    def test_partial_eof_is_typed_and_next_frame_is_fresh(self):
        receive = self.scope['receive_exact']
        for ending in (b'', ConnectionResetError(), ConnectionAbortedError(), BrokenPipeError()):
            with self.assertRaises(self.scope['CameraTransportDisconnected']):
                receive(Stream([b'old', ending]), 6)
            self.assertEqual(receive(Stream([b'fresh!']), 6), b'fresh!')

    def test_frames_retain_independent_read_only_storage(self):
        class Stream(io.BytesIO):
            recv_into = io.BytesIO.readinto

        receive = self.scope['receive_exact']
        stream = Stream(b'firstsecond')
        first = receive(stream, 5)
        second = receive(stream, 6)
        self.assertEqual(first, b'first')
        self.assertEqual(second, b'second')
        self.assertTrue(first.readonly)
        self.assertIsNot(first.obj, second.obj)

    def test_other_errors_are_not_recoverable(self):
        read = self.scope['receive_exact']
        for preview in (None, SimpleNamespace(running=True)):
            with self.subTest(wireless=preview is not None):
                with self.assertRaises(OSError) as result:
                    read(Stream([self.clock.after(25, OSError('unclassified'))]), 1, preview=preview, recover_idle=True)
                self.assertNotIsInstance(result.exception, self.scope['CameraTransportDisconnected'])

    def test_wireless_idle_expires_after_twenty_seconds_without_a_byte(self):
        polls = [self.clock.after(0.25, socket.timeout()) for _ in range(80)]
        read = self.scope['receive_exact']
        with self.assertRaisesRegex(self.scope['CameraTransportDisconnected'], 'no data for 20 seconds'):
            read(Stream(polls), 1, preview=SimpleNamespace(running=True), recover_idle=True)
        self.assertEqual(self.clock.now, 20.0)

    def test_wireless_partial_reads_renew_idle_time(self):
        read = self.scope['receive_exact']
        preview = SimpleNamespace(running=True)
        self.assertEqual(read(Stream([
            self.clock.after(19.75, socket.timeout()), self.clock.after(0.25, b'a'),
            self.clock.after(19.75, socket.timeout()), self.clock.after(0.25, b'b'),
        ]), 2, preview=preview, recover_idle=True), b'ab')

        with self.assertRaises(self.scope['CameraTransportDisconnected']):
            read(Stream([
                self.clock.after(5, b'a'), self.clock.after(19.75, socket.timeout()),
                self.clock.after(0.25, socket.timeout()),
            ]), 2, preview=preview, recover_idle=True)
        self.assertEqual(self.clock.now, 65.0)

    def test_wireless_idle_excludes_time_spent_between_reads(self):
        read = self.scope['receive_exact']
        preview = SimpleNamespace(running=True)
        self.assertEqual(read(Stream([b'a']), 1, preview=preview, recover_idle=True), b'a')
        self.clock.now += 45
        self.assertEqual(read(Stream([
            self.clock.after(0.25, socket.timeout()), b'b',
        ]), 1, preview=preview, recover_idle=True), b'b')

    def test_usb_and_captures_keep_existing_timeout_behaviour(self):
        read = self.scope['receive_exact']
        self.assertEqual(read(Stream([self.clock.after(25, socket.timeout()), b'a']), 1,
                              stop_file=SimpleNamespace(exists=lambda: False)), b'a')
        with self.assertRaises(socket.timeout):
            read(Stream([self.clock.after(25, socket.timeout())]), 1)

    def test_usb_waits_for_delayed_input_and_honours_console_quit(self):
        read = self.scope['receive_exact']
        preview = SimpleNamespace(running=True)
        self.assertEqual(read(Stream([self.clock.after(30, socket.timeout()), b'a']), 1,
                              preview=preview), b'a')

        def quit_on_usb():
            preview.running = False
            return socket.timeout()

        with self.assertRaises(self.scope['StopRequested']):
            read(Stream([quit_on_usb]), 1, preview=preview)

    def test_wireless_stop_wins_over_expired_idle_timeout(self):
        read = self.scope['receive_exact']
        for stop_at_hub in (True, False):
            with self.subTest(hub=stop_at_hub):
                preview = SimpleNamespace(running=True)
                stopped = False

                def request_stop():
                    nonlocal stopped
                    stopped = stop_at_hub
                    preview.running = stop_at_hub
                    return self.clock.after(20, socket.timeout())()

                with self.assertRaises(self.scope['StopRequested']):
                    read(Stream([request_stop]), 1, SimpleNamespace(exists=lambda: stopped),
                         preview=preview, recover_idle=True)

    def packet(self, sequence):
        width, height = self.scope['CAMERA_WIDTH'], self.scope['CAMERA_HEIGHT']
        header = self.scope['HEADER']
        payload = bytes([sequence]) * (width * height)
        return (header.pack(self.scope['MAGIC'], 3, header.size, sequence, sequence,
                            width, height, width, 1, len(payload), 1, 0), payload)

    def read_until_first_accepted_packet(self, recovered, pieces, stop_file=None):
        # Run the real pre-processing path without launching a model or preview.
        main = next(node for node in self.tree.body if getattr(node, 'name', None) == 'main')
        loop = next(node for node in ast.walk(main)
                    if isinstance(node, ast.While) and ast.unparse(node.test) == 'shared.running')
        start_metrics = next(index for index, node in enumerate(loop.body)
                             if isinstance(node, ast.Assign)
                             and ast.unparse(node.targets[0]) == 'payload_received_at')
        accepted = ast.parse('accepted.append((sequence, payload[0])); shared.running = False').body
        loop = ast.While(test=loop.test, body=loop.body[:start_metrics] + accepted, orelse=[])
        self.scope.update(discard_first_packet=recovered, connection=Stream(pieces), companion=None,
                          stop_file=stop_file, shared=SimpleNamespace(running=True),
                          arguments=SimpleNamespace(recover_idle_camera=False, tongue_still_calibration=False,
                                                    tongue_refinement_calibration=False),
                          pupil_tracker=None, accepted=[])
        self.execute([loop])
        return self.scope['accepted']

    def test_initial_connection_keeps_first_packet(self):
        self.assertEqual(self.read_until_first_accepted_packet(False, self.packet(1)), [(1, 1)])

    def test_recovered_connection_discard_precedes_processing(self):
        self.assertEqual(self.read_until_first_accepted_packet(True, self.packet(1) + self.packet(2)), [(2, 2)])

    def test_recovered_connection_checks_stop_while_waiting_for_fresh_packet(self):
        stopped = False

        def request_stop():
            nonlocal stopped
            stopped = True
            return socket.timeout()

        with self.assertRaises(self.scope['StopRequested']):
            self.read_until_first_accepted_packet(True, self.packet(1) + (request_stop,),
                                                  SimpleNamespace(exists=lambda: stopped))
        self.assertEqual(self.scope['accepted'], [])

    def test_recovered_connection_does_not_accept_a_partial_fresh_packet(self):
        header, payload = self.packet(2)
        with self.assertRaises(self.scope['CameraTransportDisconnected']):
            self.read_until_first_accepted_packet(True, self.packet(1) + (header, payload[:17], b''))
        self.assertEqual(self.scope['accepted'], [])


if __name__ == '__main__': unittest.main()
