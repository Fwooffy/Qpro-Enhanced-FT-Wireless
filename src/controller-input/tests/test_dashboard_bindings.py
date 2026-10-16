"""Dashboard binding transactions on fake runtime files; no SteamVR or ADB."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('dashboard_bindings', ROOT / 'dashboard_bindings.py')
helper = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(helper)


def encoded(value):
    return json.dumps(value, indent=2).encode()


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(encoded(value))


def binding_fixture():
    # Deliberately artificial data, not copied Valve content.
    return {'app_key': helper.APP_KEY, 'controller_type': 'oculus_touch',
            'category': 'steamvr_input', 'action_manifest_version': 0,
            'name': 'Fixture dashboard', 'unknown_metadata': {'keep': True},
            'bindings': {
                '/actions/system': {'sources': [{'path': '/user/hand/left/input/system',
                    'mode': 'button', 'inputs': {'click': {'output': '/actions/system/in/open'}}}],
                    'chords': [{'unchanged': 'fixture'}]},
                **{group: {'sources': [{'path': '/user/hand/left/input/joystick', 'mode': 'scroll',
                    'inputs': {'scroll': {'output': group + '/in/scroll'}}}], 'extra': 'keep'}
                   for group in helper.SETS}}}


def manifest_fixture():
    return {'actions': [{'name': group + '/in/Scroll', 'type': 'vector2'}
                        for group in helper.SETS]}


class DashboardTests(unittest.TestCase):
    def setUp(self):
        (ROOT.parent / 'artifacts').mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=ROOT.parent / 'artifacts')
        self.root = Path(self.temporary.name)
        self.local = self.root / 'local'
        self.runtime = self.root / 'runtime'
        self.config = self.root / 'config'
        self.addon = self.local / 'QproFaceTracking/controller-addon'
        write(self.local / 'openvr/openvrpaths.vrpath',
              {'config': [str(self.config)], 'runtime': [str(self.runtime)]})
        self.settings = self.config / 'steamvr.vrsettings'
        write(self.settings, {'steam.app.fixture': {'customBinding': 'keep'}, 'steamvr': {'keep': True}})
        self.source = self.runtime / 'drivers/oculus/resources/input/vrcompositor_bindings_touch.json'
        write(self.source, binding_fixture())
        write(self.runtime / 'resources/config/vrcompositor_actions.json', manifest_fixture())
        write(self.addon / 'qpro-owner.json', {'format': 'qpro-controller-addon-v1'})
        self.profile = {'controller_type': 'oculus_touch', 'resource_root': 'qpro_controller',
                        'input_source': {helper.SOURCE: {'type': 'joystick', 'touch': True},
                                         '/input/joystick': {'type': 'joystick', 'click': True}},
                        'default_bindings': [{'app_key': 'fixture.game', 'binding_url': 'untouched.json'}]}
        write(self.addon / helper.PROFILE, self.profile)
        self.original_profile = (self.addon / helper.PROFILE).read_bytes()
        self.original_settings = self.settings.read_bytes()
        self.original_source = self.source.read_bytes()

    def tearDown(self):
        self.temporary.cleanup()

    def read(self, relative):
        return json.loads((self.addon / relative).read_text())

    def prepare(self, **kwargs):
        return helper.prepare(self.local, self.runtime, self.addon, **kwargs)

    def test_install_adds_only_dedicated_sources_and_scoped_default(self):
        result = self.prepare()
        self.assertEqual(result['state'], 'prepared-default')
        self.assertFalse(result['automatic'])
        actual = self.read(helper.BINDING)
        original = binding_fixture()
        for group in helper.SETS:
            additions = actual['bindings'][group]['sources'][-2:]
            self.assertEqual([source['path'] for source in additions],
                             ['/user/hand/' + side + helper.SOURCE for side in ('left', 'right')])
            self.assertTrue(all(source['mode'] == 'joystick' for source in additions))
            self.assertTrue(all(source['parameters'] == {'deadzone_pct': '0', 'maxzone_pct': '100'}
                                for source in additions))
            self.assertTrue(all(source['inputs'] == {'position': {'output': group + '/in/scroll'}}
                                for source in additions))
            del actual['bindings'][group]['sources'][-2:]
        actual['name'] = original['name']
        self.assertEqual(actual, original)
        profile = self.read(helper.PROFILE)
        self.assertEqual(profile['default_bindings'][0], self.profile['default_bindings'][0])
        self.assertEqual(profile['default_bindings'][1],
                         {'app_key': helper.APP_KEY, 'binding_url': helper.BINDING.name})
        self.assertEqual(profile['input_source'], self.profile['input_source'])
        self.assertEqual(self.settings.read_bytes(), self.original_settings)
        self.assertEqual(self.source.read_bytes(), self.original_source)
        state = self.read(helper.OWNER)
        self.assertEqual(state['originalProfileSha256'], helper._sha(self.original_profile))
        self.assertEqual((self.addon / helper.BACKUP / 'original-profile.json').read_bytes(), self.original_profile)
        self.assertEqual((self.addon / helper.BACKUP / 'source-binding.json').read_bytes(), self.original_source)

    def test_repeated_install_is_idempotent(self):
        self.prepare()
        before = {str(path.relative_to(self.addon)): path.read_bytes()
                  for path in self.addon.rglob('*') if path.is_file()}
        self.prepare()
        after = {str(path.relative_to(self.addon)): path.read_bytes()
                 for path in self.addon.rglob('*') if path.is_file()}
        self.assertEqual(before, after)

    def test_scroll_route_guards_do_not_change_native_parameters_or_input_document(self):
        binding = binding_fixture()
        for index, group in enumerate(binding['bindings'].values()):
            for source in group.get('sources', []):
                source['parameters'] = {'deadzone_pct': str(14 + index), 'maxzone_pct': '82',
                                        'exponent': '0.75', 'unrelated_native_option': 'keep'}
        before = encoded(binding)
        merged = helper.merge_binding(binding, manifest_fixture())
        self.assertEqual(encoded(binding), before)
        for name, group in binding['bindings'].items():
            actual = merged['bindings'][name]
            if name in helper.SETS:
                self.assertEqual(actual['sources'][:-2], group['sources'])
                self.assertTrue(all(source['parameters'] == {'deadzone_pct': '0', 'maxzone_pct': '100'}
                                    for source in actual['sources'][-2:]))
            else:
                self.assertEqual(actual, group)

    def test_internal_verified_key_selects_only_dashboard_namespace(self):
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        settings = {'steam.app.438100': {key: 'vr-input-workshop://fixture-vrchat'},
                    'steamvr': {'existing': True}, helper.APP_KEY: {'unrelated_option': 'keep'}}
        write(self.settings, settings)
        before = self.settings.read_bytes()
        result = self.prepare(verified_key=key)
        expected = json.loads(before)
        expected[helper.APP_KEY][key] = (self.addon / helper.BINDING).as_uri()
        self.assertEqual(json.loads(self.settings.read_text()), expected)
        self.assertEqual(result['state'], 'prepared-selection')
        self.assertEqual(result['selectionProof'], 'explicit-session-observation')
        self.assertIn('do not confirm live activation', result['message'])
        state = self.read(helper.OWNER)
        self.assertEqual(state['selectionProof'], 'explicit-session-observation')
        self.assertEqual((self.addon / helper.INSTALL_SETTINGS).read_bytes(), before)
        self.assertEqual(json.loads((self.addon / helper.INSTALL_SELECTION).read_text())['choices'], {})

    def test_public_fallback_uses_one_verbatim_key_from_recognized_counterpart(self):
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        settings = {'steam.app.438100': {key: 'vr-input-workshop://fixture-vrchat'},
                    'steamvr': {'keep': True}}
        write(self.settings, settings)
        result = self.prepare()
        self.assertEqual(result['state'], 'prepared-selection')
        self.assertTrue(result['automatic'])
        self.assertEqual(result['selectionProof'], 'observed-other-app-current-key')
        self.assertEqual(result['selectionEvidence'], {'appKey': 'steam.app.438100', 'key': key})
        actual = json.loads(self.settings.read_text())
        self.assertEqual(actual['steam.app.438100'], settings['steam.app.438100'])
        self.assertEqual(actual['steamvr'], settings['steamvr'])
        self.assertEqual(actual[helper.APP_KEY], {key: (self.addon / helper.BINDING).as_uri()})
        original = json.loads((self.addon / helper.BACKUP / 'selection.json').read_text())
        self.assertEqual(original['choices'], {})
        self.assertEqual(self.read(helper.OWNER)['selectionEvidence'], result['selectionEvidence'])

    def test_public_counterpart_evidence_and_absence_stay_stable_across_retry_upgrade_and_uninstall(self):
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        write(self.settings, {'steam.app.438100': {key: 'vr-input-workshop://fixture-vrchat'}})
        first = self.prepare()
        before = {str(path.relative_to(self.addon)): path.read_bytes()
                  for path in self.addon.rglob('*') if path.is_file()}
        self.assertEqual(self.prepare(), first)
        self.assertEqual(before, {str(path.relative_to(self.addon)): path.read_bytes()
                                 for path in self.addon.rglob('*') if path.is_file()})
        previous = self.addon.with_name('controller-addon-backup-fixture')
        self.addon.rename(previous)
        write(self.addon / 'qpro-owner.json', {'format': 'qpro-controller-addon-v1'})
        write(self.addon / helper.PROFILE, self.profile)
        upgraded = self.prepare(previous=previous)
        self.assertEqual(upgraded['selectionProof'], 'observed-other-app-current-key')
        self.assertEqual(upgraded['selectionEvidence'], first['selectionEvidence'])
        recovery = self.addon.with_name('controller-addon-uninstalled-fixture')
        self.addon.rename(recovery)
        helper.restore(recovery, self.local, self.runtime, self.addon)
        self.assertNotIn(key, json.loads(self.settings.read_text())[helper.APP_KEY])
        self.assertEqual(json.loads(self.settings.read_text())['steam.app.438100'][key],
                         'vr-input-workshop://fixture-vrchat')

    def test_public_fallback_never_replaces_actual_dashboard_custom_choice(self):
        target_key = 'oculus_touch_CurrentURL_steamvrinput'
        other_key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        settings = {helper.APP_KEY: {target_key: 'vr-input-workshop://my-dashboard'},
                    'steam.app.438100': {other_key: 'vr-input-workshop://fixture-vrchat'}}
        write(self.settings, settings)
        before = self.settings.read_bytes()
        result = self.prepare()
        self.assertEqual(result['state'], 'custom-binding-kept')
        self.assertEqual(self.settings.read_bytes(), before)

    def test_discovery_requires_unique_current_key_not_autosave_previous_or_unknown_app(self):
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        other = 'oculus_touch_CurrentURL_steamvrinput'
        cases = (
            {},
            {'steam.app.unrecognized': {key: 'file:///fixture'}},
            {'steam.app.438100': {key.replace('CurrentURL', 'AutosaveURL'): 'file:///fixture'}},
            {'steam.app.438100': {key.replace('CurrentURL', 'PreviousURL'): 'file:///fixture'}},
            {'steam.app.438100': {key.replace('_steamvrinput', '_openxr'): 'file:///fixture'}},
            {'steam.app.438100': {key.replace('oculus_touch', 'knuckles'): 'file:///fixture'}},
            {'steam.app.438100': {key: 'same-url', other: 'same-url'}},
            {'steam.app.438100': {key: 'one-url', other: 'different-url'}},
            {'steam.app.438100': {key: ''}},
            {'steam.app.438100': {key: 42}},
            {helper.APP_KEY: {other: ''}, 'steam.app.438100': {key: 'file:///fixture'}},
        )
        for settings in cases:
            with self.subTest(settings=settings):
                result = helper.discover_controller_selection(settings, helper.APP_KEY)
                self.assertEqual(result['targetKeys'], ())
                self.assertEqual(result['proof'], 'unknown')
                self.assertIsNone(result['sourceApp'])

    def test_shared_discovery_supports_only_approved_namespaces_and_oculus_touch(self):
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        result = helper.discover_controller_selection({helper.APP_KEY: {key: 'fixture'}}, 'steam.app.438100')
        self.assertEqual(result['targetKeys'], (key,))
        self.assertEqual(result['sourceApp'], helper.APP_KEY)
        for target, controller in (('steam.app.unknown', 'oculus_touch'),
                                   (helper.APP_KEY, 'knuckles'), (helper.APP_KEY, 'oculus_touch_250820')):
            with self.subTest(target=target, controller=controller), self.assertRaises(helper.BindingWarning):
                helper.discover_controller_selection({}, target, controller_type=controller)

    def test_shared_discovery_gives_actual_target_keys_priority_over_other_namespace(self):
        key = 'oculus_touch_CurrentURL_steamvrinput'
        other = 'oculus_touch_250820_CurrentURL_steamvrinput'
        result = helper.discover_controller_selection({helper.APP_KEY: {key: 'my-custom'},
            'steam.app.438100': {other: 'different-custom'}}, helper.APP_KEY)
        self.assertEqual(result['targetKeys'], (key,))
        self.assertEqual(result['proof'], 'observed-current-url')
        self.assertEqual(result['sourceApp'], helper.APP_KEY)

    def test_public_fallback_cas_failure_keeps_concurrent_settings(self):
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        write(self.settings, {'steam.app.438100': {key: 'fixture'}})
        merge = helper.merge_binding
        def edit(binding, manifest):
            output = merge(binding, manifest)
            self.settings.write_text('{"concurrent": true}')
            return output
        with patch.object(helper, 'merge_binding', side_effect=edit):
            with self.assertRaisesRegex(helper.BindingWarning, 'changed during setup'):
                self.prepare()
        self.assertEqual(self.settings.read_text(), '{"concurrent": true}')
        self.assertFalse((self.addon / helper.OWNER).exists())

    def test_public_fallback_linked_config_is_refused_and_rollback_restores_absence(self):
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        write(self.settings, {'steam.app.438100': {key: 'fixture'}})
        before = self.settings.read_bytes()
        plain = helper._plain
        def linked(path):
            if path == self.settings:
                raise helper.BindingWarning('fixture config contains a link')
            return plain(path)
        with patch.object(helper, '_plain', side_effect=linked):
            with self.assertRaisesRegex(helper.BindingWarning, 'contains a link'):
                self.prepare()
        self.assertEqual(self.settings.read_bytes(), before)
        self.prepare()
        result = helper.rollback_install(self.local, self.runtime, self.addon)
        self.assertTrue(result['selectionRestored'])
        self.assertNotIn(key, json.loads(self.settings.read_text())[helper.APP_KEY])

    def test_cross_namespace_evidence_cannot_be_mislabeled_as_target_observation(self):
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        write(self.settings, {'steam.app.438100': {key: 'fixture'}})
        self.prepare()
        state = self.read(helper.OWNER)
        state['selectionEvidence']['appKey'] = helper.APP_KEY
        write(self.addon / helper.OWNER, state)
        with self.assertRaisesRegex(helper.BindingWarning, 'counterpart selection evidence'):
            helper.status(self.addon)

    def test_verified_key_never_overrides_an_existing_custom_dashboard_choice(self):
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        other_key = 'oculus_touch_CurrentURL_steamvrinput'
        value = {helper.APP_KEY: {other_key: 'vr-input-workshop://custom-dashboard'}}
        write(self.settings, value)
        before = self.settings.read_bytes()
        result = self.prepare(verified_key=key)
        self.assertEqual(result['state'], 'custom-binding-kept')
        self.assertEqual(self.settings.read_bytes(), before)
        self.assertNotIn(key, json.loads(self.settings.read_text())[helper.APP_KEY])

    def test_verified_key_setup_is_idempotent_and_keeps_absent_predecessor(self):
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        self.prepare(verified_key=key)
        before = {str(path.relative_to(self.addon)): path.read_bytes()
                  for path in self.addon.rglob('*') if path.is_file()}
        settings_before = self.settings.read_bytes()
        self.prepare(verified_key=key)
        self.assertEqual(before, {str(path.relative_to(self.addon)): path.read_bytes()
                                 for path in self.addon.rglob('*') if path.is_file()})
        self.assertEqual(self.settings.read_bytes(), settings_before)
        helper.rollback_install(self.local, self.runtime, self.addon)
        self.assertNotIn(key, json.loads(self.settings.read_text())[helper.APP_KEY])
        self.assertEqual(json.loads(self.settings.read_text())['steam.app.fixture'], {'customBinding': 'keep'})

    def test_verified_key_uninstall_keeps_original_absent_choice_across_upgrade(self):
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        self.prepare(verified_key=key)
        previous = self.addon.with_name('controller-addon-backup-fixture')
        self.addon.rename(previous)
        write(self.addon / 'qpro-owner.json', {'format': 'qpro-controller-addon-v1'})
        write(self.addon / helper.PROFILE, self.profile)
        self.prepare(previous=previous, verified_key=key)
        recovery = self.addon.with_name('controller-addon-uninstalled-fixture')
        self.addon.rename(recovery)
        helper.restore(recovery, self.local, self.runtime, self.addon)
        self.assertNotIn(key, json.loads(self.settings.read_text())[helper.APP_KEY])

    def test_verified_key_cas_race_keeps_user_edit_and_rolls_back_owned_files(self):
        merge = helper.merge_binding
        def concurrent(binding, manifest):
            output = merge(binding, manifest)
            self.settings.write_text('{"user_changed_settings": true}')
            return output
        with patch.object(helper, 'merge_binding', side_effect=concurrent):
            with self.assertRaisesRegex(helper.BindingWarning, 'changed during setup'):
                self.prepare(verified_key='oculus_touch_250820_CurrentURL_steamvrinput')
        self.assertEqual(self.settings.read_text(), '{"user_changed_settings": true}')
        self.assertFalse((self.addon / helper.OWNER).exists())
        self.assertFalse((self.addon / helper.BINDING).exists())
        self.assertEqual((self.addon / helper.PROFILE).read_bytes(), self.original_profile)

    def test_verified_key_rejects_linked_config_before_any_write(self):
        real_plain = helper._plain
        def linked(path):
            if path == self.settings:
                raise helper.BindingWarning('fixture config contains a link')
            return real_plain(path)
        with patch.object(helper, '_plain', side_effect=linked):
            with self.assertRaisesRegex(helper.BindingWarning, 'contains a link'):
                self.prepare(verified_key='oculus_touch_250820_CurrentURL_steamvrinput')
        self.assertFalse((self.addon / helper.OWNER).exists())
        self.assertEqual(self.settings.read_bytes(), self.original_settings)

    def test_verified_key_rejects_other_controller_or_api_or_malformed_key(self):
        for key in ('knuckles_CurrentURL_steamvrinput', 'oculus_touch_250820_CurrentURL_openxr',
                    'oculus_touch_250820_AutosaveURL_steamvrinput', '', 1, ['invalid']):
            with self.subTest(key=key), self.assertRaisesRegex(helper.BindingWarning, 'not recognized'):
                self.prepare(verified_key=key)
            self.assertFalse((self.addon / helper.OWNER).exists())
            self.assertEqual(self.settings.read_bytes(), self.original_settings)

    def test_old_owner_state_without_internal_test_snapshots_remains_supported(self):
        self.prepare()
        owner = self.read(helper.OWNER)
        for key in ('selectionProof', 'installSelectionSha256', 'installSettingsSha256'):
            owner.pop(key)
        write(self.addon / helper.OWNER, owner)
        (self.addon / helper.INSTALL_SELECTION).unlink()
        (self.addon / helper.INSTALL_SETTINGS).unlink()
        self.assertEqual(helper.status(self.addon)['state'], 'prepared-default')
        self.prepare()
        self.assertEqual(helper.status(self.addon)['state'], 'prepared-default')
        self.assertEqual(self.settings.read_bytes(), self.original_settings)
        self.assertTrue(helper.restore(self.addon)['restored'])

    def test_runtime_update_refreshes_original_sources_without_accumulating(self):
        self.prepare()
        new_source = binding_fixture()
        new_source['bindings']['/actions/system']['sources'][0]['new_native_option'] = 'preserve'
        write(self.source, new_source)
        self.prepare()
        actual = self.read(helper.BINDING)
        self.assertEqual(actual['bindings']['/actions/system'], new_source['bindings']['/actions/system'])
        self.assertEqual(len(actual['bindings'][helper.SETS[0]]['sources']), 3)
        self.assertEqual((self.addon / helper.BACKUP / 'original-profile.json').read_bytes(), self.original_profile)

    def test_update_after_normal_owned_backup_prepares_new_profile(self):
        self.prepare()
        previous = self.addon.with_name('controller-addon-backup-fixture')
        self.addon.rename(previous)
        write(self.addon / 'qpro-owner.json', {'format': 'qpro-controller-addon-v1'})
        write(self.addon / helper.PROFILE, self.profile)
        self.prepare(previous=previous)
        self.assertEqual(self.read(helper.OWNER)['format'], helper.FORMAT)
        self.assertTrue((previous / helper.BINDING).is_file())

    def test_user_file_edits_are_never_overwritten_or_restored(self):
        self.prepare()
        edited = self.read(helper.BINDING)
        edited['description'] = 'My personal edit'
        write(self.addon / helper.BINDING, edited)
        before = (self.addon / helper.BINDING).read_bytes()
        with self.assertRaisesRegex(helper.BindingWarning, 'edited'):
            self.prepare()
        self.assertEqual(helper.status(self.addon)['state'], 'user-edited')
        self.assertFalse(helper.restore(self.addon)['restored'])
        self.assertEqual((self.addon / helper.BINDING).read_bytes(), before)

    def test_unrelated_profile_edits_are_preserved_by_setup_and_removal(self):
        self.prepare()
        profile = self.read(helper.PROFILE)
        profile['custom_setting'] = 'keep'
        write(self.addon / helper.PROFILE, profile)
        self.prepare()
        self.assertEqual(self.read(helper.PROFILE), profile)
        helper.restore(self.addon)
        self.assertEqual(self.read(helper.PROFILE)['custom_setting'], 'keep')

    def test_vrchat_default_is_independent_of_dashboard_ownership_and_removal(self):
        self.prepare()
        profile = self.read(helper.PROFILE)
        vrchat = {'app_key': 'steam.app.438100', 'binding_url': 'qpro_vrchat_scroll.json'}
        profile['default_bindings'].append(vrchat)
        write(self.addon / helper.PROFILE, profile)
        self.assertEqual(helper.status(self.addon)['state'], 'prepared-default')
        helper.restore(self.addon)
        self.assertIn(vrchat, self.read(helper.PROFILE)['default_bindings'])
        self.assertNotIn(helper.APP_KEY, [item['app_key'] for item in self.read(helper.PROFILE)['default_bindings']])

    def test_update_preserves_modified_previous_binding_at_same_installed_path(self):
        self.prepare()
        edited = self.read(helper.BINDING)
        edited['description'] = 'Personal edit'
        write(self.addon / helper.BINDING, edited)
        write(self.settings, {helper.APP_KEY: {
            'oculus_touch_250820_CurrentURL_steamvrinput': (self.addon / helper.BINDING).as_uri()}})
        previous = self.addon.with_name('controller-addon-backup-fixture')
        self.addon.rename(previous)
        write(self.addon / 'qpro-owner.json', {'format': 'qpro-controller-addon-v1'})
        write(self.addon / helper.PROFILE, self.profile)
        result = self.prepare(previous=previous)
        self.assertEqual(json.loads((previous / helper.BINDING).read_text()), edited)
        self.assertEqual(self.read(helper.BINDING), edited)
        self.assertEqual(result['state'], 'user-edited')
        self.assertTrue(self.read(helper.OWNER)['userEdited'])
        self.assertFalse(helper.restore(self.addon)['restored'])

    def test_inactive_edited_previous_copy_cannot_mask_new_actual_custom_choice(self):
        self.prepare()
        old = self.read(helper.BINDING)
        old['description'] = 'An older edited generated copy'
        write(self.addon / helper.BINDING, old)
        active = binding_fixture()
        active['description'] = 'The new actual custom binding'
        active['bindings']['/actions/system']['sources'][0]['user_option'] = 'preserve-current'
        custom = self.root / 'new-active-dashboard.json'
        write(custom, active)
        write(self.settings, {helper.APP_KEY: {'oculus_touch_250820_CurrentURL_steamvrinput': custom.as_uri()}})
        before = self.settings.read_bytes()
        previous = self.addon.with_name('controller-addon-backup-fixture')
        self.addon.rename(previous)
        write(self.addon / 'qpro-owner.json', {'format': 'qpro-controller-addon-v1'})
        write(self.addon / helper.PROFILE, self.profile)
        result = self.prepare(previous=previous)
        self.assertEqual(result['state'], 'custom-binding-kept')
        self.assertEqual(self.settings.read_bytes(), before)
        self.assertEqual(self.read(helper.BINDING)['description'], active['description'])
        self.assertEqual(self.read(helper.BINDING)['bindings']['/actions/system'], active['bindings']['/actions/system'])
        self.assertEqual(json.loads((previous / helper.BINDING).read_text()), old)

    def test_default_with_counterpart_key_does_not_reactivate_inactive_previous_edits(self):
        self.prepare()
        old = self.read(helper.BINDING)
        old['description'] = 'An inactive edited generated copy'
        old['bindings']['/actions/system']['sources'][0]['user_option'] = 'inactive'
        write(self.addon / helper.BINDING, old)
        previous_bytes = (self.addon / helper.BINDING).read_bytes()
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        counterpart = {key: 'file:///fixture/vrchat.json'}
        write(self.settings, {'steam.app.438100': counterpart, 'steamvr': {'keep': True}})
        previous = self.addon.with_name('controller-addon-backup-fixture')
        self.addon.rename(previous)
        write(self.addon / 'qpro-owner.json', {'format': 'qpro-controller-addon-v1'})
        write(self.addon / helper.PROFILE, self.profile)

        result = self.prepare(previous=previous)

        self.assertEqual(result['state'], 'prepared-selection')
        self.assertTrue(result['automatic'])
        self.assertFalse(self.read(helper.OWNER)['userEdited'])
        self.assertNotIn('description', self.read(helper.BINDING))
        self.assertEqual(self.read(helper.BINDING)['bindings']['/actions/system'],
                         binding_fixture()['bindings']['/actions/system'])
        self.assertEqual((previous / helper.BINDING).read_bytes(), previous_bytes)
        self.assertEqual(self.read(helper.BACKUP / 'selection.json')['choices'], {})
        settings = json.loads(self.settings.read_text())
        self.assertEqual(settings['steam.app.438100'], counterpart)
        self.assertEqual(settings[helper.APP_KEY][key], (self.addon / helper.BINDING).as_uri())
        restored = helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertTrue(restored['selectionRestored'])
        self.assertNotIn(key, json.loads(self.settings.read_text()).get(helper.APP_KEY, {}))

    def test_selected_owned_binding_upgrade_reconstructs_source_without_duplicates(self):
        self.prepare()
        original_binding = (self.addon / helper.BINDING).read_bytes()
        write(self.settings, {helper.APP_KEY: {
            'oculus_touch_250820_CurrentURL_steamvrinput': (self.addon / helper.BINDING).as_uri()}})
        settings_before = self.settings.read_bytes()
        previous = self.addon.with_name('controller-addon-backup-fixture')
        self.addon.rename(previous)
        write(self.addon / 'qpro-owner.json', {'format': 'qpro-controller-addon-v1'})
        write(self.addon / helper.PROFILE, self.profile)
        self.assertFalse((self.addon / helper.BINDING).exists())
        result = self.prepare(previous=previous)
        self.assertEqual((self.addon / helper.BINDING).read_bytes(), original_binding)
        self.assertEqual(result['state'], 'prepared-selection')
        self.assertFalse(self.read(helper.OWNER)['customChoice'])
        self.assertEqual(self.settings.read_bytes(), settings_before)
        self.prepare()
        self.assertEqual((self.addon / helper.BINDING).read_bytes(), original_binding)

    def test_selected_user_edited_owned_binding_survives_upgrade_exactly(self):
        self.prepare()
        edited = self.read(helper.BINDING)
        edited['description'] = 'My exact custom dashboard binding'
        write(self.addon / helper.BINDING, edited)
        original_edited_bytes = (self.addon / helper.BINDING).read_bytes()
        write(self.settings, {helper.APP_KEY: {
            'oculus_touch_250820_CurrentURL_steamvrinput': (self.addon / helper.BINDING).as_uri()}})
        settings_before = self.settings.read_bytes()
        for number in range(2):
            previous = self.addon.with_name('controller-addon-backup-' + str(number))
            self.addon.rename(previous)
            write(self.addon / 'qpro-owner.json', {'format': 'qpro-controller-addon-v1'})
            write(self.addon / helper.PROFILE, self.profile)
            result = self.prepare(previous=previous)
            self.assertEqual(result['state'], 'user-edited')
            self.assertEqual((self.addon / helper.BINDING).read_bytes(), original_edited_bytes)
            self.assertEqual(self.settings.read_bytes(), settings_before)

    def test_selected_user_edited_profile_survives_upgrade_exactly(self):
        self.prepare()
        profile = self.read(helper.PROFILE)
        profile['input_source'][helper.SOURCE]['touch'] = False
        write(self.addon / helper.PROFILE, profile)
        exact = (self.addon / helper.PROFILE).read_bytes()
        write(self.settings, {helper.APP_KEY: {
            'oculus_touch_250820_CurrentURL_steamvrinput': (self.addon / helper.BINDING).as_uri()}})
        previous = self.addon.with_name('controller-addon-backup-fixture')
        self.addon.rename(previous)
        write(self.addon / 'qpro-owner.json', {'format': 'qpro-controller-addon-v1'})
        write(self.addon / helper.PROFILE, self.profile)
        self.prepare(previous=previous)
        self.assertEqual((self.addon / helper.PROFILE).read_bytes(), exact)

    def test_existing_custom_choice_is_kept_and_local_content_is_basis(self):
        custom = self.root / 'custom-binding.json'
        value = binding_fixture()
        value['bindings']['/actions/system']['sources'][0]['user_choice'] = 'keep'
        write(custom, value)
        settings = {helper.APP_KEY: {'oculus_touch_250820_CurrentURL_steamvrinput': custom.as_uri()},
                    'fixture.game': {'binding': 'keep'}}
        write(self.settings, settings)
        before = self.settings.read_bytes()
        result = self.prepare()
        self.assertEqual(result['state'], 'custom-binding-kept')
        self.assertFalse(result['automatic'])
        self.assertEqual(self.read(helper.BINDING)['bindings']['/actions/system'], value['bindings']['/actions/system'])
        self.assertEqual(self.settings.read_bytes(), before)
        self.assertEqual(json.loads(custom.read_text()), value)

    def test_workshop_choice_stays_selected_without_network_or_guessed_keys(self):
        value = {helper.APP_KEY: {'oculus_touch_250820_CurrentURL_steamvrinput': 'vr-input-workshop://123'}}
        write(self.settings, value)
        result = self.prepare()
        self.assertEqual(result['state'], 'custom-binding-kept')
        self.assertEqual(json.loads(self.settings.read_text()), value)

    def test_uninstall_restores_exact_original_profile_and_keeps_recovery(self):
        self.prepare()
        result = helper.restore(self.addon)
        self.assertTrue(result['restored'])
        self.assertEqual((self.addon / helper.PROFILE).read_bytes(), self.original_profile)
        self.assertTrue((self.addon / helper.BINDING).is_file())
        self.assertEqual(self.settings.read_bytes(), self.original_settings)
        self.assertTrue(helper.restore(self.addon)['restored'])

    def test_uninstall_removes_only_observed_owned_selection_and_keeps_other_settings(self):
        self.prepare()
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        settings = {'steam.app.fixture': {'user_binding': 'keep'}, helper.APP_KEY: {
            key: (self.addon / helper.BINDING).as_uri(), 'oculus_touch_AutosaveURL_steamvrinput': 'keep-auto',
            'unrelated_setting': {'keep': [1, 2]}}}
        write(self.settings, settings)
        original_settings = self.settings.read_bytes()
        recovery = self.addon.with_name('controller-addon-uninstalled-fixture')
        self.addon.rename(recovery)
        result = helper.restore(recovery, self.local, self.runtime, self.addon)
        self.assertTrue(result['selectionRestored'])
        self.assertEqual(result['selectionCount'], 1)
        del settings[helper.APP_KEY][key]
        self.assertEqual(json.loads(self.settings.read_text()), settings)
        copies = list((recovery / helper.BACKUP).glob('steamvr-settings-before-restore-*.json'))
        self.assertEqual(len(copies), 1)
        self.assertEqual(copies[0].read_bytes(), original_settings)

    def test_uninstall_restores_original_custom_selection_after_owned_selection_upgrade(self):
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        original_url = 'vr-input-workshop://fixture-original'
        write(self.settings, {helper.APP_KEY: {key: original_url}})
        self.prepare()
        write(self.settings, {helper.APP_KEY: {key: (self.addon / helper.BINDING).as_uri()}})
        previous = self.addon.with_name('controller-addon-backup-fixture')
        self.addon.rename(previous)
        write(self.addon / 'qpro-owner.json', {'format': 'qpro-controller-addon-v1'})
        write(self.addon / helper.PROFILE, self.profile)
        self.prepare(previous=previous)
        recovery = self.addon.with_name('controller-addon-uninstalled-fixture')
        self.addon.rename(recovery)
        helper.restore(recovery, self.local, self.runtime, self.addon)
        self.assertEqual(json.loads(self.settings.read_text())[helper.APP_KEY][key], original_url)

    def test_uninstall_never_replaces_a_new_unrelated_custom_selection(self):
        self.prepare()
        settings = {helper.APP_KEY: {'oculus_touch_250820_CurrentURL_steamvrinput': 'vr-input-workshop://new-user-choice'}}
        write(self.settings, settings)
        before = self.settings.read_bytes()
        recovery = self.addon.with_name('controller-addon-uninstalled-fixture')
        self.addon.rename(recovery)
        result = helper.restore(recovery, self.local, self.runtime, self.addon)
        self.assertFalse(result['selectionRestored'])
        self.assertEqual(self.settings.read_bytes(), before)
        self.assertEqual(list((recovery / helper.BACKUP).glob('steamvr-settings-before-restore-*.json')), [])

    def test_uninstall_concurrent_settings_edit_is_kept_and_backup_retained(self):
        self.prepare()
        write(self.settings, {helper.APP_KEY: {'oculus_touch_250820_CurrentURL_steamvrinput':
                                              (self.addon / helper.BINDING).as_uri()}})
        before = self.settings.read_bytes()
        recovery = self.addon.with_name('controller-addon-uninstalled-fixture')
        self.addon.rename(recovery)
        replace = Path.replace
        def concurrent(path, target):
            result = replace(path, target)
            if target.name.startswith('steamvr-settings-before-restore-'):
                self.settings.write_text('{"concurrent_user_choice": true}')
            return result
        with patch.object(Path, 'replace', concurrent):
            with self.assertRaisesRegex(helper.BindingWarning, 'changed during setup'):
                helper.restore(recovery, self.local, self.runtime, self.addon)
        self.assertEqual(self.settings.read_text(), '{"concurrent_user_choice": true}')
        copies = list((recovery / helper.BACKUP).glob('steamvr-settings-before-restore-*.json'))
        self.assertEqual(copies[0].read_bytes(), before)

    def test_uninstall_locked_settings_keep_original_and_recovery(self):
        self.prepare()
        write(self.settings, {helper.APP_KEY: {'oculus_touch_250820_CurrentURL_steamvrinput':
                                              (self.addon / helper.BINDING).as_uri()}})
        before = self.settings.read_bytes()
        recovery = self.addon.with_name('controller-addon-uninstalled-fixture')
        self.addon.rename(recovery)
        replace = Path.replace
        def locked(path, target):
            if target == self.settings:
                raise PermissionError('fixture locked settings')
            return replace(path, target)
        with patch.object(Path, 'replace', locked):
            with self.assertRaisesRegex(PermissionError, 'fixture locked settings'):
                helper.restore(recovery, self.local, self.runtime, self.addon)
        self.assertEqual(self.settings.read_bytes(), before)
        copies = list((recovery / helper.BACKUP).glob('steamvr-settings-before-restore-*.json'))
        self.assertEqual(copies[0].read_bytes(), before)

    def test_uninstall_linked_config_is_not_mutated(self):
        self.prepare()
        key = 'oculus_touch_250820_CurrentURL_steamvrinput'
        write(self.settings, {helper.APP_KEY: {key: (self.addon / helper.BINDING).as_uri()}})
        before = self.settings.read_bytes()
        recovery = self.addon.with_name('controller-addon-uninstalled-fixture')
        self.addon.rename(recovery)
        real_plain = helper._plain
        def linked(path):
            if path == self.settings:
                raise helper.BindingWarning('fixture config contains a link')
            return real_plain(path)
        with patch.object(helper, '_plain', side_effect=linked):
            with self.assertRaisesRegex(helper.BindingWarning, 'contains a link'):
                helper.restore(recovery, self.local, self.runtime, self.addon)
        self.assertEqual(self.settings.read_bytes(), before)

    def test_uninstall_ambiguous_selections_are_kept(self):
        self.prepare()
        write(self.settings, {helper.APP_KEY: {
            'oculus_touch_CurrentURL_steamvrinput': (self.addon / helper.BINDING).as_uri(),
            'oculus_touch_250820_CurrentURL_steamvrinput': 'vr-input-workshop://user-choice'}})
        before = self.settings.read_bytes()
        recovery = self.addon.with_name('controller-addon-uninstalled-fixture')
        self.addon.rename(recovery)
        with self.assertRaisesRegex(helper.BindingWarning, 'ambiguous'):
            helper.restore(recovery, self.local, self.runtime, self.addon)
        self.assertEqual(self.settings.read_bytes(), before)

    def test_changed_recovery_is_not_trusted(self):
        self.prepare()
        (self.addon / helper.BACKUP / 'original-profile.json').write_text('{}')
        with self.assertRaisesRegex(helper.BindingWarning, 'recovery copy changed'):
            helper.restore(self.addon)

    def test_transaction_failure_rolls_back_all_changes(self):
        real_replace = Path.replace
        failure = False
        def fail_profile(path, target):
            nonlocal failure
            if target == self.addon / helper.PROFILE and not failure:
                failure = True
                raise PermissionError('fixture profile is locked')
            return real_replace(path, target)
        with patch.object(Path, 'replace', fail_profile):
            with self.assertRaisesRegex(PermissionError, 'fixture profile is locked'):
                self.prepare()
        self.assertEqual((self.addon / helper.PROFILE).read_bytes(), self.original_profile)
        self.assertFalse((self.addon / helper.BINDING).exists())
        self.assertFalse((self.addon / helper.OWNER).exists())
        self.assertFalse((self.addon / helper.BACKUP).exists())
        self.assertEqual(self.settings.read_bytes(), self.original_settings)

    def test_concurrent_profile_change_is_kept_and_new_files_rolled_back(self):
        real_replace = Path.replace
        def edit_before_profile(path, target):
            result = real_replace(path, target)
            if target == self.addon / helper.BINDING:
                (self.addon / helper.PROFILE).write_text('{"user_edit": true}')
            return result
        with patch.object(Path, 'replace', edit_before_profile):
            with self.assertRaisesRegex(helper.BindingWarning, 'changed during setup'):
                self.prepare()
        self.assertEqual((self.addon / helper.PROFILE).read_text(), '{"user_edit": true}')
        self.assertFalse((self.addon / helper.BINDING).exists())
        self.assertFalse((self.addon / helper.OWNER).exists())

    def test_edit_during_derivation_is_not_adopted_as_overwritable_snapshot(self):
        original_merge = helper.merge_binding
        def edit_while_merging(binding, manifest):
            result = original_merge(binding, manifest)
            (self.addon / helper.PROFILE).write_text('{"user_edit": true}')
            return result
        with patch.object(helper, 'merge_binding', edit_while_merging):
            with self.assertRaisesRegex(helper.BindingWarning, 'not recognized|changed during setup'):
                self.prepare()
        self.assertEqual((self.addon / helper.PROFILE).read_text(), '{"user_edit": true}')
        self.assertFalse((self.addon / helper.BINDING).exists())

    def test_wrong_app_action_type_and_unknown_scroll_schema_fail_closed(self):
        cases = []
        wrong = binding_fixture()
        wrong['app_key'] = 'fixture.game'
        cases.append((wrong, manifest_fixture()))
        wrong = manifest_fixture()
        wrong['actions'][0]['type'] = 'boolean'
        cases.append((binding_fixture(), wrong))
        wrong = binding_fixture()
        wrong['bindings'][helper.SETS[0]]['sources'] = {}
        cases.append((wrong, manifest_fixture()))
        for binding, manifest in cases:
            with self.subTest(binding=binding, manifest=manifest):
                with self.assertRaises(helper.BindingWarning):
                    helper.merge_binding(binding, manifest)

    def test_duplicate_or_nonfinite_json_refused(self):
        for data in (b'{"x":1,"x":2}', b'{"x":NaN}'):
            with self.subTest(data=data), self.assertRaises(helper.BindingWarning):
                helper._json(data)

    def test_existing_unowned_destination_is_not_overwritten(self):
        write(self.addon / helper.BINDING, {'personal': 'keep'})
        with self.assertRaisesRegex(helper.BindingWarning, 'without ownership'):
            self.prepare()
        self.assertEqual(self.read(helper.BINDING), {'personal': 'keep'})

    def test_relative_or_linked_owned_path_refused(self):
        with self.assertRaisesRegex(helper.BindingWarning, 'not absolute'):
            helper.prepare(self.local, self.runtime, Path('relative-addon'))
        with patch.object(Path, 'is_symlink', return_value=True):
            with self.assertRaisesRegex(helper.BindingWarning, 'contains a link'):
                self.prepare()

    def test_readonly_sources_may_use_linked_library_folders(self):
        with patch.object(Path, 'is_symlink', return_value=True):
            self.assertEqual(helper._read(self.source), self.original_source)
            with self.assertRaisesRegex(helper.BindingWarning, 'contains a link'):
                helper._read(self.addon / helper.PROFILE, owned=True)


class SupervisorDashboardLifecycleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (ROOT.parent / 'artifacts').mkdir(parents=True, exist_ok=True)
        # Reuse the existing fake reader/socket fixture; importing it starts no
        # process and the fixture patches all headset and SteamVR boundaries.
        spec = importlib.util.spec_from_file_location(
            'dashboard_supervisor_fixture', ROOT.parent / 'test_controller_components.py')
        cls.fixture_module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.fixture_module)

    def test_all_modes_and_sides_report_dedicated_swipe_source_then_neutral_on_stop(self):
        fixture_module = self.fixture_module
        supervisor = fixture_module.module
        for mode in ('trackpad', 'joystick', 'swipe', 'mouse'):
            for side in ('both', 'left', 'right'):
                with self.subTest(mode=mode, side=side):
                    fixture = fixture_module.ComponentTests(methodName='runTest')
                    closed = threading.Event()
                    def stop():
                        closed.set()
                        return b''
                    prepared = type('Prepared', (), {'status': staticmethod(
                        lambda addon: {'state': 'prepared', 'automatic': True, 'message': 'fixture'})})()
                    with patch.object(supervisor, 'dashboard_helper', return_value=prepared):
                        fixture.run_relay(fixture_module.ReaderProcess(),
                            fixture_module.RelayStream([fixture.packet(), stop]),
                            parent_closed=closed, mode=mode, side=side)
                    reports = [json.loads(line.partition(' ')[2])
                               for line in fixture.relay_activity.getvalue().splitlines()
                               if line.startswith('CONTROLLER_DASHBOARD_BINDING ')]
                    self.assertEqual(len(reports), 2)
                    self.assertEqual([value['scrollSource'] for value in reports],
                                     ['enabled' if mode == 'swipe' else 'neutral', 'neutral'])
                    self.assertTrue(all(value['side'] == side for value in reports))
                    self.assertTrue(all(value['activationVerified'] is False for value in reports))
                    self.assertEqual(fixture.relay_configurations, [(True, mode, side), (False, mode, side)])

    def test_optional_binding_error_does_not_block_input_or_stop_cleanup(self):
        fixture_module = self.fixture_module
        fixture = fixture_module.ComponentTests(methodName='runTest')
        closed = threading.Event()
        def stop():
            closed.set()
            return b''
        with patch.object(fixture_module.module, 'dashboard_helper', side_effect=RuntimeError('fixture unsupported binding')):
            fixture.run_relay(fixture_module.ReaderProcess(),
                fixture_module.RelayStream([fixture.packet(), stop]), parent_closed=closed, mode='swipe')
        self.assertEqual(fixture.relay_configurations, [(True, 'swipe', 'both'), (False, 'swipe', 'both')])
        self.assertIn('CONTROLLER_BINDINGS_WARNING', fixture.relay_activity.getvalue())
        self.assertIn('TOUCHPAD_READY', fixture.relay_activity.getvalue())

    def install_fixture(self, root, supervisor):
        local = root / 'local'
        hands, addon = supervisor.managed_paths(local)
        python = hands / '.venv/Scripts/python.exe'
        python.parent.mkdir(parents=True)
        python.write_bytes(b'fixture python never launched')
        source = root / 'release/controller-input/addon'
        driver = source / 'bin/win64/driver_qpro_controller.dll'
        driver.parent.mkdir(parents=True)
        driver.write_bytes(b'new fixture driver never loaded')
        tool = root / 'runtime/bin/win64/vrpathreg.exe'
        return local, addon, source.parent.parent, tool

    def test_installer_retains_previous_tree_when_selected_binding_migration_is_unsupported(self):
        supervisor = self.fixture_module.module
        with tempfile.TemporaryDirectory(dir=ROOT.parent / 'artifacts') as temporary:
            local, addon, release, tool = self.install_fixture(Path(temporary), supervisor)
            write(addon / 'qpro-owner.json', {'format': supervisor.OWNER_FORMAT})
            write(addon / helper.BINDING, {'previous_binding': 'must remain selected'})
            (addon / 'old-driver-marker').write_bytes(b'original driver')
            broken = type('Broken', (), {'prepare': staticmethod(
                lambda *args: (_ for _ in ()).throw(RuntimeError('fixture unsupported migration')))})()
            with patch.object(supervisor, 'assert_steamvr_closed'), \
                 patch.object(supervisor, 'steamvr_tool', return_value=tool), \
                 patch.object(supervisor, 'download_verified'), \
                 patch.object(supervisor.lzma, 'open', return_value=io.BytesIO(b'fixture server')), \
                 patch.object(supervisor, 'command', return_value='') as command, \
                 patch.object(supervisor, 'dashboard_helper', return_value=broken), \
                 contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaisesRegex(RuntimeError, 'could not preserve.*previous add-on files will be restored'):
                    supervisor.install(release, local)
            self.assertEqual((addon / 'old-driver-marker').read_bytes(), b'original driver')
            self.assertEqual(json.loads((addon / helper.BINDING).read_text()),
                             {'previous_binding': 'must remain selected'})
            self.assertFalse(any(call.args[0][1] == 'adddriver' for call in command.call_args_list))

    def test_fresh_installer_binding_schema_warning_keeps_normal_addon_setup_usable(self):
        supervisor = self.fixture_module.module
        with tempfile.TemporaryDirectory(dir=ROOT.parent / 'artifacts') as temporary:
            local, addon, release, tool = self.install_fixture(Path(temporary), supervisor)
            broken = type('Broken', (), {'prepare': staticmethod(
                lambda *args: (_ for _ in ()).throw(RuntimeError('fixture unsupported schema')))})()
            with patch.object(supervisor, 'assert_steamvr_closed'), \
                 patch.object(supervisor, 'steamvr_tool', return_value=tool), \
                 patch.object(supervisor, 'download_verified'), \
                 patch.object(supervisor.lzma, 'open', return_value=io.BytesIO(b'fixture server')), \
                 patch.object(supervisor, 'command', return_value='') as command, \
                 patch.object(supervisor, 'dashboard_helper', return_value=broken), \
                 contextlib.redirect_stdout(io.StringIO()) as output:
                supervisor.install(release, local)
            self.assertTrue((addon / 'bin/win64/driver_qpro_controller.dll').is_file())
            self.assertTrue(any(call.args[0][1] == 'adddriver' for call in command.call_args_list))
            self.assertIn('CONTROLLER_BINDINGS_WARNING', output.getvalue())

    def test_installer_registration_failure_rolls_back_selection_before_removing_metadata(self):
        supervisor = self.fixture_module.module
        with tempfile.TemporaryDirectory(dir=ROOT.parent / 'artifacts') as temporary:
            local, addon, release, tool = self.install_fixture(Path(temporary), supervisor)
            config = Path(temporary) / 'settings.json'
            config.write_bytes(b'original selection')
            seen = []
            def prepare(*_):
                write(addon / 'resources/input/qpro-vrchat-owner.json', {'fixture': True})
                config.write_bytes(b'new owned selection')
            def rollback(*_):
                seen.append((addon.is_dir(), (addon / 'resources/input/qpro-vrchat-owner.json').exists()))
                config.write_bytes(b'original selection')
                return {'selectionRestored': True, 'selectionCount': 1}
            def command(args, **_):
                if args[1] == 'adddriver':
                    raise RuntimeError('fixture registration failed')
                return ''
            fake = type('Fake', (), {'rollback_install': staticmethod(rollback)})()
            with patch.object(supervisor, 'assert_steamvr_closed'), \
                 patch.object(supervisor, 'steamvr_tool', return_value=tool), \
                 patch.object(supervisor, 'download_verified'), \
                 patch.object(supervisor.lzma, 'open', return_value=io.BytesIO(b'fixture server')), \
                 patch.object(supervisor, 'command', side_effect=command), \
                 patch.object(supervisor, 'prepare_dashboard'), \
                 patch.object(supervisor, 'prepare_vrchat', side_effect=prepare), \
                 patch.object(supervisor, 'vrchat_helper', return_value=fake), \
                 contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaisesRegex(RuntimeError, 'fixture registration failed'):
                    supervisor.install(release, local)
            self.assertEqual(seen, [(True, True)])
            self.assertEqual(config.read_bytes(), b'original selection')
            self.assertFalse(addon.exists())

    def test_unconfirmed_selection_rollback_preserves_metadata_and_previous_driver(self):
        supervisor = self.fixture_module.module
        with tempfile.TemporaryDirectory(dir=ROOT.parent / 'artifacts') as temporary:
            local, addon, release, tool = self.install_fixture(Path(temporary), supervisor)
            write(addon / 'qpro-owner.json', {'format': supervisor.OWNER_FORMAT})
            (addon / 'old-driver-marker').write_bytes(b'previous driver')
            def prepare(*_):
                write(addon / 'resources/input/qpro-vrchat-owner.json', {'fixture': 'keep recovery'})
            def command(args, **_):
                if args[1] == 'adddriver':
                    raise RuntimeError('fixture registration failed')
                return ''
            fake = type('Fake', (), {'rollback_install': staticmethod(
                lambda *_: (_ for _ in ()).throw(RuntimeError('fixture concurrent settings edit')))})()
            with patch.object(supervisor, 'assert_steamvr_closed'), \
                 patch.object(supervisor, 'steamvr_tool', return_value=tool), \
                 patch.object(supervisor, 'download_verified'), \
                 patch.object(supervisor.lzma, 'open', return_value=io.BytesIO(b'fixture server')), \
                 patch.object(supervisor, 'command', side_effect=command), \
                 patch.object(supervisor, 'prepare_dashboard'), \
                 patch.object(supervisor, 'prepare_vrchat', side_effect=prepare), \
                 patch.object(supervisor, 'vrchat_helper', return_value=fake), \
                 contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaisesRegex(RuntimeError, 'selection recovery is unconfirmed.*Keep SteamVR closed'):
                    supervisor.install(release, local)
            self.assertEqual((addon / 'old-driver-marker').read_bytes(), b'previous driver')
            recoveries = list(addon.parent.glob('controller-addon-failed-*'))
            self.assertEqual(len(recoveries), 1)
            self.assertEqual(json.loads((recoveries[0] / 'resources/input/qpro-vrchat-owner.json').read_text()),
                             {'fixture': 'keep recovery'})

    def test_both_binding_rollbacks_are_attempted_even_when_one_conflicts(self):
        supervisor = self.fixture_module.module
        with tempfile.TemporaryDirectory(dir=ROOT.parent / 'artifacts') as temporary:
            addon = Path(temporary) / 'addon'
            write(addon / 'resources/input/qpro-vrchat-owner.json', {'fixture': True})
            write(addon / helper.OWNER, {'fixture': True})
            calls = []
            def conflict(*_):
                calls.append('VRChat')
                raise RuntimeError('fixture user changed VRChat selection')
            def dashboard(*_):
                calls.append('Dashboard')
                return {'selectionRestored': True, 'selectionCount': 1}
            chat = type('Chat', (), {'rollback_install': staticmethod(conflict)})()
            dash = type('Dash', (), {'rollback_install': staticmethod(dashboard)})()
            with patch.object(supervisor, 'assert_steamvr_closed'), \
                 patch.object(supervisor, 'vrchat_helper', return_value=chat), \
                 patch.object(supervisor, 'dashboard_helper', return_value=dash), \
                 contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaisesRegex(RuntimeError, 'fixture user changed VRChat selection'):
                    supervisor.rollback_binding_selections(Path(temporary),
                        Path(temporary) / 'runtime/bin/win64/vrpathreg.exe', addon)
            self.assertEqual(calls, ['VRChat', 'Dashboard'])


if __name__ == '__main__':
    unittest.main()
