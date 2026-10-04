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
        self.preference_output = ("[hand_tracking_enabled : true]\n"
                                  "[multimodal_hands_and_controllers_enabled : true]\n"
                                  "[simultaneous_hands_and_controllers_mode : 1]")

    def run(self, *args, **kwargs):
        self.calls.append(args)
        if args[:3] == ("shell", "dumpsys", "package"):
            return "versionName=" + self.profile["androidVersion"]
        if "oculuspreferences" in " ".join(args):
            return self.preference_output
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
    def __init__(self, events): self.events, self.callbacks = events, {}
    def on(self, name, callback): self.callbacks[name] = callback
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

    def inspect_preferences(self, values=None, output=None):
        adb = FakeAdb(self.profile)
        if output is not None:
            adb.preference_output = output
        elif values is not None:
            adb.preference_output = "\n".join("[" + name + " : " + value + "]" for name, value in values.items())
        report = controller.inspect(adb, self.profile, "17.18.0", self.server, str(self.home))
        preference_calls = [call for call in adb.calls if "oculuspreferences" in " ".join(call)]
        self.assertEqual(preference_calls, [("shell", "su -c 'oculuspreferences --getc hand_tracking_enabled "
                                              "multimodal_hands_and_controllers_enabled simultaneous_hands_and_controllers_mode'")])
        self.assertEqual(len(adb.calls), 2)
        self.assertTrue(all(call[0] == "shell" for call in adb.calls))
        self.assertEqual(report["settingSource"], "current-user")
        return report

    def enabled_preferences(self):
        return {"hand_tracking_enabled": "true", "multimodal_hands_and_controllers_enabled": "true",
                "simultaneous_hands_and_controllers_mode": "1"}

    def test_preferences_use_one_read_only_active_user_query(self):
        report = self.inspect_preferences()
        self.assertTrue(report["compatible"])
        self.assertEqual(report["problems"], [])
        self.assertEqual(report["warnings"], [])
        self.assertEqual(report["settings"], self.enabled_preferences())

    def test_each_inactive_boolean_is_an_explicit_live_readiness_warning(self):
        for name in ("hand_tracking_enabled", "multimodal_hands_and_controllers_enabled"):
            for inactive in ("false", "0"):
                with self.subTest(name=name, inactive=inactive):
                    values = self.enabled_preferences()
                    values[name] = inactive
                    report = self.inspect_preferences(values)
                    self.assertTrue(report["compatible"])
                    self.assertEqual(report["problems"], [])
                    self.assertEqual(report["warnings"], ["Headset reports " + name + " inactive before startup; "
                        "Qpro will check live optical input after requesting Virtual Desktop multimodal mode."])

    def test_mode_one_admits_inactive_multimodal_with_warning(self):
        values = self.enabled_preferences()
        values["multimodal_hands_and_controllers_enabled"] = "false"
        report = self.inspect_preferences(values)
        self.assertTrue(report["compatible"])
        self.assertTrue(report["componentsReady"])
        self.assertEqual(report["settings"]["hand_tracking_enabled"], "true")
        self.assertEqual(report["settings"]["simultaneous_hands_and_controllers_mode"], "1")
        self.assertEqual(report["problems"], [])
        self.assertEqual(len(report["warnings"]), 1)
        self.assertIn("multimodal_hands_and_controllers_enabled", report["warnings"][0])

    def test_mode_one_admits_both_inactive_booleans_with_two_warnings(self):
        values = self.enabled_preferences()
        values["hand_tracking_enabled"] = values["multimodal_hands_and_controllers_enabled"] = "false"
        report = self.inspect_preferences(values)
        self.assertTrue(report["compatible"])
        self.assertEqual(report["problems"], [])
        self.assertEqual(len(report["warnings"]), 2)
        for name, warning in zip(("hand_tracking_enabled", "multimodal_hands_and_controllers_enabled"), report["warnings"]):
            self.assertIn(name, warning)

    def test_mode_zero_is_refused_even_with_enabled_booleans(self):
        values = self.enabled_preferences()
        values["simultaneous_hands_and_controllers_mode"] = "0"
        report = self.inspect_preferences(values)
        self.assertFalse(report["compatible"])
        self.assertEqual(len(report["problems"]), 1)
        self.assertIn("current-user setting simultaneous_hands_and_controllers_mode must be 1", report["problems"][0])
        self.assertIn("Singularity, turn on Simultaneous Hands & Controllers", report["problems"][0])
        self.assertEqual(report["warnings"], [])

    def test_unsupported_mode_values_are_refused(self):
        for unsupported in ("true", "false", "2"):
            with self.subTest(value=unsupported):
                values = self.enabled_preferences()
                values["simultaneous_hands_and_controllers_mode"] = unsupported
                report = self.inspect_preferences(values)
                self.assertFalse(report["compatible"])
                self.assertEqual(len(report["problems"]), 1)
                self.assertIn("supported current-user value for simultaneous_hands_and_controllers_mode", report["problems"][0])
                self.assertEqual(report["warnings"], [])

    def test_missing_preferences_are_unverified_and_refused(self):
        for name in self.enabled_preferences():
            with self.subTest(name=name):
                values = self.enabled_preferences()
                del values[name]
                report = self.inspect_preferences(values)
                self.assertFalse(report["compatible"])
                self.assertIsNone(report["settings"][name])
                self.assertEqual(len(report["problems"]), 1)
                self.assertIn("supported current-user value for " + name, report["problems"][0])
                self.assertEqual(report["warnings"], [])

    def test_malformed_and_conflicting_preferences_are_refused(self):
        for name in self.enabled_preferences():
            for malformed in ("[" + name + " : unknown]", "[" + name + " = true]",
                              "[" + name + " : true", "[" + name + " : false]\n[" + name + " : true]",
                              "[" + name + " : 1]\n[" + name + " : 1]"):
                with self.subTest(name=name, malformed=malformed):
                    values = self.enabled_preferences()
                    del values[name]
                    output = "\n".join("[" + key + " : " + value + "]" for key, value in values.items())
                    report = self.inspect_preferences(output=output + "\n" + malformed)
                    self.assertFalse(report["compatible"])
                    self.assertEqual(len(report["problems"]), 1)
                    self.assertIn("supported current-user value for " + name, report["problems"][0])
                    self.assertEqual(report["warnings"], [])
        report = self.inspect_preferences(output="")
        self.assertFalse(report["compatible"])
        self.assertEqual(len(report["problems"]), 3)
        self.assertEqual(report["warnings"], [])

    def test_valid_value_with_malformed_duplicate_is_refused(self):
        for name in self.enabled_preferences():
            for malformed in ("[" + name + " = true]", "[" + name + " : true", "[ " + name):
                with self.subTest(name=name, malformed=malformed):
                    values = self.enabled_preferences()
                    if name != "simultaneous_hands_and_controllers_mode":
                        values[name] = "false"
                    output = "\n".join("[" + key + " : " + value + "]" for key, value in values.items())
                    report = self.inspect_preferences(output=output + "\n" + malformed)
                    self.assertFalse(report["compatible"])
                    self.assertEqual(len(report["problems"]), 1)
                    self.assertIn("supported current-user value for " + name, report["problems"][0])
                    self.assertEqual(report["warnings"], [])

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

    def test_readiness_timeout_records_fresh_optical_data_before_cleanup(self):
        supervisor, events, _, root = self.make_supervisor()
        supervisor.profile = dict(self.profile, readinessSeconds=0)
        original_status = FakeAdapter.status
        def without_pc_confirmation(adapter):
            state = original_status(adapter)
            state.update(validFrames=[3089, 3092], freshSides=[True, True],
                         activeSides=[False, False], skeletonSubmissions=[0, 0])
            return state
        records = []
        with patch.object(FakeAdapter, "status", without_pc_confirmation), patch.object(controller, "emit", lambda event, **values: records.append((event, values))):
            with self.assertRaisesRegex(RuntimeError, "headset is producing fresh optical hand data"):
                controller.run_supervised(supervisor)
        names = [name for name, _ in records]
        self.assertNotIn("READY", names)
        self.assertLess(names.index("STATUS"), names.index("STOP_CAUSE"))
        self.assertLess(names.index("STOP_CAUSE"), names.index("CLEANUP_STAGE"))
        cause = next(values for name, values in records if name == "STOP_CAUSE")
        self.assertEqual(cause["errorType"], "RuntimeError")
        self.assertEqual(cause["adapters"][1]["validFrames"], [3089, 3092])
        self.assertEqual(cause["adapters"][2]["skeletonSubmissions"], [0, 0])
        self.assertEqual(next(values for name, values in records if name == "CLEANUP"), {"confirmed": True, "problems": []})
        self.assertEqual([entry[1] for entry in events if entry[0] == "stop"], ["pc", "optical", "native"])
        self.assertTrue(root.closed)
        self.assertTrue(supervisor.finished.is_set())

    def test_destroyed_active_adapter_keeps_timeout_and_cleanup_unconfirmed(self):
        supervisor, _, _, _ = self.make_supervisor()
        adapter = FakeAdapter("pc", [])
        supervisor.adapters = [("steamvr-skeleton", adapter)]
        supervisor.active_scripts.add(id(adapter))
        session = supervisor._attach(FakeDevice([]), "vrserver.exe", "steamvr")
        records = []
        def start():
            supervisor.last_statuses = [{"queries": 6186}, {"freshSides": [True, True]},
                                        {"activeSides": [False, False], "skeletonSubmissions": [0, 0]}]
            raise RuntimeError("Hand routing confirmation timed out.")
        def destroyed():
            session.callbacks["detached"]("process-terminated", None)
            raise RuntimeError("script has been destroyed")
        with patch.object(supervisor, "start", start), patch.object(adapter, "deactivate", destroyed), patch.object(controller, "emit", lambda event, **values: records.append((event, values))):
            with self.assertRaises(controller.CleanupError) as caught:
                controller.run_supervised(supervisor)
        self.assertIn("Hand routing confirmation timed out", str(caught.exception.__context__))
        names = [name for name, _ in records]
        self.assertLess(names.index("STOP_CAUSE"), names.index("CLEANUP_STAGE"))
        self.assertLess(names.index("CLEANUP_STAGE"), names.index("DETACHED"))
        failure = next(values for name, values in records if name == "CLEANUP_ADAPTER_FAILED")
        self.assertEqual(failure["stage"], "deactivate")
        self.assertTrue(failure["active"])
        self.assertFalse(failure["restorationAcknowledged"])
        detached = next(values for name, values in records if name == "DETACHED")
        self.assertEqual(detached["session"], "steamvr")
        self.assertEqual(detached["process"], "vrserver.exe")
        self.assertEqual(detached["reason"], "process-terminated")
        self.assertTrue(detached["duringCleanup"])
        self.assertFalse(next(values for name, values in records if name == "CLEANUP")["confirmed"])
        self.assertTrue(supervisor.finished.is_set())

    def test_missing_controller_queries_are_reported_even_with_pc_routing(self):
        supervisor, _, _, _ = self.make_supervisor()
        supervisor.profile = dict(self.profile, readinessSeconds=0)
        original_status = FakeAdapter.status
        def no_queries(adapter):
            state = original_status(adapter)
            state["queries"] = 0
            return state
        with patch.object(FakeAdapter, "status", no_queries), patch.object(controller, "emit"):
            with self.assertRaisesRegex(RuntimeError, "No headset controller-selection queries were observed"):
                controller.run_supervised(supervisor)

    def test_cleanup_failure_exits_five_after_logging_the_start_error(self):
        supervisor, _, _, _ = self.make_supervisor()
        adapter = FakeAdapter("pc", [])
        supervisor.adapters = [("steamvr-skeleton", adapter)]
        supervisor.active_scripts.add(id(adapter))
        records = []
        with patch.object(controller, "inspect", return_value={"compatible": True}), patch.object(controller, "Supervisor", return_value=supervisor), patch.object(controller, "start_stop_watchers"), patch.object(controller.signal, "signal"), patch.object(supervisor, "start", side_effect=RuntimeError("original readiness timeout")), patch.object(adapter, "deactivate", side_effect=RuntimeError("script has been destroyed")), patch.object(controller, "emit", lambda event, **values: records.append((event, values))):
            code = controller.main(["--target", "serial", "--adb", "fixture",
                                    "--stop-file", str(self.home / "new-stop")])
        self.assertEqual(code, 5)
        cause = next(values for name, values in records if name == "STOP_CAUSE")
        self.assertEqual(cause["error"], "original readiness timeout")
        self.assertFalse(next(values for name, values in records if name == "CLEANUP")["confirmed"])
        self.assertIn("script has been destroyed", next(values for name, values in records if name == "CLEANUP_FAILED")["error"])

    def test_detachment_summary_is_bounded_and_excludes_report_and_paths(self):
        supervisor, _, _, _ = self.make_supervisor()
        session = supervisor._attach(FakeDevice([]), 100, "headset-virtual-desktop")
        records = []
        crash = SimpleNamespace(summary="Access violation " + "x" * 500 + "\nprivate detail", report="raw private dump")
        with patch.object(controller, "emit", lambda event, **values: records.append((event, values))):
            session.callbacks["detached"]("process-terminated", crash)
            crash.summary = "Access violation at C:\\synthetic-fixture\\secret.txt"
            session.callbacks["detached"]("process-terminated", crash)
        first, second = [values for name, values in records if name == "DETACHED"]
        self.assertLessEqual(len(first["crashSummary"]), 240)
        self.assertNotIn("private", json.dumps(first).lower())
        self.assertEqual(second["crashSummary"], "Access violation at [path omitted]")
        self.assertFalse(first["duringCleanup"])
        self.assertEqual(first["process"], 100)
        self.assertTrue(supervisor.detached)
        with self.assertRaisesRegex(RuntimeError, "disconnected"):
            supervisor._ensure_running()

    def test_destroyed_script_during_status_or_unload_stays_unconfirmed(self):
        for failed_stage in ("restoration-status", "unload"):
            with self.subTest(stage=failed_stage):
                supervisor, _, _, _ = self.make_supervisor()
                adapter = FakeAdapter("pc", [])
                supervisor.adapters = [("steamvr-skeleton", adapter)]
                supervisor.active_scripts.add(id(adapter))
                records = []
                method = "status" if failed_stage == "restoration-status" else "unload"
                with patch.object(adapter, method, side_effect=RuntimeError("script has been destroyed")), patch.object(controller, "emit", lambda event, **values: records.append((event, values))):
                    with self.assertRaisesRegex(controller.CleanupError, "script has been destroyed"):
                        supervisor.cleanup()
                failure = next(values for name, values in records if name == "CLEANUP_ADAPTER_FAILED")
                self.assertEqual(failure["stage"], failed_stage)
                self.assertEqual(failure["restorationAcknowledged"], failed_stage == "unload")
                self.assertFalse(next(values for name, values in records if name == "CLEANUP")["confirmed"])

    def test_latest_status_is_saved_before_a_heartbeat_failure(self):
        supervisor, _, _, _ = self.make_supervisor()
        records = []
        def failed_heartbeat(adapter, lease):
            if adapter.kind == "pc":
                raise RuntimeError("heartbeat script has been destroyed")
        with patch.object(FakeAdapter, "heartbeat", failed_heartbeat), patch.object(controller, "emit", lambda event, **values: records.append((event, values))):
            with self.assertRaisesRegex(RuntimeError, "heartbeat script has been destroyed"):
                controller.run_supervised(supervisor)
        cause = next(values for name, values in records if name == "STOP_CAUSE")
        self.assertEqual(len(cause["adapters"]), 3)
        self.assertEqual(cause["adapters"][2]["state"], "running")
        self.assertTrue(next(values for name, values in records if name == "CLEANUP")["confirmed"])

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
