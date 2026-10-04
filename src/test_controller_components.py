"""Offline supervisor/protocol tests. No headset, SteamVR or installation used."""
from pathlib import Path
import contextlib
import importlib.util
import json
import math
import tempfile
import threading
import unittest
from unittest.mock import patch
from types import SimpleNamespace
import io

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('controller_components', HERE / 'controller-input' / 'manage.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class FakeWorker:
    def __init__(self, output, code=1, pid=123):
        self.stdin = io.BytesIO()
        self.stdout = io.StringIO(output) if isinstance(output, str) else output
        self.pid, self.returncode = pid, code

    def poll(self):
        return self.returncode


class GatedOutput:
    def __init__(self, first='', final='HANDS_CLEANUP {"confirmed": true, "problems": []}\n'):
        self.first, self.final = first, final
        self.waiting, self.release = threading.Event(), threading.Event()
        self.closed = False

    def __iter__(self):
        if self.first:
            yield self.first
        self.waiting.set()
        if not self.release.wait(2):
            raise OSError('Fixture output was not released.')
        yield self.final

    def close(self):
        self.closed = True


class ComponentTests(unittest.TestCase):
    def run_workers(self, children, hands=True, touchpad=False, stopped=False):
        self.activity = io.StringIO()
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            hands_path, _ = module.managed_paths(local)
            (hands_path / '.venv' / 'Scripts').mkdir(parents=True)
            (hands_path / '.venv' / 'Scripts' / 'python.exe').touch()
            (hands_path / 'ready.json').touch()
            args = SimpleNamespace(hands=hands, touchpad=touchpad, parent_stdin=False,
                                   stop_file=str(local / 'stop'), root=str(HERE), mode='trackpad')
            parent_closed = threading.Event()
            if stopped:
                parent_closed.set()
            with patch.object(module.subprocess, 'Popen', side_effect=children) as launch, \
                 patch.object(module, 'parent_closed_event', return_value=parent_closed), \
                 contextlib.redirect_stdout(self.activity):
                self.launch = launch
                module.run(args, local, Path('fake-adb'), 'quest')

    def aggregate(self):
        lines = [line for line in self.activity.getvalue().splitlines()
                 if line.startswith('CONTROLLER_CLEANUP ')]
        return json.loads(lines[-1].partition(' ')[2])

    def packet(self, sequence=1, flags=3, x=.25, force=.7):
        return module.PACKET.pack(b'QPTP', 1, 64, sequence, 100, flags, x, -.5, force, 50,
                                  1, 0, 0, 0, 0)

    def test_roundtrip_and_replay(self):
        self.assertEqual(len(self.packet()), 64)
        self.assertEqual(module.validate_packet(self.packet(), 0), 1)
        with self.assertRaises(ValueError):
            module.validate_packet(self.packet(), 1)

    def test_invalid_packet_never_becomes_input(self):
        for packet in (self.packet(flags=4), self.packet(flags=2), self.packet(x=math.nan),
                       self.packet(x=1.01), self.packet(force=-.1), b'x' * 64,
                       self.packet() + b'x', self.packet()[:-1]):
            with self.subTest(packet=packet), self.assertRaises(ValueError):
                module.validate_packet(packet, 0)

    def test_atomic_settings_preserve_other_tuning(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            addon = Path(temporary)
            module.atomic_json(addon / 'qpro-owner.json', {'format': module.OWNER_FORMAT})
            module.atomic_json(addon / 'resources' / 'settings.json', {'smoothingMs': 25, 'enabled': True})
            module.set_input_enabled(addon, False, 'joystick')
            saved = json.loads((addon / 'resources' / 'settings.json').read_text())
            self.assertEqual(saved, {'smoothingMs': 25, 'enabled': False, 'mode': 'joystick'})

    def test_foreign_addon_settings_are_untouched(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            path = Path(temporary)
            with self.assertRaises(RuntimeError):
                module.set_input_enabled(path, True)
            self.assertFalse((path / 'resources').exists())

    def test_usb_selection_requires_exactly_one(self):
        for text in ('List of devices attached\n', 'a device\nb device\n', 'wifi:5555 device\n'):
            with patch.object(module, 'command', return_value=text), self.assertRaises(RuntimeError):
                module.select_target(Path('fake-adb'), None)
        with patch.object(module, 'command', return_value='a device\nb unauthorized\n'):
            self.assertEqual(module.select_target(Path('fake-adb'), None), 'a')

    def test_target_not_interpolated_into_shell(self):
        with patch.object(module, 'command') as execute, self.assertRaises(RuntimeError):
            module.select_target(Path('fake-adb'), 'serial;bad')
        execute.assert_not_called()

    def test_existing_stop_refuses_workers(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            stop = Path(temporary) / 'stop'
            stop.touch()
            args = SimpleNamespace(hands=True, touchpad=False, stop_file=str(stop))
            with patch.object(module.subprocess, 'Popen') as launch, self.assertRaises(RuntimeError):
                module.run(args, Path(temporary), Path('fake-adb'), 'quest')
            launch.assert_not_called()

    def test_stop_write_failure_still_closes_worker_pipe(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            hands, _ = module.managed_paths(local)
            (hands / '.venv' / 'Scripts').mkdir(parents=True)
            (hands / '.venv' / 'Scripts' / 'python.exe').touch()
            (hands / 'ready.json').touch()
            child = FakeWorker('HANDS_CLEANUP {"confirmed": true, "problems": []}\n')
            args = SimpleNamespace(hands=True, touchpad=False, parent_stdin=False,
                                   stop_file=str(local / 'stop'), root=str(HERE))
            with patch.object(module.subprocess, 'Popen', return_value=child), \
                 patch.object(module.Path, 'write_text', side_effect=PermissionError('test unwritable')), \
                 contextlib.redirect_stdout(io.StringIO()), \
                 self.assertRaisesRegex(RuntimeError, 'cleanup was not confirmed'):
                module.run(args, local, Path('fake-adb'), 'quest')
            self.assertTrue(child.stdin.closed)
            self.assertTrue(child.stdout.closed)

    def test_confirmed_cleanup_preserves_startup_and_compatibility_failure(self):
        for code, event in ((1, 'HANDS_FAILED'), (4, 'HANDS_INCOMPATIBLE')):
            with self.subTest(code=code):
                output = ('HANDS_CLEANUP {"confirmed": true, "problems": []}\n'
                          + event + ' {"error": "bind port 27062: 10048"}\n')
                child = FakeWorker(output, code)
                with self.assertRaisesRegex(RuntimeError, 'worker exited before Stop'):
                    self.run_workers([child])
                self.assertIn(output, self.activity.getvalue())
                self.assertTrue(self.aggregate()['confirmed'])
                self.assertEqual(self.aggregate()['reader'], 'stopped')
                self.assertTrue(child.stdin.closed)
                self.assertTrue(child.stdout.closed)
                options = self.launch.call_args.kwargs
                self.assertEqual(options['stdout'], module.subprocess.PIPE)
                self.assertEqual(options['stderr'], module.subprocess.STDOUT)
                self.assertTrue(options['text'])

    def test_missing_malformed_and_failed_cleanup_are_not_confirmed(self):
        success = 'HANDS_CLEANUP {"confirmed": true, "problems": []}\n'
        cases = ('', 'HANDS_CLEANUP invalid-json\n', 'HANDS_CLEANUP []\n',
                 'HANDS_CLEANUP {"confirmed": 1, "problems": []}\n',
                 'HANDS_CLEANUP {"confirmed": true, "problems": ["restore failed"]}\n',
                 'HANDS_CLEANUP {"confirmed": false, "problems": ["restore failed"]}\n',
                 success + 'HANDS_CLEANUP_FAILED {"error": "watchdog"}\n',
                 'HANDS_CLEANUP_FAILED {"error": "restore failed"}\n' + success,
                 'HANDS_CLEANUP invalid-json\n' + success,
                 'HANDS_CLEANUP {"confirmed": false, "problems": []}\n' + success)
        for output in cases:
            with self.subTest(output=output):
                with self.assertRaisesRegex(RuntimeError, 'cleanup was not confirmed'):
                    self.run_workers([FakeWorker(output)])
                self.assertFalse(self.aggregate()['confirmed'])
                self.assertEqual(self.aggregate()['reader'], 'unconfirmed')

    def test_cleanup_exit_code_vetoes_a_success_record(self):
        child = FakeWorker('HANDS_CLEANUP {"confirmed": true, "problems": []}\n', code=5)
        with self.assertRaisesRegex(RuntimeError, 'cleanup was not confirmed'):
            self.run_workers([child])
        self.assertFalse(self.aggregate()['confirmed'])

    def test_worker_output_is_forwarded_before_exit(self):
        stream = GatedOutput(first='HANDS_READY {"experimental": true}\n')
        child = FakeWorker(stream, code=None)
        activity = io.StringIO()
        self.addCleanup(stream.release.set)
        with contextlib.redirect_stdout(activity):
            worker = module.WorkerOutput(child, 'hands')
            worker.start()
            self.assertTrue(stream.waiting.wait(1))
            self.assertIn('HANDS_READY ', activity.getvalue())
            self.assertIsNone(worker.cleanup_confirmed)
            child.returncode = 0
            stream.release.set()
            worker.finish()
        self.assertTrue(worker.cleanup_confirmed)
        self.assertTrue(stream.closed)

    def test_cleanup_output_after_exit_is_drained_before_aggregation(self):
        stream = GatedOutput()
        child = FakeWorker(stream)
        timer = threading.Timer(.02, stream.release.set)
        self.addCleanup(stream.release.set)
        def poll():
            self.assertTrue(stream.waiting.wait(1))
            if not timer.is_alive() and not stream.release.is_set():
                timer.start()
            return child.returncode
        child.poll = poll
        with self.assertRaisesRegex(RuntimeError, 'worker exited before Stop'):
            self.run_workers([child])
        timer.join()
        self.assertTrue(self.aggregate()['confirmed'])
        self.assertTrue(stream.closed)
        self.assertLess(self.activity.getvalue().index('HANDS_CLEANUP '),
                        self.activity.getvalue().index('CONTROLLER_CLEANUP '))

    def test_thumb_rest_cleanup_uses_its_own_structured_proof(self):
        cases = (('CONTROLLER_CLEANUP {"reader": "stopped", "inputs": "disabled"}\n', True),
                 ('CONTROLLER_CLEANUP {"reader": "unconfirmed", "problems": ["reader timeout"]}\n', False),
                 ('HANDS_CLEANUP {"confirmed": true, "problems": []}\n', False))
        for output, confirmed in cases:
            with self.subTest(output=output):
                error = 'worker exited before Stop' if confirmed else 'cleanup was not confirmed'
                with self.assertRaisesRegex(RuntimeError, error):
                    self.run_workers([FakeWorker(output)], hands=False, touchpad=True)
                self.assertEqual(self.aggregate()['confirmed'], confirmed)

    def test_every_selected_worker_must_confirm_cleanup(self):
        hands = FakeWorker('HANDS_CLEANUP {"confirmed": true, "problems": []}\n')
        touchpad = FakeWorker('', pid=124)
        with self.assertRaisesRegex(RuntimeError, 'cleanup was not confirmed'):
            self.run_workers([hands, touchpad], touchpad=True)
        self.assertFalse(self.aggregate()['confirmed'])
        self.assertTrue(hands.stdin.closed and touchpad.stdin.closed)

    def test_normal_stop_emits_successful_aggregate(self):
        child = FakeWorker('HANDS_CLEANUP {"confirmed": true, "problems": []}\n', code=0)
        self.run_workers([child], stopped=True)
        self.assertTrue(self.aggregate()['confirmed'])

    def test_stop_racing_confirmed_worker_failure_keeps_operational_error(self):
        child = FakeWorker('HANDS_CLEANUP {"confirmed": true, "problems": []}\n')
        with self.assertRaisesRegex(RuntimeError, 'worker failed'):
            self.run_workers([child], stopped=True)
        self.assertTrue(self.aggregate()['confirmed'])

    def test_reader_start_failure_still_stops_drains_and_waits_owned_worker(self):
        child = FakeWorker('HANDS_CLEANUP {"confirmed": true, "problems": []}\n', code=None)
        waited = []
        def wait(timeout):
            self.assertTrue(child.stdin.closed)
            self.assertTrue(child.stdout.closed)
            waited.append(timeout)
            child.returncode = 0
            return 0
        child.wait = wait
        with patch.object(module.threading.Thread, 'start', side_effect=RuntimeError('cannot start new thread')), \
             self.assertRaisesRegex(RuntimeError, 'cleanup was not confirmed'):
            self.run_workers([child])
        self.assertEqual(waited, [10])
        self.assertEqual(child.returncode, 0)
        self.assertIn('HANDS_CLEANUP ', self.activity.getvalue())
        self.assertFalse(self.aggregate()['confirmed'])
        self.assertEqual(self.aggregate()['reader'], 'unconfirmed')
        self.assertTrue(any('cannot start new thread' in problem for problem in self.aggregate()['problems']))


if __name__ == '__main__':
    unittest.main()
