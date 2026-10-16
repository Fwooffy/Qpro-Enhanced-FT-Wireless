"""Offline contract checks for user-assignable SteamVR inputs; no VR runtime used."""
from pathlib import Path
import json
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]
RESOURCES = ROOT / 'addon' / 'resources'


class InputProfileTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.profile = json.loads((RESOURCES / 'input' / 'quest_pro_touchpad.json').read_text())
        cls.sources = cls.profile['input_source']
        cls.provider = (ROOT / 'steamvr_provider.cpp').read_text()

    def test_extra_components_match_provider_without_replacing_native_inputs(self):
        extra = self.sources['/input/trackpad']
        self.assertEqual(extra['type'], 'trackpad')
        declared = {'/input/trackpad/x', '/input/trackpad/y'}
        declared.update('/input/trackpad/' + component for component in ('touch', 'force', 'click')
                        if extra.get(component) is True)
        declared.update({'/input/qpro_scroll/x', '/input/qpro_scroll/y', '/input/qpro_scroll/touch'})
        created = set(re.findall(r'scalar\(\d+,\s*"([^"]+)"', self.provider))
        created.update(re.findall(r'CreateScalarComponent\(container,\s*"([^"]+)"', self.provider))
        created.update(re.findall(r'CreateBooleanComponent\(container,\s*"([^"]+)"', self.provider))
        self.assertEqual(declared, created)
        self.assertEqual(len(created), 8)
        self.assertEqual(self.profile['controller_type'], 'oculus_touch')
        self.assertIsNone(re.search(r'SetStringProperty\([^;]*Prop_ControllerType_String', self.provider))
        self.assertEqual(self.profile['legacy_binding'], '{oculus}/input/legacy_bindings_touch.json')

    def test_native_buttons_skeletons_haptics_and_poses_remain_available(self):
        physical = {'/input/system', '/input/trigger', '/input/joystick', '/input/grip',
                    '/input/x', '/input/a', '/input/y', '/input/b', '/input/thumbrest'}
        poses = {'/pose/raw', '/pose/base', '/pose/handgrip', '/pose/tip',
                 '/pose/openxr_aim', '/pose/openxr_grip'}
        skeletons = {'/input/skeleton/left', '/input/skeleton/right'}
        self.assertEqual(set(self.sources), physical | poses | skeletons |
                         {'/output/haptic', '/input/trackpad', '/input/qpro_scroll'})
        for path in ('/input/trigger', '/input/grip'):
            self.assertEqual(self.sources[path]['type'], 'trigger')
            self.assertIs(self.sources[path]['value'], True)
            self.assertIs(self.sources[path]['touch'], True)
        self.assertEqual(self.sources['/input/joystick']['type'], 'joystick')
        self.assertIs(self.sources['/input/joystick']['click'], True)
        self.assertIs(self.sources['/input/joystick']['touch'], True)
        self.assertIs(self.sources['/input/thumbrest']['click'], False)
        self.assertIs(self.sources['/input/thumbrest']['touch'], True)
        for side, buttons in (('left', ('system', 'x', 'y')), ('right', ('a', 'b'))):
            for button in buttons:
                source = self.sources['/input/' + button]
                self.assertEqual(source['side'], side)
                self.assertIs(source['click'], True)
                self.assertIs(source['touch'], True)
        for side in ('left', 'right'):
            source = self.sources['/input/skeleton/' + side]
            self.assertEqual(source['type'], 'skeleton')
            self.assertEqual(source['side'], side)
            self.assertEqual(source['skeleton'], '/skeleton/hand/' + side)
        self.assertTrue(all(self.sources[path]['type'] == 'pose' for path in poses))
        self.assertEqual(self.sources['/output/haptic']['type'], 'vibration')
        self.assertEqual(self.profile['mirror_mappings'], [
            ['/user/hand/left/input/x', '/user/hand/right/input/a'],
            ['/user/hand/left/input/y', '/user/hand/right/input/b']])

    def test_editor_names_distinguish_extra_surface_and_every_source_has_an_image_point(self):
        languages = json.loads((RESOURCES / 'localization' / 'localization.json').read_text())
        english = next(value for value in languages if value['language_tag'] == 'en_US')
        for path, source in self.sources.items():
            self.assertTrue(english[path].strip(), path)
            point = source['binding_image_point']
            self.assertEqual(len(point), 2)
            self.assertTrue(all(type(value) is int and value >= 0 for value in point), path)
        self.assertIn('Qpro', english['/input/trackpad'])
        self.assertIn('extra', english['/input/trackpad'])
        self.assertNotEqual(english['/input/trackpad'], english['/input/thumbrest'])
        self.assertNotEqual(english['/input/trackpad'], english['/input/joystick'])
        self.assertEqual(self.sources['/input/trackpad']['binding_image_point'],
                         self.sources['/input/thumbrest']['binding_image_point'])
        self.assertEqual(self.sources['/input/qpro_scroll']['type'], 'joystick')
        self.assertIs(self.sources['/input/qpro_scroll']['touch'], True)
        self.assertIs(self.sources['/input/qpro_scroll']['click'], False)
        self.assertIn('Swipe', english['/input/qpro_scroll'])
        self.assertNotEqual(english['/input/qpro_scroll'], english['/input/trackpad'])

    def test_user_chooses_actions_and_profile_resolves_in_this_driver(self):
        manifest = json.loads((ROOT / 'addon' / 'driver.vrdrivermanifest').read_text())
        self.assertEqual(self.profile['resource_root'], manifest['name'])
        self.assertEqual(self.profile['driver_name'], manifest['name'])
        profile_path = '{' + manifest['name'] + '}/input/quest_pro_touchpad.json'
        self.assertIn('constexpr char profile[] = "' + profile_path + '"', self.provider)
        self.assertEqual(self.profile.get('default_bindings', []), [])
        for asset in RESOURCES.rglob('*.json'):
            text = asset.read_text()
            self.assertNotIn('"app_key"', text, str(asset))
            self.assertNotIn('/actions/', text, str(asset))


if __name__ == '__main__':
    unittest.main()
