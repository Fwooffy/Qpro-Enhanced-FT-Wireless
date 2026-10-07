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
    def create_addon(self, local):
        hands, addon = module.managed_paths(local)
        module.atomic_json(addon / 'qpro-owner.json', {'format': module.OWNER_FORMAT})
        module.atomic_json(addon / 'resources' / 'settings.json', {'enabled': False, 'smoothingMs': 25})
        hands.mkdir(parents=True, exist_ok=True)
        (hands / 'ready.json').write_text('fixture hands environment')
        return hands, addon

    def test_uninstall_preserves_settings_hands_and_unique_recovery(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            hands, addon = self.create_addon(local)
            settings = (addon / 'resources' / 'settings.json').read_bytes()
            with patch.object(module, 'assert_steamvr_closed'), \
                 patch.object(module, 'steamvr_tool', return_value=Path('fixture-vrpathreg')), \
                 patch.object(module, 'command', return_value='') as execute, \
                 contextlib.redirect_stdout(io.StringIO()) as output:
                module.uninstall(local)
                self.create_addon(local)
                module.uninstall(local)
            self.assertFalse(addon.exists())
            copies = list(addon.parent.glob('controller-addon-uninstalled-*'))
            self.assertEqual(len(copies), 2)
            self.assertTrue(all((path / 'resources' / 'settings.json').read_bytes() == settings for path in copies))
            self.assertEqual((hands / 'ready.json').read_text(), 'fixture hands environment')
            self.assertEqual(execute.call_count, 2)
            self.assertTrue(all(call.args[0][1:] == ['removedriver', str(addon)] for call in execute.call_args_list))
            self.assertEqual(output.getvalue().count('"phase": "uninstalled"'), 2)

    def test_uninstall_locked_directory_does_not_unregister(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            _, addon = self.create_addon(local)
            with patch.object(module, 'assert_steamvr_closed'), \
                 patch.object(module, 'steamvr_tool', return_value=Path('fixture-vrpathreg')), \
                 patch.object(module, 'command') as execute, \
                 patch.object(Path, 'rename', side_effect=PermissionError('fixture locked')):
                with self.assertRaisesRegex(PermissionError, 'fixture locked'):
                    module.uninstall(local)
            execute.assert_not_called()
            self.assertTrue((addon / 'resources' / 'settings.json').is_file())

    def test_uninstall_registration_failure_restores_files(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            _, addon = self.create_addon(local)
            original = (addon / 'resources' / 'settings.json').read_bytes()
            with patch.object(module, 'assert_steamvr_closed'), \
                 patch.object(module, 'steamvr_tool', return_value=Path('fixture-vrpathreg')), \
                 patch.object(module, 'command', side_effect=RuntimeError('fixture registration failed')):
                with self.assertRaisesRegex(RuntimeError, 'files were restored.*registration could not be confirmed'):
                    module.uninstall(local)
            self.assertEqual((addon / 'resources' / 'settings.json').read_bytes(), original)
            self.assertEqual(list(addon.parent.glob('controller-addon-uninstalled-*')), [])

    def test_uninstall_failed_rollback_reports_preserved_recovery(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            _, addon = self.create_addon(local)
            rename = Path.rename
            def fail_restore(path, destination):
                if path != addon:
                    raise PermissionError('fixture rollback locked')
                return rename(path, destination)
            with patch.object(module, 'assert_steamvr_closed'), \
                 patch.object(module, 'steamvr_tool', return_value=Path('fixture-vrpathreg')), \
                 patch.object(module, 'command', side_effect=RuntimeError('fixture registration failed')), \
                 patch.object(Path, 'rename', fail_restore):
                with self.assertRaisesRegex(RuntimeError, 'Recovery files remain at.*fixture rollback locked'):
                    module.uninstall(local)
            copies = list(addon.parent.glob('controller-addon-uninstalled-*'))
            self.assertEqual(len(copies), 1)
            self.assertTrue((copies[0] / 'resources' / 'settings.json').is_file())

    def test_uninstall_running_steamvr_refuses_before_changes(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            _, addon = self.create_addon(local)
            with patch.object(module, 'assert_steamvr_closed', side_effect=RuntimeError('Close SteamVR')), \
                 patch.object(module, 'command') as execute:
                with self.assertRaisesRegex(RuntimeError, 'Close SteamVR'):
                    module.uninstall(local)
            execute.assert_not_called()
            self.assertTrue(addon.is_dir())

    def test_malformed_owner_is_refused_without_changes(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            addon = Path(temporary)
            for value in (None, [], 42, {'format': 'foreign'}, 'fixture'):
                module.atomic_json(addon / 'qpro-owner.json', value)
                self.assertFalse(module.owner_is_valid(addon))

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
                with self.assertRaisesRegex(module.WorkerFailure, 'bind port 27062: 10048') as failure:
                    self.run_workers([child])
                self.assertEqual(failure.exception.returncode, code)
                self.assertIn(output, self.activity.getvalue())
                self.assertTrue(self.aggregate()['confirmed'])
                self.assertEqual(self.aggregate()['reader'], 'stopped')
                self.assertTrue(child.stdin.closed)
                self.assertTrue(child.stdout.closed)
                options = self.launch.call_args.kwargs
                self.assertEqual(options['stdout'], module.subprocess.PIPE)
                self.assertEqual(options['stderr'], module.subprocess.STDOUT)
                self.assertTrue(options['text'])

    def test_failed_check_forwards_json_and_returns_the_compatibility_code(self):
        reasons = ['Enable headset hand tracking: hand_tracking_enabled is false.',
                   'Enable simultaneous hands and controllers: multimodal_hands_and_controllers_enabled is false.']
        output = 'HANDS_CHECK ' + json.dumps({'compatible': False, 'problems': reasons}) + '\n'
        result = module.subprocess.CompletedProcess(['fixture-python'], 4, output, 'fixture stderr\n')
        activity = io.StringIO()
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary, \
             patch.dict(module.os.environ, {'LOCALAPPDATA': temporary}), \
             patch.object(module, 'select_target', return_value='quest'), \
             patch.object(module.subprocess, 'run', return_value=result) as execute, \
             contextlib.redirect_stdout(activity):
            code = module.entry_point(['check', '--root', str(HERE), '--adb', 'fake-adb'])
        self.assertEqual(code, 4)
        self.assertIn(output, activity.getvalue())
        self.assertIn('fixture stderr\n', activity.getvalue())
        self.assertNotIn('CONTROLLER_CHECK ', activity.getvalue())
        error = json.loads(next(line.partition(' ')[2] for line in activity.getvalue().splitlines()
                                if line.startswith('CONTROLLER_ERROR ')))
        self.assertEqual(error['message'], '; '.join(reasons))
        self.assertTrue(execute.call_args.kwargs['capture_output'])

    def test_successful_check_still_forwards_its_structured_result(self):
        output = 'HANDS_CHECK {"compatible": true, "problems": []}\n'
        result = module.subprocess.CompletedProcess(['fixture-python'], 0, output, '')
        activity = io.StringIO()
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary, \
             patch.dict(module.os.environ, {'LOCALAPPDATA': temporary}), \
             patch.object(module, 'select_target', return_value='quest'), \
             patch.object(module.subprocess, 'run', return_value=result), \
             contextlib.redirect_stdout(activity):
            code = module.entry_point(['check', '--root', str(HERE), '--adb', 'fake-adb'])
        self.assertEqual(code, 0)
        self.assertIn(output, activity.getvalue())
        self.assertIn('CONTROLLER_CHECK ', activity.getvalue())
        self.assertNotIn('CONTROLLER_ERROR ', activity.getvalue())

    def test_run_reports_preflight_reasons_and_startup_errors_with_original_codes(self):
        reasons = ['hand_tracking_enabled is false', 'multimodal_hands_and_controllers_enabled is false']
        cases = [(4, 'HANDS_CHECK ' + json.dumps({'compatible': False, 'problems': reasons}) + '\n', '; '.join(reasons)),
                 (1, 'HANDS_FAILED {"error": "bind port 27062: 10048"}\n', 'bind port 27062: 10048')]
        for expected_code, diagnostic, reason in cases:
            with self.subTest(code=expected_code), tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
                local = Path(temporary)
                hands, _ = module.managed_paths(local)
                (hands / '.venv' / 'Scripts').mkdir(parents=True)
                (hands / '.venv' / 'Scripts' / 'python.exe').touch()
                (hands / 'ready.json').touch()
                cleanup = 'HANDS_CLEANUP {"confirmed": true, "problems": [], "phase": "not-started"}\n'
                child = FakeWorker(diagnostic + cleanup, expected_code)
                activity = io.StringIO()
                with patch.dict(module.os.environ, {'LOCALAPPDATA': temporary}), \
                     patch.object(module, 'select_target', return_value='quest'), \
                     patch.object(module.subprocess, 'Popen', return_value=child), \
                     contextlib.redirect_stdout(activity):
                    code = module.entry_point(['run', '--root', str(HERE), '--adb', 'fake-adb',
                                               '--hands', '--stop-file', str(local / 'stop')])
                self.assertEqual(code, expected_code)
                self.assertIn(diagnostic + cleanup, activity.getvalue())
                error = json.loads(next(line.partition(' ')[2] for line in activity.getvalue().splitlines()
                                        if line.startswith('CONTROLLER_ERROR ')))
                self.assertIn(reason, error['message'])
                aggregate = json.loads([line.partition(' ')[2] for line in activity.getvalue().splitlines()
                                        if line.startswith('CONTROLLER_CLEANUP ')][-1])
                self.assertTrue(aggregate['confirmed'])
                self.assertTrue(child.stdin.closed and child.stdout.closed)

    def test_cleanup_failure_keeps_original_operational_reason_visible(self):
        output = ('HANDS_STOP_CAUSE {"error": "original readiness timeout"}\n'
                  'HANDS_CLEANUP_FAILED {"error": "script has been destroyed"}\n')
        with self.assertRaisesRegex(RuntimeError, 'cleanup was not confirmed.*original readiness timeout'):
            self.run_workers([FakeWorker(output, code=5)])
        self.assertFalse(self.aggregate()['confirmed'])

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
