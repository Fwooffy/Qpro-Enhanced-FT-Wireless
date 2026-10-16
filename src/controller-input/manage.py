"""Optional component setup and supervision; all installation paths are Qpro-owned.

Importing this module performs no installation, ADB call, or process attachment.
"""
from __future__ import annotations

import argparse
from collections import deque
import hashlib
import importlib.util
import json
import lzma
import math
import os
from pathlib import Path
import re
import shlex
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
import uuid

FRIDA_VERSION = '17.18.0'
WHEEL_NAME = 'frida-17.18.0-cp37-abi3-win_amd64.whl'
WHEEL_URL = 'https://files.pythonhosted.org/packages/71/8a/0d271ce04b7eaf73eca9793597c489ff40eadf3426aff70f0ce452221a2e/' + WHEEL_NAME
WHEEL_HASH = '4bcf171a0ae184e30e95f414ce3a8f5e92e75e5c8c04ce36cea5eafe632070b1'
SERVER_URL = 'https://github.com/frida/frida/releases/download/17.18.0/frida-server-17.18.0-android-arm64.xz'
SERVER_HASH = '77c2a4aadf6010c69766abea5b6a408c66f5e2da43b3f2c920cf75a5fa1ec7f5'
PROFILE = 'legacy-51503870024400340'
READER_PATH = '/data/local/tmp/qpro-controller-input'
REMOTE_STOP = READER_PATH + '.stop'
OWNER_FORMAT = 'qpro-controller-addon-v1'
PACKET = struct.Struct('<4sHHQQIffffIffff')
NO_WINDOW = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
OUTPUT_LOCK = threading.Lock()
INPUT_SIDES = ('both', 'left', 'right')
_DASHBOARD_HELPER = None
_VRCHAT_HELPER = None


class WorkerFailure(RuntimeError):
    def __init__(self, message, returncode):
        super().__init__(message)
        self.returncode = returncode


def report(stage: str, **values):
    with OUTPUT_LOCK:
        print(stage + ' ' + json.dumps(values, sort_keys=True), flush=True)


def command(args, timeout=20, capture=True):
    result = subprocess.run([str(x) for x in args], timeout=timeout,
                            text=True, capture_output=capture, creationflags=NO_WINDOW)
    if result.returncode:
        raise RuntimeError(f'{Path(str(args[0])).name} failed ({result.returncode}): '
                           + ((result.stdout or '') + (result.stderr or '')).strip())
    return (result.stdout or '').strip()


def forward_output(output):
    if output:
        with OUTPUT_LOCK:
            print(output, end='' if output.endswith('\n') else '\n', flush=True)


def diagnostic_text(value, limit):
    if not isinstance(value, str):
        return None
    value = value.splitlines()[0] if value else ''
    value = re.sub(r'(?:[A-Za-z]:[\\/]|\\\\|/).*', '[path omitted]', value)
    value = re.sub(r'[\x00-\x1f\x7f]', '', value).strip()
    return value[:limit] or None


def bounded_reason(value, limit=2048):
    if not isinstance(value, str):
        return None
    return value.strip()[:limit] or None


def detachment_reason(result):
    if not isinstance(result, dict):
        return None
    reason = diagnostic_text(result.get('reason'), 80)
    if reason is None:
        return None
    parts = []
    session = diagnostic_text(result.get('session'), 80)
    if session:
        parts.append('session=' + session)
    process = result.get('process')
    if type(process) is int and process > 0:
        parts.append('process=' + (str(process) if process < 10 ** 80 else '[oversized]'))
    else:
        process = diagnostic_text(process, 80)
        if process:
            parts.append('process=' + process)
    parts.append('reason=' + reason)
    summary = diagnostic_text(result.get('crashSummary'), 240)
    if summary:
        parts.append('crash=' + summary)
    if type(result.get('duringCleanup')) is bool:
        parts.append('duringCleanup=' + str(result['duringCleanup']).lower())
    return 'Detached ' + ', '.join(parts)


def cleanup_phase_reason(result):
    details = []
    phase = diagnostic_text(result.get('phase'), 120)
    if phase:
        details.append('phase=' + phase)
    for key, label in (('progressStaleSeconds', 'progress stale seconds'),
                       ('lastStatusAgeSeconds', 'last status age seconds')):
        value = result.get(key)
        if type(value) in (int, float) and 0 <= value <= 1e12 and math.isfinite(value):
            details.append(label + '=' + str(value))
    return '; '.join(details) or None


def failure_reason(line):
    stage, _, payload = line.partition(' ')
    if stage not in ('HANDS_CHECK', 'HANDS_FAILED', 'HANDS_INCOMPATIBLE',
                     'HANDS_STOP_CAUSE', 'HANDS_DETACHED', 'HANDS_CLEANUP_FAILED',
                     'CONTROLLER_READER', 'CONTROLLER_ERROR'):
        return None
    try:
        result = json.loads(payload)
    except ValueError:
        return None
    if not isinstance(result, dict):
        return None
    if stage == 'HANDS_DETACHED':
        return detachment_reason(result)
    if stage == 'HANDS_CHECK':
        problems = result.get('problems')
        if result.get('compatible') is False and isinstance(problems, list):
            reasons = [problem for problem in problems if isinstance(problem, str) and problem.strip()]
            return '; '.join(reasons) or None
        return None
    reason = result.get('message' if stage in ('CONTROLLER_ERROR', 'CONTROLLER_READER') else 'error')
    reason = reason if isinstance(reason, str) and reason.strip() else None
    details = []
    if stage in ('HANDS_STOP_CAUSE', 'HANDS_CLEANUP_FAILED'):
        detached = result.get('detached')
        if isinstance(detached, list):
            for entry in detached[:4]:
                detail = detachment_reason(entry)
                if detail and detail not in details:
                    details.append(detail)
    if stage == 'HANDS_CLEANUP_FAILED':
        phase = cleanup_phase_reason(result)
        if phase:
            details.append(phase)
    return '; '.join(([reason] if reason else []) + details) or None


def managed_paths(local: Path):
    # No path is obtained from a downloaded manifest or a controller packet.
    if not local.is_absolute():
        raise RuntimeError('LOCALAPPDATA must name an absolute local Windows folder.')
    return local / 'QproFaceTracking' / 'hands', local / 'QproFaceTracking' / 'controller-addon'


def atomic_json(path: Path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.tmp')
    temporary.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
    temporary.replace(path)


def dashboard_helper():
    # Also supports importing this supervisor by path in the offline fixtures.
    global _DASHBOARD_HELPER
    if _DASHBOARD_HELPER is None:
        path = Path(__file__).with_name('dashboard_bindings.py')
        spec = importlib.util.spec_from_file_location('qpro_dashboard_bindings', path)
        helper = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(helper)
        _DASHBOARD_HELPER = helper
    return _DASHBOARD_HELPER


def vrchat_helper():
    global _VRCHAT_HELPER
    if _VRCHAT_HELPER is None:
        path = Path(__file__).with_name('vrchat_bindings.py')
        spec = importlib.util.spec_from_file_location('qpro_vrchat_bindings', path)
        helper = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(helper)
        _VRCHAT_HELPER = helper
    return _VRCHAT_HELPER


def binding_manual_warning(result, application):
    if not result.get('automatic', False):
        report('CONTROLLER_BINDINGS_WARNING', message=result.get('message', 'Binding setup needs attention.'),
               next='After reopening SteamVR, choose the Qpro Companion menu-scrolling binding for '
                    + application + ' if you want thumb-rest scrolling. Your existing choices were kept.')


def prepare_dashboard(local, tool, addon, previous=None):
    try:
        result = dashboard_helper().prepare(local, tool.parents[2], addon, previous)
        report('CONTROLLER_DASHBOARD_BINDING', **result, activationVerified=False)
        binding_manual_warning(result, 'the SteamVR dashboard')
    except (OSError, ValueError, RuntimeError) as error:
        if previous is not None and (previous / 'resources/input/qpro_dashboard_scroll.json').exists():
            # A selected file URL can point into the existing add-on. Restore
            # the whole previous tree through install's transaction if we
            # cannot migrate it, rather than leave that URL missing or replace
            # a user-edited binding with an unrelated default.
            raise RuntimeError('The controller update could not preserve its dashboard binding: '
                               + str(error) + ' The previous add-on files will be restored.') from error
        # A binding-format change must not turn successful component setup or
        # ordinary controller tracking into a failure.
        report('CONTROLLER_BINDINGS_WARNING', message=str(error),
               next='Reopen SteamVR after setup. Your existing dashboard and game bindings were kept.')


def prepare_vrchat(local, tool, addon, previous=None):
    try:
        result = vrchat_helper().prepare(local, tool.parents[2], addon, previous)
        report('CONTROLLER_VRCHAT_BINDING', **result, activationVerified=False)
        binding_manual_warning(result, 'VRChat')
    except (OSError, ValueError, RuntimeError) as error:
        owned = Path('resources/input/qpro-vrchat-owner.json')
        if (addon / owned).exists() or (previous is not None and (previous / owned).exists()):
            # Metadata can precede the final selection update. Let the install
            # transaction restore its immediate predecessor before removing it.
            raise RuntimeError('VRChat binding setup could not finish safely: ' + str(error)) from error
        report('CONTROLLER_BINDINGS_WARNING', message=str(error),
               next='Your VRChat binding was kept. Choose its Qpro Companion menu-scrolling binding '
                    'manually after reopening SteamVR if automatic setup is unavailable.')


def report_dashboard(addon, enabled, mode, side):
    try:
        result = dashboard_helper().status(addon)
        report('CONTROLLER_DASHBOARD_BINDING', **result, activationVerified=False,
               scrollSource='enabled' if enabled and mode == 'swipe' else 'neutral', side=side)
        if enabled and mode == 'swipe':
            binding_manual_warning(result, 'the SteamVR dashboard')
    except (OSError, ValueError, RuntimeError) as error:
        report('CONTROLLER_BINDINGS_WARNING', message=str(error))


def report_vrchat(addon, enabled, mode, side):
    try:
        result = vrchat_helper().status(addon)
        report('CONTROLLER_VRCHAT_BINDING', **result, activationVerified=False,
               scrollSource='enabled' if enabled and mode == 'swipe' else 'neutral', side=side)
        if enabled and mode == 'swipe':
            binding_manual_warning(result, 'VRChat')
    except (OSError, ValueError, RuntimeError) as error:
        report('CONTROLLER_BINDINGS_WARNING', message=str(error))


def rollback_binding_selections(local, tool, addon):
    failures = []
    for relative, getter, stage in (
            ('resources/input/qpro-vrchat-owner.json', vrchat_helper, 'CONTROLLER_VRCHAT_BINDING'),
            ('resources/input/qpro-dashboard-owner.json', dashboard_helper, 'CONTROLLER_DASHBOARD_BINDING')):
        if not (addon / relative).exists():
            continue
        try:
            assert_steamvr_closed()
            result = getter().rollback_install(local, tool.parents[2], addon)
            report(stage, phase='install-rollback', **result)
        except (OSError, ValueError, RuntimeError) as error:
            failures.append(str(error))
    if failures:
        raise RuntimeError('; '.join(failures))


def owner_is_valid(addon: Path):
    try:
        value = json.loads((addon / 'qpro-owner.json').read_text(encoding='utf-8'))
        return isinstance(value, dict) and value.get('format') == OWNER_FORMAT
    except (OSError, ValueError):
        return False


def assert_steamvr_closed():
    if os.name != 'nt':
        raise RuntimeError('The controller add-on currently targets Windows x64.')
    tasks = command(['tasklist.exe', '/FI', 'IMAGENAME eq vrserver.exe', '/FO', 'CSV', '/NH'])
    if '"vrserver.exe"' in tasks.lower():
        raise RuntimeError('Close SteamVR before installing or uninstalling the controller add-on.')


def steamvr_tool(local: Path):
    paths = local / 'openvr' / 'openvrpaths.vrpath'
    try:
        data = json.loads(paths.read_text(encoding='utf-8-sig'))
    except (OSError, ValueError) as error:
        raise RuntimeError('SteamVR runtime information could not be read. Launch SteamVR once, '
                           'then close it and retry controller setup.') from error
    runtimes = data.get('runtime') if isinstance(data, dict) else None
    if (not isinstance(runtimes, list) or not runtimes or
            any(not isinstance(p, str) or not p or not Path(p).is_absolute() for p in runtimes)):
        raise RuntimeError('SteamVR runtime information has no valid absolute runtime folder. '
                           'Launch SteamVR once, then close it and retry controller setup.')
    candidates = [Path(p) / 'bin' / 'win64' / 'vrpathreg.exe' for p in runtimes]
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise RuntimeError('SteamVR runtime was not found. Install and launch SteamVR once, then close it and retry.')


def download_verified(url, destination: Path, expected):
    if destination.is_file() and hashlib.sha256(destination.read_bytes()).hexdigest() == expected:
        return
    temporary = destination.with_suffix(destination.suffix + '.download')
    request = urllib.request.Request(url, headers={'User-Agent': 'QproFaceTracking-controller-setup'})
    digest = hashlib.sha256()
    try:
        with urllib.request.urlopen(request, timeout=60) as response, temporary.open('wb') as output:
            total = 0
            last_report = time.monotonic()
            while block := response.read(1024 * 1024):
                total += len(block)
                if total > 128 * 1024 * 1024:
                    raise RuntimeError('Optional component download exceeded its size limit.')
                output.write(block)
                digest.update(block)
                if time.monotonic() - last_report > 3:
                    report('CONTROLLER_SETUP', phase='downloading', file=destination.name, bytes=total)
                    last_report = time.monotonic()
        if digest.hexdigest() != expected:
            raise RuntimeError('Optional component download failed SHA-256 verification: ' + destination.name)
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)


def install(root: Path, local: Path):
    assert_steamvr_closed()
    tool = steamvr_tool(local)
    hands, addon = managed_paths(local)
    source = root / 'controller-input' / 'addon'
    if not (source / 'bin' / 'win64' / 'driver_qpro_controller.dll').is_file():
        raise RuntimeError('The controller add-on is missing. Extract the complete test ZIP.')
    if addon.exists() and not owner_is_valid(addon):
        raise RuntimeError('The destination exists without a Qpro ownership marker; no files were replaced.')
    settings_path = addon / 'resources' / 'settings.json'
    settings_before = None
    settings = {'enabled': False, 'mode': 'trackpad'}
    if settings_path.exists():
        # Match the driver's flat settings schema before downloading or moving
        # the old tree. A reinstall must retain tuning, not silently reset it.
        try:
            if settings_path.is_symlink() or settings_path.is_junction():
                raise ValueError('the file contains a link')
            with settings_path.open('rb') as saved:
                settings_before = saved.read(8193)
            if len(settings_before) > 8192:
                raise ValueError('the file exceeds 8192 bytes')
            def unique_object(pairs):
                result = {}
                for key, value in pairs:
                    if key in result:
                        raise ValueError('duplicate key ' + repr(key))
                    result[key] = value
                return result
            def reject_constant(value):
                raise ValueError('non-finite value ' + value)
            settings = json.loads(settings_before.decode('utf-8'), object_pairs_hook=unique_object,
                                  parse_constant=reject_constant)
            if not isinstance(settings, dict):
                raise ValueError('the file must contain a JSON object')
            def native_float(value):
                return struct.unpack('<f', struct.pack('<f', value))[0]
            ranges = {'smoothingMs': (0, 250), 'clickOn': (0, 1), 'clickOff': (0, 1),
                      'joystickSpan': (.1, 2), 'swipeGain': (.01, 5), 'glideMs': (0, 2000),
                      'mouseScale': (0, 4000), 'restingSize': (0, 512), 'intentionalForce': (0, 1)}
            for key, value in settings.items():
                if key in ('enabled', 'ignoreRestingThumb'):
                    valid = type(value) is bool
                elif key == 'mode':
                    valid = value in ('trackpad', 'joystick', 'swipe', 'mouse')
                elif key == 'side':
                    valid = value in INPUT_SIDES
                elif key in ranges:
                    low, high = ranges[key]
                    # Native bounds and stored values are float32, while its
                    # JSON number is first parsed as double. Preserve that exact
                    # comparison, including the current .1f minimum boundary.
                    valid = type(value) in (int, float) and native_float(low) <= value <= native_float(high)
                else:
                    raise ValueError('unknown key ' + repr(key))
                if not valid:
                    raise ValueError('invalid value for ' + repr(key))
            if native_float(settings.get('clickOff', .46)) >= native_float(settings.get('clickOn', .64)):
                raise ValueError('clickOff must be lower than clickOn at native float precision')
            settings['enabled'] = False
        except (OSError, UnicodeError, ValueError, RecursionError) as error:
            raise RuntimeError('Controller settings at ' + str(settings_path) + ' could not be preserved: '
                               + str(error) + '. The current add-on and settings were kept. '
                               'Correct this file or restore a valid recovery copy before retrying setup.') from error
    hands.mkdir(parents=True, exist_ok=True)
    report('CONTROLLER_SETUP', phase='verifying-official-downloads', version=FRIDA_VERSION)
    wheel = hands / WHEEL_NAME
    archive = hands / 'frida-server.xz'
    download_verified(WHEEL_URL, wheel, WHEEL_HASH)
    download_verified(SERVER_URL, archive, SERVER_HASH)
    server = hands / 'frida-server'
    with lzma.open(archive, 'rb') as source_file, server.with_suffix('.tmp').open('wb') as output:
        shutil.copyfileobj(source_file, output)
    server.with_suffix('.tmp').replace(server)
    python = hands / '.venv' / 'Scripts' / 'python.exe'
    if not python.exists():
        report('CONTROLLER_SETUP', phase='creating-private-python-environment')
        command([getattr(sys, '_base_executable', sys.executable), '-m', 'venv', str(hands / '.venv')], timeout=90)
    report('CONTROLLER_SETUP', phase='installing-pinned-frida')
    command([python, '-m', 'pip', 'install', '--no-index', '--no-deps', str(wheel)], timeout=120, capture=False)
    command([python, '-c', f"import frida; assert frida.__version__ == '{FRIDA_VERSION}'"], timeout=20)
    atomic_json(hands / 'ready.json', {'format': 'qpro-hands-v1', 'frida': FRIDA_VERSION,
                                      'serverSha256': hashlib.sha256(server.read_bytes()).hexdigest()})
    # Preserve a previous owned tree for recovery. Never remove someone else's driver.
    backup = None
    try:
        if settings_path.exists():
            with settings_path.open('rb') as saved:
                current_settings = saved.read(8193)
        else:
            current_settings = None
    except OSError as error:
        raise RuntimeError('Controller settings at ' + str(settings_path) + ' could not be rechecked. '
                           'The current add-on and settings were kept. Retry setup after closing other controller tools.') from error
    if current_settings != settings_before:
        raise RuntimeError('Controller settings changed during setup; your current settings were kept. '
                           'Keep SteamVR closed and retry controller setup.')
    if addon.exists():
        backup = addon.with_name('controller-addon-backup-' + time.strftime('%Y%m%d-%H%M%S'))
        if backup.exists():
            raise RuntimeError('A recovery copy already exists; retry setup in a moment.')
        addon.rename(backup)
    try:
        addon.mkdir()
        atomic_json(addon / 'qpro-owner.json', {'format': OWNER_FORMAT})
        shutil.copytree(source, addon, dirs_exist_ok=True)
        atomic_json(addon / 'resources' / 'settings.json', settings)
        assert_steamvr_closed()
        prepare_dashboard(local, tool, addon, backup)
        assert_steamvr_closed()
        prepare_vrchat(local, tool, addon, backup)
        assert_steamvr_closed()
        command([tool, 'adddriver', str(addon)])
    except Exception as original:
        if any((addon / relative).exists() for relative in (
                'resources/input/qpro-vrchat-owner.json', 'resources/input/qpro-dashboard-owner.json')):
            try:
                rollback_binding_selections(local, tool, addon)
            except (OSError, ValueError, RuntimeError) as rollback_error:
                # Do not destroy the only metadata able to undo a selected file
                # URL. Preserve this tree before restoring the previous driver.
                recovery = addon.with_name('controller-addon-failed-' + uuid.uuid4().hex)
                moved = False
                try:
                    addon.rename(recovery)
                    moved = True
                    if backup:
                        backup.rename(addon)
                except OSError as move_error:
                    raise RuntimeError('Controller setup failed and its binding selection recovery is unconfirmed. '
                                       'Keep SteamVR closed. Recovery files remain at '
                                       + str(recovery if moved else addon) + '. '
                                       + str(rollback_error) + '; file recovery: ' + str(move_error)) from original
                raise RuntimeError('Controller setup failed and its binding selection recovery is unconfirmed. '
                                   'Keep SteamVR closed and send Activity for repair. Recovery files remain at '
                                   + str(recovery) + '. ' + str(rollback_error)) from original
        if addon.exists() and owner_is_valid(addon):
            scope = (local / 'QproFaceTracking').resolve()
            if (addon.resolve().parent != scope or
                    any(path.is_symlink() or path.is_junction() for path in (addon, *addon.parents))):
                raise RuntimeError('Controller setup failed; the recovery path contains a link or is outside '
                                   'the managed folder. No files were removed. Keep SteamVR closed.') from original
            shutil.rmtree(addon)
        if backup:
            backup.rename(addon)
        raise
    report('CONTROLLER_SETUP', phase='complete', restart='Reopen SteamVR through Virtual Desktop',
           recovery=str(backup) if backup else None)


def uninstall(local: Path):
    assert_steamvr_closed()
    _, addon = managed_paths(local)
    if not addon.exists():
        report('CONTROLLER_SETUP', phase='already-uninstalled')
        return
    if not owner_is_valid(addon):
        raise RuntimeError('The controller add-on lacks its Qpro ownership marker; no files were changed.')
    tool = steamvr_tool(local)
    # Move first: locked files must not unregister a still-installed add-on.
    # A unique sibling also permits repeated install/remove cycles in one second.
    recovery = addon.with_name('controller-addon-uninstalled-' + uuid.uuid4().hex)
    scope = (local / 'QproFaceTracking').resolve()
    if (addon.resolve().parent != scope or recovery.resolve().parent != scope or
            any(path.is_symlink() or path.is_junction() for path in (addon, *addon.parents))):
        raise RuntimeError('The controller add-on path contains a link or is outside its managed folder; no files were changed.')
    addon.rename(recovery)
    try:
        assert_steamvr_closed()
        command([tool, 'removedriver', str(addon)])
    except Exception as error:
        try:
            recovery.rename(addon)
        except OSError as rollback_error:
            raise RuntimeError(f'Controller removal failed: {error}. Recovery files remain at {recovery}. '
                               f'The original folder could not be restored: {rollback_error}. '
                               'Keep SteamVR closed and repair the controller add-on before use.') from error
        raise RuntimeError(f'Controller removal failed: {error}. The add-on files were restored. '
                           'SteamVR registration could not be confirmed; repair the controller add-on before use.') from error
    try:
        helper = vrchat_helper()
        if (recovery / helper.OWNER).exists():
            report('CONTROLLER_VRCHAT_BINDING',
                   **helper.restore(recovery, local, tool.parents[2], addon))
    except (OSError, ValueError, RuntimeError) as error:
        report('CONTROLLER_BINDINGS_WARNING', message=str(error),
               next='The add-on was unregistered. Its VRChat recovery files were kept; '
                    'choose Default in SteamVR if the old binding is still selected.')
    try:
        helper = dashboard_helper()
        if (recovery / helper.OWNER).exists():
            report('CONTROLLER_DASHBOARD_BINDING',
                   **helper.restore(recovery, local, tool.parents[2], addon))
    except (OSError, ValueError, RuntimeError) as error:
        report('CONTROLLER_BINDINGS_WARNING', message=str(error),
               next='The add-on was unregistered. Its dashboard recovery files were kept.')
    report('CONTROLLER_SETUP', phase='uninstalled', recovery=str(recovery), restart='Reopen SteamVR')


def select_target(adb: Path, requested: str | None):
    if requested:
        if not re.fullmatch(r'[A-Za-z0-9_.:-]+', requested):
            raise RuntimeError('Invalid ADB target.')
        if command([adb, '-s', requested, 'get-state']) != 'device':
            raise RuntimeError('The selected Quest is not authorized.')
        return requested
    devices = command([adb, 'devices'])
    targets = [line.split()[0] for line in devices.splitlines()
               if len(line.split()) == 2 and line.split()[1] == 'device' and ':' not in line.split()[0]]
    if len(targets) != 1:
        raise RuntimeError('Connect exactly one authorized Quest by USB, or select wireless ADB in the Hub.')
    return targets[0]


def validate_packet(data: bytes, previous: int):
    if len(data) != PACKET.size:
        raise ValueError('Wrong packet length')
    values = PACKET.unpack(data)
    if values[:3] != (b'QPTP', 1, 64) or values[3] == 0 or values[4] == 0 or values[3] <= previous:
        raise ValueError('Invalid protocol or replayed packet')
    for start in (5, 10):
        flags, x, y, force, size = values[start:start + 5]
        if flags & ~3 or flags & 2 and not flags & 1:
            raise ValueError('Invalid contact flags')
        if not all(math.isfinite(v) for v in (x, y, force, size)):
            raise ValueError('Nonfinite sensor packet')
        if not (-1 <= x <= 1 and -1 <= y <= 1 and 0 <= force <= 1 and 0 <= size <= 512):
            raise ValueError('Sensor range outside profile')
    return values[3]


def validate_input_side(side):
    if side not in INPUT_SIDES:
        raise ValueError('Thumb-rest input side must be both, left, or right.')
    return side


def validate_swipe_gain(value):
    if value is None:
        return None
    if type(value) not in (int, float) or not .01 <= value <= 5 or not math.isfinite(value):
        raise ValueError('Swipe sensitivity gain must be a finite number from 0.01 to 5.')
    return float(value)


def parse_swipe_gain(value):
    try:
        return validate_swipe_gain(float(value))
    except (TypeError, ValueError) as error:
        raise argparse.ArgumentTypeError('Swipe sensitivity gain must be a finite number from 0.01 to 5.') from error


def set_input_enabled(addon: Path, enabled: bool, mode='trackpad', side='both', swipe_gain=None):
    side = validate_input_side(side)
    swipe_gain = validate_swipe_gain(swipe_gain)
    if not owner_is_valid(addon):
        raise RuntimeError('Install hand/controller components before enabling thumb-rest input.')
    path = addon / 'resources' / 'settings.json'
    try:
        settings = json.loads(path.read_text(encoding='utf-8')) if path.exists() else {}
    except (OSError, ValueError) as error:
        raise RuntimeError('The controller add-on settings could not be read. Close SteamVR and '
                           'reinstall the controller add-on before using thumb-rest input.') from error
    if not isinstance(settings, dict):
        raise RuntimeError('The controller add-on settings must be a JSON object. Close SteamVR '
                           'and reinstall the controller add-on before using thumb-rest input.')
    settings.update(enabled=enabled, mode=mode, side=side)
    if mode == 'swipe' and swipe_gain is not None:
        settings['swipeGain'] = swipe_gain
    atomic_json(path, settings)


def parent_closed_event(watch: bool):
    closed = threading.Event()
    if watch:
        def read_to_eof():
            sys.stdin.read()
            closed.set()
        threading.Thread(target=read_to_eof, daemon=True).start()
    return closed


class NativeReaderOutput:
    """Drain the foreground reader and retain bounded startup/exit evidence.

    An ADB forward accepting a local socket does not prove the remote server
    is listening. Only the reader's flushed marker follows its source checks.
    """
    def __init__(self, process):
        self.process = process
        self.listening = threading.Event()
        self.stopped = threading.Event()
        self.closed = threading.Event()
        self.error = None
        self._lines = deque(maxlen=8)
        self._lock = threading.Lock()
        self.thread = threading.Thread(target=self._read, daemon=True)

    def start(self):
        self.thread.start()

    def _read(self):
        try:
            for raw in self.process.stdout:
                line = raw.rstrip('\r\n')
                with self._lock:
                    self._lines.append(line[-1024:])
                forward_output(line)
                if line == 'QPTP_LISTENING address=127.0.0.1 port=27063 rate=60':
                    self.listening.set()
                elif line == 'QPTP_STOPPED':
                    self.stopped.set()
        except (OSError, ValueError) as error:
            self.error = str(error)
        finally:
            self._close_output()

    def _close_output(self):
        try:
            self.process.stdout.close()
        except (OSError, ValueError) as error:
            self.error = str(error)
        finally:
            self.closed.set()

    def finish(self):
        if self.thread.ident is not None:
            self.thread.join(timeout=1)
        else:
            self._close_output()

    def failure(self, message):
        code = self.process.poll()
        if code is not None:
            self.finish()
        with self._lock:
            detail = ' | '.join(self._lines)
        code_text = str(code) if code is not None else 'still running'
        message = (message + ' Reader/ADB exit: ' + code_text + '.' +
                   (' Native diagnostic: ' + detail if detail else '') +
                   (' Output read failed: ' + self.error if self.error else '') +
                   ' Thumb-rest input is disabled; check the reader diagnostic before retrying.')
        report('CONTROLLER_READER', phase='failed', exitCode=code,
               listening=self.listening.is_set(), diagnostic=detail,
               outputError=self.error, message=message)
        return RuntimeError(message)

    def wait_listening(self, stop_file, parent_closed, timeout=12):
        deadline = time.monotonic() + timeout
        while not stop_file.exists() and not parent_closed.is_set():
            if self.error:
                raise self.failure('The headset sensor reader output could not be read.')
            if self.process.poll() is not None:
                raise self.failure('The headset sensor reader exited before listening.')
            if self.listening.is_set():
                return True
            if self.closed.is_set():
                raise self.failure('The headset sensor reader closed its output before listening.')
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise self.failure('The headset sensor reader did not report readiness within 12 seconds.')
            self.listening.wait(min(.1, remaining))
        return False


def verify_addon_current(root: Path, addon: Path):
    relative = Path('bin') / 'win64' / 'driver_qpro_controller.dll'
    bundled = root / 'controller-input' / 'addon' / relative
    installed = addon / relative
    action = 'Close SteamVR, reinstall the controller add-on from this build, then reopen SteamVR.'
    if not bundled.is_file():
        raise RuntimeError('The bundled controller add-on DLL is missing. Extract the complete current test ZIP. ' + action)
    if not owner_is_valid(addon) or not installed.is_file():
        raise RuntimeError('The current controller add-on is not installed in its managed folder. ' + action)
    def digest(path):
        value = hashlib.sha256()
        with path.open('rb') as source:
            for block in iter(lambda: source.read(256 * 1024), b''):
                value.update(block)
        return value.digest()
    try:
        if digest(installed) != digest(bundled):
            raise RuntimeError('The installed controller add-on DLL does not match this build. ' + action)
    except OSError as error:
        raise RuntimeError('The installed controller add-on DLL could not be verified: ' + str(error) + '. ' + action) from error


def decode_driver_health(data, sender, nonce):
    if sender != ('127.0.0.1', 27064) or len(data) > 1024:
        return None
    try:
        def unique_pairs(pairs):
            value = {}
            for key, item in pairs:
                if key in value:
                    raise ValueError('duplicate health key')
                value[key] = item
            return value
        value = json.loads(data, object_pairs_hook=unique_pairs)
        if not isinstance(value, dict) or value.get('protocol') != 'QPRO-CTRL-HEALTH-v1' or value.get('nonce') != nonce:
            return None
        if value.get('mode') not in ('trackpad', 'joystick', 'swipe', 'mouse') or value.get('side') not in INPUT_SIDES:
            return None
        for key in ('enabled', 'settingsValid', 'neutral'):
            if type(value.get(key)) is not bool:
                return None
        for key in ('adoptedSides', 'successfulSides'):
            if not isinstance(value.get(key), list) or len(value[key]) != 2 or any(type(item) is not bool for item in value[key]):
                return None
        errors = value.get('inputErrors')
        if not isinstance(errors, list) or len(errors) != 2 or any(type(item) is not int or not 0 <= item <= 2147483647 for item in errors):
            return None
        for key in ('mouseError', 'restoreErrors', 'settingsRevision', 'appliedRevision'):
            if type(value.get(key)) is not int or not 0 <= value[key] <= 18446744073709551615:
                return None
        if value['appliedRevision'] > value['settingsRevision']:
            return None
        return value
    except (ValueError, TypeError, UnicodeError):
        return None


class DriverHealthClient:
    """Bounded loopback sideband; QPTP bytes and the reader cadence are unchanged."""
    def __init__(self, output):
        self.output = output
        output.setblocking(False)
        self.reset()

    def reset(self):
        # A fresh token prevents a queued enabled-session reply from proving Stop.
        self.nonce = uuid.uuid4().hex
        self.next_request = 0
        self.status = None

    def poll(self, interval=1):
        now = time.monotonic()
        if now >= self.next_request:
            self.next_request = now + interval
            try:
                self.output.sendto(('QPRO_CTRL_HEALTH_V1 ' + self.nonce).encode('ascii'), ('127.0.0.1', 27064))
            except OSError:
                pass
        received = None
        for _ in range(8):
            try:
                data, sender = self.output.recvfrom(1025)
            except (BlockingIOError, socket.timeout):
                break
            except OSError:
                break
            value = decode_driver_health(data, sender, self.nonce)
            if value is not None:
                self.status = received = value
        return received

    def confirm_disabled(self, mode, side, timeout=2):
        self.reset()
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            self.poll(.25)
            value = self.status
            if value and not value['enabled'] and value['settingsValid'] and value['neutral'] \
                    and value['mode'] == mode and value['side'] == side \
                    and value['settingsRevision'] == value['appliedRevision'] and value['appliedRevision'] > 0 \
                    and not any(value['inputErrors']) and not value['mouseError'] and not value['restoreErrors']:
                report('CONTROLLER_DRIVER_STATUS', **value)
                return True
            time.sleep(.05)
        if self.status:
            report('CONTROLLER_DRIVER_STATUS', **self.status)
        return False


def relay(root: Path, local: Path, adb: Path, target: str, stop_file: Path, mode: str,
          parent_closed=None, side='both', swipe_gain=None):
    side = validate_input_side(side)
    swipe_gain = validate_swipe_gain(swipe_gain)
    input_options = {'swipe_gain': swipe_gain} if mode == 'swipe' and swipe_gain is not None else {}
    parent_closed = parent_closed or threading.Event()
    _, addon = managed_paths(local)
    verify_addon_current(root, addon)
    reader = root / 'controller-input' / 'qpro-controller-input'
    if not reader.is_file():
        raise RuntimeError('The headset reader is missing from the test ZIP.')
    base = [str(adb), '-s', target]
    existing = command([adb, 'forward', '--list'])
    if any('tcp:27063' in line.split() for line in existing.splitlines()):
        raise RuntimeError('ADB port 27063 is already forwarded; no existing forward was replaced.')
    if 'uid=0' not in command(base + ['shell', 'su', '-c', 'id'], timeout=8):
        raise RuntimeError('Allow Android Shell in Magisk before enabling controller inputs.')
    # Profile probing happens before enabling the SteamVR add-on or starting a reader.
    command(base + ['push', str(reader), READER_PATH])
    command(base + ['shell', 'chmod', '700', READER_PATH])
    command(base + ['shell', 'su', '-c', shlex.quote(READER_PATH + ' --profile ' + PROFILE + ' --check')])
    process = None
    stream = None
    output = None
    driver_health = None
    enabled = False
    reader_output = None
    forwarded = False
    try:
        command(base + ['forward', '--no-rebind', 'tcp:27063', 'tcp:27063'])
        forwarded = True
        output = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        driver_health = DriverHealthClient(output)
        command(base + ['shell', 'rm', '-f', REMOTE_STOP])
        process = subprocess.Popen(base + ['shell', 'su', '-c', shlex.quote(READER_PATH + ' --profile ' + PROFILE +
                                          ' --port 27063 --rate 60 --stop-file ' + REMOTE_STOP)],
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, bufsize=1, creationflags=NO_WINDOW)
        reader_output = NativeReaderOutput(process)
        reader_output.start()
        if not reader_output.wait_listening(stop_file, parent_closed):
            return
        deadline = time.monotonic() + 12
        while not stop_file.exists() and not parent_closed.is_set() and time.monotonic() < deadline:
            try:
                stream = socket.create_connection(('127.0.0.1', 27063), timeout=.5)
                break
            except OSError:
                if process.poll() is not None:
                    raise reader_output.failure('The headset sensor reader exited before connecting.')
                time.sleep(.1)
        if not stream:
            if stop_file.exists() or parent_closed.is_set():
                return
            raise reader_output.failure('The thumb-rest reader did not connect within 12 seconds.')
        stream.settimeout(.25)
        previous = -1
        buffer = bytearray()
        ready = False
        last_valid = time.monotonic()
        while not stop_file.exists() and not parent_closed.is_set():
            try:
                block = stream.recv(4096)
            except socket.timeout:
                if stop_file.exists() or parent_closed.is_set():
                    break
                if process.poll() is not None:
                    raise reader_output.failure('The headset sensor reader exited during tracking.')
                if time.monotonic() - last_valid > 2:
                    raise reader_output.failure('The thumb-rest sensor feed stopped for more than two seconds.')
                continue
            except OSError as error:
                if stop_file.exists() or parent_closed.is_set():
                    break
                raise reader_output.failure('The headset sensor connection failed: ' + str(error)) from error
            if stop_file.exists() or parent_closed.is_set():
                break
            if not block:
                raise reader_output.failure('The headset sensor reader disconnected during tracking.')
            buffer.extend(block)
            while len(buffer) >= PACKET.size:
                if stop_file.exists() or parent_closed.is_set():
                    break
                packet = bytes(buffer[:PACKET.size])
                del buffer[:PACKET.size]
                previous = validate_packet(packet, previous)
                if stop_file.exists() or parent_closed.is_set():
                    break
                fields = PACKET.unpack(packet)
                last_valid = time.monotonic()
                selected_valid = ((side in ('both', 'left') and fields[5] & 1)
                                  or (side in ('both', 'right') and fields[10] & 1))
                if not enabled:
                    # Neither a listening marker nor a local forward alone is
                    # input data. Require a fresh side before enabling input;
                    # later neutral packets still clear an active controller.
                    if not selected_valid:
                        continue
                    set_input_enabled(addon, True, mode, side, **input_options)
                    enabled = True
                    report_dashboard(addon, True, mode, side)
                    report_vrchat(addon, True, mode, side)
                if stop_file.exists() or parent_closed.is_set():
                    break
                output.sendto(packet, ('127.0.0.1', 27064))
                health = driver_health.poll()
                if health:
                    report('CONTROLLER_DRIVER_STATUS', **health)
                if not ready and selected_valid:
                    report('TOUCHPAD_READY', protocol='QPTP-v1', mode=mode, side=side)
                    ready = True
    finally:
        original_error = sys.exc_info()[1]
        failures = []
        reader_exit_code = None
        try:
            set_input_enabled(addon, False, mode, side, **input_options)
            if enabled:
                report_dashboard(addon, False, mode, side)
                report_vrchat(addon, False, mode, side)
        except (OSError, ValueError, RuntimeError) as error:
            failures.append(str(error))
        if stream:
            try:
                stream.close()
            except OSError as error:
                failures.append('Reader socket close: ' + str(error))
        if enabled and driver_health:
            try:
                if not driver_health.confirm_disabled(mode, side):
                    failures.append('SteamVR add-on disabled/neutral acknowledgement was not confirmed; check Activity before restarting.')
            except (OSError, ValueError, RuntimeError) as error:
                failures.append('SteamVR add-on cleanup acknowledgement: ' + str(error))
        if output:
            try:
                output.close()
            except OSError as error:
                failures.append('Controller output socket close: ' + str(error))
        if process:
            try:
                command(base + ['shell', 'touch', REMOTE_STOP], timeout=8)
            except (OSError, RuntimeError, subprocess.SubprocessError) as error:
                failures.append(str(error))
            try:
                reader_exit_code = process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                # The reader is read-only and its connection is closed; do not kill
                # trackingservice or search/kill arbitrary Android processes.
                try:
                    process.terminate()
                    reader_exit_code = process.wait(timeout=3)
                except (OSError, subprocess.SubprocessError) as error:
                    failures.append(str(error))
                failures.append('The reader exit was not confirmed; check the headset before restarting.')
            except (OSError, subprocess.SubprocessError) as error:
                failures.append('Reader wait: ' + str(error))
        if reader_output:
            reader_output.finish()
            if reader_output.thread.is_alive():
                failures.append('The reader diagnostic stream did not close after cleanup.')
            if reader_output.error:
                failures.append('Reader diagnostic output: ' + reader_output.error)
        if process:
            if reader_exit_code != 0:
                failures.append('Reader/ADB exit: ' + str(reader_exit_code) +
                                '; native reader shutdown was not confirmed.')
            if reader_output is None or not reader_output.stopped.is_set():
                failures.append('The native reader did not report QPTP_STOPPED; its cleanup was not confirmed.')
        try:
            # Remove only the forward we created for this selected target.
            forwards = command([adb, 'forward', '--list'])
            if forwarded and any(line.split() == [target, 'tcp:27063', 'tcp:27063'] for line in forwards.splitlines()):
                command(base + ['forward', '--remove', 'tcp:27063'])
        except (OSError, RuntimeError, subprocess.SubprocessError) as error:
            failures.append(str(error))
        if failures:
            problems = (['Original failure: ' + (bounded_reason(str(original_error)) or type(original_error).__name__)]
                        if original_error else []) + failures
            report('CONTROLLER_CLEANUP', reader='unconfirmed', problems=problems)
            raise RuntimeError('Controller cleanup needs attention: ' + '; '.join(failures) +
                               (' Original failure: ' + str(original_error) if original_error else ''))
        report('CONTROLLER_CLEANUP', reader='stopped', inputs='disabled')


class WorkerOutput:
    """Forward Activity lines and retain explicit restoration evidence."""
    def __init__(self, process, name):
        self.process, self.name = process, name
        self.cleanup_confirmed = None
        self.output_error = None
        self._first_failure_reason = None
        self.context_reasons = []
        self.cleanup_problems = []
        self.reader = None
        self.reader_started = False

    @property
    def failure_reason(self):
        reasons = [self._first_failure_reason] if self._first_failure_reason else []
        reasons.extend(reason for reason in self.context_reasons
                       if not self._first_failure_reason or reason not in self._first_failure_reason)
        reasons.extend('Cleanup: ' + problem for problem in self.cleanup_problems
                       if not any(problem in reason for reason in reasons))
        return '; '.join(reasons) or None

    def start(self):
        try:
            self.reader = threading.Thread(target=self._read, daemon=True)
            self.reader.start()
            self.reader_started = True
        except (OSError, RuntimeError) as error:
            self.cleanup_confirmed = False
            self.output_error = 'Could not start worker output reader: ' + str(error)
            self.reader_started = self.reader is not None and self.reader.ident is not None
            raise

    def _keep_cleanup_problem(self, value):
        problem = bounded_reason(value, 512)
        if problem and problem not in self.cleanup_problems and len(self.cleanup_problems) < 8:
            self.cleanup_problems.append(problem)

    def _keep_context_reason(self, value):
        reason = bounded_reason(value, 480)
        if reason and reason not in self.context_reasons and len(self.context_reasons) < 4:
            self.context_reasons.append(reason)

    def _observe(self, line):
        stage, _, payload = line.partition(' ')
        reason = failure_reason(line)
        primary_reason = reason
        if self.name == 'hands' and stage in ('HANDS_STOP_CAUSE', 'HANDS_CLEANUP_FAILED'):
            try:
                result = json.loads(payload)
            except ValueError:
                result = None
            if isinstance(result, dict):
                primary_reason = result.get('error')
                if stage == 'HANDS_CLEANUP_FAILED':
                    self._keep_cleanup_problem(reason)
                    self._keep_cleanup_problem(cleanup_phase_reason(result))
                detached = result.get('detached')
                if isinstance(detached, list):
                    for entry in detached[:4]:
                        self._keep_context_reason(detachment_reason(entry))
        if stage == 'HANDS_DETACHED' and self.name == 'hands':
            self._keep_context_reason(reason)
        elif self._first_failure_reason is None:
            self._first_failure_reason = bounded_reason(primary_reason, 1024)
        elif stage == 'HANDS_CLEANUP_FAILED' and self.name == 'hands':
            if reason and 'phase=' in reason:
                self._keep_context_reason(reason)
        if self.name == 'hands' and stage == 'HANDS_CLEANUP_FAILED':
            self._keep_cleanup_problem(reason)
            self.cleanup_confirmed = False
            return
        expected = 'HANDS_CLEANUP' if self.name == 'hands' else 'CONTROLLER_CLEANUP'
        if stage != expected:
            return
        try:
            result = json.loads(payload)
            if isinstance(result, dict) and isinstance(result.get('problems'), list):
                for value in result['problems'][:8]:
                    self._keep_cleanup_problem(value)
            if not isinstance(result, dict):
                confirmed = False
            elif self.name == 'hands':
                confirmed = result.get('confirmed') is True and result.get('problems') == []
            else:
                confirmed = (result.get('reader') == 'stopped' and result.get('inputs') == 'disabled'
                             and result.get('problems', []) == [] and result.get('confirmed', True) is True)
        except ValueError:
            confirmed = False
        # A later success line cannot erase failed or malformed restoration evidence.
        if self.cleanup_confirmed is not False:
            self.cleanup_confirmed = confirmed

    def _read(self):
        try:
            for raw in self.process.stdout:
                line = raw.rstrip('\r\n')
                self._observe(line)
                with OUTPUT_LOCK:
                    print(line, flush=True)
        except (OSError, ValueError) as error:
            self.cleanup_confirmed = False
            self.output_error = str(error)
        finally:
            try:
                self.process.stdout.close()
            except (OSError, ValueError) as error:
                self.cleanup_confirmed = False
                self.output_error = self.output_error or 'Worker output close failed: ' + str(error)

    def finish(self):
        if not self.reader_started:
            return
        # Process exit can precede the reader consuming its final cleanup line.
        self.reader.join(timeout=10)
        if self.reader.is_alive():
            self.cleanup_confirmed = False
            self.output_error = 'Worker output did not close after exit.'


def run(args, local: Path, adb: Path, target: str):
    side = validate_input_side(getattr(args, 'side', 'both'))
    swipe_gain = validate_swipe_gain(getattr(args, 'swipe_gain', None))
    if not args.hands and not args.touchpad:
        raise RuntimeError('Select Hands + controllers or Touch Pro thumb-rest input first.')
    if not args.stop_file:
        raise RuntimeError('A stop file is required for supervised controller input.')
    stop = Path(args.stop_file).resolve()
    if stop.exists():
        raise RuntimeError('The stop file already exists. Stop the previous session before starting another.')
    if not stop.parent.is_dir():
        raise RuntimeError('The stop-file directory is missing; no controller worker was started.')
    # Probe writability without creating a stop signal that a worker could see.
    with tempfile.TemporaryFile(dir=stop.parent):
        pass
    parent_closed = parent_closed_event(args.parent_stdin)
    root = Path(args.root).resolve()
    hands, addon = managed_paths(local)
    children = []
    exited_before_stop = False
    try:
        if args.touchpad:
            verify_addon_current(root, addon)
        if args.hands:
            python = hands / '.venv' / 'Scripts' / 'python.exe'
            if not (hands / 'ready.json').is_file() or not python.is_file():
                raise RuntimeError('Install hand/controller components first.')
            child = subprocess.Popen([str(python), '-u', str(root / 'hybrid' / 'controller.py'),
                '--target', target, '--adb', str(adb), '--stop-file', str(stop),
                '--frida-server', str(hands / 'frida-server'), '--parent-stdin'],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, bufsize=1, creationflags=NO_WINDOW)
            worker = WorkerOutput(child, 'hands')
            children.append(worker)
            worker.start()
        if args.touchpad:
            child_arguments = [sys.executable, '-u', __file__, 'relay', '--root', str(root),
                '--adb', str(adb), '--target', target, '--stop-file', str(stop), '--mode', args.mode,
                '--side', side, '--parent-stdin']
            if args.mode == 'swipe' and swipe_gain is not None:
                child_arguments.extend(['--swipe-gain', str(swipe_gain)])
            child = subprocess.Popen(child_arguments,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, bufsize=1, creationflags=NO_WINDOW)
            worker = WorkerOutput(child, 'touchpad')
            children.append(worker)
            worker.start()
        while not stop.exists() and not parent_closed.is_set():
            exited = [worker for worker in children if worker.process.poll() is not None]
            if exited:
                # Read the final diagnostic and cleanup lines before deciding
                # which failure to report; process exit can precede that drain.
                # Stop/EOF can arrive while poll runs. A clean worker exit then
                # belongs to that Stop; nonzero exits are still reported below.
                exited_before_stop = not stop.exists() and not parent_closed.is_set()
                break
            time.sleep(.1)
    finally:
        original_error = sys.exc_info()[1]
        failures = []
        try:
            stop.write_text('controller stop requested\n', encoding='utf-8')
        except OSError as error:
            failures.append('Stop file: ' + str(error))
            report('CONTROLLER_CLEANUP', restoration='stop-file-failed', message=str(error))
        for worker in children:
            child = worker.process
            if child.stdin:
                try:
                    child.stdin.close()
                except OSError as error:
                    failures.append(worker.name + ' stop pipe: ' + str(error))
        for worker in children:
            child = worker.process
            if not worker.reader_started:
                # After Stop/EOF, drain in this thread so an unmonitored worker
                # cannot block its restoration writes on a full output pipe.
                worker._read()
            while child.poll() is None:
                try:
                    child.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    # Remain the process supervised by the Hub until all owned
                    # workers finish; never orphan a still-restoring adapter.
                    report('CONTROLLER_CLEANUP', workerPid=child.pid, restoration='still-running')
            worker.finish()
            if worker.cleanup_confirmed is not True or child.returncode == 5:
                detail = worker.name + ' worker cleanup was not confirmed (exit ' + str(child.returncode) + ').'
                if worker.failure_reason:
                    detail += ' Original failure: ' + bounded_reason(worker.failure_reason, 3800)
                failures.append(detail)
                failures.extend(worker.name + ' cleanup: ' + problem for problem in worker.cleanup_problems)
            if worker.output_error:
                failures.append(worker.name + ' output: ' + worker.output_error)
        if failures and original_error:
            failures.append('Original failure: ' + (bounded_reason(str(original_error)) or type(original_error).__name__))
        report('CONTROLLER_CLEANUP', confirmed=not failures,
               restoration='unconfirmed' if failures else 'confirmed',
               reader='unconfirmed' if failures else 'stopped', problems=failures)
        if failures:
            reasons = [worker.name + ': ' + bounded_reason(worker.failure_reason, 3800) for worker in children
                       if worker.failure_reason]
            if original_error:
                reasons.append(bounded_reason(str(original_error)) or type(original_error).__name__)
            raise RuntimeError('Controller cleanup was not confirmed. Keep the Hub open and check Activity.'
                               + (' Original failure: ' + '; '.join(reasons) if reasons else '')
                               + ' Cleanup problems: ' + '; '.join(failures))
        if sys.exc_info()[0] is None:
            failed = [worker for worker in children if worker.process.returncode != 0]
            if failed:
                prefix = ('A controller worker exited before Stop' if exited_before_stop
                          else 'A controller worker failed')
                details = [worker.name + ' (exit ' + str(worker.process.returncode) + '): '
                           + (worker.failure_reason or 'See its Activity lines for the startup or runtime error.')
                           for worker in failed]
                raise WorkerFailure(prefix + ': ' + '; '.join(details), failed[0].process.returncode)
            if exited_before_stop:
                raise RuntimeError('A controller worker exited before Stop without reporting an error.')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['install', 'uninstall', 'check', 'run', 'relay'])
    parser.add_argument('--root', required=True)
    parser.add_argument('--adb')
    parser.add_argument('--target')
    parser.add_argument('--hands', action='store_true')
    parser.add_argument('--touchpad', action='store_true')
    parser.add_argument('--stop-file')
    parser.add_argument('--parent-stdin', action='store_true')
    parser.add_argument('--mode', choices=['trackpad', 'joystick', 'swipe', 'mouse'], default='trackpad')
    parser.add_argument('--side', choices=INPUT_SIDES, default='both')
    parser.add_argument('--swipe-gain', type=parse_swipe_gain)
    args = parser.parse_args(argv)
    local_value = os.environ.get('LOCALAPPDATA')
    if not local_value:
        raise RuntimeError('Windows LOCALAPPDATA is unavailable; controller components cannot '
                           'use their private per-user folder. Restart the Hub from your Windows account.')
    local = Path(local_value)
    managed_paths(local)
    root = Path(args.root).resolve()
    if args.action == 'install':
        install(root, local)
    elif args.action == 'uninstall':
        uninstall(local)
    else:
        adb = Path(args.adb) if args.adb else root / 'platform-tools' / 'adb.exe'
        target = select_target(adb, args.target)
        if args.action == 'check':
            hands, _ = managed_paths(local)
            python = hands / '.venv' / 'Scripts' / 'python.exe'
            # Inspect the same private Frida environment and Android helper
            # used by run. The tracking runtime deliberately does not own them.
            # Before installation, the parent Python can still inspect the
            # headset and driver without creating or downloading components.
            if not python.is_file():
                python = Path(sys.executable)
            result = subprocess.run([str(python), '-u', str(root / 'hybrid' / 'controller.py'),
                                     '--adb', str(adb), '--target', target,
                                     '--frida-server', str(hands / 'frida-server'), '--check'],
                                    timeout=30, text=True, capture_output=True, creationflags=NO_WINDOW)
            output = result.stdout or ''
            errors = result.stderr or ''
            forward_output(output)
            forward_output(errors)
            if result.returncode:
                reasons = [reason for line in (output + '\n' + errors).splitlines()
                           if (reason := failure_reason(line))]
                fallback = 'Hand/controller compatibility check failed (exit ' + str(result.returncode) + ').'
                detail = (output + errors).strip()
                raise WorkerFailure(reasons[0] if reasons else fallback + (' ' + detail if detail else ''),
                                    result.returncode)
            report('CONTROLLER_CHECK', touchpad='Sensor compatibility is checked at start; legacy firmware profile required',
                   frida='installed' if (managed_paths(local)[0] / 'ready.json').is_file() else 'not-installed')
        elif args.action == 'run':
            run(args, local, adb, target)
        else:
            relay(root, local, adb, target, Path(args.stop_file), args.mode,
                  parent_closed_event(args.parent_stdin), args.side, args.swipe_gain)


def entry_point(argv=None):
    try:
        main(argv)
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
        report('CONTROLLER_ERROR', message=str(error))
        return error.returncode if isinstance(error, WorkerFailure) else 1
    return 0


if __name__ == '__main__':
    sys.exit(entry_point())
