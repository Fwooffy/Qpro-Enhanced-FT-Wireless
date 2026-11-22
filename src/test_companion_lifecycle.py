"""Receiver ownership tests without camera, headset or installed-runtime changes."""

import ast
import io
import os
import socket
import subprocess
import sys
import threading
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

from companion_lifecycle import CompanionLifetime, _WindowsProcessApi
import receiver


class CompanionLifetimeTests(unittest.TestCase):
    def api(self):
        api = mock.Mock()
        api.open.return_value = 42
        api.creation_filetime.return_value = 133000000000000001
        api.alive.return_value = True
        return api

    def test_standalone_never_opens_a_windows_handle(self):
        with mock.patch("companion_lifecycle._WindowsProcessApi") as api:
            owner = CompanionLifetime()
            self.assertFalse(owner.enabled)
            self.assertTrue(owner.alive())
            owner.close()
            self.assertTrue(owner.alive())
        api.assert_not_called()

    def test_identifiers_must_be_supplied_together_and_in_range(self):
        for pid, created in [(1, None), (None, 1), (0, 1), (-1, 1), (1, 0),
                             (2**32, 1), (1, 2**63)]:
            with self.subTest(pid=pid, created=created), self.assertRaises(ValueError):
                CompanionLifetime(pid, created, _api=self.api())

    def test_handle_is_retained_and_closed_exactly_once(self):
        api = self.api()
        owner = CompanionLifetime(987, api.creation_filetime.return_value, _api=api)
        self.assertTrue(owner.alive())
        api.alive.return_value = False
        self.assertFalse(owner.alive())
        api.open.assert_called_once_with(987)
        api.creation_filetime.assert_called_once_with(42)
        self.assertEqual(api.alive.call_args_list, [mock.call(42), mock.call(42)])
        owner.close()
        owner.close()
        api.close.assert_called_once_with(42)
        self.assertFalse(owner.alive())

    def test_recycled_pid_is_rejected_and_handle_closed(self):
        api = self.api()
        with self.assertRaisesRegex(OSError, "identity changed"):
            CompanionLifetime(987, api.creation_filetime.return_value + 1, _api=api)
        api.close.assert_called_once_with(42)

    def test_creation_query_failure_closes_handle(self):
        api = self.api()
        api.creation_filetime.side_effect = OSError("access denied")
        with self.assertRaisesRegex(OSError, "access denied"):
            CompanionLifetime(987, 133000000000000001, _api=api)
        api.close.assert_called_once_with(42)

    def test_owner_already_gone_is_not_alive(self):
        api = self.api()
        api.open.return_value = None
        owner = CompanionLifetime(987, 133000000000000001, _api=api)
        self.assertFalse(owner.alive())
        owner.close()
        api.creation_filetime.assert_not_called()
        api.close.assert_not_called()

    def test_wait_failure_is_not_treated_as_live(self):
        api = self.api()
        owner = CompanionLifetime(987, api.creation_filetime.return_value, _api=api)
        api.alive.side_effect = OSError("invalid handle")
        try:
            with self.assertRaisesRegex(OSError, "invalid handle"):
                owner.alive()
        finally:
            owner.close()
        api.close.assert_called_once_with(42)

    @unittest.skipUnless(os.name == "nt", "Windows process handles")
    def test_real_child_exit_signals_the_retained_handle(self):
        child = subprocess.Popen(
            [sys.executable, "-c", "import time; time.sleep(30)"],
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        owner = None
        try:
            api = _WindowsProcessApi()
            handle = api.open(child.pid)
            try:
                created = api.creation_filetime(handle)
            finally:
                api.close(handle)
            owner = CompanionLifetime(child.pid, created)
            self.assertTrue(owner.alive())
            child.terminate()
            child.wait(timeout=5)
            self.assertFalse(owner.alive())
        finally:
            if owner is not None:
                owner.close()
            if child.poll() is None:
                child.terminate()
                child.wait(timeout=5)


class ReceiverOwnerTests(unittest.TestCase):
    def owner(self):
        owner = mock.Mock(enabled=True)
        owner.alive.return_value = True
        return owner

    def test_stalled_socket_stops_when_owner_exits(self):
        owner = self.owner()
        connection = mock.Mock()

        def timeout(_view):
            owner.alive.return_value = False
            raise socket.timeout()

        connection.recv_into.side_effect = timeout
        with self.assertRaisesRegex(receiver.StopRequested, "Companion closed"):
            receiver.receive_exact(connection, 12, companion=owner)
        connection.recv_into.assert_called_once()

    def test_partial_and_final_reads_check_owner_before_returning(self):
        for size in (1, 2):
            with self.subTest(size=size):
                owner = self.owner()
                connection = mock.Mock()

                def read(view):
                    view[0] = 1
                    owner.alive.return_value = False
                    return 1

                connection.recv_into.side_effect = read
                with self.assertRaises(receiver.StopRequested):
                    receiver.receive_exact(connection, size, companion=owner)
                connection.recv_into.assert_called_once()

    def test_standalone_socket_timeout_is_not_swallowed(self):
        connection = mock.Mock()
        connection.recv_into.side_effect = socket.timeout()
        with self.assertRaises(socket.timeout):
            receiver.receive_exact(connection, 12, companion=CompanionLifetime())

    def test_stop_file_still_stops_standalone_capture(self):
        stop_file = mock.Mock(spec=Path)
        stop_file.exists.return_value = True
        connection = mock.Mock()
        with self.assertRaises(receiver.StopRequested):
            receiver.receive_exact(connection, 12, stop_file)
        connection.recv_into.assert_not_called()

    def test_incomplete_owner_arguments_fail_before_starting_services(self):
        with (mock.patch.object(sys, "argv", ["receiver.py", "--companion-pid", "42"]),
              mock.patch.object(receiver, "start_mjpeg_server") as server,
              redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error):
            receiver.main()
        self.assertEqual(error.exception.code, 2)
        server.assert_not_called()

    def run_main(self, owner, connection_factory, *, camera_outputs=False, configure_cleanup=None):
        import pupil_dilation
        import pupil_inference
        import tongue_model_preview

        arguments = ["receiver.py", "--no-window", "--companion-pid", "42",
                     "--companion-start-filetime", "133000000000000001"]
        if camera_outputs:
            arguments.extend(["--tongue-model", "unused-test-model.pt", "--tongue-output",
                              "--pupil-output"])
        labels = mock.Mock(sample_count=0)
        preview = SimpleNamespace(device=SimpleNamespace(type="cpu"),
                                  checkpoint_path="unused-test-model.pt",
                                  direction_checkpoint_path=None,
                                  target_names=["visibility", "extension"])
        pupil = SimpleNamespace(backend="cpu", device="cpu", device_name="CPU", backend_notice="")
        server = mock.Mock()
        output = io.StringIO()
        with (redirect_stdout(output),
              mock.patch.object(sys, "argv", arguments),
              mock.patch.object(receiver, "CompanionLifetime", return_value=owner),
              mock.patch.object(receiver, "start_mjpeg_server", return_value=server),
              mock.patch.object(receiver.threading.Thread, "start"),
              mock.patch.object(receiver, "LabelSidecarRecorder", return_value=labels),
              mock.patch.object(receiver.socket, "create_connection", side_effect=connection_factory) as connect,
              mock.patch.object(receiver.time, "sleep"),
              mock.patch.object(tongue_model_preview, "LiveTongueModelPreview", return_value=preview),
              mock.patch.object(tongue_model_preview, "TongueBroadcaster") as tongue,
              mock.patch.object(tongue_model_preview, "TongueInferenceWorker") as inference,
              mock.patch.object(pupil_dilation, "RelativePupilTracker", return_value=pupil),
              mock.patch.object(pupil_dilation, "PupilBroadcaster"),
              mock.patch.object(pupil_inference, "PupilInferenceWorker") as pupil_worker):
            if configure_cleanup is not None:
                configure_cleanup(server, tongue.return_value, inference.return_value,
                                  pupil_worker.return_value, labels)
            self.assertEqual(receiver.main(), 0)
        owner.close.assert_called_once()
        server.shutdown.assert_called_once()
        server.server_close.assert_called_once()
        self.assertIn("STOP_REQUESTED Companion closed", output.getvalue())
        if camera_outputs:
            tongue.return_value.close.assert_called_once()
            inference.return_value.close.assert_called_once()
            pupil_worker.return_value.close.assert_called_once()
            labels.close.assert_called_once()
        return connect

    def test_owner_exits_before_startup(self):
        owner = self.owner()
        owner.alive.return_value = False
        connect = self.run_main(owner, AssertionError("must not connect"))
        connect.assert_not_called()

    def test_owner_exits_during_connection_retries(self):
        owner = self.owner()

        def refused(*_args, **_kwargs):
            owner.alive.return_value = False
            raise ConnectionRefusedError()

        connect = self.run_main(owner, refused, camera_outputs=True)
        connect.assert_called_once_with(("127.0.0.1", 27273), timeout=0.25)

    def test_owner_exits_during_frame_read_closes_all_outputs(self):
        owner = self.owner()
        connection = mock.Mock()

        def timeout(_view):
            owner.alive.return_value = False
            raise socket.timeout()

        connection.recv_into.side_effect = timeout
        self.run_main(owner, lambda *_args, **_kwargs: connection, camera_outputs=True)
        connection.settimeout.assert_called_once_with(0.25)
        connection.close.assert_called_once()

    def test_final_overrides_precede_paused_tongue_join_and_http_shutdown(self):
        owner = self.owner()
        events = []
        paused = threading.Event()
        release = threading.Event()
        http_paused = threading.Event()
        http_release = threading.Event()
        errors = []

        def configure(server, tongue, inference, pupil, labels):
            tongue.close.side_effect = lambda: events.append("tongue final invalid")
            pupil.close.side_effect = lambda: events.append("pupil final invalid")

            def slow_join():
                events.append("tongue join")
                paused.set()
                if not release.wait(5):
                    raise AssertionError("test cleanup was not released")

            inference.close.side_effect = slow_join
            def slow_http_shutdown():
                events.append("HTTP shutdown")
                http_paused.set()
                if not http_release.wait(5):
                    raise AssertionError("test HTTP cleanup was not released")

            server.shutdown.side_effect = slow_http_shutdown
            labels.close.side_effect = lambda: events.append("labels finalized")

        def run():
            try:
                def refused(*_args, **_kwargs):
                    owner.alive.return_value = False
                    raise ConnectionRefusedError()
                self.run_main(owner, refused,
                              camera_outputs=True, configure_cleanup=configure)
            except BaseException as error:
                errors.append(error)

        runner = threading.Thread(target=run)
        runner.start()
        try:
            self.assertTrue(paused.wait(5), "cleanup never reached paused join")
            self.assertEqual(events, ["tongue final invalid", "pupil final invalid", "tongue join"])
        finally:
            release.set()
        try:
            self.assertTrue(http_paused.wait(5), "cleanup never reached paused HTTP shutdown")
            self.assertEqual(events, ["tongue final invalid", "pupil final invalid", "tongue join", "HTTP shutdown"])
        finally:
            http_release.set()
            runner.join(10)
        self.assertFalse(runner.is_alive())
        if errors:
            raise errors[0]
        self.assertEqual(events[-2:], ["HTTP shutdown", "labels finalized"])

    def test_http_cleanup_failure_preserves_primary_error_and_finalizes_labels(self):
        events = []

        def configure(server, tongue, inference, pupil, labels):
            tongue.close.side_effect = lambda: events.append("tongue restored")
            pupil.close.side_effect = lambda: events.append("pupil restored")
            server.shutdown.side_effect = RuntimeError("HTTP cleanup failed")
            labels.close.side_effect = lambda: events.append("labels finalized")

        with redirect_stderr(io.StringIO()), self.assertRaisesRegex(ValueError, "original failure"):
            self.run_main(self.owner(), ValueError("original failure"),
                          camera_outputs=True, configure_cleanup=configure)
        self.assertEqual(events, ["tongue restored", "pupil restored", "labels finalized"])

    def test_failed_finalizers_never_report_capture_or_labels_complete(self):
        tree = ast.parse(Path(receiver.__file__).read_text(encoding="utf-8"))
        main = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "main")
        teardown = next(node for node in main.body if isinstance(node, ast.Try) and node.finalbody)
        failures = [RuntimeError(name) for name in ("stereo", "calibration", "tongue", "capture", "labels")]
        stereo, calibration, tongue, capture, labels = [mock.Mock() for _ in range(5)]
        for item, method, error in zip((stereo, calibration, tongue, capture, labels),
                                       ("close", "finish", "finish", "close", "close"), failures):
            getattr(item, method).side_effect = error
        labels.sample_count = 1
        labels.schema_names = ["test"]
        state = dict(sys=sys, time=receiver.time, shared=SimpleNamespace(running=True, lock=threading.Condition()),
                     tongue_broadcaster=None, pupil_worker=None, pupil_broadcaster=None,
                     tongue_inference_worker=None, companion=None, connection=None, server=mock.Mock(),
                     window_created=False, stereo_eye_calibration=stereo, calibration_session=calibration,
                     tongue_still_session=tongue, capture_writer=capture, label_recorder=labels,
                     capture_completed=True, calibration_completed=True)
        output, errors = io.StringIO(), io.StringIO()
        with redirect_stdout(output), redirect_stderr(errors), self.assertRaises(RuntimeError) as failure:
            exec(compile(ast.Module(body=teardown.finalbody, type_ignores=[]), receiver.__file__, "exec"), state)
        self.assertIs(failure.exception, failures[0])
        self.assertEqual(output.getvalue(), "")
        self.assertEqual(errors.getvalue().count("CLEANUP_FAILED"), 5)
        capture.close.assert_called_once_with(completed=True)
        labels.close.assert_called_once()


if __name__ == "__main__":
    unittest.main()
