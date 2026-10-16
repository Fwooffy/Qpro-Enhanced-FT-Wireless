"""Prepare an additive SteamVR dashboard binding in the owned add-on folder.

The Valve binding is read from the installed runtime and is never redistributed
or modified. A profile default is prepared, and selection requires an actually
observed controller key. Installation keeps explicit custom Dashboard choices.
Uninstall only restores observed keys still pointing to our own file; no private
binding-UI API or guessed settings key is used.
"""
from __future__ import annotations

import copy
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import uuid
from urllib.parse import urlsplit
from urllib.request import url2pathname


APP_KEY = 'openvr.component.vrcompositor'
RECOGNIZED_APPS = (APP_KEY, 'steam.app.438100')
FORMAT = 'qpro-dashboard-binding-v1'
PROFILE = Path('resources/input/quest_pro_touchpad.json')
BINDING = PROFILE.with_name('qpro_dashboard_scroll.json')
OWNER = PROFILE.with_name('qpro-dashboard-owner.json')
BACKUP = PROFILE.parent / '.qpro-dashboard-backup'
LIMIT = 2 * 1024 * 1024
SETS = ('/actions/scroll_smooth', '/actions/scroll_discrete')
SOURCE = '/input/qpro_scroll'
CURRENT_KEY = re.compile(r'oculus_touch(?:_[A-Za-z0-9]+)*_CurrentURL_steamvrinput')
INSTALL_SELECTION = BACKUP / 'install-selection.json'
INSTALL_SETTINGS = BACKUP / 'steamvr-settings-before-install.json'


class BindingWarning(RuntimeError):
    """A safe refusal of optional binding setup, not a tracking failure."""


def _sha(data):
    return hashlib.sha256(data).hexdigest()


def _pairs(items):
    value = {}
    for key, item in items:
        if key in value:
            raise BindingWarning('A dashboard JSON file contains duplicate keys; no binding was changed.')
        value[key] = item
    return value


def _json(data):
    try:
        return json.loads(data.decode('utf-8-sig'), object_pairs_hook=_pairs,
                          parse_constant=lambda _: (_ for _ in ()).throw(ValueError('nonfinite JSON')))
    except (UnicodeError, ValueError) as error:
        raise BindingWarning('A dashboard JSON file could not be read; no binding was changed.') from error


def _bytes(value):
    return (json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + '\n').encode('utf-8')


def _plain(path):
    """Reject links, junctions and ambiguous relative destinations before writes."""
    if not path.is_absolute():
        raise BindingWarning('The dashboard setup path is not absolute; no binding was changed.')
    for part in (path, *path.parents):
        if part.is_symlink() or (hasattr(part, 'is_junction') and part.is_junction()):
            raise BindingWarning('The dashboard setup path contains a link; no binding was changed.')


def _read(path, *, owned=False):
    if owned:
        _plain(path)
    elif not path.is_absolute():
        raise BindingWarning('The SteamVR source path is not absolute; no binding was changed.')
    # Steam library and config folders can legitimately be junctions. They are
    # read-only inputs; only writes and owned recovery files reject links.
    try:
        if path.stat().st_size > LIMIT:
            raise BindingWarning('A dashboard JSON file exceeds the supported size; no binding was changed.')
        return path.read_bytes()
    except OSError as error:
        raise BindingWarning('A required SteamVR dashboard file is unavailable; reopen SteamVR once, '
                             'close it, then retry controller component setup.') from error


def _scope(addon):
    _plain(addon)
    marker = _json(_read(addon / 'qpro-owner.json', owned=True))
    if not isinstance(marker, dict) or marker.get('format') != 'qpro-controller-addon-v1':
        raise BindingWarning('The controller add-on has no Qpro ownership marker; no binding was changed.')


def _configuration(local, runtime):
    paths = _json(_read(local / 'openvr/openvrpaths.vrpath'))
    configs = paths.get('config') if isinstance(paths, dict) else None
    runtimes = paths.get('runtime') if isinstance(paths, dict) else None
    if (not isinstance(configs, list) or len(configs) != 1 or not isinstance(configs[0], str)
            or not isinstance(runtimes, list) or not all(isinstance(p, str) for p in runtimes)
            or runtime.resolve() not in [Path(p).resolve() for p in runtimes]):
        raise BindingWarning('SteamVR has ambiguous runtime or configuration folders; no binding was changed.')
    settings = Path(configs[0]) / 'steamvr.vrsettings'
    settings_bytes = _read(settings)
    data = _json(settings_bytes)
    if not isinstance(data, dict):
        raise BindingWarning('The SteamVR dashboard configuration is not recognized; no binding was changed.')
    return settings, data, settings_bytes


def _selection(local, runtime):
    _, data, settings_bytes = _configuration(local, runtime)
    return _selection_from_data(data, settings_bytes)


def _selection_from_data(data, settings_bytes):
    section = data.get(APP_KEY, {}) if isinstance(data, dict) else None
    if not isinstance(section, dict):
        raise BindingWarning('The SteamVR dashboard configuration is not recognized; no binding was changed.')
    # Discover only actual choices; never manufacture the controller/version part
    # of SteamVR's private storage key (it differs between runtime versions).
    selected = {key: value for key, value in section.items()
                if CURRENT_KEY.fullmatch(key)
                and isinstance(value, str) and value.strip()}
    return {'choices': selected, 'settingsSha256': _sha(settings_bytes)}


def discover_controller_selection(settings, target_app, *, controller_type='oculus_touch'):
    """Reuse one observed key, never infer SteamVR's controller/version suffix.

    Both recognized apps use the validated Oculus Touch SteamVR Input profile.
    Actual target choices have priority. Only their absence permits looking in
    the one recognized counterpart namespace. The result is separate from the
    target's actual choices so rollback can retain an originally absent key.
    """
    if target_app not in RECOGNIZED_APPS or controller_type != 'oculus_touch' or not isinstance(settings, dict):
        raise BindingWarning('Automatic controller bindings require a recognized Oculus Touch SteamVR Input app.')
    unknown = {'targetKeys': (), 'proof': 'unknown', 'sourceApp': None, 'sourceKeys': ()}

    def choices(app):
        section = settings.get(app, {})
        if not isinstance(section, dict):
            return None
        return {key: value for key, value in section.items() if CURRENT_KEY.fullmatch(key)}

    target = choices(target_app)
    if target is None or any(not isinstance(value, str) or not value.strip() for value in target.values()):
        return {**unknown, 'reason': 'The existing target controller selection needs review.'}
    if target:
        if len(set(target.values())) > 1:
            return {**unknown, 'reason': 'The target app has different current controller selections.'}
        keys = tuple(target)
        return {'targetKeys': keys, 'proof': 'observed-current-url', 'sourceApp': target_app,
                'sourceKeys': keys, 'reason': 'An actual target controller selection was observed.'}
    source_app = next(app for app in RECOGNIZED_APPS if app != target_app)
    source = choices(source_app)
    if source is None or len(source) != 1 or any(not isinstance(value, str) or not value.strip()
                                               for value in source.values()):
        return {**unknown, 'reason': 'No unique current controller key was observed in the recognized counterpart app.'}
    key = next(iter(source))
    return {'targetKeys': (key,), 'proof': 'observed-other-app-current-key', 'sourceApp': source_app,
            'sourceKeys': (key,), 'reason': 'One verbatim current controller key was observed in the recognized counterpart app.'}


def _source(runtime, selection):
    canonical = runtime / 'drivers/oculus/resources/input/vrcompositor_bindings_touch.json'
    choices = set(selection['choices'].values())
    if len(choices) == 1:
        uri = urlsplit(next(iter(choices)))
        # Read a selected local JSON binding when available. Workshop/remote or
        # ambiguous choices stay selected and receive a clear manual-choice hint.
        if uri.scheme.lower() == 'file' and uri.netloc in ('', 'localhost'):
            candidate = Path(url2pathname(uri.path))
            if candidate.is_absolute():
                return candidate, _read(candidate)
    return canonical, _read(canonical)


def _selects_owned(selection, addon):
    values = set(selection['choices'].values())
    if not values:
        return False
    for value in values:
        if not _owned_url(value, addon):
            return False
    return True


def _owned_url(value, addon):
    if not isinstance(value, str):
        return False
    uri = urlsplit(value)
    if uri.scheme.lower() != 'file' or uri.netloc not in ('', 'localhost') or uri.query or uri.fragment:
        return False
    path = Path(url2pathname(uri.path))
    return path.is_absolute() and path.resolve() == (addon / BINDING).resolve()


def merge_binding(binding, manifest):
    """Append four dedicated vector sources without rewriting ordinary controls."""
    if (not isinstance(binding, dict) or binding.get('app_key') != APP_KEY
            or binding.get('controller_type') != 'oculus_touch'
            or binding.get('category') != 'steamvr_input'
            or type(binding.get('action_manifest_version')) is not int
            or binding['action_manifest_version'] != 0):
        raise BindingWarning('This SteamVR dashboard binding format is not supported; your binding was kept.')
    actions = manifest.get('actions') if isinstance(manifest, dict) else None
    if not isinstance(actions, list):
        raise BindingWarning('SteamVR dashboard actions are not recognized; your binding was kept.')
    for action_set in SETS:
        matches = [action for action in actions if isinstance(action, dict)
                   and str(action.get('name', '')).lower() == action_set + '/in/scroll']
        if len(matches) != 1 or matches[0].get('type') != 'vector2':
            raise BindingWarning('SteamVR dashboard scroll actions changed; your binding was kept.')
    result = copy.deepcopy(binding)
    groups = result.get('bindings')
    if not isinstance(groups, dict):
        raise BindingWarning('SteamVR dashboard binding groups are not recognized; your binding was kept.')
    for group in groups.values():
        if not isinstance(group, dict) or not isinstance(group.get('sources', []), list):
            raise BindingWarning('SteamVR dashboard sources are not recognized; your binding was kept.')
        for source in group.get('sources', []):
            if not isinstance(source, dict) or not isinstance(source.get('path'), str):
                raise BindingWarning('SteamVR dashboard sources are not recognized; your binding was kept.')
            if source['path'].endswith(SOURCE):
                raise BindingWarning('This dashboard binding already uses the Qpro scroll source; it was kept.')
    for action_set in SETS:
        group = groups.get(action_set)
        if not isinstance(group, dict) or not isinstance(group.get('sources'), list):
            raise BindingWarning('SteamVR dashboard scroll groups changed; your binding was kept.')
        # Route the dedicated Swipe vector directly to these vector actions.
        # This keeps the current accepted routing; the worker publishes neutral
        # values outside Swipe. Valve's alternate Scroll transform has separate
        # joystick semantics and is not assumed to be another differentiation.
        for side in ('left', 'right'):
            group['sources'].append({
                'path': '/user/hand/' + side + SOURCE,
                'mode': 'joystick',
                'parameters': {'deadzone_pct': '0', 'maxzone_pct': '100'},
                'inputs': {'position': {'output': action_set + '/in/scroll'}}})
    result['name'] = str(result.get('name', 'VR Dashboard')) + ' + Qpro Companion thumb-rest scrolling'
    return result


def _profile_with_default(profile):
    if (not isinstance(profile, dict) or profile.get('controller_type') != 'oculus_touch'
            or profile.get('resource_root') != 'qpro_controller'):
        raise BindingWarning('The installed controller profile is not recognized; no binding was changed.')
    sources = profile.get('input_source')
    source = sources.get(SOURCE) if isinstance(sources, dict) else None
    if not isinstance(source, dict) or source.get('type') != 'joystick':
        raise BindingWarning('Reinstall the current controller add-on to enable dashboard scrolling.')
    result = copy.deepcopy(profile)
    defaults = result.setdefault('default_bindings', [])
    if not isinstance(defaults, list) or any(not isinstance(item, dict) for item in defaults):
        raise BindingWarning('The controller profile defaults are not recognized; no binding was changed.')
    matches = [index for index, item in enumerate(defaults) if item.get('app_key') == APP_KEY]
    if len(matches) > 1:
        raise BindingWarning('The controller profile has multiple dashboard defaults; no binding was changed.')
    entry = {'app_key': APP_KEY, 'binding_url': BINDING.name}
    if matches:
        defaults[matches[0]] = entry
    else:
        defaults.append(entry)
    return result


def _profile_defaults(profile):
    defaults = profile.get('default_bindings', []) if isinstance(profile, dict) else None
    if not isinstance(defaults, list) or any(not isinstance(item, dict) for item in defaults):
        raise BindingWarning('The controller profile defaults are not recognized; your files were kept.')
    matches = [item for item in defaults if item.get('app_key') == APP_KEY]
    if len(matches) > 1:
        raise BindingWarning('The controller profile has multiple dashboard defaults; your files were kept.')
    return matches


def _owned_profile_hash(profile):
    sources = profile.get('input_source') if isinstance(profile, dict) else None
    return _sha(_bytes({'dashboard': _profile_defaults(profile),
                        'source': sources.get(SOURCE) if isinstance(sources, dict) else None,
                        'controller_type': profile.get('controller_type'),
                        'resource_root': profile.get('resource_root')}))


def _restore_profile_default(current, original):
    before = _profile_defaults(original)
    result = copy.deepcopy(current)
    defaults = result.get('default_bindings', [])
    _profile_defaults(result)
    indices = [index for index, item in enumerate(defaults) if item.get('app_key') == APP_KEY]
    insertion = indices[0] if indices else len(defaults)
    defaults = [item for item in defaults if item.get('app_key') != APP_KEY]
    defaults[insertion:insertion] = copy.deepcopy(before)
    if defaults or 'default_bindings' in original:
        result['default_bindings'] = defaults
    else:
        result.pop('default_bindings', None)
    return result


def _state(addon):
    path = addon / OWNER
    if not path.exists():
        return None
    state = _json(_read(path, owned=True))
    keys = ('profileSha256', 'ownedProfileSha256', 'bindingSha256', 'originalProfileSha256',
            'sourceSha256', 'selectionSha256')
    if (not isinstance(state, dict) or state.get('format') != FORMAT
            or any(not re.fullmatch('[a-f0-9]{64}', str(state.get(key, ''))) for key in keys)
            or type(state.get('customChoice')) is not bool
            or type(state.get('userEdited', False)) is not bool
            or any(key in state and not re.fullmatch('[a-f0-9]{64}', str(state[key]))
                   for key in ('installSelectionSha256', 'installSettingsSha256'))
            or state.get('selectionProof', 'profile-default-only') not in (
                'profile-default-only', 'explicit-session-observation', 'observed-owned-selection',
                'custom-choice-preserved', 'observed-current-url', 'observed-other-app-current-key')):
        raise BindingWarning('Dashboard setup ownership information is invalid; your files were kept.')
    evidence = state.get('selectionEvidence')
    if evidence is not None and (not isinstance(evidence, dict)
            or set(evidence) != {'appKey', 'key'} or evidence.get('appKey') not in RECOGNIZED_APPS
            or not isinstance(evidence.get('key'), str) or not CURRENT_KEY.fullmatch(evidence['key'])):
        raise BindingWarning('Dashboard selection evidence is invalid; your files were kept.')
    if state.get('selectionProof') == 'observed-other-app-current-key' and (
            evidence is None or evidence['appKey'] == APP_KEY):
        raise BindingWarning('Dashboard counterpart selection evidence is invalid; your files were kept.')
    return state


def _transaction(writes):
    """Replace only expected file versions; roll back our writes after a failure."""
    staged = []
    committed = []
    created_dirs = []
    try:
        for path, data, expected in writes:
            _plain(path)
            missing = []
            parent = path.parent
            while not parent.exists():
                missing.append(parent)
                parent = parent.parent
            for parent in reversed(missing):
                parent.mkdir()
                created_dirs.append(parent)
            with tempfile.NamedTemporaryFile(prefix='.' + path.name + '-', suffix='.tmp',
                                             dir=path.parent, delete=False) as output:
                temporary = Path(output.name)
                output.write(data)
                output.flush()
                os.fsync(output.fileno())
            staged.append((path, temporary, data, expected))
        for path, temporary, data, expected in staged:
            _plain(path)
            current = path.read_bytes() if path.exists() else None
            if current != expected:
                raise BindingWarning('Dashboard files changed during setup; your changes were kept. Retry setup.')
            temporary.replace(path)
            committed.append((path, data, expected))
    except Exception as original:
        conflicts = []
        for path, data, expected in reversed(committed):
            if not path.exists() or path.read_bytes() != data:
                conflicts.append(path.name)
                continue
            if expected is None:
                path.unlink()
            else:
                with tempfile.NamedTemporaryFile(prefix='.' + path.name + '-', suffix='.tmp',
                                                 dir=path.parent, delete=False) as output:
                    rollback = Path(output.name)
                    output.write(expected)
                rollback.replace(path)
        if conflicts:
            raise BindingWarning('Dashboard setup could not restore files changed by another program: '
                                 + ', '.join(conflicts) + '. Recovery copies were kept.') from original
        raise
    finally:
        for _, temporary, _, _ in staged:
            temporary.unlink(missing_ok=True)
        for parent in reversed(created_dirs):
            if parent.exists() and not any(parent.iterdir()):
                parent.rmdir()


def _unchanged(addon, state):
    return (addon.joinpath(PROFILE).is_file() and addon.joinpath(BINDING).is_file()
            and _owned_profile_hash(_json(_read(addon / PROFILE, owned=True))) == state['ownedProfileSha256']
            and _sha(_read(addon / BINDING, owned=True)) == state['bindingSha256'])


def prepare(local, runtime, addon, previous=None, *, verified_key=None):
    """Called with SteamVR closed, after copying a new owned add-on tree.

    ``verified_key`` is an internal experiment for an exact controller key read
    directly during this session. It is not exposed as a command-line or Hub
    option and is never inferred from AutosaveURL or a controller version guess.
    Public calls can reuse one unique verbatim CurrentURL key from the recognized
    counterpart app. Without that evidence they only prepare a manual choice.
    """
    _scope(addon)
    relative_paths = (BACKUP / 'original-profile.json', BACKUP / 'source-binding.json',
                      BACKUP / 'selection.json', INSTALL_SELECTION, INSTALL_SETTINGS,
                      BINDING, PROFILE, OWNER)
    # Capture expected bytes before deriving any output, so a user edit during
    # parsing/staging cannot become the version that our transaction overwrites.
    snapshots = {relative: _read(addon / relative, owned=True) if (addon / relative).exists() else None
                 for relative in relative_paths}
    state = _state(addon)
    if state and (state.get('userEdited') or not _unchanged(addon, state)):
        raise BindingWarning('You edited the Qpro dashboard binding or profile; your changes were kept.')
    settings_path, settings, settings_bytes = _configuration(local, runtime)
    selection = _selection_from_data(settings, settings_bytes)
    if verified_key is not None and (not isinstance(verified_key, str) or not CURRENT_KEY.fullmatch(verified_key)):
        raise BindingWarning('The observed Dashboard controller selection key is not recognized; your choices were kept.')
    discovery = discover_controller_selection(settings, APP_KEY)
    candidate_keys = ((verified_key,) if verified_key is not None else discovery['targetKeys'])
    # Keep every actual target custom choice. The public fallback only creates
    # a missing choice; an existing owned URL needs no setting rewrite.
    select_observed_key = bool(candidate_keys) and not selection['choices']
    if select_observed_key:
        # The target app and owned URL are fixed. Every candidate is an exact
        # observed key; no version/prefix is constructed.
        _plain(settings_path)
    old_state = None
    if previous and (previous / OWNER).exists():
        old_state = _state(previous)
    prior = previous if old_state else addon if state else None
    prior_state = old_state or state
    # Only an actively selected owned file carries edits into the replacement.
    # An older edited file remains in recovery after choosing Default or another
    # custom binding, including when a counterpart key enables fresh selection.
    user_edited = bool(old_state and _selects_owned(selection, addon)
                       and (old_state.get('userEdited') or not _unchanged(previous, old_state)))
    if _selects_owned(selection, addon) and prior:
        # During upgrade the old add-on has already been renamed to recovery.
        # A selected file URL still points to its original, temporarily missing
        # location. Rebuild from the saved *unmerged* source, never from our own
        # generated binding (which would duplicate the dedicated sources).
        source_path = prior / BACKUP / 'source-binding.json'
        source_bytes = _read(source_path, owned=True)
        if _sha(source_bytes) != prior_state['sourceSha256']:
            raise BindingWarning('The selected dashboard binding recovery source changed; its recovery copy was kept.')
    elif user_edited:
        source_path = previous / BACKUP / 'source-binding.json'
        source_bytes = _read(source_path, owned=True)
    else:
        source_path, source_bytes = _source(runtime, selection)
    profile_path = addon / PROFILE
    profile_bytes = _read(profile_path, owned=True)
    if user_edited:
        # User changes keep the same installed file URL and bytes across an
        # upgrade. Mark them as user-owned so future repair/removal cannot
        # reinterpret this copy as generated content and overwrite it.
        merged = _read(previous / BINDING, owned=True)
        old_profile = _read(previous / PROFILE, owned=True)
        profile = (old_profile if _sha(old_profile) != old_state['profileSha256']
                   else _bytes(_profile_with_default(_json(profile_bytes))))
    else:
        manifest = _json(_read(runtime / 'resources/config/vrcompositor_actions.json'))
        merged = _bytes(merge_binding(_json(source_bytes), manifest))
        profile = _bytes(_profile_with_default(_json(profile_bytes)))
    if state:
        original = _read(addon / BACKUP / 'original-profile.json', owned=True)
        if _sha(original) != state['originalProfileSha256']:
            raise BindingWarning('The dashboard profile recovery copy changed; your files were kept.')
    else:
        if (addon / BINDING).exists() or (addon / BACKUP).exists():
            raise BindingWarning('Dashboard recovery files exist without ownership information; they were kept.')
        original = profile_bytes
    next_state = {'format': FORMAT, 'profileSha256': _sha(profile), 'bindingSha256': _sha(merged),
                  'ownedProfileSha256': _owned_profile_hash(_json(profile)),
                  'originalProfileSha256': _sha(original), 'sourceSha256': _sha(source_bytes),
                  'customChoice': bool(selection['choices']) and not _selects_owned(selection, addon),
                  'sourcePath': (prior_state.get('sourcePath', str(source_path))
                                 if prior_state and _selects_owned(selection, addon) else str(source_path)),
                  'userEdited': user_edited}
    if select_observed_key:
        next_state['selectionProof'] = ('explicit-session-observation' if verified_key is not None
                                        else discovery['proof'])
        if verified_key is None:
            next_state['selectionEvidence'] = {'appKey': discovery['sourceApp'], 'key': candidate_keys[0]}
    elif _selects_owned(selection, addon):
        proof = prior_state.get('selectionProof') if prior_state else None
        next_state['selectionProof'] = (proof if proof in ('explicit-session-observation', 'observed-owned-selection',
            'observed-current-url', 'observed-other-app-current-key') else 'observed-owned-selection')
        if prior_state and 'selectionEvidence' in prior_state:
            next_state['selectionEvidence'] = copy.deepcopy(prior_state['selectionEvidence'])
    else:
        next_state['selectionProof'] = ('custom-choice-preserved' if selection['choices']
                                        else 'profile-default-only')
    if prior:
        original_selection = _read(prior / BACKUP / 'selection.json', owned=True)
        if _sha(original_selection) != prior_state['selectionSha256']:
            raise BindingWarning('The original dashboard selection recovery copy changed; your files were kept.')
    else:
        original_selection = _bytes(selection)
    next_state['selectionSha256'] = _sha(original_selection)
    install_snapshots = []
    for relative, digest, current in ((INSTALL_SELECTION, 'installSelectionSha256', _bytes(selection)),
                                      (INSTALL_SETTINGS, 'installSettingsSha256', settings_bytes)):
        existing = snapshots[relative]
        if state and digest in state:
            if existing is None or _sha(existing) != state[digest]:
                raise BindingWarning('The immediate Dashboard selection recovery copy changed; your files were kept.')
            data = existing
        elif existing is not None:
            raise BindingWarning('Dashboard install recovery files have no ownership information; they were kept.')
        else:
            data = current
        next_state[digest] = _sha(data)
        install_snapshots.append((relative, data))
    writes = []
    for relative, data in ((BACKUP / 'original-profile.json', original),
                           (BACKUP / 'source-binding.json', source_bytes),
                           (BACKUP / 'selection.json', original_selection),
                           *install_snapshots,
                           (BINDING, merged), (PROFILE, profile), (OWNER, _bytes(next_state))):
        path = addon / relative
        expected = snapshots[relative]
        if expected != data:
            writes.append((path, data, expected))
    if select_observed_key:
        updated = copy.deepcopy(settings)
        for key in candidate_keys:
            updated.setdefault(APP_KEY, {})[key] = (addon / BINDING).as_uri()
        writes.append((settings_path, _bytes(updated), settings_bytes))
    _transaction(writes)
    return status(addon)


def status(addon):
    """Read-only startup diagnostic; never switches a live SteamVR binding."""
    _scope(addon)
    state = _state(addon)
    if not state:
        return {'state': 'not-prepared', 'automatic': False,
                'message': 'Reinstall controller components with SteamVR closed to prepare dashboard scrolling.'}
    if state.get('userEdited'):
        return {'state': 'user-edited', 'automatic': False,
                'message': 'Your edited dashboard binding was preserved at its original installed location. '
                           'Qpro will not replace your choices.'}
    if not _unchanged(addon, state):
        return {'state': 'user-edited', 'automatic': False,
                'message': 'Your dashboard binding or profile was edited; Qpro kept your changes.'}
    if state['customChoice']:
        return {'state': 'custom-binding-kept', 'automatic': False,
                'message': 'Your custom SteamVR dashboard binding was kept. Select the Qpro Companion '
                           'dashboard binding in SteamVR to use thumb-rest scrolling.'}
    if state.get('selectionProof') in ('explicit-session-observation', 'observed-owned-selection',
                                      'observed-current-url', 'observed-other-app-current-key'):
        return {'state': 'prepared-selection', 'automatic': True,
                'selectionProof': state['selectionProof'],
                **({'selectionEvidence': state['selectionEvidence']} if 'selectionEvidence' in state else {}),
                'message': 'The observed Dashboard selection uses the additive Qpro Companion binding. '
                           'Reopen SteamVR, then select Swipe and Start tracking. Verify scrolling in the '
                           'dashboard; prepared files do not confirm live activation.'}
    return {'state': 'prepared-default', 'automatic': False,
            'message': 'The Dashboard binding is prepared, but no unique current controller key was observed '
                       'in the recognized apps. Choose the Qpro Companion dashboard binding in SteamVR. '
                       'A prepared profile default alone does not confirm that SteamVR selected it.'}


def restore_selection(local, runtime, recovery, installed, *, rollback=False):
    """Remove only actual selections pointing to the owned generated file URL.

    The caller must have closed SteamVR. No guessed key is ever added, and an
    unrelated dashboard choice is never replaced. A linked configuration or a
    concurrent write is refused with its full pre-write recovery copy retained.
    """
    _scope(recovery)
    state = _state(recovery)
    if not state:
        return {'selectionRestored': False, 'selectionCount': 0}
    path, data, expected = _configuration(local, runtime)
    _plain(path)
    section = data.get(APP_KEY, {})
    if not isinstance(section, dict):
        raise BindingWarning('The dashboard selection is not recognized. Choose Default in SteamVR bindings.')
    choices = {key: value for key, value in section.items()
               if CURRENT_KEY.fullmatch(key)
               and isinstance(value, str) and value.strip()}
    owned = [key for key, value in choices.items() if _owned_url(value, installed)]
    if not owned:
        return {'selectionRestored': False, 'selectionCount': 0}
    if len(set(choices.values())) > 1:
        raise BindingWarning('SteamVR has ambiguous dashboard selections. Your choices were kept; '
                             'choose Default for Qpro Companion in SteamVR bindings.')
    original_path = INSTALL_SELECTION if rollback else BACKUP / 'selection.json'
    original_digest = state.get('installSelectionSha256') if rollback else state['selectionSha256']
    if original_digest is None:
        # Old public prepare calls never selected a dashboard key, so there is
        # no automatic selection to undo for these owner records.
        return {'selectionRestored': False, 'selectionCount': 0}
    original_bytes = _read(recovery / original_path, owned=True)
    if _sha(original_bytes) != original_digest:
        raise BindingWarning('The original dashboard selection recovery copy changed. Choose Default in SteamVR bindings.')
    original = _json(original_bytes)
    previous = original.get('choices') if isinstance(original, dict) else None
    if (not isinstance(previous, dict) or any(not isinstance(key, str) or not isinstance(value, str)
                                           for key, value in previous.items())):
        raise BindingWarning('The original dashboard selection is not recognized. Choose Default in SteamVR bindings.')
    updated = copy.deepcopy(data)
    target = updated[APP_KEY]
    for key in owned:
        value = previous.get(key)
        if value and (rollback or not _owned_url(value, installed)):
            target[key] = value
        else:
            target.pop(key)
    backup = recovery / BACKUP / ('steamvr-settings-before-restore-' + uuid.uuid4().hex + '.json')
    # Do not restore the whole settings file: every unrelated choice from the
    # current document remains in place. The exact bytes are only a backup.
    _transaction([(backup, expected, None)])
    _transaction([(path, _bytes(updated), expected)])
    return {'selectionRestored': True, 'selectionCount': len(owned)}


def rollback_install(local, runtime, addon):
    """Restore the immediate pre-test choice before discarding a failed tree."""
    return restore_selection(local, runtime, addon, addon, rollback=True)


def restore(addon, local=None, runtime=None, installed=None):
    """Compare-restore owned files and observed owned selections on uninstall."""
    _scope(addon)
    state = _state(addon)
    if not state:
        return {'state': 'not-prepared', 'restored': True}
    selection = (restore_selection(local, runtime, addon, installed)
                 if all(value is not None for value in (local, runtime, installed)) else {})
    if state.get('userEdited'):
        return {'state': 'user-edited', 'restored': False, **selection,
                'message': 'Your edited dashboard files were kept in the uninstalled recovery copy.'}
    original = _read(addon / BACKUP / 'original-profile.json', owned=True)
    if _sha(original) != state['originalProfileSha256']:
        raise BindingWarning('The dashboard profile recovery copy changed; no file was restored.')
    current = _read(addon / PROFILE, owned=True)
    current_profile = _json(current)
    original_profile = _json(original)
    if (_owned_profile_hash(current_profile) == _owned_profile_hash(original_profile)
            and _sha(_read(addon / BINDING, owned=True)) == state['bindingSha256']):
        return {'state': 'restored', 'restored': True, **selection}
    if not _unchanged(addon, state):
        return {'state': 'user-edited', 'restored': False, **selection,
                'message': 'Dashboard files were edited; their recovery copy was kept without overwriting them.'}
    if _owned_profile_hash(current_profile) != state['ownedProfileSha256']:
        raise BindingWarning('The dashboard profile changed during removal; your changes were kept.')
    restored_profile = _restore_profile_default(current_profile, original_profile)
    restored_bytes = original if restored_profile == original_profile else _bytes(restored_profile)
    _transaction([(addon / PROFILE, restored_bytes, current)])
    # Keep generated content and all recovery metadata in the uninstalled copy;
    # it is no longer referenced by the restored profile or registered driver.
    return {'state': 'restored', 'restored': True, **selection}
