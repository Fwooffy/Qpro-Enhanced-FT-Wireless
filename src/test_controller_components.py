"""Offline supervisor/protocol tests. No headset, SteamVR or installation used."""
from pathlib import Path
import contextlib
import importlib.util
import json
import math
import tempfile
import threading
import unittest
import venv
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
            yield from self.first.splitlines(keepends=True)
        self.waiting.set()
        if not self.release.wait(2):
            raise OSError('Fixture output was not released.')
        yield from self.final.splitlines(keepends=True)

    def close(self):
        self.closed = True


class ReaderProcess(FakeWorker):
    def __init__(self, output='QPTP_LISTENING address=127.0.0.1 port=27063 rate=60\nQPTP_STOPPED\n', code=None):
        super().__init__(output, code)
        self.waited = False

    def wait(self, timeout):
        self.waited = True
        if self.returncode is None:
            self.returncode = 0
        return self.returncode


class RelayStream:
    def __init__(self, reads):
        self.reads = iter(reads)
        self.closed = False

    def recv(self, _length):
        value = next(self.reads)
        if callable(value):
            return value()
        if isinstance(value, Exception):
            raise value
        return value

    def settimeout(self, timeout):
        self.timeout = timeout

    def close(self):
        self.closed = True


class CloseFailureOutput(io.StringIO):
    def close(self):
        super().close()
        raise OSError('fixture close failed')


class ComponentTests(unittest.TestCase):
    def create_addon(self, local):
        hands, addon = module.managed_paths(local)
        module.atomic_json(addon / 'qpro-owner.json', {'format': module.OWNER_FORMAT})
        module.atomic_json(addon / 'resources' / 'settings.json', {'enabled': False, 'smoothingMs': 25})
        driver = addon / 'bin' / 'win64' / 'driver_qpro_controller.dll'
        driver.parent.mkdir(parents=True, exist_ok=True)
        driver.write_bytes(b'fixture-current-controller-dll')
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

    def test_current_addon_dll_hash_precheck_is_read_only(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary); _, addon = self.create_addon(local)
            root = local / 'root'; bundled = root / 'controller-input' / 'addon' / 'bin' / 'win64' / 'driver_qpro_controller.dll'
            bundled.parent.mkdir(parents=True); bundled.write_bytes(b'fixture-current-controller-dll')
            before = {path: path.read_bytes() for path in local.rglob('*') if path.is_file()}
            module.verify_addon_current(root, addon)
            self.assertEqual({path: path.read_bytes() for path in local.rglob('*') if path.is_file()}, before)

    def test_old_and_missing_addon_dlls_fail_before_use_with_reinstall_instruction(self):
        for state in ('old', 'missing-installed', 'missing-bundle'):
            with self.subTest(state=state), tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
                local = Path(temporary); _, addon = self.create_addon(local)
                root = local / 'root'; bundled = root / 'controller-input' / 'addon' / 'bin' / 'win64' / 'driver_qpro_controller.dll'
                bundled.parent.mkdir(parents=True); bundled.write_bytes(b'fixture-current-controller-dll')
                installed = addon / 'bin' / 'win64' / 'driver_qpro_controller.dll'
                if state == 'old': installed.write_bytes(b'fixture-old-controller-dll')
                elif state == 'missing-installed': installed.unlink()
                else: bundled.unlink()
                before = {path: path.read_bytes() for path in local.rglob('*') if path.is_file()}
                with self.assertRaisesRegex(RuntimeError, 'Close SteamVR, reinstall the controller add-on.*reopen SteamVR'):
                    module.verify_addon_current(root, addon)
                self.assertEqual({path: path.read_bytes() for path in local.rglob('*') if path.is_file()}, before)

    def test_old_addon_refuses_before_any_worker_or_headset_probe(self):
        for standalone in (False, True):
            with self.subTest(standalone=standalone), tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
                local = Path(temporary); hands, addon = self.create_addon(local)
                python = hands / '.venv' / 'Scripts' / 'python.exe'; python.parent.mkdir(parents=True); python.touch()
                root = local / 'root'; bundled = root / 'controller-input' / 'addon' / 'bin' / 'win64' / 'driver_qpro_controller.dll'
                bundled.parent.mkdir(parents=True); bundled.write_bytes(b'fixture-current-controller-dll')
                (addon / 'bin' / 'win64' / 'driver_qpro_controller.dll').write_bytes(b'fixture-old-controller-dll')
                args = SimpleNamespace(hands=True, touchpad=True, parent_stdin=False, stop_file=str(local / 'stop'),
                                       root=str(root), mode='trackpad', side='both')
                settings = (addon / 'resources' / 'settings.json').read_bytes()
                with patch.object(module.subprocess, 'Popen') as launch, patch.object(module, 'command') as execute, \
                     patch.object(module.socket, 'socket') as udp, contextlib.redirect_stdout(io.StringIO()), \
                     self.assertRaisesRegex(RuntimeError, 'installed controller add-on DLL does not match'):
                    if standalone:
                        module.relay(root, local, Path('fake-adb'), 'quest', local / 'stop', 'trackpad')
                    else:
                        module.run(args, local, Path('fake-adb'), 'quest')
                launch.assert_not_called(); execute.assert_not_called(); udp.assert_not_called()
                self.assertEqual((addon / 'resources' / 'settings.json').read_bytes(), settings)

    def run_workers(self, children, hands=True, touchpad=False, stopped=False, parent_closed=None, side='both',
                    mode='trackpad', swipe_gain=None):
        self.activity = io.StringIO()
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            self.create_addon(local)
            root = local / 'root'
            bundled = root / 'controller-input' / 'addon' / 'bin' / 'win64' / 'driver_qpro_controller.dll'
            bundled.parent.mkdir(parents=True)
            bundled.write_bytes(b'fixture-current-controller-dll')
            hands_path, _ = module.managed_paths(local)
            (hands_path / '.venv' / 'Scripts').mkdir(parents=True)
            (hands_path / '.venv' / 'Scripts' / 'python.exe').touch()
            (hands_path / 'ready.json').touch()
            args = SimpleNamespace(hands=hands, touchpad=touchpad, parent_stdin=False,
                                   stop_file=str(local / 'stop'), root=str(root), mode=mode, side=side,
                                   swipe_gain=swipe_gain)
            parent_closed = parent_closed or threading.Event()
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

    def packet(self, sequence=1, flags=3, x=.25, force=.7, timestamp=100, right_flags=1):
        return module.PACKET.pack(b'QPTP', 1, 64, sequence, timestamp, flags, x, -.5, force, 50,
                                  right_flags, 0, 0, 0, 0)

    def run_relay(self, native, stream=None, parent_closed=None, connect_check=None, send_check=None,
                  input_update=None, mode='trackpad', side='both', health_update=None, swipe_gain=None):
        self.relay_activity = io.StringIO()
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            self.create_addon(local)
            root = local / 'root'
            reader = root / 'controller-input' / 'qpro-controller-input'
            reader.parent.mkdir(parents=True)
            reader.touch()
            bundled = root / 'controller-input' / 'addon' / 'bin' / 'win64' / 'driver_qpro_controller.dll'
            bundled.parent.mkdir(parents=True)
            bundled.write_bytes(b'fixture-current-controller-dll')
            stop = local / 'stop'
            self.relay_stop = stop
            enabled = []
            self.relay_configurations = []
            self.relay_swipe_gains = []
            def input_enabled(_addon, state, _mode, _side, swipe_gain=None):
                enabled.append(state)
                self.relay_enabled = enabled
                self.relay_configurations.append((state, _mode, _side))
                self.relay_swipe_gains.append(swipe_gain)
                if input_update:
                    input_update(_addon, state, _mode, _side)
            forwards = []
            def execute(args, **_kwargs):
                if args[-1] == 'id':
                    return 'uid=0'
                if args[-2:] == ['forward', '--list']:
                    forwards.append(True)
                    return '' if len(forwards) == 1 else 'quest tcp:27063 tcp:27063'
                return ''
            def connect(*_args, **_kwargs):
                if connect_check:
                    connect_check()
                return stream
            self.relay_commands = []
            def record_command(args, **kwargs):
                self.relay_commands.append(args)
                return execute(args, **kwargs)
            self.sent_packets = []
            replies = []
            def send(packet, _destination):
                if packet.startswith(b'QPRO_CTRL_HEALTH_V1 '):
                    value = dict(protocol='QPRO-CTRL-HEALTH-v1', nonce=packet.decode('ascii').split()[1],
                                 enabled=bool(enabled and enabled[-1]), settingsValid=True, mode=mode, side=side,
                                 adoptedSides=[True, True], successfulSides=[True, True], neutral=not bool(enabled and enabled[-1]),
                                 inputErrors=[0, 0], mouseError=0, restoreErrors=0, settingsRevision=1, appliedRevision=1)
                    if health_update:
                        value = health_update(value)
                    if value is not None:
                        replies.append((json.dumps(value).encode('utf8'), ('127.0.0.1', 27064)))
                    return
                self.sent_packets.append(packet)
                if send_check:
                    send_check()
            def receive(_size):
                if not replies:
                    raise BlockingIOError()
                return replies.pop(0)
            output = SimpleNamespace(sendto=send, recvfrom=receive, setblocking=lambda _value: None, close=lambda: None)
            with patch.object(module, 'command', side_effect=record_command), \
                 patch.object(module, 'set_input_enabled', side_effect=input_enabled), \
                 patch.object(module.subprocess, 'Popen', return_value=native) as launch, \
                 patch.object(module.socket, 'socket', return_value=output), \
                 patch.object(module.socket, 'create_connection', side_effect=connect) as socket_connect, \
                 contextlib.redirect_stdout(self.relay_activity):
                self.reader_launch = launch
                self.reader_connect = socket_connect
                module.relay(root, local, Path('fake-adb'), 'quest', stop, mode, parent_closed, side, swipe_gain)

    def test_native_readiness_requires_its_exact_flushed_marker(self):
        process = ReaderProcess('QPTP_PROFILE build=fixture\n'
                                'QPTP_LISTENING address=127.0.0.1 port=27062 rate=60\n')
        monitor = module.NativeReaderOutput(process)
        with contextlib.redirect_stdout(io.StringIO()):
            monitor.start()
            self.assertTrue(monitor.closed.wait(1))
            with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary, \
                 self.assertRaisesRegex(RuntimeError, 'closed its output before listening'):
                monitor.wait_listening(Path(temporary) / 'stop', threading.Event(), timeout=.01)
            monitor.finish()
        self.assertFalse(monitor.listening.is_set())

    def test_driver_health_requires_fresh_token_sender_types_and_applied_revision(self):
        value = dict(protocol='QPRO-CTRL-HEALTH-v1', nonce='a' * 32, enabled=False, settingsValid=True,
                     mode='trackpad', side='both', adoptedSides=[True, False], successfulSides=[True, False],
                     neutral=True, inputErrors=[0, 0], mouseError=0, restoreErrors=0,
                     settingsRevision=4, appliedRevision=4)
        sender = ('127.0.0.1', 27064)
        self.assertEqual(module.decode_driver_health(json.dumps(value).encode(), sender, 'a' * 32), value)
        self.assertIsNone(module.decode_driver_health(json.dumps(value).encode(), sender, 'b' * 32))
        self.assertIsNone(module.decode_driver_health(json.dumps(value).encode(), ('127.0.0.1', 1234), 'a' * 32))
        for change in ({'neutral': 1}, {'adoptedSides': [True]}, {'inputErrors': [False, 0]},
                       {'appliedRevision': 5}, {'mouseError': -1}, {'settingsRevision': True}, {'side': 'any'}):
            with self.subTest(change=change):
                self.assertIsNone(module.decode_driver_health(json.dumps(dict(value, **change)).encode(), sender, 'a' * 32))
        duplicate = json.dumps(value)[:-1] + ',"enabled":false}'
        self.assertIsNone(module.decode_driver_health(duplicate.encode(), sender, 'a' * 32))
        self.assertIsNone(module.decode_driver_health(b'x' * 1025, sender, 'a' * 32))

    def test_relay_stop_requires_native_disabled_neutral_acknowledgement(self):
        for change in ({'neutral': False}, {'enabled': True}, {'settingsValid': False},
                       {'inputErrors': [1, 0]}, {'mouseError': 5}, {'restoreErrors': 1},
                       {'settingsRevision': 2, 'appliedRevision': 1}, {'nonce': 'stale'}, {'side': 'right'}):
            with self.subTest(change=change):
                closed = threading.Event()
                def stop_after_packet():
                    closed.set()
                    return b''
                ticks = iter(index * .1 for index in range(1000))
                with patch.object(module.time, 'monotonic', side_effect=lambda: next(ticks)), \
                     patch.object(module.time, 'sleep'), \
                     self.assertRaisesRegex(RuntimeError, 'disabled/neutral acknowledgement was not confirmed'):
                    self.run_relay(ReaderProcess(), RelayStream([self.packet(), stop_after_packet]), parent_closed=closed,
                                   health_update=lambda value: dict(value, **change))
                self.assertIn('"reader": "unconfirmed"', self.relay_activity.getvalue())

    def test_relay_confirmed_native_ack_remains_separate_from_sensor_readiness(self):
        closed = threading.Event()
        def stop_after_packet():
            closed.set()
            return b''
        self.run_relay(ReaderProcess(), RelayStream([self.packet(), stop_after_packet]), parent_closed=closed)
        lines = self.relay_activity.getvalue().splitlines()
        statuses = [json.loads(line.partition(' ')[2]) for line in lines if line.startswith('CONTROLLER_DRIVER_STATUS ')]
        self.assertIn('TOUCHPAD_READY ', self.relay_activity.getvalue())
        self.assertTrue(statuses[-1]['neutral'])
        self.assertFalse(statuses[-1]['enabled'])
        self.assertIn('"inputs": "disabled"', self.relay_activity.getvalue())

    def test_missing_driver_ack_does_not_skip_reader_or_forward_cleanup(self):
        native = ReaderProcess(); closed = threading.Event()
        def stop_after_packet():
            closed.set()
            return b''
        ticks = iter(index * .1 for index in range(1000))
        with patch.object(module.time, 'monotonic', side_effect=lambda: next(ticks)), \
             patch.object(module.time, 'sleep'), \
             self.assertRaisesRegex(RuntimeError, 'disabled/neutral acknowledgement was not confirmed'):
            self.run_relay(native, RelayStream([self.packet(), stop_after_packet]), parent_closed=closed,
                           health_update=lambda _value: None)
        self.assertTrue(native.waited)
        self.assertIn('QPTP_STOPPED', self.relay_activity.getvalue())
        self.assertIn(['fake-adb', '-s', 'quest', 'forward', '--remove', 'tcp:27063'], self.relay_commands)
        self.assertNotIn('"inputs": "disabled"', self.relay_activity.getvalue())

    def test_native_stopped_requires_exact_marker_and_survives_diagnostic_trimming(self):
        for marker, stopped in (('QPTP_STOPPED\r\n', True),
                                ('prefix QPTP_STOPPED\n', False),
                                ('QPTP_STOPPED suffix\n', False)):
            with self.subTest(marker=marker):
                process = ReaderProcess(marker + 'later diagnostic\n' * 12)
                monitor = module.NativeReaderOutput(process)
                with contextlib.redirect_stdout(io.StringIO()):
                    monitor.start()
                    monitor.finish()
                self.assertEqual(monitor.stopped.is_set(), stopped)
                self.assertEqual(len(monitor._lines), 8)

    def test_native_output_close_failure_is_recorded_and_always_latched(self):
        for started in (True, False):
            with self.subTest(started=started):
                process = ReaderProcess()
                process.stdout = CloseFailureOutput('fixture output\n')
                monitor = module.NativeReaderOutput(process)
                with contextlib.redirect_stdout(io.StringIO()):
                    if started:
                        monitor.start()
                    monitor.finish()
                self.assertTrue(monitor.closed.is_set())
                self.assertEqual(monitor.error, 'fixture close failed')
                self.assertFalse(monitor.thread.is_alive())

    def test_native_diagnostics_are_bounded_and_include_exit_code(self):
        process = ReaderProcess('\n'.join(['x' * 2000 for _ in range(30)]) +
                                '\nQPTP_UNSUPPORTED build=fixture\n', code=3)
        monitor = module.NativeReaderOutput(process)
        with contextlib.redirect_stdout(io.StringIO()) as output:
            monitor.start()
            monitor.finish()
            with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary, \
                 self.assertRaisesRegex(RuntimeError, 'exit: 3.*QPTP_UNSUPPORTED'):
                monitor.wait_listening(Path(temporary) / 'stop', threading.Event())
        diagnostic = json.loads(next(line.partition(' ')[2] for line in output.getvalue().splitlines()
                                     if line.startswith('CONTROLLER_READER ')))
        self.assertEqual(diagnostic['exitCode'], 3)
        self.assertLessEqual(len(diagnostic['diagnostic']), 8 * 1024 + 7 * 3)
        self.assertEqual(module.failure_reason('CONTROLLER_READER ' + json.dumps(diagnostic)),
                         diagnostic['message'])

    def test_native_reader_cause_survives_later_cleanup_error(self):
        output = ('CONTROLLER_READER {"message": "Native diagnostic: QPTP_SOURCE_UNAVAILABLE"}\n'
                  'CONTROLLER_CLEANUP {"reader": "unconfirmed", "problems": ["fixture cleanup failed"]}\n'
                  'CONTROLLER_ERROR {"message": "cleanup needs attention"}\n')
        with self.assertRaisesRegex(RuntimeError, 'cleanup was not confirmed.*QPTP_SOURCE_UNAVAILABLE') as failure:
            self.run_workers([FakeWorker(output)], hands=False, touchpad=True)
        self.assertFalse(self.aggregate()['confirmed'])
        self.assertIn('fixture cleanup failed', '; '.join(self.aggregate()['problems']))
        self.assertIn('fixture cleanup failed', str(failure.exception))

    def test_cleanup_only_problems_survive_aggregate_and_public_error(self):
        output = 'CONTROLLER_CLEANUP {"reader": "unconfirmed", "problems": ["RESTORE_DENIED"]}\n'
        with self.assertRaisesRegex(RuntimeError, 'RESTORE_DENIED'):
            self.run_workers([FakeWorker(output)], hands=False, touchpad=True)
        self.assertIn('RESTORE_DENIED', '; '.join(self.aggregate()['problems']))

    def test_cleanup_context_has_independent_bounded_capacity(self):
        worker = module.WorkerOutput(FakeWorker(''), 'hands')
        for index in range(4):
            worker._observe('HANDS_DETACHED ' + json.dumps({'reason': 'disconnect-' + str(index)}))
        worker._observe('HANDS_CLEANUP ' + json.dumps({
            'confirmed': False, 'problems': ['restore-' + str(index) + 'x' * 1000 for index in range(20)]}))
        self.assertEqual(len(worker.context_reasons), 4)
        self.assertEqual(len(worker.cleanup_problems), 8)
        self.assertTrue(all(len(problem) <= 512 for problem in worker.cleanup_problems))
        self.assertIn('restore-0', worker.failure_reason)
        self.assertFalse(worker.cleanup_confirmed)
        for problems in (True, 'restore', [True, None, 7]):
            before = list(worker.cleanup_problems)
            worker._observe('HANDS_CLEANUP ' + json.dumps({'confirmed': False, 'problems': problems}))
            self.assertEqual(worker.cleanup_problems, before)

    def test_second_worker_spawn_error_survives_unconfirmed_cleanup(self):
        first = FakeWorker('HANDS_CLEANUP {"confirmed": false, "problems": ["FIRST_RESTORE_FAILED"]}\n')
        with self.assertRaisesRegex(RuntimeError, 'SECOND_WORKER_SPAWN_FAILED') as failure:
            self.run_workers([first, OSError('SECOND_WORKER_SPAWN_FAILED')], touchpad=True)
        self.assertIn('FIRST_RESTORE_FAILED', str(failure.exception))
        details = '; '.join(self.aggregate()['problems'])
        self.assertIn('SECOND_WORKER_SPAWN_FAILED', details)
        self.assertIn('FIRST_RESTORE_FAILED', details)
        self.assertFalse(self.aggregate()['confirmed'])

    def test_second_worker_spawn_error_keeps_its_type_after_confirmed_cleanup(self):
        first = FakeWorker('HANDS_CLEANUP {"confirmed": true, "problems": []}\n', code=0)
        with self.assertRaisesRegex(OSError, 'SECOND_WORKER_SPAWN_FAILED'):
            self.run_workers([first, OSError('SECOND_WORKER_SPAWN_FAILED')], touchpad=True)
        self.assertTrue(self.aggregate()['confirmed'])

    def test_relay_waits_for_native_marker_before_connecting_or_enabling(self):
        output = GatedOutput(first='QPTP_PROFILE build=fixture\n',
                             final='QPTP_LISTENING address=127.0.0.1 port=27063 rate=60\nQPTP_STOPPED\n')
        native = ReaderProcess(output)
        closed = threading.Event()
        def stop_after_packet():
            closed.set()
            return b''
        stream = RelayStream([self.packet(), stop_after_packet])
        timer = threading.Timer(.02, output.release.set)
        self.addCleanup(output.release.set)
        timer.start()
        def connect_check():
            self.assertTrue(output.release.is_set())
            self.assertNotIn(True, getattr(self, 'relay_enabled', []))
        self.run_relay(native, stream, parent_closed=closed, connect_check=connect_check)
        timer.join()
        self.assertEqual(self.relay_enabled, [True, False])
        self.assertTrue(native.waited and stream.closed and output.closed)
        self.assertIn('TOUCHPAD_READY ', self.relay_activity.getvalue())
        self.assertEqual(self.reader_connect.call_count, 1)
        self.assertEqual(self.reader_launch.call_args.kwargs['stderr'], module.subprocess.STDOUT)
        self.assertTrue(self.reader_launch.call_args.kwargs['text'])

    def test_relay_native_build_failure_is_not_retried_or_enabled(self):
        native = ReaderProcess('QPTP_UNSUPPORTED build=fixture profile=legacy\n', code=3)
        with self.assertRaisesRegex(RuntimeError, 'exited before listening.*exit: 3.*QPTP_UNSUPPORTED'):
            self.run_relay(native)
        self.reader_connect.assert_not_called()
        self.assertEqual(self.relay_enabled, [False])
        self.assertTrue(native.waited)
        self.assertIn('"reader": "unconfirmed"', self.relay_activity.getvalue())

    def test_relay_cleanup_requires_native_stop_marker_and_zero_exit(self):
        for marker, code in (('', 0), ('QPTP_STOPPED\n', 0),
                             ('', 1), ('QPTP_STOPPED\n', 1), ('QPTP_STOPPED\n', 8)):
            with self.subTest(marker=marker, code=code):
                closed = threading.Event()
                native = ReaderProcess('QPTP_LISTENING address=127.0.0.1 port=27063 rate=60\n' + marker)
                def wait(timeout):
                    native.waited = True
                    native.returncode = code
                    return code
                native.wait = wait
                def stop_during_recv():
                    closed.set()
                    return b''
                if marker and code == 0:
                    self.run_relay(native, RelayStream([stop_during_recv]), parent_closed=closed)
                    self.assertIn('"reader": "stopped"', self.relay_activity.getvalue())
                else:
                    with self.assertRaisesRegex(RuntimeError, 'cleanup needs attention'):
                        self.run_relay(native, RelayStream([stop_during_recv]), parent_closed=closed)
                    self.assertIn('"reader": "unconfirmed"', self.relay_activity.getvalue())
                self.assertEqual(self.relay_enabled, [False])
                self.assertTrue(native.waited and native.stdout.closed)

    def test_relay_cleanup_drains_stop_marker_after_native_wait(self):
        output = GatedOutput(first='QPTP_LISTENING address=127.0.0.1 port=27063 rate=60\n',
                             final='QPTP_STOPPED\n')
        self.addCleanup(output.release.set)
        native = ReaderProcess(output)
        closed = threading.Event()
        def wait(timeout):
            self.assertTrue(output.waiting.wait(1))
            native.waited = True
            native.returncode = 0
            output.release.set()
            return 0
        native.wait = wait
        def stop_during_recv():
            closed.set()
            return b''
        self.run_relay(native, RelayStream([stop_during_recv]), parent_closed=closed)
        self.assertIn('QPTP_STOPPED', self.relay_activity.getvalue())
        self.assertIn('"reader": "stopped"', self.relay_activity.getvalue())

    def test_relay_forced_reader_termination_never_confirms_cleanup(self):
        output = GatedOutput(first='QPTP_LISTENING address=127.0.0.1 port=27063 rate=60\n',
                             final='QPTP_STOPPED\n')
        self.addCleanup(output.release.set)
        native = ReaderProcess(output)
        closed = threading.Event()
        def wait(timeout):
            if timeout == 5:
                raise module.subprocess.TimeoutExpired('fake reader', timeout)
            native.returncode = 0
            return 0
        native.wait = wait
        native.terminate = output.release.set
        def stop_during_recv():
            closed.set()
            return b''
        with self.assertRaisesRegex(RuntimeError, 'reader exit was not confirmed'):
            self.run_relay(native, RelayStream([stop_during_recv]), parent_closed=closed)
        self.assertIn('QPTP_STOPPED', self.relay_activity.getvalue())
        self.assertIn('"reader": "unconfirmed"', self.relay_activity.getvalue())

    def test_relay_output_close_failure_vetoes_stop_marker_and_zero_exit(self):
        native = ReaderProcess()
        native.stdout = CloseFailureOutput('QPTP_LISTENING address=127.0.0.1 port=27063 rate=60\nQPTP_STOPPED\n')
        closed = threading.Event()
        closed.set()
        with self.assertRaisesRegex(RuntimeError, 'Reader diagnostic output: fixture close failed'):
            self.run_relay(native, parent_closed=closed)
        self.assertIn('QPTP_STOPPED', self.relay_activity.getvalue())
        self.assertIn('"reader": "unconfirmed"', self.relay_activity.getvalue())

    def test_relay_eof_before_first_packet_never_enables_inputs(self):
        native = ReaderProcess()
        with self.assertRaisesRegex(RuntimeError, 'disconnected during tracking.*still running'):
            self.run_relay(native, RelayStream([b'']))
        self.assertEqual(self.relay_enabled, [False])
        self.assertEqual(self.reader_connect.call_count, 1)
        self.assertIn('QPTP_LISTENING', self.relay_activity.getvalue())

    def test_relay_invalid_first_packet_never_enables_inputs(self):
        native = ReaderProcess()
        with self.assertRaisesRegex(ValueError, 'Invalid protocol'):
            self.run_relay(native, RelayStream([b'x' * module.PACKET.size]))
        self.assertEqual(self.relay_enabled, [False])
        self.assertTrue(native.waited)

    def test_relay_neutral_first_packet_does_not_enable_or_send_input(self):
        native = ReaderProcess()
        closed = threading.Event()
        neutral = module.PACKET.pack(b'QPTP', 1, 64, 1, 100, *([0] * 10))
        def stopped():
            closed.set()
            return b''
        self.run_relay(native, RelayStream([neutral, stopped]), parent_closed=closed)
        self.assertEqual(self.relay_enabled, [False])
        self.assertEqual(self.sent_packets, [])
        self.assertNotIn('TOUCHPAD_READY ', self.relay_activity.getvalue())

    def test_relay_neutral_packets_after_ready_still_clear_input(self):
        native = ReaderProcess()
        closed = threading.Event()
        neutral = module.PACKET.pack(b'QPTP', 1, 64, 2, 101, *([0] * 10))
        def stopped():
            closed.set()
            return b''
        valid = self.packet()
        self.run_relay(native, RelayStream([valid, neutral, stopped]), parent_closed=closed)
        self.assertEqual(self.relay_enabled, [True, False])
        self.assertEqual(self.sent_packets, [valid, neutral])

    def test_relay_established_disconnect_is_not_reconnected(self):
        native = ReaderProcess()
        with self.assertRaisesRegex(RuntimeError, 'disconnected during tracking'):
            self.run_relay(native, RelayStream([self.packet(), b'']))
        self.assertEqual(self.relay_enabled, [True, False])
        self.assertEqual(self.reader_connect.call_count, 1)

    def test_relay_socket_read_error_retains_native_diagnostic(self):
        native = ReaderProcess()
        with self.assertRaisesRegex(RuntimeError, 'sensor connection failed: fixture reset.*QPTP_LISTENING'):
            self.run_relay(native, RelayStream([OSError('fixture reset')]))
        self.assertEqual(self.relay_enabled, [False])
        self.assertTrue(native.waited)

    def test_relay_socket_close_failure_still_stops_reader_and_removes_owned_forward(self):
        native = ReaderProcess()
        stream = RelayStream([b''])
        def fail_close():
            stream.closed = True
            raise OSError('fixture socket close failed')
        stream.close = fail_close
        with self.assertRaisesRegex(RuntimeError,
            'cleanup needs attention.*socket close failed.*Original failure:.*disconnected'):
            self.run_relay(native, stream)
        self.assertTrue(native.waited and native.stdout.closed and stream.closed)
        self.assertIn(['fake-adb', '-s', 'quest', 'forward', '--remove', 'tcp:27063'], self.relay_commands)
        self.assertIn('"reader": "unconfirmed"', self.relay_activity.getvalue())

    def test_relay_reader_wait_error_still_removes_owned_forward_and_keeps_cause(self):
        native = ReaderProcess()
        def fail_wait(timeout):
            raise OSError('fixture wait failed')
        native.wait = fail_wait
        with self.assertRaisesRegex(RuntimeError, 'cleanup needs attention.*wait failed.*Original failure:.*disconnected'):
            self.run_relay(native, RelayStream([b'']))
        self.assertIn(['fake-adb', '-s', 'quest', 'forward', '--remove', 'tcp:27063'], self.relay_commands)

    def test_relay_explicit_stop_during_recv_eof_is_normal_cleanup(self):
        closed = threading.Event()
        def stop_during_recv():
            closed.set()
            return b''
        native = ReaderProcess()
        self.run_relay(native, RelayStream([stop_during_recv]), parent_closed=closed)
        self.assertEqual(self.relay_enabled, [False])
        self.assertNotIn('CONTROLLER_READER ', self.relay_activity.getvalue())
        self.assertTrue(native.waited)

    def test_relay_stop_racing_first_packet_does_not_enable_input(self):
        closed = threading.Event()
        def stopped_packet():
            closed.set()
            return self.packet()
        native = ReaderProcess()
        self.run_relay(native, RelayStream([stopped_packet]), parent_closed=closed)
        self.assertEqual(self.relay_enabled, [False])
        self.assertNotIn('TOUCHPAD_READY ', self.relay_activity.getvalue())

    def test_relay_stop_during_validation_does_not_enable_or_send(self):
        closed = threading.Event()
        validate = module.validate_packet
        def stopped_validation(packet, previous):
            result = validate(packet, previous)
            closed.set()
            return result
        with patch.object(module, 'validate_packet', side_effect=stopped_validation):
            self.run_relay(ReaderProcess(), RelayStream([self.packet()]), parent_closed=closed)
        self.assertEqual(self.relay_enabled, [False])
        self.assertEqual(self.sent_packets, [])

    def test_relay_stop_during_coalesced_packets_does_not_send_the_remainder(self):
        closed = threading.Event()
        first = self.packet()
        second = self.packet(sequence=2, timestamp=101)
        self.run_relay(ReaderProcess(), RelayStream([first + second]), parent_closed=closed,
                       send_check=closed.set)
        self.assertEqual(self.sent_packets, [first])
        self.assertEqual(self.relay_enabled, [True, False])

    def test_relay_explicit_stop_before_readiness_does_not_connect(self):
        closed = threading.Event()
        closed.set()
        native = ReaderProcess()
        self.run_relay(native, parent_closed=closed)
        self.reader_connect.assert_not_called()
        self.assertEqual(self.relay_enabled, [False])
        self.assertTrue(native.waited)

    def test_native_readiness_timeout_is_bounded_without_connecting(self):
        output = GatedOutput(final='QPTP_STOPPED\n')
        native = ReaderProcess(output)
        monitor = module.NativeReaderOutput(native)
        self.addCleanup(output.release.set)
        with contextlib.redirect_stdout(io.StringIO()):
            monitor.start()
            self.assertTrue(output.waiting.wait(1))
            with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary, \
                 self.assertRaisesRegex(RuntimeError, 'did not report readiness'):
                monitor.wait_listening(Path(temporary) / 'stop', threading.Event(), timeout=.01)
            output.release.set()
            monitor.finish()
        self.assertFalse(monitor.listening.is_set())

    def test_roundtrip_and_replay(self):
        self.assertEqual(len(self.packet()), 64)
        self.assertEqual(module.validate_packet(self.packet(), 0), 1)
        with self.assertRaises(ValueError):
            module.validate_packet(self.packet(), 1)

    def test_invalid_packet_never_becomes_input(self):
        for packet in (self.packet(flags=4), self.packet(flags=2), self.packet(x=math.nan),
                       self.packet(x=1.01), self.packet(force=-.1), self.packet(sequence=0),
                       self.packet(timestamp=0), b'x' * 64,
                       self.packet() + b'x', self.packet()[:-1]):
            with self.subTest(packet=packet), self.assertRaises(ValueError):
                module.validate_packet(packet, 0)

    def test_atomic_settings_preserve_other_tuning(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            addon = Path(temporary)
            module.atomic_json(addon / 'qpro-owner.json', {'format': module.OWNER_FORMAT})
            module.atomic_json(addon / 'resources' / 'settings.json',
                               {'smoothingMs': 25, 'enabled': True, 'futureOption': {'keep': [1, 2]}})
            module.set_input_enabled(addon, False, 'joystick')
            saved = json.loads((addon / 'resources' / 'settings.json').read_text())
            self.assertEqual(saved, {'smoothingMs': 25, 'enabled': False, 'mode': 'joystick',
                                     'side': 'both', 'futureOption': {'keep': [1, 2]}})

    def test_selected_side_persists_on_enable_and_disable_without_overwriting_unknown_settings(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            _, addon = self.create_addon(Path(temporary))
            path = addon / 'resources' / 'settings.json'
            module.atomic_json(path, {'smoothingMs': 29, 'futureOption': {'keep': 'exact'}})
            for side in ('both', 'left', 'right'):
                for enabled in (True, False):
                    with self.subTest(side=side, enabled=enabled):
                        module.set_input_enabled(addon, enabled, 'swipe', side)
                        self.assertEqual(json.loads(path.read_text()), {
                            'smoothingMs': 29, 'futureOption': {'keep': 'exact'},
                            'enabled': enabled, 'mode': 'swipe', 'side': side})

    def test_invalid_side_is_rejected_before_settings_or_workers_are_touched(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            _, addon = self.create_addon(Path(temporary))
            settings = addon / 'resources' / 'settings.json'
            before = settings.read_bytes()
            for side in ('Both', 'middle', '', None, True, []):
                with self.subTest(side=side), \
                     patch.object(module, 'owner_is_valid') as owner, \
                     patch.object(module, 'command') as command, \
                     patch.object(module.subprocess, 'Popen') as launch:
                    with self.assertRaisesRegex(ValueError, 'side must be'):
                        module.set_input_enabled(addon, True, side=side)
                    with self.assertRaisesRegex(ValueError, 'side must be'):
                        module.run(SimpleNamespace(side=side), Path(temporary), Path('fake-adb'), 'quest')
                    with self.assertRaisesRegex(ValueError, 'side must be'):
                        module.relay(HERE, Path(temporary), Path('fake-adb'), 'quest', HERE / 'stop',
                                     'trackpad', side=side)
                    owner.assert_not_called()
                    command.assert_not_called()
                    launch.assert_not_called()
                    self.assertEqual(settings.read_bytes(), before)

    def test_supervisor_passes_side_to_thumb_rest_worker_only(self):
        for side in ('both', 'left', 'right'):
            with self.subTest(side=side):
                hands = FakeWorker('HANDS_CLEANUP {"confirmed": true, "problems": []}\n', code=0)
                touchpad = FakeWorker('CONTROLLER_CLEANUP {"reader": "stopped", "inputs": "disabled"}\n', code=0)
                self.run_workers([hands, touchpad], touchpad=True, stopped=True, side=side)
                commands = [call.args[0] for call in self.launch.call_args_list]
                self.assertNotIn('--side', commands[0])
                self.assertEqual(commands[1][commands[1].index('--side') + 1], side)
                self.assertTrue(self.aggregate()['confirmed'])

    def test_main_side_choices_default_and_rejection(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            for side in (None, 'both', 'left', 'right'):
                with self.subTest(side=side), \
                     patch.dict(module.os.environ, {'LOCALAPPDATA': temporary}), \
                     patch.object(module, 'select_target', return_value='quest'), \
                     patch.object(module, 'run') as run:
                    arguments = ['run', '--root', str(HERE), '--touchpad']
                    if side:
                        arguments += ['--side', side]
                    module.main(arguments)
                    self.assertEqual(run.call_args.args[0].side, side or 'both')
            with patch.object(module, 'select_target') as select, \
                 contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as failure:
                module.main(['run', '--root', str(HERE), '--side', 'Both'])
            self.assertEqual(failure.exception.code, 2)
            select.assert_not_called()

    def test_relay_requires_selected_side_validity_and_keeps_packet_bytes(self):
        for mode in ('trackpad', 'joystick', 'swipe', 'mouse'):
            for side, flags, right_flags in (('both', 1, 0), ('both', 0, 1),
                                             ('left', 1, 0), ('right', 0, 1)):
                with self.subTest(mode=mode, side=side, flags=flags):
                    closed = threading.Event()
                    packet = self.packet(flags=flags, right_flags=right_flags)
                    def stopped():
                        closed.set()
                        return b''
                    self.run_relay(ReaderProcess(), RelayStream([packet, stopped]),
                                   parent_closed=closed, mode=mode, side=side)
                    self.assertEqual(self.sent_packets, [packet])
                    self.assertEqual(self.relay_configurations, [(True, mode, side), (False, mode, side)])
                    ready = json.loads(next(line.partition(' ')[2] for line in self.relay_activity.getvalue().splitlines()
                                            if line.startswith('TOUCHPAD_READY ')))
                    self.assertEqual(ready['side'], side)

    def test_relay_unselected_side_cannot_enable_input_or_report_ready(self):
        for side, flags, right_flags in (('left', 0, 1), ('right', 1, 0)):
            with self.subTest(side=side):
                closed = threading.Event()
                def stopped():
                    closed.set()
                    return b''
                self.run_relay(ReaderProcess(),
                               RelayStream([self.packet(flags=flags, right_flags=right_flags), stopped]),
                               parent_closed=closed, side=side)
                self.assertEqual(self.sent_packets, [])
                self.assertEqual(self.relay_configurations, [(False, 'trackpad', side)])
                self.assertNotIn('TOUCHPAD_READY ', self.relay_activity.getvalue())

    def test_non_object_controller_settings_are_rejected_without_overwriting(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            _, addon = self.create_addon(Path(temporary))
            path = addon / 'resources' / 'settings.json'
            for value in ([], None, True, 25, 'enabled'):
                with self.subTest(value=value):
                    module.atomic_json(path, value)
                    before = path.read_bytes()
                    with self.assertRaisesRegex(RuntimeError, 'settings must be a JSON object'):
                        module.set_input_enabled(addon, False)
                    self.assertEqual(path.read_bytes(), before)

    def test_unreadable_controller_settings_have_repair_guidance(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            _, addon = self.create_addon(Path(temporary))
            path = addon / 'resources' / 'settings.json'
            path.write_text('{invalid json')
            with self.assertRaisesRegex(RuntimeError, 'settings could not be read.*Close SteamVR'):
                module.set_input_enabled(addon, False)
            self.assertEqual(path.read_text(), '{invalid json')

    def test_malformed_settings_do_not_skip_relay_reader_and_forward_cleanup(self):
        update = module.set_input_enabled
        def corrupt_settings(addon, state, mode, side):
            if state:
                module.atomic_json(addon / 'resources' / 'settings.json', [])
            update(addon, state, mode, side)
        native = ReaderProcess()
        stream = RelayStream([self.packet()])
        with self.assertRaisesRegex(RuntimeError, 'cleanup needs attention.*settings must be a JSON object'):
            self.run_relay(native, stream=stream, input_update=corrupt_settings)
        self.assertTrue(native.waited)
        self.assertTrue(stream.closed)
        self.assertTrue(native.stdout.closed)
        self.assertEqual(self.sent_packets, [])
        self.assertIn(['fake-adb', '-s', 'quest', 'shell', 'touch', module.REMOTE_STOP], self.relay_commands)
        self.assertIn(['fake-adb', '-s', 'quest', 'forward', '--remove', 'tcp:27063'], self.relay_commands)
        report = json.loads(next(line.partition(' ')[2] for line in self.relay_activity.getvalue().splitlines()
                                 if line.startswith('CONTROLLER_CLEANUP ')))
        self.assertEqual(report['reader'], 'unconfirmed')

    def test_steamvr_paths_reject_malformed_and_relative_runtime_information(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            paths = local / 'openvr' / 'openvrpaths.vrpath'
            for value in ([], None, {}, {'runtime': None}, {'runtime': 'relative'},
                          {'runtime': [None]}, {'runtime': ['relative/SteamVR']}):
                with self.subTest(value=value):
                    module.atomic_json(paths, value)
                    with self.assertRaisesRegex(RuntimeError, 'no valid absolute runtime folder.*Launch SteamVR'):
                        module.steamvr_tool(local)

    def test_steamvr_runtime_tool_reads_absolute_paths_and_reports_missing_information(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            with self.assertRaisesRegex(RuntimeError, 'runtime information could not be read.*Launch SteamVR'):
                module.steamvr_tool(local)
            paths = local / 'openvr' / 'openvrpaths.vrpath'
            tool = local / 'SteamVR' / 'bin' / 'win64' / 'vrpathreg.exe'
            tool.parent.mkdir(parents=True)
            tool.touch()
            module.atomic_json(paths, {'runtime': [str(local / 'missing-runtime'), str(local / 'SteamVR')]})
            self.assertEqual(module.steamvr_tool(local), tool)

    def test_missing_or_relative_localappdata_fails_before_adb_or_setup(self):
        for value in (None, '', 'relative', r'C:relative'):
            with self.subTest(value=value), patch.dict(module.os.environ, {}, clear=True):
                if value is not None:
                    module.os.environ['LOCALAPPDATA'] = value
                with patch.object(module, 'select_target') as target, \
                     patch.object(module, 'install') as install, \
                     contextlib.redirect_stdout(io.StringIO()) as activity:
                    code = module.entry_point(['check', '--root', str(HERE), '--adb', 'fake-adb'])
                self.assertEqual(code, 1)
                self.assertIn('CONTROLLER_ERROR ', activity.getvalue())
                self.assertIn('LOCALAPPDATA', activity.getvalue())
                target.assert_not_called()
                install.assert_not_called()

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

    def test_check_uses_installed_private_python_and_explicit_helper(self):
        output = ('HANDS_CHECK {"compatible": false, "problems": '
                  '["This Virtual Desktop Streamer driver has no validated hand profile."], '
                  '"componentsReady": true, "componentProblems": []}\n')
        result = module.subprocess.CompletedProcess(['fixture-python'], 4, output, '')
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            hands, _ = module.managed_paths(local)
            python = hands / '.venv' / 'Scripts' / 'python.exe'
            python.parent.mkdir(parents=True)
            python.touch()
            (hands / 'frida-server').touch()
            (hands / 'ready.json').touch()
            before = sorted(path.relative_to(local) for path in local.rglob('*'))
            with patch.dict(module.os.environ, {'LOCALAPPDATA': temporary}), \
                 patch.object(module, 'select_target', return_value='quest'), \
                 patch.object(module.subprocess, 'run', return_value=result) as execute, \
                 contextlib.redirect_stdout(io.StringIO()) as activity:
                code = module.entry_point(['check', '--root', str(HERE), '--adb', 'fake-adb'])
            command = execute.call_args.args[0]
            self.assertEqual(command[0], str(python))
            self.assertEqual(command[command.index('--frida-server') + 1], str(hands / 'frida-server'))
            self.assertIn('--check', command)
            self.assertNotIn('--stop-file', command)
            self.assertEqual(code, 4)  # Installed components do not validate a new driver.
            self.assertIn('no validated hand profile', activity.getvalue())
            self.assertEqual(before, sorted(path.relative_to(local) for path in local.rglob('*')))

    def test_check_before_installation_keeps_inspection_read_only(self):
        result = module.subprocess.CompletedProcess(['fixture-python'], 0,
            'HANDS_CHECK {"compatible": true, "problems": [], "componentsReady": false}\n', '')
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            hands, _ = module.managed_paths(local)
            with patch.dict(module.os.environ, {'LOCALAPPDATA': temporary}), \
                 patch.object(module, 'select_target', return_value='quest'), \
                 patch.object(module.subprocess, 'run', return_value=result) as execute, \
                 contextlib.redirect_stdout(io.StringIO()) as activity:
                code = module.entry_point(['check', '--root', str(HERE), '--adb', 'fake-adb'])
            command = execute.call_args.args[0]
            self.assertEqual(command[0], module.sys.executable)
            self.assertEqual(command[command.index('--frida-server') + 1], str(hands / 'frida-server'))
            self.assertEqual(code, 0)
            self.assertFalse(hands.exists())
            self.assertIn('"frida": "not-installed"', activity.getvalue())

    @unittest.skipUnless(module.os.name == 'nt', 'The private hand interpreter is a Windows environment.')
    def test_check_reads_components_from_real_private_interpreter(self):
        # A temporary venv has a fixture Frida module; the parent runtime does
        # not. Running the child proves import lookup and helper selection,
        # without touching installed components, a headset or SteamVR.
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            local = Path(temporary)
            hands, _ = module.managed_paths(local)
            venv.EnvBuilder(with_pip=False).create(hands / '.venv')
            site_packages = hands / '.venv' / 'Lib' / 'site-packages'
            (site_packages / 'frida.py').write_text("__version__ = '" + module.FRIDA_VERSION + "'\n")
            (hands / 'frida-server').write_bytes(b'fixture helper')
            (hands / 'ready.json').write_text('{}')
            root = local / 'fixture-release'
            controller = root / 'hybrid' / 'controller.py'
            controller.parent.mkdir(parents=True)
            controller.write_text(
                'import argparse, json\n'
                'from pathlib import Path\n'
                'import frida\n'
                'parser = argparse.ArgumentParser()\n'
                'parser.add_argument("--target")\n'
                'parser.add_argument("--adb")\n'
                'parser.add_argument("--check", action="store_true")\n'
                'parser.add_argument("--frida-server", type=Path)\n'
                'args = parser.parse_args()\n'
                'ready = args.check and args.frida_server.is_file()\n'
                'print("HANDS_CHECK " + json.dumps({"compatible": True, "problems": [], '
                '"fridaVersion": frida.__version__, "componentsReady": ready, '
                '"componentProblems": [] if ready else ["helper missing"]}))\n')
            with patch.dict(module.os.environ, {'LOCALAPPDATA': temporary}), \
                 patch.object(module, 'select_target', return_value='quest'), \
                 contextlib.redirect_stdout(io.StringIO()) as activity:
                code = module.entry_point(['check', '--root', str(root), '--adb', 'fake-adb'])
            report = json.loads(next(line.partition(' ')[2] for line in activity.getvalue().splitlines()
                                     if line.startswith('HANDS_CHECK ')))
            self.assertEqual(code, 0, activity.getvalue())
            self.assertEqual(report['fridaVersion'], module.FRIDA_VERSION)
            self.assertTrue(report['componentsReady'])
            self.assertEqual(report['componentProblems'], [])

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
        with self.assertRaisesRegex(RuntimeError, 'cleanup was not confirmed.*original readiness timeout') as failure:
            self.run_workers([FakeWorker(output, code=5)])
        self.assertFalse(self.aggregate()['confirmed'])
        self.assertIn('script has been destroyed', str(failure.exception))
        self.assertIn('script has been destroyed', '; '.join(self.aggregate()['problems']))

    def test_detachment_context_survives_before_and_after_the_original_stop_cause(self):
        detached = 'HANDS_DETACHED ' + json.dumps({
            'session': 'headset-virtual-desktop', 'process': 100,
            'reason': 'process-terminated', 'duringCleanup': False,
            'crashSummary': 'Access violation at C:\\fixture\\private.dmp\nprivate dump',
            'report': 'raw private dump'}) + '\n'
        cause = 'HANDS_STOP_CAUSE {"error": "A tracking process disconnected"}\n'
        cleanup = 'HANDS_CLEANUP_FAILED {"error": "script has been destroyed"}\n'
        for output in (detached + cause + cleanup, cause + detached + cleanup):
            with self.subTest(output=output), self.assertRaisesRegex(
                    RuntimeError, 'Original failure: hands: A tracking process disconnected.*session=headset-virtual-desktop'):
                self.run_workers([FakeWorker(output, code=5)])
            report = self.aggregate()
            self.assertFalse(report['confirmed'])
            details = report['problems'][0]
            self.assertIn('process=100', details)
            self.assertIn('reason=process-terminated', details)
            self.assertIn('crash=Access violation at [path omitted]', details)
            self.assertIn('duringCleanup=false', details)
            self.assertNotIn('private', details)
            self.assertNotIn('raw', details)
            self.assertNotIn('C:', details)
            self.assertEqual(details.count('Detached '), 1)

    def test_stop_cause_nested_detachment_enriches_aggregate_without_a_separate_event(self):
        output = 'HANDS_STOP_CAUSE ' + json.dumps({
            'error': 'Original disconnect', 'detached': [
                {'session': 'steamvr', 'process': 'vrserver.exe', 'reason': 'process-terminated',
                 'crashSummary': 'Access violation\nprivate report'}]}) + '\n'
        with self.assertRaisesRegex(RuntimeError, 'Original disconnect.*process=vrserver.exe'):
            self.run_workers([FakeWorker(output, code=5)])
        details = self.aggregate()['problems'][0]
        self.assertIn('session=steamvr', details)
        self.assertIn('crash=Access violation', details)
        self.assertNotIn('private', details)

    def test_detachment_diagnostics_are_bounded_and_do_not_confirm_cleanup(self):
        worker = module.WorkerOutput(FakeWorker(''), 'hands')
        for index in range(20):
            worker._observe('HANDS_DETACHED ' + json.dumps({
                'session': 's' * 200, 'process': 'vrserver.exe',
                'reason': 'process-terminated-' + str(index), 'crashSummary': 'x' * 1000}))
        self.assertEqual(len(worker.context_reasons), 4)
        self.assertLess(len(worker.failure_reason), 2000)
        self.assertIsNone(worker.cleanup_confirmed)
        for payload in ('[]', '{"reason": true}', 'invalid-json'):
            self.assertIsNone(module.failure_reason('HANDS_DETACHED ' + payload))

    def test_long_original_failure_cannot_erase_bounded_detachment_and_cleanup_context(self):
        output = 'HANDS_STOP_CAUSE ' + json.dumps({'error': 'original failure ' + 'x' * 5000}) + '\n'
        for index in range(4):
            output += 'HANDS_DETACHED ' + json.dumps({
                'session': 'headset-' + str(index), 'process': 10 ** 1000 + index,
                'reason': 'process-terminated-' + str(index), 'crashSummary': 'c' * 1000}) + '\n'
        output += 'HANDS_CLEANUP_FAILED ' + json.dumps({
            'error': 'restore failed', 'phase': 'cleanup:status:optical-hands'}) + '\n'
        with self.assertRaisesRegex(RuntimeError, 'original failure.*phase=cleanup:status:optical-hands') as failure:
            self.run_workers([FakeWorker(output, code=5)])
        details = self.aggregate()['problems'][0]
        self.assertLess(len(details), 4096)
        for index in range(4):
            self.assertIn('session=headset-' + str(index), details)
            self.assertIn('session=headset-' + str(index), str(failure.exception))
        self.assertIn('phase=cleanup:status:optical-hands', details)

    def test_nested_detachments_and_phase_survive_long_primary_and_cleanup_errors(self):
        output = 'HANDS_STOP_CAUSE ' + json.dumps({
            'error': 'original failure ' + 'x' * 5000,
            'detached': [{'session': 'nested-' + str(index), 'process': 100 + index,
                          'reason': 'process-terminated'} for index in range(4)]}) + '\n'
        output += 'HANDS_CLEANUP_FAILED ' + json.dumps({
            'error': 'restore failure ' + 'y' * 5000,
            'phase': 'cleanup:status:optical-hands', 'progressStaleSeconds': 31.5}) + '\n'
        with self.assertRaisesRegex(RuntimeError, 'original failure.*phase=cleanup:status:optical-hands'):
            self.run_workers([FakeWorker(output, code=5)])
        details = '; '.join(self.aggregate()['problems'])
        for index in range(4):
            self.assertIn('session=nested-' + str(index), details)
        self.assertIn('restore failure', details)
        self.assertIn('progress stale seconds=31.5', details)
        self.assertTrue(all(len(problem) < 4096 for problem in self.aggregate()['problems']))

    def test_watchdog_phase_survives_an_earlier_failure_and_keeps_cleanup_unconfirmed(self):
        output = ('HANDS_STOP_CAUSE {"error": "original readiness timeout"}\n'
                  'HANDS_CLEANUP_FAILED ' + json.dumps({
                      'error': 'The hand supervisor stopped responding',
                      'phase': 'cleanup:restoration-status:optical-hands',
                      'progressStaleSeconds': 31.5, 'lastStatusAgeSeconds': 38.2}) + '\n')
        with self.assertRaisesRegex(RuntimeError, 'original readiness timeout.*phase=cleanup:restoration-status:optical-hands'):
            self.run_workers([FakeWorker(output, code=5)])
        details = self.aggregate()['problems'][0]
        self.assertIn('progress stale seconds=31.5', details)
        self.assertIn('last status age seconds=38.2', details)
        self.assertFalse(self.aggregate()['confirmed'])
        for value in (True, float('inf'), -1, 10 ** 1000):
            reason = module.failure_reason('HANDS_CLEANUP_FAILED ' + json.dumps({
                'error': 'watchdog', 'phase': 'status:optical-hands', 'progressStaleSeconds': value}))
            self.assertIn('phase=status:optical-hands', reason)
            self.assertNotIn('progress stale seconds', reason)

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

    def test_parent_stop_during_poll_preserves_successful_worker_cleanup(self):
        parent_closed = threading.Event()
        child = FakeWorker('HANDS_CLEANUP {"confirmed": true, "problems": []}\n', code=0)
        def poll():
            parent_closed.set()
            return child.returncode
        child.poll = poll
        self.run_workers([child], parent_closed=parent_closed)
        self.assertTrue(self.aggregate()['confirmed'])
        self.assertTrue(child.stdin.closed and child.stdout.closed)

    def test_parent_stop_during_poll_does_not_hide_a_worker_failure(self):
        parent_closed = threading.Event()
        child = FakeWorker('HANDS_FAILED {"error": "tracking process disconnected"}\n'
                           'HANDS_CLEANUP {"confirmed": true, "problems": []}\n', code=1)
        def poll():
            parent_closed.set()
            return child.returncode
        child.poll = poll
        with self.assertRaisesRegex(module.WorkerFailure, 'tracking process disconnected'):
            self.run_workers([child], parent_closed=parent_closed)
        self.assertTrue(self.aggregate()['confirmed'])

    def test_worker_output_close_failure_vetoes_cleanup_success(self):
        output = CloseFailureOutput('HANDS_CLEANUP {"confirmed": true, "problems": []}\n')
        with self.assertRaisesRegex(RuntimeError, 'cleanup was not confirmed'):
            self.run_workers([FakeWorker(output, code=0)], stopped=True)
        report = self.aggregate()
        self.assertFalse(report['confirmed'])
        self.assertTrue(any('Worker output close failed: fixture close failed' in reason
                            for reason in report['problems']))

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
