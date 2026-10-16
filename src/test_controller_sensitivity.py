"""Offline Swipe preference/launch/settings checks; no device or installed driver."""
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
import contextlib
import io
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest

import test_controller_components as fixtures

module = fixtures.module
HERE = Path(__file__).resolve().parent


class SensitivityTests(unittest.TestCase):
    def setUp(self):
        (HERE / 'artifacts').mkdir(parents=True, exist_ok=True)

    def test_native_gain_bounds_and_nonfinite_inputs(self):
        self.assertIsNone(module.validate_swipe_gain(None))
        for gain in (.01, .015, .06, .18, 1, 5):
            self.assertEqual(module.validate_swipe_gain(gain), float(gain))
        for invalid in (True, False, '0.06', [], {}, -.1, 0, .009, 5.001,
                        math.nan, math.inf, -math.inf, 10 ** 1000):
            with self.subTest(invalid=repr(invalid)[:80]), self.assertRaisesRegex(ValueError, 'finite number'):
                module.validate_swipe_gain(invalid)

    def test_explicit_swipe_gain_merges_without_changing_other_tuning(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            helper = fixtures.ComponentTests()
            _, addon = helper.create_addon(Path(temporary))
            path = addon / 'resources' / 'settings.json'
            original = {'smoothingMs': 29, 'swipeGain': .22, 'mouseScale': 765,
                        'futureOption': {'keep': ['exact']}}
            for enabled in (True, False):
                module.atomic_json(path, original)
                module.set_input_enabled(addon, enabled, 'swipe', 'left', swipe_gain=.06)
                self.assertEqual(json.loads(path.read_text()), dict(original, enabled=enabled,
                    mode='swipe', side='left', swipeGain=.06))
            for mode, gain in (('trackpad', .06), ('joystick', .06), ('mouse', .06), ('swipe', None)):
                module.atomic_json(path, original)
                module.set_input_enabled(addon, True, mode, 'right', swipe_gain=gain)
                self.assertEqual(json.loads(path.read_text()), dict(original, enabled=True, mode=mode, side='right'))

    def test_invalid_gain_stops_before_settings_probe_or_worker(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            root = Path(temporary)
            helper = fixtures.ComponentTests()
            _, addon = helper.create_addon(root)
            path = addon / 'resources' / 'settings.json'
            before = path.read_bytes()
            for gain in (True, math.nan, math.inf, 0, 5.001):
                with self.subTest(gain=gain), patch.object(module, 'owner_is_valid') as owner, \
                     patch.object(module, 'verify_addon_current') as verify, \
                     patch.object(module, 'command') as command, \
                     patch.object(module.subprocess, 'Popen') as launch:
                    with self.assertRaisesRegex(ValueError, 'finite number'):
                        module.set_input_enabled(addon, True, 'swipe', swipe_gain=gain)
                    with self.assertRaisesRegex(ValueError, 'finite number'):
                        module.run(SimpleNamespace(side='both', swipe_gain=gain), root, Path('fake-adb'), 'quest')
                    with self.assertRaisesRegex(ValueError, 'finite number'):
                        module.relay(root, root, Path('fake-adb'), 'quest', root / 'stop', 'swipe', swipe_gain=gain)
                    owner.assert_not_called()
                    verify.assert_not_called()
                    command.assert_not_called()
                    launch.assert_not_called()
                    self.assertEqual(path.read_bytes(), before)

    def test_supervisor_passes_gain_only_to_swipe_thumb_rest_worker(self):
        for mode, gain in (('swipe', .06), ('swipe', None), ('trackpad', .06), ('joystick', .06), ('mouse', .06)):
            with self.subTest(mode=mode, gain=gain):
                helper = fixtures.ComponentTests()
                workers = [fixtures.FakeWorker('HANDS_CLEANUP {"confirmed": true, "problems": []}\n', code=0),
                    fixtures.FakeWorker('CONTROLLER_CLEANUP {"reader": "stopped", "inputs": "disabled"}\n', code=0)]
                helper.run_workers(workers, touchpad=True, stopped=True, mode=mode, swipe_gain=gain)
                commands = [call.args[0] for call in helper.launch.call_args_list]
                self.assertNotIn('--swipe-gain', commands[0])
                if mode == 'swipe' and gain is not None:
                    self.assertEqual(commands[1][commands[1].index('--swipe-gain') + 1], '0.06')
                else:
                    self.assertNotIn('--swipe-gain', commands[1])
                self.assertTrue(helper.aggregate()['confirmed'])

    def test_relay_carries_gain_to_enable_and_stop_without_changing_packets(self):
        for mode, gain in (('swipe', .06), ('swipe', None), ('trackpad', .06), ('mouse', .06)):
            with self.subTest(mode=mode, gain=gain):
                helper = fixtures.ComponentTests()
                closed = threading.Event()
                packet = helper.packet()
                def stop():
                    closed.set()
                    return b''
                helper.run_relay(fixtures.ReaderProcess(), fixtures.RelayStream([packet, stop]),
                    parent_closed=closed, mode=mode, swipe_gain=gain)
                self.assertEqual(helper.sent_packets, [packet])
                self.assertEqual(helper.relay_enabled, [True, False])
                self.assertEqual(helper.relay_swipe_gains,
                    [.06, .06] if mode == 'swipe' and gain is not None else [None, None])

    def test_cli_rejects_bad_gain_before_target_probe(self):
        for gain in ('nan', 'inf', '-inf', '0', '5.001', 'not-a-number'):
            with self.subTest(gain=gain), patch.object(module, 'select_target') as target, \
                 contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                module.main(['run', '--root', str(HERE), '--swipe-gain=' + gain])
            self.assertEqual(error.exception.code, 2)
            target.assert_not_called()

    def test_cli_keeps_gain_optional_for_existing_callers(self):
        with tempfile.TemporaryDirectory(dir=HERE / 'artifacts') as temporary:
            for gain in (None, '0.06', '0.015', '0.18'):
                with self.subTest(gain=gain), patch.dict(module.os.environ, {'LOCALAPPDATA': temporary}), \
                     patch.object(module, 'select_target', return_value='quest'), patch.object(module, 'run') as run:
                    arguments = ['run', '--root', str(HERE), '--touchpad', '--mode', 'swipe']
                    if gain is not None:
                        arguments += ['--swipe-gain', gain]
                    module.main(arguments)
                    self.assertEqual(run.call_args.args[0].swipe_gain, None if gain is None else float(gain))

    @unittest.skipUnless(os.name == 'nt', 'Windows PowerShell wrapper')
    def test_actual_powershell_wrapper_passes_invariant_gain_only_in_swipe(self):
        powershell = Path(os.environ['SystemRoot']) / 'System32' / 'WindowsPowerShell' / 'v1.0' / 'powershell.exe'
        with tempfile.TemporaryDirectory(prefix='swipe wrapper ', dir=HERE / 'artifacts') as temporary:
            root = Path(temporary)
            wrapper = root / 'controller-input.ps1'
            shutil.copyfile(HERE / 'controller-input.ps1', wrapper)
            (root / 'runtime-python.ps1').write_text('function Test-QproPython312 { param($candidate) return $true }\n')
            manager = root / 'controller-input' / 'manage.py'
            manager.parent.mkdir()
            manager.write_text('import json, sys\nprint("WRAPPER_ARGS " + json.dumps(sys.argv[1:]))\n')
            environment = dict(os.environ, QPRO_PYTHON=sys.executable)
            for mode, gain in (('swipe', '0.06'), ('swipe', None), ('trackpad', '0.06'), ('mouse', '0.06')):
                arguments = [str(powershell), '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                    '-File', str(wrapper), '-Action', 'run', '-Mode', mode]
                if gain is not None:
                    arguments += ['-SwipeGain', gain]
                result = subprocess.run(arguments, capture_output=True, text=True, env=environment,
                    timeout=15, creationflags=module.NO_WINDOW)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                line = next(line for line in result.stdout.splitlines() if line.startswith('WRAPPER_ARGS '))
                forwarded = json.loads(line.partition(' ')[2])
                if mode == 'swipe' and gain is not None:
                    self.assertEqual(forwarded[forwarded.index('--swipe-gain') + 1], '0.06')
                else:
                    self.assertNotIn('--swipe-gain', forwarded)
            for gain in ('NaN', '0', '5.001'):
                result = subprocess.run([str(powershell), '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                    '-File', str(wrapper), '-Action', 'run', '-Mode', 'swipe', '-SwipeGain', gain],
                    capture_output=True, text=True, env=environment, timeout=15, creationflags=module.NO_WINDOW)
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn('WRAPPER_ARGS ', result.stdout)


if __name__ == '__main__':
    unittest.main()
