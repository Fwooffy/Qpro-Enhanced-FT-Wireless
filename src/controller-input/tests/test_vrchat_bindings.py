"""VRChat additive binding transactions against fake local files only."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('vrchat_bindings', ROOT / 'vrchat_bindings.py')
helper = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(helper)
CURRENT = 'oculus_touch_250820_CurrentURL_steamvrinput'


def encoded(value):
    return (json.dumps(value, indent=2) + '\n').encode()


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(encoded(value))


def fixture_binding(alias=False):
    # Artificial actions and assignments; no Valve or VRChat file is bundled.
    return {'app_key': helper.CANONICAL_ALIAS if alias else helper.APP_KEY,
            'category': 'steamvr_input', 'controller_type': 'oculus_touch',
            'action_manifest_version': 0, 'name': 'Personal fixture',
            'description': 'Preserve this text', 'options': {'kept': True},
            'bindings': {group: {
                'sources': [{'path': '/user/hand/left/input/joystick', 'mode': 'joystick',
                             'parameters': {'deadzone_pct': '27', 'maxzone_pct': '81', 'exponent': '2',
                                            'personal_parameter': 'unchanged'},
                             'inputs': {'position': {'output': group + '/in/scroll'}}},
                            {'path': '/user/hand/right/input/a', 'mode': 'button',
                             'inputs': {'click': {'output': group + '/in/gesture'}}}],
                'poses': [{'output': group + '/in/pose', 'path': '/user/hand/left/pose/raw'}],
                'haptics': [{'output': group + '/out/haptic', 'path': '/user/hand/right/output/haptic'}],
                'skeleton': [{'path': '/user/hand/left/input/skeleton/left', 'output': group + '/in/skeleton'}],
                'chords': [{'inputs': [['/user/hand/left/input/trigger', 'click']], 'output': group + '/in/chord'}]}
                for group in (*helper.SETS, '/actions/gameplay')}}


class VRChatBindingsTests(unittest.TestCase):
    def setUp(self):
        (ROOT.parent / 'artifacts').mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=ROOT.parent / 'artifacts')
        self.root = Path(self.temporary.name)
        self.local, self.runtime = self.root / 'local', self.root / 'runtime'
        self.config = self.root / 'steam/config'
        self.settings = self.config / 'steamvr.vrsettings'
        self.registry = self.config / 'steamapps.vrmanifest'
        self.manifest_path = self.root / 'games/VRChat/SteamVR/actions.json'
        self.default_source = self.manifest_path.with_name('bindings_oculus_touch.json')
        self.user_source = self.root / 'personal/custom.json'
        self.addon = self.local / 'QproFaceTracking/controller-addon'
        self.manifest = {'actions': [{'name': '/actions/Global/in/Scroll', 'type': 'vector2'},
                                     {'name': '/actions/Menu/in/Scroll', 'type': 'vector2'},
                                     {'name': '/actions/Global/in/Action_MenuAxis', 'type': 'vector2'}],
                         'default_bindings': [{'controller_type': 'oculus_touch',
                                               'binding_url': self.default_source.name}]}
        self.profile = {'controller_type': 'oculus_touch', 'resource_root': 'qpro_controller',
                        'input_source': {helper.SOURCE: {'type': 'joystick', 'touch': True},
                                         '/input/joystick': {'type': 'joystick', 'click': True}},
                        'default_bindings': [{'app_key': 'fixture.other', 'binding_url': 'untouched.json'}]}
        self.initial_settings = {'steamvr': {'keep': True}, 'fixture.other': {'custom': 'unchanged'},
                                 helper.APP_KEY: {'oculus_touch_250820_AutosaveURL_steamvrinput':
                                                   'vr-input-workshop://1234',
                                                 'oculus_touch_250820_PreviousURL_steamvrinput':
                                                   'vr-input-workshop://1234', 'NeedToUpdateAutosave': False}}
        write(self.local / 'openvr/openvrpaths.vrpath',
              {'config': [str(self.config)], 'runtime': [str(self.runtime)]})
        write(self.settings, self.initial_settings)
        write(self.registry, {'applications': [{'app_key': helper.APP_KEY,
                                               'action_manifest_path': str(self.manifest_path),
                                               'default_bindings': [{'controller_type': 'oculus_touch',
                                                                     'url': 'vr-input-workshop://9999'}]}]})
        write(self.manifest_path, self.manifest)
        write(self.default_source, fixture_binding(alias=True))
        write(self.user_source, fixture_binding())
        self.fresh_addon()

    def tearDown(self):
        self.temporary.cleanup()

    def fresh_addon(self):
        write(self.addon / 'qpro-owner.json', {'format': 'qpro-controller-addon-v1'})
        write(self.addon / helper.PROFILE, self.profile)

    def read(self, relative):
        return json.loads((self.addon / relative).read_text())

    def settings_data(self):
        return json.loads(self.settings.read_text())

    def choose(self, value=None, key=CURRENT):
        settings = self.settings_data()
        settings[helper.APP_KEY][key] = value or self.user_source.as_uri()
        write(self.settings, settings)

    def dashboard_choice(self, key=CURRENT, value=None):
        settings = self.settings_data()
        settings.setdefault(helper.DASHBOARD_KEY, {})[key] = value or 'vr-input-workshop://5678'
        write(self.settings, settings)

    def prepare(self, **kwargs):
        return helper.prepare(self.local, self.runtime, self.addon, **kwargs)

    def all_bytes(self):
        return {str(path.relative_to(self.root)): path.read_bytes()
                for path in self.root.rglob('*') if path.is_file()}

    def upgrade(self):
        previous = self.addon.with_name('previous-controller-addon')
        self.addon.rename(previous)
        self.fresh_addon()
        self.prepare(previous=previous)
        return previous

    def test_merge_only_adds_four_scroll_routes_and_preserves_all_assignments(self):
        original = fixture_binding()
        merged = helper.merge_binding(original, self.manifest)
        self.assertEqual(original, fixture_binding())
        for group in helper.SETS:
            additions = merged['bindings'][group]['sources'][-2:]
            self.assertEqual(additions, [{'path': '/user/hand/' + side + helper.SOURCE,
                                         'mode': 'joystick',
                                         'parameters': {'deadzone_pct': '0', 'maxzone_pct': '100'},
                                         'inputs': {'position': {'output': group + '/in/scroll'}}}
                                        for side in ('left', 'right')])
            del merged['bindings'][group]['sources'][-2:]
        merged['name'] = original['name']
        self.assertEqual(merged, original)

    def test_generated_scroll_parameters_are_independent_and_native_parameters_are_unchanged(self):
        original = fixture_binding()
        merged = helper.merge_binding(original, self.manifest)
        native = original['bindings']['/actions/global']['sources'][0]['parameters']
        self.assertEqual(merged['bindings']['/actions/global']['sources'][0]['parameters'], native)
        added = [source for group in helper.SETS for source in merged['bindings'][group]['sources'][-2:]]
        self.assertTrue(all(source['parameters'] == helper.SCROLL_PARAMETERS for source in added))
        added[0]['parameters']['deadzone_pct'] = '19'
        self.assertTrue(all(source['parameters']['deadzone_pct'] == '0' for source in added[1:]))
        self.assertEqual(helper.SCROLL_PARAMETERS['deadzone_pct'], '0')
        self.assertEqual(original, fixture_binding())

    def test_missing_current_key_prepares_only_default_without_promoting_history(self):
        before = self.settings.read_bytes()
        source = self.default_source.read_bytes()
        result = self.prepare()
        self.assertEqual(result['state'], 'prepared-default')
        self.assertFalse(result['automatic'])
        self.assertEqual(self.settings.read_bytes(), before)
        self.assertEqual(self.default_source.read_bytes(), source)
        self.assertEqual(self.read(helper.BINDING)['app_key'], helper.APP_KEY)
        self.assertEqual(self.read(helper.PROFILE)['default_bindings'][0], self.profile['default_bindings'][0])
        self.assertEqual(self.read(helper.PROFILE)['default_bindings'][1],
                         {'app_key': helper.APP_KEY, 'binding_url': helper.BINDING.name})

    def test_unique_dashboard_current_key_enables_vrchat_without_copying_dashboard_binding(self):
        observed = 'oculus_touch_runtimeA_CurrentURL_steamvrinput'
        self.dashboard_choice(key=observed)
        before = self.settings_data()
        result = self.prepare()
        self.assertTrue(result['automatic'])
        self.assertEqual(result['selectionProof'], 'observed-other-app-current-key')
        self.assertEqual(result['selectionEvidence'], {'appKey': helper.DASHBOARD_KEY, 'key': observed})
        actual = self.settings_data()
        self.assertEqual(actual[helper.APP_KEY][observed], (self.addon / helper.BINDING).as_uri())
        del actual[helper.APP_KEY][observed]
        self.assertEqual(actual, before)
        self.assertEqual((self.addon / helper.BACKUP / 'source-binding.json').read_bytes(), self.default_source.read_bytes())
        original = json.loads((self.addon / helper.BACKUP / 'original-selection.json').read_text())
        self.assertEqual(original['choices'], {})
        self.assertIn('live active binding is not verified', result['message'])

    def test_borrowed_key_install_is_idempotent_and_keeps_initial_evidence_across_upgrade(self):
        self.dashboard_choice()
        self.prepare()
        before = self.all_bytes()
        self.prepare()
        self.assertEqual(self.all_bytes(), before)
        first = (self.addon / helper.BACKUP / 'original-selection.json').read_bytes()
        self.upgrade()
        state = self.read(helper.OWNER)
        self.assertEqual(state['selectionProof'], 'observed-other-app-current-key')
        self.assertEqual(state['selectionEvidence'], {'appKey': helper.DASHBOARD_KEY, 'key': CURRENT})
        self.assertEqual((self.addon / helper.BACKUP / 'original-selection.json').read_bytes(), first)
        restored = helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertEqual(restored['selectionCount'], 1)
        self.assertNotIn(CURRENT, self.settings_data()[helper.APP_KEY])
        self.assertEqual(self.settings_data()[helper.DASHBOARD_KEY][CURRENT], 'vr-input-workshop://5678')

    def test_borrowed_key_failed_registration_restores_default_absence(self):
        self.dashboard_choice()
        before = self.settings_data()
        self.prepare()
        helper.rollback_install(self.local, self.runtime, self.addon)
        self.assertEqual(self.settings_data(), before)

    def test_actual_target_custom_choice_wins_over_different_dashboard_key(self):
        self.dashboard_choice(key='oculus_touch_other_CurrentURL_steamvrinput')
        custom = fixture_binding()
        custom['bindings']['/actions/gameplay']['sources'][0]['user_option'] = 'preserved'
        write(self.user_source, custom)
        self.choose()
        original = self.settings_data()
        result = self.prepare()
        self.assertTrue(result['automatic'])
        self.assertEqual(result['selectionProof'], 'observed-current-url')
        self.assertNotIn('selectionEvidence', result)
        self.assertEqual(self.read(helper.BINDING)['bindings']['/actions/gameplay'], custom['bindings']['/actions/gameplay'])
        self.assertNotIn('oculus_touch_other_CurrentURL_steamvrinput', self.settings_data()[helper.APP_KEY])
        helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertEqual(self.settings_data(), original)

    def test_ambiguous_dashboard_keys_keep_manual_fallback_even_when_values_match(self):
        self.dashboard_choice()
        self.dashboard_choice(key='oculus_touch_other_CurrentURL_steamvrinput')
        before = self.settings.read_bytes()
        result = self.prepare()
        self.assertFalse(result['automatic'])
        self.assertEqual(result['state'], 'prepared-default')
        self.assertEqual(self.settings.read_bytes(), before)

    def test_history_other_apps_and_other_api_or_controller_keys_are_not_evidence(self):
        settings = self.settings_data()
        settings[helper.DASHBOARD_KEY] = {
            'oculus_touch_250820_AutosaveURL_steamvrinput': 'vr-input-workshop://1234',
            'oculus_touch_250820_PreviousURL_steamvrinput': 'vr-input-workshop://1234',
            'oculus_touch_250820_CurrentURL_openxr': 'vr-input-workshop://1234',
            'knuckles_250820_CurrentURL_steamvrinput': 'vr-input-workshop://1234'}
        settings['steam.overlay.unknown'] = {CURRENT: 'vr-input-workshop://1234'}
        write(self.settings, settings)
        before = self.settings.read_bytes()
        self.assertFalse(self.prepare()['automatic'])
        self.assertEqual(self.settings.read_bytes(), before)

    def test_malformed_target_current_choice_never_uses_other_app_fallback(self):
        self.dashboard_choice()
        settings = self.settings_data()
        settings[helper.APP_KEY][CURRENT] = ''
        write(self.settings, settings)
        before = self.all_bytes()
        with self.assertRaises(helper.BindingWarning):
            self.prepare()
        self.assertEqual(self.all_bytes(), before)

    def test_borrowed_key_uninstall_preserves_later_user_target_and_dashboard_changes(self):
        self.dashboard_choice()
        self.prepare()
        settings = self.settings_data()
        settings[helper.APP_KEY][CURRENT] = 'vr-input-workshop://999'
        settings[helper.DASHBOARD_KEY][CURRENT] = 'vr-input-workshop://888'
        settings['steamvr']['new_option'] = 'keep'
        write(self.settings, settings)
        result = helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertFalse(result['selectionRestored'])
        self.assertEqual(self.settings_data(), settings)

    def test_invalid_cross_app_evidence_is_refused_without_restoring_any_settings(self):
        self.dashboard_choice()
        self.prepare()
        state = self.read(helper.OWNER)
        state['selectionEvidence']['appKey'] = 'steam.overlay.unknown'
        write(self.addon / helper.OWNER, state)
        before = self.all_bytes()
        with self.assertRaises(helper.BindingWarning):
            helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertEqual(self.all_bytes(), before)

    def test_observed_current_key_selects_additive_copy_and_uninstall_restores_choice(self):
        self.choose()
        original = self.settings_data()
        source = self.user_source.read_bytes()
        self.assertTrue(self.prepare()['automatic'])
        actual = self.settings_data()
        self.assertEqual(actual[helper.APP_KEY][CURRENT], (self.addon / helper.BINDING).as_uri())
        actual[helper.APP_KEY][CURRENT] = original[helper.APP_KEY][CURRENT]
        self.assertEqual(actual, original)
        self.assertEqual(self.user_source.read_bytes(), source)
        self.assertEqual((self.addon / helper.BACKUP / 'steamvr-settings-before-install.json').read_bytes(), encoded(original))
        restored = helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertTrue(restored['restored'])
        self.assertEqual(restored['selectionCount'], 1)
        self.assertEqual(self.settings_data(), original)
        self.assertEqual(self.read(helper.PROFILE), self.profile)

    def test_internal_session_observation_restores_absence_not_an_autosave_choice(self):
        original = self.settings_data()
        self.assertTrue(self.prepare(verified_key=CURRENT)['automatic'])
        self.assertEqual(self.settings_data()[helper.APP_KEY][CURRENT], (self.addon / helper.BINDING).as_uri())
        self.assertEqual(self.read(helper.OWNER)['selectionProof'], 'explicit-session-observation')
        helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertEqual(self.settings_data(), original)

    def test_invalid_or_conflicting_internal_key_refuses_without_writes(self):
        for value in ('steamvr_CurrentURL_steamvrinput', 'oculus_touch_250820_AutosaveURL_steamvrinput', 4):
            before = self.all_bytes()
            with self.assertRaises(helper.BindingWarning):
                self.prepare(verified_key=value)
            self.assertEqual(self.all_bytes(), before)
        self.choose()
        before = self.all_bytes()
        with self.assertRaises(helper.BindingWarning):
            self.prepare(verified_key='oculus_touch_other_CurrentURL_steamvrinput')
        self.assertEqual(self.all_bytes(), before)

    def test_cached_explicit_workshop_binding_keeps_every_user_assignment(self):
        cached = self.config.parent / 'steamapps/workshop/content/250820/1234/fixture_legacy.bin'
        custom = fixture_binding()
        custom['bindings']['/actions/gameplay']['sources'][0]['user_parameter'] = 'keep'
        write(cached, custom)
        self.choose('vr-input-workshop://1234')
        before = cached.read_bytes()
        self.assertTrue(self.prepare()['automatic'])
        self.assertEqual(self.read(helper.BINDING)['bindings']['/actions/gameplay'], custom['bindings']['/actions/gameplay'])
        self.assertEqual(cached.read_bytes(), before)
        helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertEqual(self.settings_data()[helper.APP_KEY][CURRENT], 'vr-input-workshop://1234')

    def test_ambiguous_or_uncached_workshop_refuses_without_writes(self):
        self.choose('vr-input-workshop://1234')
        before = self.all_bytes()
        with self.assertRaises(helper.BindingWarning):
            self.prepare()
        self.assertEqual(self.all_bytes(), before)
        cache = self.config.parent / 'steamapps/workshop/content/250820/1234'
        write(cache / 'first.bin', fixture_binding())
        write(cache / 'second.bin', fixture_binding())
        before = self.all_bytes()
        with self.assertRaises(helper.BindingWarning):
            self.prepare()
        self.assertEqual(self.all_bytes(), before)

    def test_unsupported_selected_url_never_falls_back_and_overwrites_custom_choice(self):
        for value in ('https://example.invalid/binding.json', 'vr-input-workshop://../123',
                      self.user_source.as_uri() + '?different=1'):
            self.choose(value)
            before = self.all_bytes()
            with self.assertRaises(helper.BindingWarning):
                self.prepare()
            self.assertEqual(self.all_bytes(), before)

    def test_default_alias_accepted_only_for_registered_default(self):
        write(self.user_source, fixture_binding(alias=True))
        self.choose()
        before = self.all_bytes()
        with self.assertRaises(helper.BindingWarning):
            self.prepare()
        self.assertEqual(self.all_bytes(), before)
        self.choose(self.default_source.as_uri())
        self.assertTrue(self.prepare()['automatic'])

    def test_repeated_prepare_is_byte_idempotent_with_first_predecessor_intact(self):
        self.choose()
        self.prepare()
        before = self.all_bytes()
        self.prepare()
        self.assertEqual(self.all_bytes(), before)
        self.assertEqual(json.loads((self.addon / helper.BACKUP / 'original-selection.json').read_text())['choices'],
                         {CURRENT: self.user_source.as_uri()})

    def test_upgrade_preserves_first_selection_and_rebuilds_from_unmerged_source(self):
        self.choose()
        self.prepare()
        first = (self.addon / helper.BACKUP / 'original-selection.json').read_bytes()
        self.upgrade()
        self.assertEqual((self.addon / helper.BACKUP / 'original-selection.json').read_bytes(), first)
        for group in helper.SETS:
            self.assertEqual(len(self.read(helper.BINDING)['bindings'][group]['sources']), 4)
        helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertEqual(self.settings_data()[helper.APP_KEY][CURRENT], self.user_source.as_uri())

    def test_upgrade_failure_rollback_keeps_previous_owned_url_until_old_tree_is_restored(self):
        self.choose()
        self.prepare()
        previous = self.upgrade()
        original_selected = self.settings_data()[helper.APP_KEY][CURRENT]
        helper.rollback_install(self.local, self.runtime, self.addon)
        self.assertEqual(self.settings_data()[helper.APP_KEY][CURRENT], original_selected)
        self.assertTrue((previous / helper.BINDING).exists())

    def test_first_install_failure_rollback_restores_default_absence(self):
        before = self.settings_data()
        self.prepare(verified_key=CURRENT)
        helper.rollback_install(self.local, self.runtime, self.addon)
        self.assertEqual(self.settings_data(), before)

    def test_first_install_failure_rollback_restores_explicit_user_choice(self):
        self.choose()
        before = self.settings_data()
        self.prepare()
        helper.rollback_install(self.local, self.runtime, self.addon)
        self.assertEqual(self.settings_data(), before)

    def test_user_binding_edit_same_tree_is_refused_but_migrated_at_same_url_on_upgrade(self):
        self.choose()
        self.prepare()
        edited = self.read(helper.BINDING)
        edited['description'] = 'My local custom edit'
        write(self.addon / helper.BINDING, edited)
        before = self.all_bytes()
        with self.assertRaisesRegex(helper.BindingWarning, 'edited'):
            self.prepare()
        self.assertEqual(self.all_bytes(), before)
        self.upgrade()
        self.assertEqual(self.read(helper.BINDING), edited)
        self.assertEqual(helper.status(self.addon)['state'], 'user-edited')
        self.assertEqual(self.settings_data()[helper.APP_KEY][CURRENT], (self.addon / helper.BINDING).as_uri())

    def test_upgrade_uses_new_custom_choice_instead_of_inactive_old_edited_generated_file(self):
        self.choose()
        self.prepare()
        inactive_edit = self.read(helper.BINDING)
        inactive_edit['description'] = 'Inactive old generated binding edit'
        inactive_edit['bindings']['/actions/gameplay']['sources'][0]['old_edit'] = True
        write(self.addon / helper.BINDING, inactive_edit)
        current_source = self.root / 'personal/new-custom.json'
        current = fixture_binding()
        current['description'] = 'My new active custom binding'
        current['bindings']['/actions/gameplay']['sources'][0]['new_custom_option'] = 'keep'
        write(current_source, current)
        self.choose(current_source.as_uri())
        previous = self.upgrade()
        merged = self.read(helper.BINDING)
        self.assertEqual(merged['description'], current['description'])
        self.assertEqual(merged['bindings']['/actions/gameplay'], current['bindings']['/actions/gameplay'])
        self.assertEqual(json.loads((previous / helper.BINDING).read_text()), inactive_edit)
        self.assertFalse(self.read(helper.OWNER)['userEdited'])
        self.assertEqual((self.addon / helper.BACKUP / 'source-binding.json').read_bytes(), current_source.read_bytes())
        self.assertEqual(self.settings_data()[helper.APP_KEY][CURRENT], (self.addon / helper.BINDING).as_uri())

    def test_upgrade_from_default_ignores_inactive_edited_file_with_other_app_key_fallback(self):
        self.choose()
        self.prepare()
        inactive_edit = self.read(helper.BINDING)
        inactive_edit['description'] = 'Inactive old generated binding edit'
        write(self.addon / helper.BINDING, inactive_edit)
        settings = self.settings_data()
        del settings[helper.APP_KEY][CURRENT]
        settings[helper.DASHBOARD_KEY] = {CURRENT: 'vr-input-workshop://5678'}
        write(self.settings, settings)
        previous = self.upgrade()
        self.assertEqual(self.read(helper.BINDING)['description'], fixture_binding(alias=True)['description'])
        self.assertEqual((self.addon / helper.BACKUP / 'source-binding.json').read_bytes(), self.default_source.read_bytes())
        self.assertEqual(json.loads((previous / helper.BINDING).read_text()), inactive_edit)
        self.assertFalse(self.read(helper.OWNER)['userEdited'])
        self.assertEqual(self.read(helper.OWNER)['selectionProof'], 'observed-other-app-current-key')

    def test_uninstall_preserves_new_user_selection_and_unrelated_settings(self):
        self.choose()
        self.prepare()
        settings = self.settings_data()
        settings[helper.APP_KEY][CURRENT] = 'vr-input-workshop://98765'
        settings[helper.APP_KEY]['another_setting'] = 'new'
        settings['steamvr']['keep'] = 'user edited'
        write(self.settings, settings)
        helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertEqual(self.settings_data(), settings)

    def test_restore_only_owned_current_keys_when_user_changed_another_key(self):
        second = 'oculus_touch_other_CurrentURL_steamvrinput'
        self.choose()
        self.choose(key=second)
        self.prepare()
        settings = self.settings_data()
        settings[helper.APP_KEY][second] = 'vr-input-workshop://98765'
        write(self.settings, settings)
        result = helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertEqual(result['selectionCount'], 1)
        self.assertEqual(self.settings_data()[helper.APP_KEY][CURRENT], self.user_source.as_uri())
        self.assertEqual(self.settings_data()[helper.APP_KEY][second], 'vr-input-workshop://98765')

    def test_uninstall_preserves_other_default_entries_and_profile_properties(self):
        self.prepare()
        profile = self.read(helper.PROFILE)
        profile['default_bindings'].append({'app_key': 'another.game', 'binding_url': 'new.json'})
        profile['user_property'] = 'keep'
        write(self.addon / helper.PROFILE, profile)
        helper.restore(self.addon)
        expected = copy.deepcopy(self.profile)
        expected['default_bindings'].append({'app_key': 'another.game', 'binding_url': 'new.json'})
        expected['user_property'] = 'keep'
        self.assertEqual(self.read(helper.PROFILE), expected)

    def test_uninstall_preserves_user_edited_own_default(self):
        self.prepare()
        profile = self.read(helper.PROFILE)
        profile['default_bindings'][-1]['binding_url'] = 'my-own.json'
        write(self.addon / helper.PROFILE, profile)
        result = helper.restore(self.addon)
        self.assertFalse(result['restored'])
        self.assertEqual(self.read(helper.PROFILE), profile)

    def test_unknown_manifest_action_or_binding_schema_is_read_only(self):
        for change in ('action', 'binding', 'registry', 'duplicate'):
            with self.subTest(change=change):
                write(self.manifest_path, self.manifest)
                write(self.default_source, fixture_binding(alias=True))
                if change == 'action':
                    value = copy.deepcopy(self.manifest)
                    value['actions'][1]['type'] = 'boolean'
                    write(self.manifest_path, value)
                elif change == 'binding':
                    value = fixture_binding(alias=True)
                    value['bindings']['/actions/menu']['sources'] = {}
                    write(self.default_source, value)
                elif change == 'registry':
                    value = json.loads(self.registry.read_text())
                    value['applications'].append(copy.deepcopy(value['applications'][0]))
                    write(self.registry, value)
                else:
                    self.default_source.write_bytes(b'{"app_key":"a","app_key":"b"}')
                before = self.all_bytes()
                with self.assertRaises(helper.BindingWarning):
                    self.prepare()
                self.assertEqual(self.all_bytes(), before)
                if change == 'registry':
                    value['applications'].pop()
                    write(self.registry, value)

    def test_existing_qpro_source_not_blindly_duplicated(self):
        value = fixture_binding(alias=True)
        value['bindings']['/actions/menu']['sources'].append(
            {'path': '/user/hand/left' + helper.SOURCE, 'mode': 'joystick', 'inputs': {}})
        write(self.default_source, value)
        before = self.all_bytes()
        with self.assertRaises(helper.BindingWarning):
            self.prepare()
        self.assertEqual(self.all_bytes(), before)

    def test_concurrent_profile_edit_during_merge_is_preserved_and_owned_writes_rolled_back(self):
        self.choose()
        original = helper.merge_binding
        def concurrent_edit(*args, **kwargs):
            profile = copy.deepcopy(self.profile)
            profile['user_property'] = 'concurrent edit'
            write(self.addon / helper.PROFILE, profile)
            return original(*args, **kwargs)
        settings = self.settings.read_bytes()
        with patch.object(helper, 'merge_binding', side_effect=concurrent_edit):
            with self.assertRaises(helper.BindingWarning):
                self.prepare()
        self.assertEqual(self.read(helper.PROFILE)['user_property'], 'concurrent edit')
        self.assertFalse((self.addon / helper.OWNER).exists())
        self.assertFalse((self.addon / helper.BINDING).exists())
        self.assertEqual(self.settings.read_bytes(), settings)

    def test_failed_final_settings_write_rolls_back_every_owned_write(self):
        self.choose()
        before = self.all_bytes()
        original = Path.replace
        def failing_replace(path, target):
            if Path(target) == self.settings:
                raise OSError('fixture settings replacement failed')
            return original(path, target)
        with patch.object(Path, 'replace', failing_replace):
            with self.assertRaises(OSError):
                self.prepare()
        after = self.all_bytes()
        backup = str((self.addon / helper.BACKUP / 'steamvr-settings-before-install.json').relative_to(self.root))
        self.assertEqual(after.pop(backup), before[str(self.settings.relative_to(self.root))])
        self.assertEqual(after, before)

    def test_corrupt_uninstall_predecessor_refuses_before_any_setting_change(self):
        self.choose()
        self.prepare()
        backup = self.addon / helper.BACKUP / 'original-profile.json'
        backup.write_bytes(b'{}')
        before = self.all_bytes()
        with self.assertRaises(helper.BindingWarning):
            helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertEqual(self.all_bytes(), before)

    def test_profile_guard_does_not_take_ownership_of_other_app_defaults(self):
        self.prepare()
        profile = self.read(helper.PROFILE)
        profile['default_bindings'].append({'app_key': 'openvr.component.vrcompositor', 'binding_url': 'dashboard.json'})
        write(self.addon / helper.PROFILE, profile)
        self.assertEqual(helper.status(self.addon)['state'], 'prepared-default')
        self.prepare()
        self.assertEqual(self.read(helper.PROFILE)['default_bindings'][-1]['app_key'], 'openvr.component.vrcompositor')

    def test_owned_profile_edit_is_not_overwritten_during_reinstall_or_upgrade(self):
        self.prepare()
        profile = self.read(helper.PROFILE)
        profile['default_bindings'][-1]['binding_url'] = 'my-local-binding.json'
        write(self.addon / helper.PROFILE, profile)
        before = self.all_bytes()
        with self.assertRaisesRegex(helper.BindingWarning, 'edited'):
            self.prepare()
        self.assertEqual(self.all_bytes(), before)
        previous = self.addon.with_name('previous-controller-addon')
        self.addon.rename(previous)
        self.fresh_addon()
        before = self.all_bytes()
        with self.assertRaisesRegex(helper.BindingWarning, 'edited'):
            self.prepare(previous=previous)
        self.assertEqual(self.all_bytes(), before)

    def test_failed_restore_keeps_full_settings_recovery_and_every_actual_setting(self):
        self.choose()
        self.prepare()
        settings = self.settings.read_bytes()
        profile = (self.addon / helper.PROFILE).read_bytes()
        original = Path.replace
        def failing_replace(path, target):
            if Path(target) == self.settings:
                raise OSError('fixture settings replacement failed')
            return original(path, target)
        with patch.object(Path, 'replace', failing_replace):
            with self.assertRaises(OSError):
                helper.restore(self.addon, self.local, self.runtime, self.addon)
        self.assertEqual(self.settings.read_bytes(), settings)
        self.assertEqual((self.addon / helper.PROFILE).read_bytes(), profile)
        self.assertEqual((self.addon / helper.BACKUP / 'steamvr-settings-before-restore.json').read_bytes(), settings)

    def test_dashboard_and_vrchat_defaults_restore_independently_in_either_order(self):
        dashboard = helper._files
        for vrchat_first in (True, False):
            with self.subTest(vrchat_first=vrchat_first):
                if not vrchat_first:
                    self.addon = self.addon.with_name('second-controller-addon')
                    self.fresh_addon()
                groups = ('/actions/scroll_smooth', '/actions/scroll_discrete')
                write(self.runtime / 'resources/config/vrcompositor_actions.json',
                      {'actions': [{'name': group + '/in/Scroll', 'type': 'vector2'} for group in groups]})
                write(self.runtime / 'drivers/oculus/resources/input/vrcompositor_bindings_touch.json',
                      {'app_key': dashboard.APP_KEY, 'category': 'steamvr_input', 'controller_type': 'oculus_touch',
                       'action_manifest_version': 0,
                       'bindings': {group: {'sources': []} for group in groups}})
                dashboard.prepare(self.local, self.runtime, self.addon)
                self.prepare()
                operations = [lambda: helper.restore(self.addon), lambda: dashboard.restore(self.addon)]
                if not vrchat_first:
                    operations.reverse()
                for operation in operations:
                    self.assertTrue(operation()['restored'])
                self.assertEqual(self.read(helper.PROFILE), self.profile)


if __name__ == '__main__':
    unittest.main()
