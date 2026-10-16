"""Controller reinstall preference transactions on fake files; no installation."""
from contextlib import ExitStack, redirect_stdout
from pathlib import Path
from unittest.mock import patch
import io
import json
import tempfile
import unittest

import test_controller_components as fixtures


module = fixtures.module
HERE = Path(__file__).resolve().parent


class InstallSettingsTests(unittest.TestCase):
    def setUp(self):
        (HERE / 'artifacts').mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=HERE / 'artifacts')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.local = self.root / 'local'
        hands, self.addon = module.managed_paths(self.local)
        python = hands / '.venv/Scripts/python.exe'
        python.parent.mkdir(parents=True)
        python.write_bytes(b'fixture python never launched')
        self.release = self.root / 'release'
        driver = self.release / 'controller-input/addon/bin/win64/driver_qpro_controller.dll'
        driver.parent.mkdir(parents=True)
        driver.write_bytes(b'fixture new driver never loaded')
        self.tool = self.root / 'runtime/bin/win64/vrpathreg.exe'
        self.settings = self.addon / 'resources/settings.json'

    def previous(self, content):
        module.atomic_json(self.addon / 'qpro-owner.json', {'format': module.OWNER_FORMAT})
        self.settings.parent.mkdir(parents=True, exist_ok=True)
        self.settings.write_bytes(content if isinstance(content, bytes) else json.dumps(content).encode())
        (self.addon / 'old-driver-marker').write_bytes(b'fixture original driver')

    def install(self, command=None):
        with ExitStack() as stack:
            stack.enter_context(patch.object(module, 'assert_steamvr_closed'))
            stack.enter_context(patch.object(module, 'steamvr_tool', return_value=self.tool))
            self.download = stack.enter_context(patch.object(module, 'download_verified'))
            stack.enter_context(patch.object(module.lzma, 'open', return_value=io.BytesIO(b'fixture server')))
            stack.enter_context(patch.object(module, 'command', side_effect=command or (lambda *args, **kwargs: '')))
            stack.enter_context(patch.object(module, 'prepare_dashboard'))
            stack.enter_context(patch.object(module, 'prepare_vrchat'))
            stack.enter_context(redirect_stdout(io.StringIO()))
            module.install(self.release, self.local)

    def test_reinstall_keeps_swipe_gain_and_other_controller_tuning_but_disables_input(self):
        previous = {'enabled': True, 'mode': 'swipe', 'side': 'right', 'swipeGain': .06,
                    'smoothingMs': 37, 'mouseScale': 765, 'joystickSpan': .7, 'clickOn': .8,
                    'clickOff': .3, 'glideMs': 250, 'ignoreRestingThumb': True,
                    'restingSize': 122, 'intentionalForce': .4}
        self.previous(previous)
        exact = self.settings.read_bytes()
        self.install()
        self.assertEqual(json.loads(self.settings.read_text()), dict(previous, enabled=False))
        recoveries = list(self.addon.parent.glob('controller-addon-backup-*'))
        self.assertEqual(len(recoveries), 1)
        self.assertEqual((recoveries[0] / 'resources/settings.json').read_bytes(), exact)

    def test_first_install_keeps_existing_neutral_trackpad_schema(self):
        self.install()
        self.assertEqual(json.loads(self.settings.read_text()),
                         {'enabled': False, 'mode': 'trackpad'})

    def test_legacy_optional_mode_and_side_remain_absent_without_dropping_tuning(self):
        self.previous({'smoothingMs': 25, 'swipeGain': .06})
        self.install()
        self.assertEqual(json.loads(self.settings.read_text()),
                         {'enabled': False, 'smoothingMs': 25, 'swipeGain': .06})

    def test_malformed_previous_settings_refused_before_downloads_or_rename(self):
        cases = (b'{broken', b'[]', b'{"mode":"unknown"}', b'{"side":"any"}',
                 b'{"swipeGain":true}', b'{"swipeGain":NaN}', b'{"swipeGain":5.001}',
                 b'{"enabled":1}', b'{"swipeGain":.06}', b'{"swipeGain":1,"swipeGain":2}',
                 b'{"clickOn":0.2,"clickOff":0.3}', b'{"smoothingMs":251}',
                 b'{"ignoreRestingThumb":0}', b'{"restingSize":513}',
                 b'{"mouseScale":-1}', b'{"intentionalForce":1.01}',
                 b'{"glideMs":2001}', b'{"joystickSpan":0}', b'{"futureOption":1}',
                 b'{"mode":[]}', b'{"swipeGain":1e999}', b' ' * 8193)
        for content in cases:
            with self.subTest(content=content[:100]):
                self.previous(content)
                with self.assertRaisesRegex(RuntimeError, 'settings.*kept|settings.*unchanged'):
                    self.install()
                self.assertEqual(self.settings.read_bytes(), content)
                self.assertEqual(list(self.addon.parent.glob('controller-addon-backup-*')), [])
                self.download.assert_not_called()

    def test_registration_failure_restores_original_settings_bytes_exactly(self):
        content = b'{\n  "enabled": true, "mode": "swipe", "side": "left", "swipeGain": 0.06\n}\n'
        self.previous(content)
        def fail_registration(arguments, **kwargs):
            if arguments[1] == 'adddriver':
                self.assertEqual(json.loads(self.settings.read_text())['swipeGain'], .06)
                self.assertIs(json.loads(self.settings.read_text())['enabled'], False)
                raise RuntimeError('fixture registration failure')
            return ''
        with self.assertRaisesRegex(RuntimeError, 'fixture registration failure'):
            self.install(fail_registration)
        self.assertEqual(self.settings.read_bytes(), content)
        self.assertEqual((self.addon / 'old-driver-marker').read_bytes(), b'fixture original driver')
        self.assertEqual(list(self.addon.parent.glob('controller-addon-backup-*')), [])

    def test_native_float_boundary_and_collapsed_click_thresholds_refuse_before_changes(self):
        for content in (b'{"joystickSpan":0.1}',
                        b'{"clickOff":0.5000000001,"clickOn":0.5000000002}'):
            with self.subTest(content=content):
                self.previous(content)
                with self.assertRaisesRegex(RuntimeError, 'joystickSpan|clickOff'):
                    self.install()
                self.assertEqual(self.settings.read_bytes(), content)
                self.assertEqual(list(self.addon.parent.glob('controller-addon-backup-*')), [])
                self.download.assert_not_called()

    def test_native_float_boundary_values_remain_exact_when_accepted(self):
        previous = {'joystickSpan': 0.10000000149011612, 'swipeGain': 0.009999999776482582,
                    'clickOff': .5, 'clickOn': 0.5000000596046448}
        self.previous(previous)
        self.install()
        self.assertEqual(json.loads(self.settings.read_text()), dict(previous, enabled=False))

    def test_settings_changed_during_download_are_kept_without_tree_rename(self):
        self.previous({'swipeGain': .06})
        changed = b'{"swipeGain":0.18,"mode":"swipe","side":"left"}'
        with patch.object(module, 'download_verified', side_effect=lambda *args: self.settings.write_bytes(changed)):
            # This call uses a dedicated fake setup because install() patches
            # downloads itself; no actual network/process boundary is entered.
            with patch.object(module, 'assert_steamvr_closed'), \
                 patch.object(module, 'steamvr_tool', return_value=self.tool), \
                 patch.object(module.lzma, 'open', return_value=io.BytesIO(b'fixture server')), \
                 patch.object(module, 'command', return_value=''), \
                 redirect_stdout(io.StringIO()), \
                 self.assertRaisesRegex(RuntimeError, 'settings changed.*kept'):
                module.install(self.release, self.local)
        self.assertEqual(self.settings.read_bytes(), changed)
        self.assertEqual(list(self.addon.parent.glob('controller-addon-backup-*')), [])


if __name__ == '__main__':
    unittest.main()
