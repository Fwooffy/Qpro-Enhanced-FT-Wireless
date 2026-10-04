"""Optional component setup and supervision; all installation paths are Qpro-owned.

Importing this module performs no installation, ADB call, or process attachment.
"""
from __future__ import annotations

import argparse
import hashlib
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


def owner_is_valid(addon: Path):
    try:
        return json.loads((addon / 'qpro-owner.json').read_text(encoding='utf-8'))['format'] == OWNER_FORMAT
    except (OSError, ValueError, KeyError):
        return False


def assert_steamvr_closed():
    if os.name != 'nt':
        raise RuntimeError('The controller add-on currently targets Windows x64.')
    tasks = command(['tasklist.exe', '/FI', 'IMAGENAME eq vrserver.exe', '/FO', 'CSV', '/NH'])
    if '"vrserver.exe"' in tasks.lower():
        raise RuntimeError('Close SteamVR before installing or uninstalling the controller add-on.')


def steamvr_tool(local: Path):
    paths = local / 'openvr' / 'openvrpaths.vrpath'
    data = json.loads(paths.read_text(encoding='utf-8-sig'))
    candidates = [Path(p) / 'bin' / 'win64' / 'vrpathreg.exe' for p in data.get('runtime', [])]
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
    if addon.exists():
        backup = addon.with_name('controller-addon-backup-' + time.strftime('%Y%m%d-%H%M%S'))
        if backup.exists():
            raise RuntimeError('A recovery copy already exists; retry setup in a moment.')
        addon.rename(backup)
    try:
        addon.mkdir()
        atomic_json(addon / 'qpro-owner.json', {'format': OWNER_FORMAT})
        shutil.copytree(source, addon, dirs_exist_ok=True)
        atomic_json(addon / 'resources' / 'settings.json', {'enabled': False, 'mode': 'trackpad'})
        command([tool, 'adddriver', str(addon)])
    except Exception:
        if addon.exists() and owner_is_valid(addon):
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
    command([steamvr_tool(local), 'removedriver', str(addon)])
    # Keep recovery files; no recursive deletion is needed to unregister the add-on.
    recovery = addon.with_name('controller-addon-uninstalled-' + time.strftime('%Y%m%d-%H%M%S'))
    addon.rename(recovery)
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
    if values[:3] != (b'QPTP', 1, 64) or values[3] <= previous:
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


def set_input_enabled(addon: Path, enabled: bool, mode='trackpad'):
    if not owner_is_valid(addon):
        raise RuntimeError('Install hand/controller components before enabling thumb-rest input.')
    path = addon / 'resources' / 'settings.json'
    settings = json.loads(path.read_text(encoding='utf-8')) if path.exists() else {}
    settings.update(enabled=enabled, mode=mode)
    atomic_json(path, settings)


def parent_closed_event(watch: bool):
    closed = threading.Event()
    if watch:
        def read_to_eof():
            sys.stdin.read()
            closed.set()
        threading.Thread(target=read_to_eof, daemon=True).start()
    return closed


def relay(root: Path, local: Path, adb: Path, target: str, stop_file: Path, mode: str, parent_closed=None):
    parent_closed = parent_closed or threading.Event()
    _, addon = managed_paths(local)
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
    forwarded = False
    try:
        command(base + ['forward', '--no-rebind', 'tcp:27063', 'tcp:27063'])
        forwarded = True
        output = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        command(base + ['shell', 'rm', '-f', REMOTE_STOP])
        process = subprocess.Popen(base + ['shell', 'su', '-c', shlex.quote(READER_PATH + ' --profile ' + PROFILE +
                                          ' --port 27063 --rate 60 --stop-file ' + REMOTE_STOP)], creationflags=NO_WINDOW)
        deadline = time.monotonic() + 12
        while not stop_file.exists() and not parent_closed.is_set() and time.monotonic() < deadline:
            try:
                stream = socket.create_connection(('127.0.0.1', 27063), timeout=.5)
                break
            except OSError:
                if process.poll() is not None:
                    raise RuntimeError('The headset sensor reader exited before connecting.')
                time.sleep(.1)
        if not stream:
            raise RuntimeError('The thumb-rest reader did not connect. No controller input was enabled.')
        stream.settimeout(.25)
        set_input_enabled(addon, True, mode)
        previous = -1
        buffer = bytearray()
        ready = False
        last_valid = time.monotonic()
        while not stop_file.exists() and not parent_closed.is_set():
            try:
                block = stream.recv(4096)
            except socket.timeout:
                if time.monotonic() - last_valid > 2:
                    raise RuntimeError('Thumb-rest sensor feed stopped; custom input is being disabled.')
                continue
            if not block:
                raise RuntimeError('Headset sensor reader disconnected.')
            buffer.extend(block)
            while len(buffer) >= PACKET.size:
                packet = bytes(buffer[:PACKET.size])
                del buffer[:PACKET.size]
                previous = validate_packet(packet, previous)
                output.sendto(packet, ('127.0.0.1', 27064))
                last_valid = time.monotonic()
                fields = PACKET.unpack(packet)
                if not ready and (fields[5] & 1 or fields[10] & 1):
                    report('TOUCHPAD_READY', protocol='QPTP-v1', mode=mode)
                    ready = True
    finally:
        failures = []
        try:
            set_input_enabled(addon, False, mode)
        except (OSError, ValueError, RuntimeError) as error:
            failures.append(str(error))
        if stream:
            stream.close()
        if output:
            output.close()
        if process:
            try:
                command(base + ['shell', 'touch', REMOTE_STOP], timeout=8)
            except (OSError, RuntimeError, subprocess.SubprocessError) as error:
                failures.append(str(error))
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                # The reader is read-only and its connection is closed; do not kill
                # trackingservice or search/kill arbitrary Android processes.
                try:
                    process.terminate()
                    process.wait(timeout=3)
                except (OSError, subprocess.SubprocessError) as error:
                    failures.append(str(error))
                failures.append('The reader exit was not confirmed; check the headset before restarting.')
        try:
            # Remove only the forward we created for this selected target.
            forwards = command([adb, 'forward', '--list'])
            if forwarded and any(line.split() == [target, 'tcp:27063', 'tcp:27063'] for line in forwards.splitlines()):
                command(base + ['forward', '--remove', 'tcp:27063'])
        except (OSError, RuntimeError, subprocess.SubprocessError) as error:
            failures.append(str(error))
        if failures:
            report('CONTROLLER_CLEANUP', reader='unconfirmed', problems=failures)
            raise RuntimeError('Controller cleanup needs attention: ' + '; '.join(failures))
        report('CONTROLLER_CLEANUP', reader='stopped', inputs='disabled')


class WorkerOutput:
    """Forward Activity lines and retain explicit restoration evidence."""
    def __init__(self, process, name):
        self.process, self.name = process, name
        self.cleanup_confirmed = None
        self.output_error = None
        self.reader = None
        self.reader_started = False

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

    def _observe(self, line):
        stage, _, payload = line.partition(' ')
        if self.name == 'hands' and stage == 'HANDS_CLEANUP_FAILED':
            self.cleanup_confirmed = False
            return
        expected = 'HANDS_CLEANUP' if self.name == 'hands' else 'CONTROLLER_CLEANUP'
        if stage != expected:
            return
        try:
            result = json.loads(payload)
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
            self.process.stdout.close()

    def finish(self):
        if not self.reader_started:
            return
        # Process exit can precede the reader consuming its final cleanup line.
        self.reader.join(timeout=10)
        if self.reader.is_alive():
            self.cleanup_confirmed = False
            self.output_error = 'Worker output did not close after exit.'


def run(args, local: Path, adb: Path, target: str):
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
    hands, _ = managed_paths(local)
    children = []
    try:
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
            child = subprocess.Popen([sys.executable, '-u', __file__, 'relay', '--root', str(root),
                '--adb', str(adb), '--target', target, '--stop-file', str(stop), '--mode', args.mode, '--parent-stdin'],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, bufsize=1, creationflags=NO_WINDOW)
            worker = WorkerOutput(child, 'touchpad')
            children.append(worker)
            worker.start()
        while not stop.exists() and not parent_closed.is_set():
            exited = [worker for worker in children if worker.process.poll() is not None]
            if exited:
                raise RuntimeError('A controller worker exited before Stop. See its Activity lines for the compatibility or cleanup result.')
            time.sleep(.1)
    finally:
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
                failures.append(worker.name + ' worker cleanup was not confirmed (exit '
                                + str(child.returncode) + ').')
            if worker.output_error:
                failures.append(worker.name + ' output: ' + worker.output_error)
        report('CONTROLLER_CLEANUP', confirmed=not failures,
               restoration='unconfirmed' if failures else 'confirmed',
               reader='unconfirmed' if failures else 'stopped', problems=failures)
        if failures:
            raise RuntimeError('Controller cleanup was not confirmed. Keep the Hub open and check Activity.')
        if sys.exc_info()[0] is None and any(worker.process.returncode != 0 for worker in children):
            raise RuntimeError('A controller worker failed. See its Activity lines for the startup or runtime error.')


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
    args = parser.parse_args(argv)
    local = Path(os.environ['LOCALAPPDATA'])
    root = Path(args.root).resolve()
    if args.action == 'install':
        install(root, local)
    elif args.action == 'uninstall':
        uninstall(local)
    else:
        adb = Path(args.adb) if args.adb else root / 'platform-tools' / 'adb.exe'
        target = select_target(adb, args.target)
        if args.action == 'check':
            command([sys.executable, '-u', root / 'hybrid' / 'controller.py', '--adb', adb,
                     '--target', target, '--check'], timeout=30, capture=False)
            report('CONTROLLER_CHECK', touchpad='Sensor compatibility is checked at start; legacy firmware profile required',
                   frida='installed' if (managed_paths(local)[0] / 'ready.json').is_file() else 'not-installed')
        elif args.action == 'run':
            run(args, local, adb, target)
        else:
            relay(root, local, adb, target, Path(args.stop_file), args.mode, parent_closed_event(args.parent_stdin))


if __name__ == '__main__':
    try:
        main()
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
        report('CONTROLLER_ERROR', message=str(error))
        sys.exit(1)
