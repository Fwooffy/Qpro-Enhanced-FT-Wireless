"""Add only thumb-rest menu scrolling to a locally installed VRChat binding.

The original game binding remains a read-only input. The generated local copy
keeps movement, gestures, buttons, poses, haptics and chords byte-for-value. Only
the two existing menu Scroll vector actions receive extra sources. SteamVR must
be closed while prepare, rollback_install or restore changes its selection.
"""
from __future__ import annotations

import copy
import importlib.util
from pathlib import Path
import re
from urllib.parse import urlsplit
from urllib.request import url2pathname


APP_KEY = 'steam.app.438100'
DASHBOARD_KEY = 'openvr.component.vrcompositor'
CANONICAL_ALIAS = 'application.generated.unity.vrchat.exe'
FORMAT = 'qpro-vrchat-binding-v1'
PROFILE = Path('resources/input/quest_pro_touchpad.json')
BINDING = PROFILE.with_name('qpro_vrchat_scroll.json')
OWNER = PROFILE.with_name('qpro-vrchat-owner.json')
BACKUP = PROFILE.parent / '.qpro-vrchat-backup'
SOURCE = '/input/qpro_scroll'
SETS = ('/actions/global', '/actions/menu')
SCROLL_PARAMETERS = {'deadzone_pct': '0', 'maxzone_pct': '100'}
KEY = re.compile(r'oculus_touch(?:_[A-Za-z0-9]+)*_CurrentURL_steamvrinput')
SNAPSHOTS = (BACKUP / 'original-profile.json', BACKUP / 'source-binding.json',
             BACKUP / 'original-selection.json', BACKUP / 'install-selection.json',
             BACKUP / 'steamvr-settings-before-install.json', BINDING, PROFILE, OWNER)

# Use the same strict JSON reader and expected-bytes transaction as dashboard
# setup. Loading by sibling path also supports supervisor imports in fixtures.
_spec = importlib.util.spec_from_file_location(
    'qpro_vrchat_binding_files', Path(__file__).with_name('dashboard_bindings.py'))
_files = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_files)
BindingWarning = _files.BindingWarning
_bytes, _sha, _plain, _transaction = _files._bytes, _files._sha, _files._plain, _files._transaction


def _read(path, *, owned=False):
    try:
        return _files._read(path, owned=owned)
    except BindingWarning as error:
        raise BindingWarning(str(error).replace('SteamVR dashboard', 'SteamVR/VRChat')
                             .replace('dashboard', 'VRChat')) from error


def _json(data):
    try:
        return _files._json(data)
    except BindingWarning as error:
        raise BindingWarning(str(error).replace('dashboard', 'VRChat')) from error


def _scope(addon):
    _plain(addon)
    marker = _json(_read(addon / 'qpro-owner.json', owned=True))
    if not isinstance(marker, dict) or marker.get('format') != 'qpro-controller-addon-v1':
        raise BindingWarning('The controller add-on has no Qpro ownership marker; VRChat bindings were kept.')


def _configuration(local, runtime):
    try:
        return _files._configuration(local, runtime)
    except BindingWarning as error:
        raise BindingWarning(str(error).replace('dashboard', 'VRChat')) from error


def _selection(data, expected, *, allow_ambiguous=False):
    section = data.get(APP_KEY, {})
    if not isinstance(section, dict):
        raise BindingWarning('The VRChat binding selection is not recognized; your choices were kept.')
    choices = {key: value for key, value in section.items() if KEY.fullmatch(key)}
    if any(not isinstance(value, str) or not value.strip() for value in choices.values()):
        raise BindingWarning('A VRChat binding choice is not recognized; your choices were kept.')
    if not allow_ambiguous and len(set(choices.values())) > 1:
        raise BindingWarning('SteamVR has multiple different VRChat controller bindings; your choices were kept.')
    return {'choices': choices, 'settingsSha256': _sha(expected)}


def _registered_manifest(config):
    registry = _json(_read(config / 'steamapps.vrmanifest'))
    applications = registry.get('applications') if isinstance(registry, dict) else None
    if not isinstance(applications, list):
        raise BindingWarning('SteamVR has no recognized application registry. Open VRChat once, close SteamVR, '
                             'then retry controller setup.')
    matches = [app for app in applications if isinstance(app, dict) and app.get('app_key') == APP_KEY]
    if len(matches) != 1 or not isinstance(matches[0].get('action_manifest_path'), str):
        raise BindingWarning('VRChat has no unique registered action manifest. Open VRChat once, close SteamVR, '
                             'then retry controller setup.')
    path = Path(matches[0]['action_manifest_path'])
    if not path.is_absolute():
        raise BindingWarning('The registered VRChat manifest path is not absolute; your bindings were kept.')
    return path, _json(_read(path))


def _default_source(manifest_path, manifest):
    defaults = manifest.get('default_bindings') if isinstance(manifest, dict) else None
    if not isinstance(defaults, list):
        raise BindingWarning('VRChat has no recognized default controller binding; your binding was kept.')
    matches = [item for item in defaults if isinstance(item, dict) and item.get('controller_type') == 'oculus_touch']
    if len(matches) != 1 or not isinstance(matches[0].get('binding_url'), str):
        raise BindingWarning('VRChat has no unique Touch controller default; your binding was kept.')
    value = matches[0]['binding_url']
    uri = urlsplit(value)
    if uri.scheme or uri.netloc or uri.query or uri.fragment:
        raise BindingWarning('VRChat uses an unsupported default binding location; your binding was kept.')
    relative = Path(value)
    if relative.is_absolute():
        raise BindingWarning('VRChat uses an unsupported default binding location; your binding was kept.')
    path = manifest_path.parent / relative
    if not path.resolve().is_relative_to(manifest_path.parent.resolve()):
        raise BindingWarning('VRChat default binding leaves its registered manifest folder; your binding was kept.')
    return path, _read(path), True


def _source(choice, config, manifest_path, manifest):
    if not choice:
        return _default_source(manifest_path, manifest)
    uri = urlsplit(choice)
    if uri.query or uri.fragment:
        raise BindingWarning('The VRChat binding URL is not supported; your choice was kept.')
    if uri.scheme.lower() == 'file' and uri.netloc in ('', 'localhost'):
        path = Path(url2pathname(uri.path))
        if path.is_absolute():
            data = _read(path)
            canonical = False
            binding = _json(data)
            if isinstance(binding, dict) and binding.get('app_key') == CANONICAL_ALIAS:
                default, _, _ = _default_source(manifest_path, manifest)
                canonical = path.resolve() == default.resolve()
            return path, data, canonical
    if (uri.scheme.lower() == 'vr-input-workshop' and re.fullmatch(r'[0-9]{1,20}', uri.netloc)
            and uri.path in ('', '/')):
        # This is the observed SteamVR input workshop cache, not a download.
        # Never infer an active choice from historical AutosaveURL/PreviousURL.
        cache = config.parent / 'steamapps/workshop/content/250820' / uri.netloc
        if not cache.is_dir():
            raise BindingWarning('The selected VRChat workshop binding is not cached locally; your choice was kept.')
        files = [path for path in cache.iterdir() if path.is_file()]
        if len(files) > 64:
            raise BindingWarning('The VRChat workshop cache is ambiguous; your choice was kept.')
        matches = []
        for path in files:
            if path.suffix.lower() not in ('.json', '.bin'):
                continue
            try:
                data = _read(path)
                value = _json(data)
            except BindingWarning:
                continue
            if (isinstance(value, dict) and value.get('app_key') == APP_KEY
                    and value.get('controller_type') == 'oculus_touch'):
                matches.append((path, data, False))
        if len(matches) == 1:
            return matches[0]
        raise BindingWarning('The selected VRChat workshop binding is ambiguous or unsupported; your choice was kept.')
    raise BindingWarning('The selected VRChat binding is not a supported local or cached binding; your choice was kept.')


def _owned_url(value, installed):
    if not isinstance(value, str):
        return False
    try:
        uri = urlsplit(value)
        if uri.scheme.lower() != 'file' or uri.netloc not in ('', 'localhost') or uri.query or uri.fragment:
            return False
        path = Path(url2pathname(uri.path))
        return path.is_absolute() and path.resolve() == (installed / BINDING).resolve()
    except (OSError, ValueError):
        # An unrelated user choice can be malformed. Do not let it block
        # compare-restoring another key that still names our exact local file.
        return False


def merge_binding(binding, manifest, *, canonical=False):
    """Copy the selected binding and append only the two menu Scroll routes."""
    allowed_key = binding.get('app_key') if isinstance(binding, dict) else None
    if (not isinstance(binding, dict) or allowed_key not in ((APP_KEY, CANONICAL_ALIAS) if canonical else (APP_KEY,))
            or binding.get('controller_type') != 'oculus_touch' or binding.get('category') != 'steamvr_input'
            or type(binding.get('action_manifest_version')) is not int or binding['action_manifest_version'] != 0):
        raise BindingWarning('This VRChat controller binding format is unsupported; your binding was kept.')
    actions = manifest.get('actions') if isinstance(manifest, dict) else None
    if not isinstance(actions, list):
        raise BindingWarning('VRChat menu actions are not recognized; your binding was kept.')
    for group in SETS:
        matches = [action for action in actions if isinstance(action, dict)
                   and str(action.get('name', '')).lower() == group + '/in/scroll']
        if len(matches) != 1 or matches[0].get('type') != 'vector2':
            raise BindingWarning('VRChat menu Scroll actions changed; your binding was kept.')
    result = copy.deepcopy(binding)
    groups = result.get('bindings')
    if not isinstance(groups, dict):
        raise BindingWarning('VRChat binding groups are not recognized; your binding was kept.')
    for value in groups.values():
        if not isinstance(value, dict) or not isinstance(value.get('sources', []), list):
            raise BindingWarning('VRChat binding sources are not recognized; your binding was kept.')
        for source in value.get('sources', []):
            if not isinstance(source, dict) or not isinstance(source.get('path'), str):
                raise BindingWarning('VRChat binding sources are not recognized; your binding was kept.')
            if source['path'].endswith(SOURCE):
                raise BindingWarning('This VRChat binding already uses the Qpro scroll source; it was kept.')
    for group in SETS:
        value = groups.get(group)
        if not isinstance(value, dict) or not isinstance(value.get('sources'), list):
            raise BindingWarning('VRChat menu binding groups changed; your binding was kept.')
        for side in ('left', 'right'):
            value['sources'].append({'path': '/user/hand/' + side + SOURCE, 'mode': 'joystick',
                                     'parameters': SCROLL_PARAMETERS.copy(),
                                     'inputs': {'position': {'output': group + '/in/scroll'}}})
    # The game-shipped default has a generated Unity alias. Only its proven
    # registered-manifest default may use that alias; the owned copy targets
    # Steam's actual VRChat app key.
    result['app_key'] = APP_KEY
    result['name'] = str(result.get('name', 'VRChat')) + ' + Qpro Companion menu scrolling'
    return result


def _default(profile):
    if (not isinstance(profile, dict) or profile.get('controller_type') != 'oculus_touch'
            or profile.get('resource_root') != 'qpro_controller'):
        raise BindingWarning('The Qpro controller profile is unsupported; VRChat bindings were kept.')
    defaults = profile.get('default_bindings', [])
    if not isinstance(defaults, list) or any(not isinstance(item, dict) for item in defaults):
        raise BindingWarning('The Qpro controller defaults are unsupported; VRChat bindings were kept.')
    matches = [item for item in defaults if item.get('app_key') == APP_KEY]
    if len(matches) > 1:
        raise BindingWarning('The Qpro profile has duplicate VRChat defaults; your choices were kept.')
    return copy.deepcopy(matches[0]) if matches else None


def _with_default(profile, entry):
    _default(profile)
    result = copy.deepcopy(profile)
    values = result.setdefault('default_bindings', [])
    indices = [index for index, item in enumerate(values) if item.get('app_key') == APP_KEY]
    if indices:
        if entry is None:
            del values[indices[0]]
        else:
            values[indices[0]] = copy.deepcopy(entry)
    elif entry is not None:
        values.append(copy.deepcopy(entry))
    return result


def _profile_entry(profile):
    sources = profile.get('input_source') if isinstance(profile, dict) else None
    source = sources.get(SOURCE) if isinstance(sources, dict) else None
    if not isinstance(source, dict) or source.get('type') != 'joystick':
        raise BindingWarning('Reinstall the current controller add-on to enable VRChat menu scrolling.')
    _default(profile)
    return {'app_key': APP_KEY, 'binding_url': BINDING.name}


def _state(addon):
    path = addon / OWNER
    if not path.exists():
        return None
    state = _json(_read(path, owned=True))
    hashes = ('bindingSha256', 'sourceSha256', 'originalProfileSha256', 'originalSelectionSha256',
              'installSelectionSha256', 'installSettingsSha256', 'profileEntrySha256')
    if (not isinstance(state, dict) or state.get('format') != FORMAT
            or any(not re.fullmatch('[a-f0-9]{64}', str(state.get(key, ''))) for key in hashes)
            or type(state.get('userEdited')) is not bool or type(state.get('canonicalSource')) is not bool
            or type(state.get('automatic')) is not bool or not isinstance(state.get('sourcePath'), str)
            or not Path(state.get('sourcePath', '')).is_absolute()
            or state.get('selectionProof') not in ('observed-current-url', 'observed-other-app-current-key',
                                                  'explicit-session-observation', 'unknown')):
        raise BindingWarning('VRChat binding ownership information is invalid; your files were kept.')
    evidence = state.get('selectionEvidence')
    if evidence is not None and (not isinstance(evidence, dict) or evidence.get('appKey') != DASHBOARD_KEY
                                 or not isinstance(evidence.get('key'), str) or not KEY.fullmatch(evidence['key'])):
        raise BindingWarning('VRChat controller selection evidence is invalid; your files were kept.')
    if state['selectionProof'] == 'observed-other-app-current-key' and evidence is None:
        raise BindingWarning('VRChat controller selection evidence is missing; your files were kept.')
    return state


def _backup(addon, state, name, key):
    data = _read(addon / BACKUP / name, owned=True)
    if _sha(data) != state[key]:
        raise BindingWarning('A VRChat binding recovery copy changed; your files and selection were kept.')
    return data


def _edited(addon, state):
    return (state['userEdited'] or not (addon / BINDING).is_file()
            or _sha(_read(addon / BINDING, owned=True)) != state['bindingSha256'])


def _profile_edited(addon, state):
    profile = _json(_read(addon / PROFILE, owned=True))
    current = _default(profile)
    original = _json(_backup(addon, state, 'original-profile.json', 'originalProfileSha256'))
    # A previously restored default is a valid reinstall predecessor. Changes
    # to other applications and unrelated profile properties are independent.
    return (_sha(_bytes(current)) != state['profileEntrySha256'] and current != _default(original))


def _commit(writes):
    # A locked settings file or failed compare must not erase the private full
    # settings snapshot needed to diagnose/recover that refusal. Commit those
    # recovery copies first; every actual setting/profile mutation still shares
    # one expected-bytes transaction and rolls back together.
    backups = [write for write in writes if write[0].name.startswith('steamvr-settings-before-')]
    _transaction(backups)
    _transaction([write for write in writes if write not in backups])


def prepare(local, runtime, addon, previous=None, *, verified_key=None):
    """Generate an additive binding and select only an observed CurrentURL key.

    ``verified_key`` is an internal live-test override for a key directly read
    earlier in that same session before SteamVR's Default button removed it. It
    is not a public discovery rule and the installer never supplies it. The
    exact current absence is still the uninstall/rollback predecessor.
    """
    _scope(addon)
    snapshots = {relative: _read(addon / relative, owned=True) if (addon / relative).exists() else None
                 for relative in SNAPSHOTS}
    settings_path, settings, settings_bytes = _configuration(local, runtime)
    selection = _selection(settings, settings_bytes)
    manifest_path, manifest = _registered_manifest(settings_path.parent)
    if verified_key is not None and (not isinstance(verified_key, str) or not KEY.fullmatch(verified_key)
                                     or (selection['choices'] and verified_key not in selection['choices'])):
        raise BindingWarning('The previously observed VRChat selection key does not match this configuration.')
    discovery = _files.discover_controller_selection(settings, APP_KEY)
    selected_keys = tuple(discovery['targetKeys'])
    proof = discovery['proof']
    evidence = ({'appKey': discovery['sourceApp'], 'key': selected_keys[0]}
                if proof == 'observed-other-app-current-key' else None)
    if verified_key is not None and not selection['choices']:
        selected_keys, proof, evidence = (verified_key,), 'explicit-session-observation', None
    state = _state(addon)
    old_state = _state(previous) if previous and (previous / OWNER).exists() else None
    prior, prior_state = ((previous, old_state) if old_state else (addon, state))
    if state and _edited(addon, state):
        raise BindingWarning('You edited the Qpro VRChat binding; your changes were kept.')
    if state and _profile_edited(addon, state):
        raise BindingWarning('You edited the VRChat controller default; your changes were kept.')
    if old_state and _profile_edited(previous, old_state):
        raise BindingWarning('The previous VRChat profile default was edited. Its add-on and recovery copy '
                             'must be kept together; this update was not applied.')
    if not state and any((addon / path).exists() for path in (BINDING, BACKUP)):
        raise BindingWarning('VRChat recovery files exist without ownership information; your files were kept.')
    choice = next(iter(selection['choices'].values()), None)
    owned_selected = bool(choice and _owned_url(choice, addon))
    # Migrate edited generated bytes only while that exact owned file remains
    # the user's active choice. An inactive edit stays in the previous recovery
    # tree; it must not replace a newly selected custom binding or Default.
    user_edited = bool(owned_selected and old_state and _edited(previous, old_state))
    if owned_selected:
        if not prior_state:
            raise BindingWarning('The selected Qpro VRChat binding has no recovery source; your choice was kept.')
        source_bytes = _backup(prior, prior_state, 'source-binding.json', 'sourceSha256')
        source_path = Path(prior_state['sourcePath'])
        canonical = prior_state['canonicalSource']
        if prior_state['selectionProof'] != 'unknown':
            # Keep the first recorded provenance when an upgrade reads our own
            # generated selection. Do not relabel borrowed-key evidence as a
            # target choice merely because we wrote that key last time.
            proof = prior_state['selectionProof']
            evidence = prior_state.get('selectionEvidence')
    else:
        source_path, source_bytes, canonical = _source(choice, settings_path.parent, manifest_path, manifest)
    profile_bytes = snapshots[PROFILE]
    if profile_bytes is None:
        raise BindingWarning('The Qpro controller profile is missing; reinstall controller components.')
    profile = _json(profile_bytes)
    entry = _profile_entry(profile)
    if user_edited:
        merged = _read(previous / BINDING, owned=True)
        # A selected owned URL survives an upgrade at exactly the same path.
        # Preserve edited generated bytes instead of silently replacing them.
    else:
        merged = _bytes(merge_binding(_json(source_bytes), manifest, canonical=canonical))
    updated_profile = _bytes(_with_default(profile, entry))
    if prior_state:
        original_profile = _backup(prior, prior_state, 'original-profile.json', 'originalProfileSha256')
        original_selection = _backup(prior, prior_state, 'original-selection.json', 'originalSelectionSha256')
    else:
        original_profile, original_selection = profile_bytes, _bytes(selection)
    # Retrying the same prepared tree must not replace its immediate predecessor
    # with its own generated URL. A fresh tree in an upgrade gets a fresh undo
    # snapshot, while its first uninstall predecessor remains unchanged.
    install_selection = (_backup(addon, state, 'install-selection.json', 'installSelectionSha256')
                         if state else _bytes(selection))
    install_settings = (_backup(addon, state, 'steamvr-settings-before-install.json', 'installSettingsSha256')
                        if state else settings_bytes)
    automatic = bool(selected_keys)
    next_state = {'format': FORMAT, 'bindingSha256': _sha(merged), 'sourceSha256': _sha(source_bytes),
                  'sourcePath': str(source_path), 'canonicalSource': canonical, 'userEdited': user_edited,
                  'originalProfileSha256': _sha(original_profile), 'originalSelectionSha256': _sha(original_selection),
                  'installSelectionSha256': _sha(install_selection), 'installSettingsSha256': _sha(install_settings),
                  'profileEntrySha256': _sha(_bytes(entry)),
                  'automatic': automatic, 'selectionProof': proof}
    if evidence is not None:
        next_state['selectionEvidence'] = evidence
    values = [(BACKUP / 'original-profile.json', original_profile),
              (BACKUP / 'source-binding.json', source_bytes),
              (BACKUP / 'original-selection.json', original_selection),
              (BACKUP / 'install-selection.json', install_selection),
              (BACKUP / 'steamvr-settings-before-install.json', install_settings),
              (BINDING, merged), (PROFILE, updated_profile), (OWNER, _bytes(next_state))]
    # Snapshot every destination before staging so a user edit is never adopted
    # as the transaction's expected predecessor after output has been derived.
    writes = []
    for relative, data in values:
        path = addon / relative
        expected = snapshots[relative]
        if expected != data:
            writes.append((path, data, expected))
    if automatic:
        _plain(settings_path)
        updated = copy.deepcopy(settings)
        target = updated.setdefault(APP_KEY, {})
        for key in selected_keys:
            target[key] = (addon / BINDING).as_uri()
        output = _bytes(updated)
        if output != settings_bytes:
            writes.append((settings_path, output, settings_bytes))
    _commit(writes)
    return status(addon)


def status(addon):
    """Read-only diagnostic; it never rewrites a running application's choice."""
    _scope(addon)
    state = _state(addon)
    if not state:
        return {'state': 'not-prepared', 'automatic': False,
                'message': 'Reinstall controller components with SteamVR closed to prepare VRChat menu scrolling.'}
    if _edited(addon, state):
        return {'state': 'user-edited', 'automatic': False,
                'message': 'Your edited Qpro VRChat binding was preserved; Qpro will not replace its content.'}
    profile = _json(_read(addon / PROFILE, owned=True))
    if _sha(_bytes(_default(profile))) != state['profileEntrySha256']:
        return {'state': 'profile-changed', 'automatic': False,
                'message': 'The VRChat controller default was changed; your choice was kept.'}
    if not state['automatic']:
        return {'state': 'prepared-default', 'automatic': False,
                'selectionProof': state['selectionProof'],
                'message': 'VRChat menu scrolling is prepared, but a controller binding selection could not be '
                           'confirmed. Choose the Qpro Companion menu-scrolling binding once in SteamVR. '
                           'The game may prefer its own default binding until then.'}
    provenance = (' The controller selection key was already recorded by the SteamVR dashboard.'
                  if state['selectionProof'] == 'observed-other-app-current-key' else '')
    return {'state': 'prepared', 'automatic': True,
            'selectionProof': state['selectionProof'],
            **({'selectionEvidence': state['selectionEvidence']} if state.get('selectionEvidence') else {}),
            'message': 'VRChat menu scrolling was configured during installation using a backed-up additive '
                       'Qpro Companion copy.' + provenance + ' The live active binding is not verified. Reopen SteamVR, select '
                       'Swipe and Start tracking. Only menu scrolling is added; other modes and Stop leave '
                       'the extra source neutral.'}


def _selection_plan(local, runtime, recovery, installed, *, rollback=False):
    state = _state(recovery)
    if not state:
        return {'selectionRestored': False, 'selectionCount': 0}, []
    path, settings, expected = _configuration(local, runtime)
    selection = _selection(settings, expected, allow_ambiguous=True)
    owned = [key for key, value in selection['choices'].items() if _owned_url(value, installed)]
    if not owned:
        return {'selectionRestored': False, 'selectionCount': 0}, []
    name = 'install-selection.json' if rollback else 'original-selection.json'
    digest = 'installSelectionSha256' if rollback else 'originalSelectionSha256'
    original = _json(_backup(recovery, state, name, digest))
    choices = original.get('choices') if isinstance(original, dict) else None
    if (not isinstance(choices, dict) or any(not isinstance(key, str) or not KEY.fullmatch(key)
                                          or not isinstance(value, str) for key, value in choices.items())):
        raise BindingWarning('The original VRChat selection is invalid; your current choices were kept.')
    updated = copy.deepcopy(settings)
    for key in owned:
        value = choices.get(key)
        if value and (rollback or not _owned_url(value, installed)):
            updated[APP_KEY][key] = value
        else:
            updated[APP_KEY].pop(key)
    _plain(path)
    backup = recovery / BACKUP / ('steamvr-settings-before-rollback.json' if rollback
                                  else 'steamvr-settings-before-restore.json')
    backup_expected = _read(backup, owned=True) if backup.exists() else None
    return ({'selectionRestored': True, 'selectionCount': len(owned)},
            [(backup, expected, backup_expected), (path, _bytes(updated), expected)])


def rollback_install(local, runtime, addon):
    """Restore the immediate pre-attempt choice if add-on registration fails."""
    _scope(addon)
    result, writes = _selection_plan(local, runtime, addon, addon, rollback=True)
    _commit(writes)
    return result


def restore(recovery, local=None, runtime=None, installed=None):
    """Undo only the owned default and selections still pointing at our copy."""
    _scope(recovery)
    state = _state(recovery)
    if not state:
        return {'state': 'not-prepared', 'restored': True}
    selected, writes = (_selection_plan(local, runtime, recovery, installed)
                        if all(item is not None for item in (local, runtime, installed)) else ({}, []))
    profile_path = recovery / PROFILE
    profile_bytes = _read(profile_path, owned=True)
    profile = _json(profile_bytes)
    original_profile = _json(_backup(recovery, state, 'original-profile.json', 'originalProfileSha256'))
    current, original = _default(profile), _default(original_profile)
    if current == original:
        _commit(writes)
        return {'state': 'restored', 'restored': True, **selected}
    if _sha(_bytes(current)) != state['profileEntrySha256']:
        _commit(writes)
        return {'state': 'user-edited', 'restored': False, **selected,
                'message': 'Your changed VRChat profile default was kept in the uninstalled recovery copy.'}
    output = _bytes(_with_default(profile, original))
    _commit(writes + [(profile_path, output, profile_bytes)])
    # The unregistered recovery tree retains the generated file, any user edits
    # and every private predecessor. No selected owned URL remains dangling.
    return {'state': 'restored', 'restored': True, **selected}
