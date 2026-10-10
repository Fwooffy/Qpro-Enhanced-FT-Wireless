"""Supervise opt-in Virtual Desktop hand/controller adapters.

No dependencies are installed here. Unknown builds fail before attachment; the
read-only --check path never starts a helper or loads an adapter.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import signal
import subprocess
import sys
import threading
import time
import uuid

HERE = Path(__file__).resolve().parent
HELPER = "/data/local/tmp/qpro-hands-frida"
PORT = 27062
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


class CompatibilityError(RuntimeError):
    pass


class CleanupError(RuntimeError):
    pass


def emit(event: str, **details):
    print("HANDS_" + event + " " + json.dumps(details, ensure_ascii=True), flush=True)


def load_profile():
    return json.loads((HERE / "compatibility.json").read_text(encoding="utf-8"))


def shell_quote(value: str):
    return "'" + value.replace("'", "'\\''") + "'"


def input_is_ready(statuses):
    return any(fresh and routed for fresh, routed in zip(
        statuses[1].get("freshSides", [False, False]),
        statuses[2].get("activeSides", [False, False]))) and statuses[0].get("queries", 0) > 0


def start_stop_watchers(stop, stop_file, parent_input=None):
    def watch_file():
        while not stop.wait(0.2):
            if stop_file is not None and stop_file.exists():
                stop.set()
    threading.Thread(target=watch_file, daemon=True).start()
    if parent_input is not None:
        def watch_input():
            parent_input.read()
            stop.set()
        threading.Thread(target=watch_input, daemon=True).start()


class Adb:
    def __init__(self, executable: str, target: str):
        if not re.fullmatch(r"[A-Za-z0-9_.:-]+", target):
            raise CompatibilityError("The selected ADB target is invalid.")
        self.executable, self.target = executable, target

    def run(self, *args: str, timeout=15):
        result = subprocess.run([self.executable, "-s", self.target, *args],
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, timeout=timeout, creationflags=NO_WINDOW)
        if result.returncode:
            raise RuntimeError("ADB command failed: " + result.stdout.strip())
        return result.stdout.strip()


def inspect(adb: Adb, profile: dict, frida_version: str | None,
            server_path: Path | None, program_files: str | None = None,
            require_components=True):
    """Reads facts only; callers decide whether a failed check prevents startup."""
    driver = Path(program_files or os.environ.get("ProgramFiles", r"C:\Program Files")) / profile["pcDriverRelativePath"]
    problems, warnings = [], []
    driver_hash = hashlib.sha256(driver.read_bytes()).hexdigest() if driver.is_file() else None
    # Additional fingerprints share the individually inspected PC layout. A
    # known PC build never admits an unknown headset version or live ABI.
    admitted_drivers = (profile["pcDriverSha256"], *profile.get("additionalPcDriverSha256s", []))
    if driver_hash not in admitted_drivers:
        if driver_hash is None:
            problems.append("The Virtual Desktop Streamer hand driver was not found. Install or repair "
                            "Virtual Desktop Streamer, reopen SteamVR through Virtual Desktop, then check again.")
        else:
            problems.append("This Virtual Desktop Streamer driver has no validated hand profile. "
                            "Send the PC driver SHA-256 from this check to Qpro support; reinstalling "
                            "the hand components will not add support for a different driver build.")
    component_problems = []
    if frida_version != profile["fridaVersion"]:
        component_problems.append("Install the experimental hand components first (matching Frida is missing).")
    if server_path is None or not server_path.is_file():
        component_problems.append("The matching Android hand helper is missing.")
    if require_components:
        problems.extend(component_problems)
    package = adb.run("shell", "dumpsys", "package", profile["androidPackage"])
    version = re.search(r"\bversionName=([^\s]+)", package)
    version = version.group(1) if version else None
    if version != profile["androidVersion"]:
        if version is None:
            problems.append("The headset Virtual Desktop version could not be read. Confirm that "
                            "Virtual Desktop is installed on the selected Quest and check again.")
        else:
            problems.append("This headset Virtual Desktop version has no validated hand profile: "
                            + version + ". This Qpro build currently checks version " + profile["androidVersion"]
                            + ". Reinstalling the hand components does not change that compatibility limit.")
    required = ("hand_tracking_enabled", "multimodal_hands_and_controllers_enabled",
                "simultaneous_hands_and_controllers_mode")
    # --getc reads the currently active headset user. --get instead reads the
    # calling user, which can differ when this command runs through root.
    preference_command = "oculuspreferences --getc " + " ".join(required)
    # adb shell joins its arguments into a remote command; quoting must survive
    # that join so the single preference query stays inside the root command.
    settings = adb.run("shell", "su -c " + shell_quote(preference_command))
    entries = re.findall(r"\[(\w+)\s*:\s*([^\[\]\r\n]+)\]", settings)
    values = dict(entries)
    for name in required:
        value = values.get(name, "").strip().lower()
        is_mode = name == "simultaneous_hands_and_controllers_mode"
        supported = ("0", "1") if is_mode else ("true", "1", "false", "0")
        mentions = len(re.findall(r"\[\s*" + re.escape(name) + r"\b", settings))
        if mentions != 1 or sum(key == name for key, _ in entries) != 1 or value not in supported:
            problems.append("Could not read one supported current-user value for " + name +
                            ". Check the headset connection and root access, then check compatibility again.")
        elif is_mode and value != "1":
            problems.append("Headset current-user setting " + name + " must be 1. "
                            "In Singularity, turn on Simultaneous Hands & Controllers, then check compatibility again.")
        elif value in ("false", "0"):
            # Preliminary booleans cannot prove live optical availability;
            # activation and the fresh same-side readiness check establish it.
            warnings.append("Headset reports " + name + " inactive before startup; "
                            "Qpro will check live optical input after requesting Virtual Desktop multimodal mode.")
    return {"compatible": not problems, "androidVersion": version,
            "pcDriverSha256": driver_hash, "fridaVersion": frida_version,
            "settings": {key: values.get(key) for key in required}, "settingSource": "current-user",
            "experimental": True, "runtimeValidated": False, "problems": problems, "warnings": warnings,
            "componentsReady": not component_problems, "componentProblems": component_problems}


class RootChannel:
    """One bounded root shell; its EXIT trap owns helper recovery after a drop."""
    def __init__(self, adb: Adb):
        self.process = subprocess.Popen([adb.executable, "-s", adb.target, "shell", "su"],
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, text=True,
                                        creationflags=NO_WINDOW, bufsize=1)
        self.lines = queue.Queue()
        threading.Thread(target=self._read, daemon=True).start()
        if "uid=0" not in self.command("id"):
            self.close()
            raise CompatibilityError("Magisk root access is required for experimental hands.")

    def _read(self):
        for line in self.process.stdout:
            self.lines.put(line.rstrip("\r\n"))
        self.lines.put(None)

    def command(self, command: str, timeout=12):
        marker = "QPRO_END_" + uuid.uuid4().hex
        self.process.stdin.write(command + "\nprintf '\\n" + marker + "=%s\\n' \"$?\"\n")
        self.process.stdin.flush()
        deadline = time.monotonic() + timeout
        output = []
        while time.monotonic() < deadline:
            try:
                line = self.lines.get(timeout=max(0.01, deadline - time.monotonic()))
            except queue.Empty:
                break
            if line is None:
                raise RuntimeError("The headset root shell closed.")
            if line.startswith(marker + "="):
                if line != marker + "=0":
                    raise RuntimeError("Headset helper command failed: " + "\n".join(output))
                return "\n".join(output)
            output.append(line)
        raise RuntimeError("The headset root command timed out.")

    def close(self):
        if self.process.poll() is None:
            try:
                self.process.stdin.write("exit\n")
                self.process.stdin.flush()
                self.process.wait(timeout=3)
            except (OSError, subprocess.TimeoutExpired):
                self.process.kill()
                self.process.wait(timeout=3)
        for stream in (self.process.stdin, self.process.stdout):
            try:
                stream.close()
            except OSError:
                pass


class Supervisor:
    def __init__(self, adb, frida, profile, server_path, stop, root_factory=RootChannel):
        self.adb, self.frida, self.profile = adb, frida, profile
        self.server_path, self.stop_requested = server_path, stop
        self.root_factory = root_factory
        self.root = None
        self.sessions, self.adapters = [], []
        self.active_scripts = set()
        self.forward_owned = self.helper_owned = False
        self.forward_port = None
        self.forward_uncertain = False
        self.helper_pid = None
        self.detached = False
        self.detached_sessions = []
        self.cleaning_up = False
        self.last_statuses = []
        self.progress = time.monotonic()
        self.finished = threading.Event()

    def _attach(self, device, process, label=None):
        session = device.attach(process)
        self.sessions.append(session)
        def on_detached(reason="unknown", crash=None, *_):
            self.detached = True
            # Frida's crash report can contain dumps and paths. Keep only a
            # short summary, without serials, reports, or local file paths.
            summary = getattr(crash, "summary", None)
            if isinstance(summary, str):
                summary = summary.splitlines()[0] if summary else ""
                summary = re.sub(r"(?:[A-Za-z]:[\\/]|\\\\|/).*", "[path omitted]", summary)
                summary = summary[:240]
            else:
                summary = None
            details = {"session": label or str(process), "process": process,
                       "reason": str(reason)[:80], "duringCleanup": self.cleaning_up,
                       "crashSummary": summary}
            self.detached_sessions.append(details)
            emit("DETACHED", **details)
        session.on("detached", on_detached)
        return session

    def _adapter(self, session, filename, name):
        source = "globalThis.QPRO_PROFILE = " + json.dumps(self.profile) + ";\n"
        source += (HERE / filename).read_text(encoding="utf-8")
        script = session.create_script(source)
        script.on("message", lambda message, _: emit("ADAPTER", name=name, message=message))
        self.adapters.append((name, script))
        try:
            script.load()
            facts = script.exports_sync.validate()
        except Exception as error:
            raise CompatibilityError(name + " adapter validation failed: " + str(error)) from error
        if facts.get("compatible") is not True:
            raise CompatibilityError(name + " adapter did not validate.")
        emit("STAGE", stage="validated", adapter=name, facts=facts)
        return script

    def _ensure_running(self):
        self.progress = time.monotonic()
        if self.stop_requested.is_set():
            raise InterruptedError("Stop requested.")
        if self.detached:
            raise RuntimeError("A tracking process disconnected; stopping the hand adapters.")

    def start(self):
        self._ensure_running()
        self.progress = time.monotonic() + 12
        self.root = self.root_factory(self.adb)
        self.progress = time.monotonic()
        existing = self.root.command("pidof qpro-hands-frida quest-hybrid-frida frida-server || true").strip()
        if existing:
            raise CompatibilityError("A Frida server is already running. Close the other Qpro Hub or turn off Singularity's Frida Server first.")
        listening = self.root.command("awk 'NR > 1 && $2 ~ /:69B6$/ && $4 == \"0A\" {print}' /proc/net/tcp /proc/net/tcp6").strip()
        if listening:
            raise CompatibilityError("The headset hand-helper port is already used by another process.")
        self._ensure_running()
        # Own cleanup before copying, so a partial transfer is also removed.
        trap = "if [ -n \"$qpro_hands_pid\" ] && [ \"$(readlink /proc/$qpro_hands_pid/exe)\" = '" + HELPER + "' ]; then kill \"$qpro_hands_pid\"; fi; rm -f '" + HELPER + "'"
        self.root.command("qpro_hands_pid=''; trap '" + trap.replace("'", "'\\''") + "' EXIT")
        self.helper_owned = True
        self.progress = time.monotonic() + 90
        self.adb.run("push", str(self.server_path), HELPER, timeout=90)
        self.progress = time.monotonic()
        self._ensure_running()
        local_hash = hashlib.sha256(self.server_path.read_bytes()).hexdigest()
        remote_hash = self.root.command("sha256sum " + HELPER).split()[0]
        if local_hash != remote_hash:
            raise RuntimeError("Headset hand helper hash check failed.")
        self.root.command("chmod 755 " + HELPER +
                          "; " + HELPER + " --listen 127.0.0.1:" + str(PORT) + " --disable-preload >/dev/null 2>&1 & qpro_hands_pid=$!; echo $qpro_hands_pid")
        self.helper_pid = self.root.command("echo $qpro_hands_pid").strip()
        if not self.helper_pid.isdigit():
            raise RuntimeError("The owned hand helper did not return a valid PID.")
        # Let ADB reserve a free PC port atomically. The headset helper keeps
        # its checked port; existing PC listeners and forwards are untouched.
        try:
            allocated = self.adb.run("forward", "--no-rebind", "tcp:0", "tcp:" + str(PORT))
        except subprocess.TimeoutExpired:
            self.forward_uncertain = True
            raise
        if not re.fullmatch(r"[0-9]{1,5}", allocated) or not 1 <= int(allocated) <= 65535:
            self.forward_uncertain = True
            raise RuntimeError("ADB did not report a valid allocated hand port; no unknown forward was removed.")
        self.forward_port = int(allocated)
        self.forward_owned = True
        emit("TRANSPORT", localPort=self.forward_port, headsetPort=PORT)
        device = self.frida.get_device_manager().add_remote_device("127.0.0.1:" + str(self.forward_port))
        for attempt in range(24):
            self._ensure_running()
            try:
                device.enumerate_processes()
                break
            except Exception:
                if attempt == 23:
                    raise
                self.stop_requested.wait(0.25)
        pids = self.adb.run("shell", "pidof", self.profile["androidPackage"]).split()
        if len(pids) != 1 or not pids[0].isdigit():
            raise CompatibilityError("Open Virtual Desktop on the headset and connect to this PC.")
        quest = self._attach(device, int(pids[0]), "headset-virtual-desktop")
        pc = self._attach(self.frida.get_local_device(), "vrserver.exe", "steamvr")
        native = self._adapter(quest, "quest_controller_adapter.js", "controller-selection")
        hands = self._adapter(quest, "quest_hand_adapter.js", "optical-hands")
        routing = self._adapter(pc, "steamvr_skeleton_adapter.js", "steamvr-skeleton")
        lease = self.profile["leaseSeconds"]
        for script in (native, hands, routing):
            self._ensure_running()
            # Include the adapter before calling activate: partial activation
            # must still be restored if its RPC fails midway through.
            self.active_scripts.add(id(script))
            script.exports_sync.activate(lease)
        emit("STAGE", stage="waiting-for-optical-hands", seconds=self.profile["readinessSeconds"])
        deadline = time.monotonic() + self.profile["readinessSeconds"]
        next_beat = 0.0
        ready = False
        while True:
            self._ensure_running()
            now = time.monotonic()
            if now >= next_beat:
                statuses = []
                for index, (name, script) in enumerate(self.adapters):
                    status = script.exports_sync.status()
                    # Save each observation before heartbeat/state checks, so
                    # a later RPC or cleanup error cannot hide the last facts.
                    if index < len(self.last_statuses):
                        self.last_statuses[index] = status
                    else:
                        self.last_statuses.append(status)
                    if status.get("state") not in ("starting", "running"):
                        raise RuntimeError(name + " stopped: " + str(status.get("error")))
                    script.exports_sync.heartbeat(lease)
                    statuses.append(status)
                if not ready:
                    ready = input_is_ready(statuses)
                    if ready:
                        emit("READY", activeSides=statuses[2]["activeSides"], experimental=True)
                emit("STATUS", ready=ready, adapters=statuses)
                if not ready and now >= deadline:
                    if statuses[0].get("queries", 0) <= 0:
                        raise RuntimeError("Hand routing confirmation timed out. No headset controller-selection queries were observed. Check the latest HANDS_STATUS adapter counters.")
                    if any(statuses[1].get("freshSides", [False, False])):
                        raise RuntimeError("Hand routing confirmation timed out. The headset is producing fresh optical hand data, but SteamVR skeleton routing for that data was not confirmed. Check the latest HANDS_STATUS adapter counters.")
                    raise RuntimeError("Hand routing confirmation timed out. No side had both fresh headset optical data and confirmed SteamVR skeleton routing. Wear the headset, hold a controller, and keep your fingers visible; check the latest HANDS_STATUS adapter counters.")
                next_beat = now + self.profile["heartbeatSeconds"]
            self.stop_requested.wait(0.1)

    def cleanup(self):
        self.cleaning_up = True
        failures = []
        if self.forward_uncertain:
            failures.append("ADB hand-forward allocation was ambiguous; no unidentified forward was removed.")
        for name, script in reversed(self.adapters):
            active = id(script) in self.active_scripts
            stage = "unload" if not active else "deactivate"
            acknowledged = False
            try:
                self.progress = time.monotonic()
                emit("CLEANUP_STAGE", adapter=name, stage=stage, active=active)
                if not active:
                    script.unload()
                    continue
                script.exports_sync.deactivate()
                stage = "restoration-status"
                emit("CLEANUP_STAGE", adapter=name, stage=stage, active=active)
                deadline = time.monotonic() + 6
                while time.monotonic() < deadline:
                    state = script.exports_sync.status()
                    if state.get("state") == "stopped":
                        acknowledged = True
                        break
                    if state.get("state") == "restore-failed":
                        raise CleanupError(str(state.get("error")))
                    time.sleep(0.1)
                else:
                    raise CleanupError("Restoration was not acknowledged.")
                stage = "unload"
                emit("CLEANUP_STAGE", adapter=name, stage=stage, active=active)
                script.unload()
            except Exception as error:
                failures.append(name + ": " + str(error))
                emit("CLEANUP_ADAPTER_FAILED", adapter=name, stage=stage,
                     active=active, restorationAcknowledged=acknowledged, error=str(error))
        for session in reversed(self.sessions):
            try:
                self.progress = time.monotonic()
                session.detach()
            except Exception as error:
                failures.append("Detach: " + str(error))
        if self.root is not None:
            try:
                self.progress = time.monotonic()
                if self.helper_owned:
                    self.root.command("if [ -n \"$qpro_hands_pid\" ] && [ \"$(readlink /proc/$qpro_hands_pid/exe)\" = '" + HELPER + "' ]; then kill \"$qpro_hands_pid\"; "
                                      "for qpro_wait in 1 2 3 4 5; do kill -0 \"$qpro_hands_pid\" 2>/dev/null || break; sleep 0.1; done; "
                                      "kill -0 \"$qpro_hands_pid\" 2>/dev/null && exit 6; fi; rm -f '" + HELPER + "'; test ! -e '" + HELPER + "'")
                self.root.close()
            except Exception as error:
                failures.append("Headset helper cleanup: " + str(error))
                try:
                    self.root.close()
                except Exception:
                    pass
        if self.forward_owned:
            try:
                current = self.adb.run("forward", "--list")
                expected = [self.adb.target, "tcp:" + str(self.forward_port), "tcp:" + str(PORT)]
                if any(line.split() == expected for line in current.splitlines()):
                    self.adb.run("forward", "--remove", "tcp:" + str(self.forward_port))
            except Exception as error:
                failures.append("Forward cleanup: " + str(error))
        emit("CLEANUP", confirmed=not failures, problems=failures)
        if failures:
            raise CleanupError("Restart headset Virtual Desktop and SteamVR; hand cleanup was not confirmed. " + "; ".join(failures))


def run_supervised(supervisor):
    def watchdog():
        while not supervisor.finished.wait(1):
            if time.monotonic() - supervisor.progress > 30:
                emit("CLEANUP_FAILED", error="The hand supervisor stopped responding. Adapter leases will expire; restart Virtual Desktop and SteamVR before retrying.")
                os._exit(5)
    threading.Thread(target=watchdog, daemon=True).start()
    try:
        supervisor.start()
    except Exception as error:
        if not isinstance(error, InterruptedError):
            emit("STOP_CAUSE", error=str(error), errorType=type(error).__name__,
                 adapters=supervisor.last_statuses, detached=supervisor.detached_sessions)
        raise
    finally:
        supervisor.progress = time.monotonic()
        try:
            supervisor.cleanup()
        finally:
            supervisor.finished.set()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True)
    parser.add_argument("--adb", required=True)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--frida-server", type=Path)
    parser.add_argument("--stop-file", type=Path)
    parser.add_argument("--parent-stdin", action="store_true",
                        help="Also stop on parent stdin EOF when a stop file is supplied.")
    args = parser.parse_args(argv)
    try:
        import frida
    except ImportError:
        frida = None
    profile = load_profile()
    adb = Adb(args.adb, args.target)
    supervisor = None
    try:
        if not args.check and args.stop_file is not None and args.stop_file.exists():
            raise RuntimeError("The stop file already exists; start a fresh Hub session before enabling hands.")
        report = inspect(adb, profile, getattr(frida, "__version__", None), args.frida_server,
                         require_components=not args.check)
        emit("CHECK", **report)
        if not report["compatible"]:
            return 4
        if args.check:
            return 0
        stop = threading.Event()
        signal.signal(signal.SIGINT, lambda *_: stop.set())
        signal.signal(signal.SIGTERM, lambda *_: stop.set())
        start_stop_watchers(stop, args.stop_file,
                            sys.stdin if args.stop_file is None or args.parent_stdin else None)
        supervisor = Supervisor(adb, frida, profile, args.frida_server, stop)
        run_supervised(supervisor)
        return 0
    except InterruptedError:
        return 0
    except CompatibilityError as error:
        emit("INCOMPATIBLE", error=str(error))
        return 4
    except CleanupError as error:
        emit("CLEANUP_FAILED", error=str(error))
        return 5
    except Exception as error:
        emit("FAILED", error=str(error))
        return 1
    finally:
        if not args.check and supervisor is None:
            emit("CLEANUP", confirmed=True, problems=[], phase="not-started")


if __name__ == "__main__":
    raise SystemExit(main())
