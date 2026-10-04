"""Offline lifecycle checks. No real ADB, Frida attachment, or headset writes."""
import contextlib
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import controller


class FakeAdb:
    def __init__(self, profile, rules=""):
        self.profile, self.rules, self.calls = profile, rules, []
        self.target = "serial"
        self.allocated_port = "43817"

    def run(self, *args, **kwargs):
        self.calls.append(args)
        if args[:3] == ("shell", "dumpsys", "package"):
            return "versionName=" + self.profile["androidVersion"]
        if "oculuspreferences" in " ".join(args):
            return "[hand_tracking_enabled : true]\n[multimodal_hands_and_controllers_enabled : true]\n[simultaneous_hands_and_controllers_mode : 1]"
        if args == ("forward", "--list"):
            return self.rules
        if args[:2] == ("forward", "--no-rebind"):
            if args[2] != "tcp:0":
                raise AssertionError("A fixed PC port must not be requested")
            self.rules += "\n" + self.target + " tcp:" + self.allocated_port + " " + args[3]
            return self.allocated_port
        if args[:2] == ("shell", "pidof"):
            return "100"
        return ""


class FakeRoot:
    def __init__(self, adb, server, existing="", listener=""):
        self.calls, self.closed = [], False
        self.server, self.existing, self.listener = server, existing, listener

    def command(self, text):
        self.calls.append(text)
        if text.startswith("pidof"):
            return self.existing
        if text.startswith("awk"):
            return self.listener
        if text.startswith("sha256sum"):
            return hashlib.sha256(self.server.read_bytes()).hexdigest() + "  " + controller.HELPER
        if text == "echo $qpro_hands_pid":
            return "200"
        return ""

    def close(self):
        self.closed = True


class FakeAdapter:
    def __init__(self, kind, events, fail_stop=False):
        self.kind, self.events, self.fail_stop = kind, events, fail_stop
        self.exports_sync = self
        self.state = "idle"

    def on(self, *args): pass
    def load(self): self.events.append(("load", self.kind))
    def validate(self): return {"compatible": True}
    def activate(self, lease):
        self.state = "running"; self.events.append(("activate", self.kind))
    def heartbeat(self, lease): self.events.append(("heartbeat", self.kind))
    def status(self):
        return {"state": self.state, "queries": 2, "validFrames": [1, 0],
                "freshSides": [True, False], "activeSides": [True, False]}
    def deactivate(self):
        self.events.append(("stop", self.kind))
        if self.fail_stop: raise RuntimeError("restore refused")
        self.state = "stopped"
    def unload(self): self.events.append(("unload", self.kind))


class FakeSession:
    def __init__(self, events): self.events = events
    def on(self, *args): pass
    def detach(self): self.events.append(("detach",))
    def create_script(self, source):
        kind = "optical" if "libmonosgen" in source else "native" if "ARM64 runtime" in source else "pc"
        return FakeAdapter(kind, self.events)


class FakeDevice:
    def __init__(self, events): self.events = events
    def attach(self, process):
        self.events.append(("attach", process)); return FakeSession(self.events)
    def enumerate_processes(self): return []


class HybridTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.home = Path(self.directory.name)
        self.server = self.home / "frida-server"; self.server.write_bytes(b"offline fixture")
        self.profile = controller.load_profile()
        driver = self.home / self.profile["pcDriverRelativePath"]
        driver.parent.mkdir(parents=True); driver.write_bytes(b"approved fixture")
        self.profile["pcDriverSha256"] = hashlib.sha256(driver.read_bytes()).hexdigest()

    def test_diagnostic_allows_uninstalled_components_without_mutation(self):
        adb = FakeAdb(self.profile)
        report = controller.inspect(adb, self.profile, None, None, str(self.home), require_components=False)
        self.assertTrue(report["compatible"])
        self.assertFalse(report["componentsReady"])
        self.assertEqual(len(report["componentProblems"]), 2)
        self.assertTrue(all(call[0] == "shell" for call in adb.calls))

    def test_tracking_requires_components(self):
        report = controller.inspect(FakeAdb(self.profile), self.profile, None, None, str(self.home))
        self.assertFalse(report["compatible"])

    def test_unknown_driver_and_headset_versions_are_refused(self):
        profile = dict(self.profile, pcDriverSha256="0" * 64)
        headset = dict(profile, androidVersion="unknown")
        report = controller.inspect(FakeAdb(headset), profile, "17.18.0", self.server, str(self.home))
        self.assertFalse(report["compatible"])
        self.assertEqual(len(report["problems"]), 2)

    def test_target_is_never_shell_code(self):
        for target in ("x;reboot", "x y", "$(bad)", ""):
            with self.assertRaises(controller.CompatibilityError): controller.Adb("adb", target)
        controller.Adb("adb", "192.168.1.2:5555")

    def make_supervisor(self, existing="", rules="", listener=""):
        events, stop = [], threading.Event()
        adb = FakeAdb(self.profile, rules)
        root = FakeRoot(adb, self.server, existing, listener)
        device = FakeDevice(events)
        def remote(address):
            events.append(("remote", address))
            return device
        frida = SimpleNamespace(get_local_device=lambda: device,
                                get_device_manager=lambda: SimpleNamespace(add_remote_device=remote))
        return controller.Supervisor(adb, frida, self.profile, self.server, stop,
                                     root_factory=lambda _: root), events, adb, root

    def test_ready_and_reverse_restoration_then_owned_forward_cleanup(self):
        supervisor, events, adb, root = self.make_supervisor()
        records = []
        def record(event, **values):
            records.append(event)
            if event == "READY": supervisor.stop_requested.set()
        with patch.object(controller, "emit", record):
            with self.assertRaises(InterruptedError): controller.run_supervised(supervisor)
        self.assertIn("READY", records)
        self.assertEqual([kind for action, *rest in events if action == "stop" for kind in rest], ["pc", "optical", "native"])
        self.assertTrue(root.closed)
        self.assertIn(("remote", "127.0.0.1:43817"), events)
        self.assertIn(("forward", "--remove", "tcp:43817"), adb.calls)
        self.assertTrue(any("readlink /proc/$qpro_hands_pid/exe" in text for text in root.calls))

    def test_foreign_helper_and_headset_listener_are_not_removed(self):
        cases = [dict(existing="99"), dict(listener="existing listener")]
        for values in cases:
            supervisor, _, adb, root = self.make_supervisor(**values)
            with patch.object(controller, "emit", lambda *_args, **_kwargs: None):
                with self.assertRaises(controller.CompatibilityError): controller.run_supervised(supervisor)
            self.assertTrue(root.closed)
            self.assertFalse(any(call[0] == "push" for call in adb.calls))
            self.assertNotIn(("forward", "--remove", "tcp:27062"), adb.calls)
            self.assertFalse(any("kill " in text for text in root.calls))

    def test_busy_fixed_pc_port_does_not_replace_foreign_forward(self):
        foreign = "other tcp:27062 tcp:1234"
        supervisor, events, adb, root = self.make_supervisor(rules=foreign)
        def record(event, **values):
            if event == "READY": supervisor.stop_requested.set()
        with patch.object(controller, "emit", record):
            with self.assertRaises(InterruptedError): controller.run_supervised(supervisor)
        self.assertIn(foreign, adb.rules)
        self.assertIn(("remote", "127.0.0.1:43817"), events)
        self.assertIn(("forward", "--remove", "tcp:43817"), adb.calls)
        self.assertNotIn(("forward", "--remove", "tcp:27062"), adb.calls)
        self.assertTrue(root.closed)

    def test_port_allocation_failure_still_cleans_the_owned_helper(self):
        supervisor, events, adb, root = self.make_supervisor()
        run = adb.run
        def fail_allocation(*args, **kwargs):
            if args[:2] == ("forward", "--no-rebind"):
                raise RuntimeError("cannot bind listener (10048)")
            return run(*args, **kwargs)
        records = []
        with patch.object(adb, "run", side_effect=fail_allocation), patch.object(controller, "emit", lambda event, **values: records.append((event, values))):
            with self.assertRaisesRegex(RuntimeError, "cannot bind listener"):
                controller.run_supervised(supervisor)
        self.assertTrue(root.closed)
        self.assertTrue(supervisor.helper_owned)
        self.assertFalse(supervisor.forward_owned)
        self.assertFalse(any(call[:2] == ("forward", "--remove") for call in adb.calls))
        self.assertFalse(any(event[0] == "attach" for event in events))
        self.assertIn(("CLEANUP", {"confirmed": True, "problems": []}), records)

    def test_ambiguous_allocation_does_not_claim_confirmed_cleanup(self):
        for response in ("", "port=43817", "0", "65536", "43817\n43818"):
            with self.subTest(response=response):
                supervisor, events, adb, root = self.make_supervisor()
                run = adb.run
                def malformed(*args, **kwargs):
                    result = run(*args, **kwargs)
                    return response if args[:2] == ("forward", "--no-rebind") else result
                with patch.object(adb, "run", side_effect=malformed), patch.object(controller, "emit"):
                    with self.assertRaisesRegex(controller.CleanupError, "allocation was ambiguous"):
                        controller.run_supervised(supervisor)
                self.assertTrue(root.closed)
                self.assertFalse(any(call[:2] == ("forward", "--remove") for call in adb.calls))
                self.assertFalse(any(event[0] == "attach" for event in events))

    def test_cleanup_failure_is_propagated(self):
        supervisor, _, _, root = self.make_supervisor()
        adapter = FakeAdapter("optical", [], fail_stop=True)
        supervisor.adapters = [("optical", adapter)]
        supervisor.active_scripts.add(id(adapter))
        with patch.object(controller, "emit", lambda *_args, **_kwargs: None):
            with self.assertRaises(controller.CleanupError): supervisor.cleanup()

    def test_unactivated_adapter_is_unloaded_without_restoration_rpc(self):
        supervisor, events, _, _ = self.make_supervisor()
        supervisor.adapters = [("optical", FakeAdapter("optical", events, fail_stop=True))]
        with patch.object(controller, "emit", lambda *_args, **_kwargs: None): supervisor.cleanup()
        self.assertEqual(events, [("unload", "optical")])

    def test_preexisting_stop_file_prevents_even_diagnostic_adb_reads(self):
        stop = self.home / "stop"; stop.write_text("already stopped")
        output = io.StringIO()
        with patch.object(controller, "inspect", side_effect=AssertionError("No ADB call expected")):
            with contextlib.redirect_stdout(output):
                code = controller.main(["--target", "serial", "--adb", "fixture", "--stop-file", str(stop), "--parent-stdin"])
        self.assertEqual(code, 1)
        self.assertIn('HANDS_CLEANUP {"confirmed": true, "problems": [], "phase": "not-started"}', output.getvalue())

    def test_incompatible_preflight_proves_no_adapters_started(self):
        for check in (False, True):
            with self.subTest(check=check):
                output = io.StringIO()
                arguments = ["--target", "serial", "--adb", "fixture"]
                if check: arguments.append("--check")
                with patch.object(controller, "inspect", return_value={"compatible": False}), contextlib.redirect_stdout(output):
                    code = controller.main(arguments)
                self.assertEqual(code, 4)
                self.assertEqual("HANDS_CLEANUP " in output.getvalue(), not check)

    def test_parent_eof_requests_stop_even_with_a_stop_file(self):
        stop = threading.Event()
        controller.start_stop_watchers(stop, self.home / "absent-stop", io.StringIO(""))
        self.assertTrue(stop.wait(1))

    def test_stale_or_opposite_side_frames_do_not_establish_readiness(self):
        native = {"queries": 1}
        self.assertFalse(controller.input_is_ready([native, {"validFrames": [5, 5]}, {"activeSides": [True, False]}]))
        self.assertFalse(controller.input_is_ready([native, {"freshSides": [False, True]}, {"activeSides": [True, False]}]))
        self.assertTrue(controller.input_is_ready([native, {"freshSides": [True, False]}, {"activeSides": [True, False]}]))

    def test_intervening_foreign_rebind_is_not_removed(self):
        supervisor, _, adb, root = self.make_supervisor()
        def record(event, **values):
            if event == "READY":
                adb.rules = "foreign tcp:43817 tcp:9000"
                supervisor.stop_requested.set()
        with patch.object(controller, "emit", record):
            with self.assertRaises(InterruptedError): controller.run_supervised(supervisor)
        self.assertTrue(root.closed)
        self.assertNotIn(("forward", "--remove", "tcp:43817"), adb.calls)


if __name__ == "__main__": unittest.main()
