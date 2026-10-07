#!/usr/bin/env python3
"""Receive Quest Pro inward-camera frames over an ADB-forwarded socket."""

from __future__ import annotations

import argparse
import http.server
import socket
import struct
import sys
import threading
import time
import zlib
from collections import deque
from dataclasses import dataclass, field
from pathlib import Path

import cv2
import numpy as np

from capture_format import CaptureWriter
from calibration import CalibrationSession, STEPS, play_cue, prompt_image
from tongue_calibration import TONGUE_STEPS
from tongue_still_capture import (
    TONGUE_ARC_PROMPTS,
    TONGUE_CORRECTION_PROMPTS,
    TongueStillCaptureSession,
)
from label_capture import LabelSidecarRecorder
from companion_lifecycle import CompanionLifetime


HEADER = struct.Struct("<8sIIQQIIIIIIQ")
MAGIC = b"QPLIVE3\0"
CAMERA_WIDTH = 400
CAMERA_HEIGHT = 400
CAMERA_NAMES = {
    0: "left eye",
    1: "right eye",
    2: "left face",
    3: "right face",
    4: "center eyebrow",
}


class StopRequested(Exception):
    """The session was stopped, or its owning Companion process exited."""


def check_stop_requested(stop_file: Path | None, companion: CompanionLifetime | None = None) -> None:
    if companion is not None and not companion.alive():
        raise StopRequested("Companion closed; camera preview closing")
    if stop_file is not None and stop_file.exists():
        raise StopRequested("camera preview closing")


def receive_exact(
    connection: socket.socket, size: int, stop_file: Path | None = None,
    companion: CompanionLifetime | None = None,
) -> bytes:
    output = bytearray(size)
    view = memoryview(output)
    received = 0
    while received < size:
        check_stop_requested(stop_file, companion)
        try:
            amount = connection.recv_into(view[received:])
        except socket.timeout:
            if stop_file is None and (companion is None or not companion.enabled):
                raise
            continue
        if amount == 0:
            raise ConnectionError("The headset streamer disconnected")
        received += amount
    check_stop_requested(stop_file, companion)
    return bytes(output)


def camera_ids_from_mask(mask: int) -> list[int]:
    return [camera_id for camera_id in range(5) if mask & (1 << camera_id)]


def stereo_mouth_strip(strip: np.ndarray, camera_ids: list[int]) -> np.ndarray:
    """Select cameras 2 and 3 even when the stream also carries both eyes."""
    if 2 not in camera_ids or 3 not in camera_ids:
        raise ValueError("Tongue output requires both face cameras (2 and 3)")
    panels = [
        strip[:, camera_ids.index(camera_id) * CAMERA_WIDTH:
              (camera_ids.index(camera_id) + 1) * CAMERA_WIDTH]
        for camera_id in (2, 3)
    ]
    return np.concatenate(panels, axis=1)


@dataclass
class StreamGapStats:
    """Recent source and PC-arrival cadence for diagnosing visible pauses."""

    source_ms: deque[float] = field(default_factory=lambda: deque(maxlen=240))
    arrival_ms: deque[float] = field(default_factory=lambda: deque(maxlen=240))
    clock_offsets_ns: deque[int] = field(default_factory=lambda: deque(maxlen=240))
    last_source_ns: int | None = None
    last_arrival_ns: int | None = None

    def add(self, source_ns: int, arrival_ns: int) -> None:
        if self.last_source_ns is not None and source_ns > self.last_source_ns:
            self.source_ms.append((source_ns - self.last_source_ns) / 1_000_000.0)
        if self.last_arrival_ns is not None and arrival_ns > self.last_arrival_ns:
            self.arrival_ms.append((arrival_ns - self.last_arrival_ns) / 1_000_000.0)
        self.last_source_ns = source_ns
        self.last_arrival_ns = arrival_ns
        if source_ns > 0:
            self.clock_offsets_ns.append(arrival_ns - source_ns)

    @staticmethod
    def _summary(values: deque[float]) -> tuple[float, float]:
        if not values:
            return 0.0, 0.0
        samples = np.asarray(values, dtype=np.float64)
        return float(np.percentile(samples, 95)), float(np.max(samples))

    def summaries(self) -> tuple[tuple[float, float], tuple[float, float]]:
        return self._summary(self.source_ms), self._summary(self.arrival_ms)

    def extra_queue_p95_ms(self) -> float:
        """Relative transport backlog above the best recent arrival, not RTT."""
        if len(self.clock_offsets_ns) < 2:
            return 0.0
        baseline = min(self.clock_offsets_ns)
        extras = [(value - baseline) / 1_000_000.0 for value in self.clock_offsets_ns]
        return float(np.percentile(extras, 95))


@dataclass
class FrameReplayStats:
    """Detect exact nonconsecutive payloads resurfacing from a DMA ring."""

    recent: deque[int] = field(default_factory=lambda: deque(maxlen=12))
    suspected_replays: int = 0

    def add(self, payload: bytes) -> bool:
        fingerprint = zlib.crc32(payload)
        replayed = bool(
            len(self.recent) >= 2
            and fingerprint != self.recent[-1]
            and fingerprint in list(self.recent)[:-1]
        )
        if replayed:
            self.suspected_replays += 1
        self.recent.append(fingerprint)
        return replayed


@dataclass
class SharedPreview:
    lock: threading.Condition = field(default_factory=threading.Condition)
    jpegs: dict[str, bytes] = field(default_factory=dict)
    generation: int = 0
    selected: int | str = 3
    available: list[int] = field(default_factory=list)
    calibration_keys: list[str] = field(default_factory=list)
    runtime_keys: list[str] = field(default_factory=list)
    running: bool = True
    subscribers: dict[str, int] = field(default_factory=dict)

    def subscribe(self, requested: str) -> None:
        with self.lock:
            self.subscribers[requested] = self.subscribers.get(requested, 0) + 1

    def unsubscribe(self, requested: str) -> None:
        with self.lock:
            remaining = self.subscribers.get(requested, 0) - 1
            if remaining > 0:
                self.subscribers[requested] = remaining
            else:
                self.subscribers.pop(requested, None)

    def select(self, choice: int | str) -> bool:
        with self.lock:
            if choice != "strip" and choice not in self.available:
                return False
            self.selected = choice
            self.lock.notify_all()
            return True

    def update(self, strip: np.ndarray, camera_ids: list[int]) -> None:
        # Tracking uses the raw strip. JPEG copies are only for open HTTP
        # previews, so a hidden/no-client preview must not encode every camera.
        with self.lock:
            self.available = list(camera_ids)
            if self.selected != "strip" and self.selected not in camera_ids:
                self.selected = camera_ids[0]
            requested = {
                self.selected_key() if key == "selected" else key
                for key in self.subscribers
            }
        encoded: dict[str, bytes] = {}
        for index, camera_id in enumerate(camera_ids):
            key = f"camera{camera_id}"
            if key not in requested:
                continue
            image = strip[:, index * CAMERA_WIDTH : (index + 1) * CAMERA_WIDTH]
            ok, jpeg = cv2.imencode(
                ".jpg", image, [cv2.IMWRITE_JPEG_QUALITY, 85]
            )
            if ok:
                encoded[key] = jpeg.tobytes()
        if "strip" in requested:
            ok, jpeg = cv2.imencode(".jpg", strip, [cv2.IMWRITE_JPEG_QUALITY, 85])
            if ok:
                encoded["strip"] = jpeg.tobytes()
        with self.lock:
            self.jpegs = encoded
            self.generation += 1
            self.lock.notify_all()

    def selected_key(self) -> str:
        return "strip" if self.selected == "strip" else f"camera{self.selected}"

    def queue_calibration_key(self, key: str) -> None:
        with self.lock:
            self.calibration_keys.append(key)

    def take_calibration_keys(self) -> list[str]:
        with self.lock:
            keys = self.calibration_keys
            self.calibration_keys = []
            return keys

    def queue_runtime_key(self, key: str) -> None:
        with self.lock:
            self.runtime_keys.append(key)

    def take_runtime_keys(self) -> list[str]:
        with self.lock:
            keys = self.runtime_keys
            self.runtime_keys = []
            return keys


class MjpegHandler(http.server.BaseHTTPRequestHandler):
    shared: SharedPreview

    def do_GET(self) -> None:  # noqa: N802
        if self.path in ("/", "/selected.mjpg"):
            requested = "selected"
        elif self.path == "/strip.mjpg":
            requested = "strip"
        elif self.path.startswith("/camera") and self.path.endswith(".mjpg"):
            requested = self.path[1:-5]
            if requested not in {f"camera{i}" for i in range(5)}:
                self.send_error(404)
                return
        else:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
        self.send_header("Pragma", "no-cache")
        self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=frame")
        self.end_headers()
        generation = -1
        self.shared.subscribe(requested)
        try:
            while self.shared.running:
                with self.shared.lock:
                    self.shared.lock.wait_for(
                        lambda: not self.shared.running
                        or (self.shared.generation != generation
                            and bool(self.shared.jpegs)),
                        timeout=1.0,
                    )
                    if not self.shared.running:
                        break
                    key = (self.shared.selected_key()
                           if requested == "selected" else requested)
                    jpeg = self.shared.jpegs.get(key)
                    generation = self.shared.generation
                if jpeg is None:
                    continue
                self.wfile.write(b"--frame\r\nContent-Type: image/jpeg\r\n")
                self.wfile.write(
                    f"Content-Length: {len(jpeg)}\r\n\r\n".encode("ascii")
                )
                self.wfile.write(jpeg)
                self.wfile.write(b"\r\n")
        except (BrokenPipeError, ConnectionResetError):
            pass
        finally:
            self.shared.unsubscribe(requested)

    def log_message(self, _format: str, *_args: object) -> None:
        return


def start_mjpeg_server(
    shared: SharedPreview, port: int
) -> http.server.ThreadingHTTPServer:
    handler = type("QuestProMjpegHandler", (MjpegHandler,), {"shared": shared})
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), handler)
    threading.Thread(
        target=server.serve_forever, name="mjpeg-server", daemon=True
    ).start()
    return server


def handle_key(shared: SharedPreview, key: str) -> None:
    key = key.lower()
    if key in ("0", "1", "2", "3", "4"):
        shared.select(int(key))
    elif key == "s":
        shared.select("strip")
    elif key in ("q", "\x1b"):
        shared.running = False
    elif key == "t":
        shared.queue_runtime_key(key)
    elif key in (" ", "\r", "\n", "r", "b", "k", "x", "\x08"):
        shared.queue_calibration_key(key)


def console_keyboard_worker(shared: SharedPreview) -> None:
    if sys.platform != "win32":
        return
    import msvcrt

    while shared.running:
        if msvcrt.kbhit():
            handle_key(shared, msvcrt.getwch())
        else:
            time.sleep(0.02)


def label_strip(
    strip: np.ndarray,
    camera_ids: list[int],
    fps: float,
    selected: int | str,
    sequence_gap: int,
    rejected_torn: int,
    throughput_mbps: float,
    captured_frames: int,
    label_samples: int,
    suspected_replays: int = 0,
    source_gap_ms: tuple[float, float] = (0.0, 0.0),
    arrival_gap_ms: tuple[float, float] = (0.0, 0.0),
    pupil_detections: tuple[object | None, object | None] | None = None,
    pupil_values: tuple[float | None, float | None] | None = None,
) -> np.ndarray:
    display = cv2.cvtColor(strip, cv2.COLOR_GRAY2BGR)
    for index, camera_id in enumerate(camera_ids):
        x = index * CAMERA_WIDTH
        cv2.putText(
            display,
            f"camera {camera_id} - {CAMERA_NAMES[camera_id]}",
            (x + 10, 25),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.55,
            (60, 255, 60),
            1,
            cv2.LINE_AA,
        )
        color = (0, 255, 255) if selected == camera_id else (180, 180, 180)
        thickness = 4 if selected == camera_id else 1
        cv2.rectangle(
            display,
            (x + 1, 1),
            (x + CAMERA_WIDTH - 2, CAMERA_HEIGHT - 2),
            color,
            thickness,
        )
        if camera_id in (0, 1) and pupil_detections is not None:
            detection = pupil_detections[camera_id]
            value = pupil_values[camera_id] if pupil_values else None
            if detection is not None:
                center = (x + round(detection.center[0]), round(detection.center[1]))
                cv2.circle(display, center, round(detection.diameter_px / 2), (70, 240, 110), 2)
                cv2.circle(display, center, 2, (70, 240, 110), -1)
            caption = f"relative pupil {value:.2f}" if value is not None else "pupil warming/invalid"
            cv2.putText(display, caption, (x + 10, 49), cv2.FONT_HERSHEY_SIMPLEX,
                        0.48, (70, 240, 110) if value is not None else (90, 200, 255),
                        1, cv2.LINE_AA)
    if selected == "strip":
        cv2.rectangle(
            display, (1, 1), (display.shape[1] - 2, CAMERA_HEIGHT - 2),
            (0, 255, 255), 4
        )
    status = (
        f"{fps:4.1f} FPS | {throughput_mbps:4.1f} MB/s | selected: {selected} | "
        f"source skips: {sequence_gap} | torn rejected: {rejected_torn} | "
        f"exact replays: {suspected_replays} | "
        + (f"REC {captured_frames} | " if captured_frames else "")
        + (f"LBL {label_samples} | " if captured_frames else "")
        + "keys 0-4/S, click, Q quits"
    )
    cadence = (
        f"cadence p95/max: headset {source_gap_ms[0]:.0f}/{source_gap_ms[1]:.0f} ms | "
        f"PC receive {arrival_gap_ms[0]:.0f}/{arrival_gap_ms[1]:.0f} ms"
    )
    cv2.putText(
        display,
        cadence,
        (10, 371),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.43,
        (150, 205, 255),
        1,
        cv2.LINE_AA,
    )
    cv2.putText(
        display,
        status,
        (10, 392),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.48,
        (80, 220, 255),
        1,
        cv2.LINE_AA,
    )
    max_width = 1500
    if display.shape[1] > max_width:
        scale = max_width / display.shape[1]
        display = cv2.resize(
            display, (max_width, round(display.shape[0] * scale)),
            interpolation=cv2.INTER_AREA
        )
    return display


def main() -> int:
    # These small camera operations share a process with neural inference and
    # the VR apps. OpenCV's full-machine worker pool adds contention here.
    cv2.setNumThreads(1)
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=27273)
    parser.add_argument("--mjpeg-port", type=int, default=8081)
    parser.add_argument("--no-window", action="store_true")
    parser.add_argument(
        "--record",
        nargs="?",
        const="auto",
        help="losslessly record synchronized raw frames to a .qpcap file",
    )
    parser.add_argument(
        "--record-seconds",
        type=float,
        default=0.0,
        help="stop after this many seconds of recorded frames (0 is unlimited)",
    )
    parser.add_argument("--labels-port", type=int, default=27274)
    parser.add_argument("--no-labels", action="store_true")
    parser.add_argument("--calibration", action="store_true")
    parser.add_argument("--tongue-calibration", action="store_true")
    parser.add_argument("--tongue-still-calibration", action="store_true")
    parser.add_argument("--tongue-correction-calibration", action="store_true")
    parser.add_argument("--tongue-refinement-calibration", action="store_true")
    parser.add_argument("--tongue-arc-calibration", action="store_true")
    parser.add_argument("--cheek-still-calibration", action="store_true")
    parser.add_argument("--cheek-output", action="store_true")
    parser.add_argument("--model")
    parser.add_argument("--model-device", default="auto")
    parser.add_argument("--tongue-model")
    parser.add_argument("--tongue-direction-model")
    parser.add_argument("--tongue-model-device", default="auto")
    parser.add_argument("--tongue-smoothing", type=int, default=55)
    parser.add_argument(
        "--tongue-visibility-mode",
        choices=("camera", "native", "weighted", "agreement"),
        default="weighted",
    )
    parser.add_argument(
        "--tongue-output",
        action="store_true",
        help="start the experimental tongue-only VRCFT override enabled",
    )
    parser.add_argument(
        "--pupil-output",
        action="store_true",
        help="send opt-in relative pupil-size estimates from both eye cameras to VRCFT",
    )
    parser.add_argument(
        "--pupil-sensitivity",
        type=float,
        default=1.4,
        help="relative pupil animation response from 1.0 to 3.0 (default 1.4)",
    )
    parser.add_argument(
        "--stop-file",
        help="exit cleanly when this supervisor-owned file appears",
    )
    parser.add_argument("--companion-pid", type=int, help="owning Windows Companion process")
    parser.add_argument("--companion-start-filetime", type=int, help="owner creation time as a Windows UTC FILETIME")
    parser.add_argument("--open-source-preview", action="store_true")
    parser.add_argument("--hybrid-preview", action="store_true")
    parser.add_argument(
        "--hybrid-calibration",
        default="calibration/qpro-hybrid-eye-calibration.json",
    )
    parser.add_argument("--next-model")
    parser.add_argument("--face-model")
    parser.add_argument("--eye-calibration", action="store_true")
    parser.add_argument("--calibration-overlay")
    parser.add_argument(
        "--eye-calibration-output",
        default="calibration/qpro-stereo-eye-calibration.json",
    )
    parser.add_argument("--gaze-calibration-seconds", type=int, default=60)
    parser.add_argument("--convergence-calibration-seconds", type=int, default=40)
    arguments = parser.parse_args()
    if (arguments.companion_pid is None) != (arguments.companion_start_filetime is None):
        parser.error("--companion-pid and --companion-start-filetime must be supplied together")
    if arguments.companion_pid is not None and (
        not 0 < arguments.companion_pid <= 0xFFFFFFFF
        or not 0 < arguments.companion_start_filetime <= 0x7FFFFFFFFFFFFFFF
    ):
        parser.error("Companion PID and creation FILETIME must be positive integers")
    if not 1.0 <= arguments.pupil_sensitivity <= 3.0:
        parser.error("--pupil-sensitivity must be between 1.0 and 3.0")
    stop_file = Path(arguments.stop_file).resolve() if arguments.stop_file else None
    if arguments.record_seconds < 0:
        parser.error("--record-seconds cannot be negative")
    if not 0 <= arguments.tongue_smoothing <= 100:
        parser.error("--tongue-smoothing must be between 0 and 100")
    if arguments.record_seconds and arguments.record is None:
        parser.error("--record-seconds requires --record")
    if arguments.calibration and arguments.record is None:
        parser.error("--calibration requires --record")
    if arguments.tongue_calibration and arguments.record is None:
        parser.error("--tongue-calibration requires --record")
    if arguments.tongue_still_calibration and arguments.record is None:
        parser.error("--tongue-still-calibration requires --record")
    if arguments.tongue_correction_calibration and arguments.record is None:
        parser.error("--tongue-correction-calibration requires --record")
    if arguments.tongue_refinement_calibration and arguments.record is None:
        parser.error("--tongue-refinement-calibration requires --record")
    if arguments.cheek_still_calibration and arguments.record is None:
        parser.error("--cheek-still-calibration requires --record")
    if arguments.cheek_still_calibration and arguments.no_window:
        parser.error("--cheek-still-calibration requires the visible prompt window")
    if arguments.tongue_arc_calibration and arguments.record is None:
        parser.error("--tongue-arc-calibration requires --record")
    calibration_modes = sum((
        bool(arguments.calibration),
        bool(arguments.tongue_calibration),
        bool(arguments.tongue_still_calibration),
        bool(arguments.tongue_correction_calibration),
        bool(arguments.tongue_refinement_calibration),
        bool(arguments.tongue_arc_calibration),
        bool(arguments.cheek_still_calibration),
    ))
    if calibration_modes > 1:
        parser.error("choose only one calibration mode")
    if (arguments.calibration or arguments.tongue_calibration
            or arguments.tongue_still_calibration) and arguments.no_window:
        parser.error("--calibration requires the visible prompt window")
    if (arguments.tongue_correction_calibration
            or arguments.tongue_refinement_calibration
            or arguments.tongue_arc_calibration) and arguments.no_window:
        parser.error("--calibration requires the visible prompt window")
    if arguments.model and arguments.no_window:
        parser.error("--model requires a visible comparison window")
    if arguments.model and arguments.no_labels:
        parser.error("--model comparison requires live factory labels")
    if arguments.tongue_model and arguments.no_labels:
        parser.error("--tongue-model requires live native TongueOut confidence")
    if arguments.cheek_output and not arguments.tongue_model:
        parser.error("--cheek-output requires a trained lower-face model")
    if arguments.tongue_model and (
        arguments.model or arguments.open_source_preview or arguments.hybrid_preview
        or arguments.calibration or arguments.tongue_calibration
        or arguments.tongue_still_calibration or arguments.eye_calibration
        or arguments.tongue_correction_calibration
        or arguments.tongue_refinement_calibration
        or arguments.tongue_arc_calibration or arguments.cheek_still_calibration
    ):
        parser.error("--tongue-model must run by itself")
    if (arguments.open_source_preview or arguments.hybrid_preview
            or arguments.eye_calibration) and arguments.no_window:
        parser.error("--open-source-preview requires visible windows")
    if arguments.open_source_preview and arguments.model:
        parser.error("choose either --open-source-preview or --model")
    if arguments.hybrid_preview and (
        arguments.open_source_preview or arguments.model
        or arguments.calibration or arguments.eye_calibration
    ):
        parser.error("--hybrid-preview must run by itself")
    if (arguments.open_source_preview or arguments.hybrid_preview
            or arguments.eye_calibration) and not arguments.next_model:
        parser.error("--open-source-preview requires --next-model")
    if (arguments.open_source_preview or arguments.hybrid_preview
            or arguments.eye_calibration) and not arguments.face_model:
        parser.error("--open-source-preview requires --face-model")
    if arguments.hybrid_preview and arguments.no_labels:
        parser.error("--hybrid-preview requires live Meta/VRCFT labels")
    if arguments.eye_calibration and arguments.calibration:
        parser.error("--eye-calibration cannot be combined with --calibration")
    if arguments.eye_calibration and arguments.model:
        parser.error("--eye-calibration cannot be combined with --model")
    if arguments.eye_calibration and not arguments.calibration_overlay:
        parser.error("--eye-calibration requires --calibration-overlay")
    if arguments.gaze_calibration_seconds < 10:
        parser.error("--gaze-calibration-seconds must be at least 10")
    if arguments.convergence_calibration_seconds < 10:
        parser.error("--convergence-calibration-seconds must be at least 10")

    shared = SharedPreview()
    server = start_mjpeg_server(shared, arguments.mjpeg_port)
    threading.Thread(
        target=console_keyboard_worker,
        args=(shared,),
        name="console-keyboard",
        daemon=True,
    ).start()
    connection: socket.socket | None = None
    print(f"Selected MJPEG: http://127.0.0.1:{arguments.mjpeg_port}/selected.mjpg")
    print("Per-camera MJPEG: /camera0.mjpg through /camera4.mjpg; /strip.mjpg")

    frames = 0
    fps = 0.0
    interval_bytes = 0
    throughput_mbps = 0.0
    fps_started = time.perf_counter()
    transport_report_at = fps_started
    last_sequence: int | None = None
    sequence_gap = 0
    stream_gap_stats = StreamGapStats()
    frame_replay_stats = FrameReplayStats()
    window_name = "Quest Pro inward cameras"
    window_created = False
    current_ids: list[int] = []
    capture_writer: CaptureWriter | None = None
    capture_started_ns: int | None = None
    capture_completed = False
    label_recorder: LabelSidecarRecorder | None = None
    calibration_session: CalibrationSession | None = None
    tongue_still_session: TongueStillCaptureSession | None = None
    calibration_completed = False
    labels_live_reported: bool | None = None
    manual_reference_reported: tuple[bool, bool] | None = None
    prompt_window_name = (
        "Quest Pro lower-face calibration"
        if (arguments.tongue_calibration or arguments.tongue_still_calibration
            or arguments.tongue_correction_calibration
            or arguments.tongue_refinement_calibration
            or arguments.tongue_arc_calibration or arguments.cheek_still_calibration)
        else "Quest Pro whole-face calibration"
    )
    model_window_name = "Quest Pro pilot model validation"
    live_model = None
    tongue_model_window_name = "Quest Pro personalized stereo tongue preview"
    tongue_model_preview = None
    tongue_broadcaster = None
    tongue_inference_worker = None
    pupil_tracker = None
    pupil_broadcaster = None
    pupil_worker = None
    pupil_backend_reported = None
    cheek_broadcaster = None
    last_pupil_status = 0.0
    last_cheek_status = 0.0
    last_tongue_status = None
    last_tongue_completed = 0
    open_source_window_name = "Quest Pro open-source model preview"
    open_source_preview = None
    hybrid_preview = None
    stereo_eye_calibration = None
    companion = None

    def mouse_callback(event: int, x: int, _y: int, _flags: int, _data: object) -> None:
        if event != cv2.EVENT_LBUTTONDOWN or not current_ids:
            return
        display_width = min(1500, len(current_ids) * CAMERA_WIDTH)
        panel = min(len(current_ids) - 1, x * len(current_ids) // display_width)
        shared.select(current_ids[panel])

    try:
        companion = CompanionLifetime(arguments.companion_pid, arguments.companion_start_filetime)
        check_stop_requested(stop_file, companion)
        if arguments.record is not None:
            if arguments.record == "auto":
                stamp = time.strftime("%Y%m%d-%H%M%S")
                subseconds = time.time_ns() % 1_000_000_000
                capture_path = Path("captures") / f"questpro-{stamp}-{subseconds:09d}.qpcap"
            else:
                capture_path = Path(arguments.record)
            capture_writer = CaptureWriter(capture_path)
            print(f"Recording lossless synchronized frames to {capture_writer.path}")
            if arguments.calibration or arguments.tongue_calibration:
                steps = TONGUE_STEPS if arguments.tongue_calibration else STEPS
                calibration_session = CalibrationSession(
                    capture_writer.path.with_suffix(".qpsession.json"),
                    steps=steps,
                    session_type=(
                        "tongue-stereo-v1"
                        if arguments.tongue_calibration
                        else "whole-face-v1"
                    ),
                )
                print(
                    f"User-paced calibration: {len(steps)} steps, "
                    f"{calibration_session.total_seconds / 60:.1f} minutes "
                    "minimum recommended capture time"
                )
            elif (arguments.tongue_still_calibration
                  or arguments.tongue_correction_calibration
                  or arguments.tongue_refinement_calibration
                  or arguments.tongue_arc_calibration
                  or arguments.cheek_still_calibration):
                journal_path = capture_writer.path.with_suffix(".qpsession.json")
                if arguments.tongue_still_calibration or arguments.tongue_refinement_calibration:
                    from cheek_still_capture import LowerFaceCaptureSession
                    tongue_still_session = LowerFaceCaptureSession(
                        journal_path,
                        refinement=arguments.tongue_refinement_calibration,
                    )
                elif arguments.cheek_still_calibration:
                    from cheek_still_capture import CheekStillCaptureSession
                    tongue_still_session = CheekStillCaptureSession(journal_path)
                else:
                    tongue_still_session = TongueStillCaptureSession(
                        journal_path,
                        prompts=(TONGUE_ARC_PROMPTS if arguments.tongue_arc_calibration
                                 else TONGUE_CORRECTION_PROMPTS),
                        session_type=("tongue-stereo-arc-v3" if arguments.tongue_arc_calibration
                                      else "tongue-stereo-corrections-v1"),
                        title=("Quest Pro tongue extreme-arc refinement" if arguments.tongue_arc_calibration
                               else "Quest Pro targeted tongue correction capture"),
                    )
                print(
                    f"Manual stereo mouth capture: "
                    f"{len(tongue_still_session.prompts)} cards; "
                    "SPACE saves exactly one synchronized pair"
                )
                print(
                    "Capture guidance: keep the tongue visible in both camera "
                    "views; with facial hair, vary jaw and headset fit slightly "
                    "between stills. Reposition if hair or tape covers the tongue."
                )
        if not arguments.no_labels and (
            arguments.record is not None
            or arguments.model is not None
            or arguments.tongue_model is not None
            or arguments.eye_calibration
            or arguments.hybrid_preview
        ):
            if capture_writer is not None:
                label_path = capture_writer.path.with_suffix(".qplabel.jsonl")
            else:
                label_path = None
            label_recorder = LabelSidecarRecorder(
                label_path, port=arguments.labels_port
            )
            print(
                f"Listening for timestamped factory reference labels on "
                f"127.0.0.1:{arguments.labels_port}"
            )
        if arguments.model is not None:
            from model_preview import LiveModelPreview

            live_model = LiveModelPreview(
                arguments.model, device_name=arguments.model_device
            )
            print(
                f"Loaded pilot model on {live_model.device}: "
                f"{live_model.checkpoint_path}"
            )
        if arguments.tongue_model is not None:
            from tongue_model_preview import (
                LiveTongueModelPreview,
                TongueBroadcaster,
                TongueInferenceWorker,
            )

            tongue_model_preview = LiveTongueModelPreview(
                arguments.tongue_model,
                device_name=arguments.tongue_model_device,
                direction_checkpoint_path=arguments.tongue_direction_model,
                smoothing=1.0 - 0.88 * (arguments.tongue_smoothing / 100.0),
                visibility_mode=arguments.tongue_visibility_mode,
            )
            tongue_broadcaster = TongueBroadcaster(enabled=arguments.tongue_output)
            if arguments.cheek_output:
                from cheek_camera import CameraCheekBroadcaster
                if not {"cheekPuffLeft", "cheekPuffRight"}.issubset(tongue_model_preview.target_names):
                    raise ValueError("Select a trained tongue-and-cheek camera model before enabling Camera cheek puff")
                cheek_broadcaster = CameraCheekBroadcaster(enabled=True)
                print("EXPERIMENTAL_CHEEK_CAMERA_OUTPUT_ON native_fallback=enabled", flush=True)
            tongue_inference_worker = TongueInferenceWorker(
                tongue_model_preview, tongue_broadcaster,
                render_preview=not arguments.no_window,
                cheek_broadcaster=cheek_broadcaster,
            )
            print(
                f"Loaded opt-in stereo tongue model on "
                f"{tongue_model_preview.device}: {tongue_model_preview.checkpoint_path}"
            )
            import torch

            if tongue_model_preview.device.type == "cpu":
                inference_backend = "cpu"
                inference_name = "CPU"
            elif tongue_model_preview.device.type == "cuda":
                inference_backend = (
                    "amd-rocm" if torch.version.hip else
                    "nvidia-cuda" if torch.version.cuda else "other"
                )
                inference_name = torch.cuda.get_device_name(tongue_model_preview.device)
            else:
                inference_backend = "other"
                inference_name = str(tongue_model_preview.device)
            print(
                "INFERENCE_BACKEND feature=tongue "
                f"backend={inference_backend} device={tongue_model_preview.device} "
                f"name={inference_name}",
                flush=True,
            )
            if tongue_model_preview.direction_checkpoint_path is not None:
                print(
                    "Direction ensemble model: "
                    f"{tongue_model_preview.direction_checkpoint_path}"
                )
            print(
                "Experimental VRCFT tongue output starts "
                + ("ON." if arguments.tongue_output else "OFF. Press T to toggle it.")
            )
        if arguments.pupil_output:
            from pupil_dilation import PupilBroadcaster, RelativePupilTracker
            from pupil_inference import PupilInferenceWorker

            pupil_tracker = RelativePupilTracker(sensitivity=arguments.pupil_sensitivity)
            pupil_broadcaster = PupilBroadcaster()
            pupil_worker = PupilInferenceWorker(pupil_tracker, pupil_broadcaster)
            print(
                f"INFERENCE_BACKEND feature=pupil backend={pupil_tracker.backend} "
                f"device={pupil_tracker.device} name={pupil_tracker.device_name}",
                flush=True,
            )
            pupil_backend_reported = pupil_tracker.backend
            if pupil_tracker.backend_notice:
                print(f"PUPIL_BACKEND_NOTICE {pupil_tracker.backend_notice}", flush=True)
            print(
                "Experimental relative pupil dilation ON. Each eye starts at "
                f"a session baseline of 5 mm; response {arguments.pupil_sensitivity:.1f}x. "
                "Values are animation estimates."
            )
        if (arguments.open_source_preview or arguments.hybrid_preview
                or arguments.eye_calibration):
            from open_source_preview import OpenSourceModelPreview

            open_source_preview = OpenSourceModelPreview(
                arguments.next_model,
                arguments.face_model,
                smoothing=1.0 if arguments.eye_calibration else 0.35,
            )
            print(
                "Loaded observation-only open-source models: "
                f"NEXT={open_source_preview.eye_model_path}; "
                f"Babble={open_source_preview.face_model_path}"
            )
        if arguments.hybrid_preview:
            from hybrid_preview import HybridModelPreview

            hybrid_preview = HybridModelPreview(arguments.hybrid_calibration)
            open_source_window_name = "Quest Pro hybrid tracking preview"
            print(
                "Loaded observation-only hybrid calibration: "
                f"{hybrid_preview.calibration_path}"
            )
        if arguments.eye_calibration:
            from stereo_eye_calibration import StereoEyeCalibrationController

            stereo_eye_calibration = StereoEyeCalibrationController(
                overlay_executable=arguments.calibration_overlay,
                output_path=arguments.eye_calibration_output,
                gaze_seconds=arguments.gaze_calibration_seconds,
                convergence_seconds=arguments.convergence_calibration_seconds,
            )
            stereo_eye_calibration.start()
            print(
                "Stereo eye calibration starting. Follow the instructions in VR; "
                "press Space in the PC window or console when prompted."
            )
        if not arguments.no_window:
            cv2.namedWindow(window_name, cv2.WINDOW_AUTOSIZE)
            window_created = True
            cv2.setMouseCallback(window_name, mouse_callback)
            if calibration_session is not None or tongue_still_session is not None:
                cv2.namedWindow(prompt_window_name, cv2.WINDOW_NORMAL)
                cv2.setWindowProperty(
                    prompt_window_name,
                    cv2.WND_PROP_FULLSCREEN,
                    cv2.WINDOW_FULLSCREEN,
                )
            if live_model is not None:
                cv2.namedWindow(model_window_name, cv2.WINDOW_NORMAL)
                cv2.resizeWindow(model_window_name, 1100, 760)
            if tongue_model_preview is not None:
                cv2.namedWindow(tongue_model_window_name, cv2.WINDOW_NORMAL)
                cv2.resizeWindow(tongue_model_window_name, 1100, 760)
            if open_source_preview is not None:
                cv2.namedWindow(open_source_window_name, cv2.WINDOW_NORMAL)
                cv2.resizeWindow(open_source_window_name, 1240, 880)
        print("Connecting to headset streamer (up to 20 seconds)...")
        deadline = time.monotonic() + 20.0
        while connection is None:
            check_stop_requested(stop_file, companion)
            try:
                connection = socket.create_connection(
                    (arguments.host, arguments.port), timeout=0.25 if companion.enabled else 2
                )
            except OSError:
                if time.monotonic() >= deadline:
                    raise ConnectionError(
                        "The injected streamer did not begin listening. "
                        "Send the three logs."
                    )
                time.sleep(0.25)
        # A stalled camera relay must not keep the preview open after Stop.
        connection.settimeout(0.25 if stop_file is not None or companion.enabled else None)
        connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        print("Connected. Keys 0-4 select cameras; S selects strip; Q quits.")
        while shared.running:
            check_stop_requested(stop_file, companion)
            raw_header = receive_exact(connection, HEADER.size, stop_file, companion)
            (magic, version, header_size, sequence, timestamp_ns, width, height,
             stride, pixel_format, payload_size, camera_mask,
             rejected_torn) = HEADER.unpack(raw_header)
            if magic != MAGIC or version != 3 or header_size != HEADER.size:
                raise ValueError("Unexpected live-stream header")
            camera_ids = camera_ids_from_mask(camera_mask)
            if (arguments.cheek_still_calibration or arguments.tongue_still_calibration
                    or arguments.tongue_refinement_calibration) and camera_ids not in ([2, 3], [2, 3, 4]):
                raise ValueError("Lower-face capture requires mouth or face mode (cameras 2 and 3)")
            if pupil_tracker is not None and not {0, 1}.issubset(camera_ids):
                raise ValueError("Pupil output requires both eye cameras; use camera mode eyes or all")
            expected_width = len(camera_ids) * CAMERA_WIDTH
            if (width != expected_width or height != CAMERA_HEIGHT
                    or stride != width or pixel_format != 1):
                raise ValueError(
                    f"Unsupported frame layout {width}x{height}, format {pixel_format}"
                )
            if payload_size != width * height:
                raise ValueError("Invalid payload size")
            payload = receive_exact(connection, payload_size, stop_file, companion)
            pc_monotonic_ns = time.monotonic_ns()
            stream_gap_stats.add(timestamp_ns, pc_monotonic_ns)
            frame_replay_stats.add(payload)
            record_this_frame = bool(
                capture_writer is not None
                and tongue_still_session is None
                and (
                    calibration_session is None
                    or calibration_session.phase in ("active", "ready")
                )
            )
            if record_this_frame and capture_writer is not None:
                capture_writer.write(
                    raw_header, payload, pc_monotonic_ns, time.time_ns()
                )
                if capture_started_ns is None:
                    capture_started_ns = pc_monotonic_ns
            if tongue_still_session is not None and capture_writer is not None:
                if tongue_still_session.consume_frame(
                    capture_writer,
                    raw_header,
                    payload,
                    pc_monotonic_ns,
                    time.time_ns(),
                ):
                    if capture_started_ns is None:
                        capture_started_ns = pc_monotonic_ns
                    play_cue("advanced")
            strip = np.frombuffer(payload, dtype=np.uint8).reshape((height, stride))
            current_ids[:] = camera_ids
            pupil_values: tuple[float | None, float | None] = (None, None)
            pupil_result = None
            if pupil_worker is not None:
                pupil_worker.submit(strip, camera_ids)
                pupil_result = pupil_worker.latest()
                if pupil_result is not None:
                    pupil_values = pupil_result.values
                    if pupil_result.backend != pupil_backend_reported:
                        print(
                            f"INFERENCE_BACKEND feature=pupil backend={pupil_result.backend} "
                            f"device={pupil_result.device} name={pupil_result.device_name}",
                            flush=True,
                        )
                        pupil_backend_reported = pupil_result.backend
                        if pupil_result.notice:
                            print(f"PUPIL_BACKEND_NOTICE {pupil_result.notice}", flush=True)
                if time.monotonic() - last_pupil_status >= 2.0:
                    def show(value: float | None) -> str:
                        return f"{value:.2f}" if value is not None else "warming/invalid"

                    print(
                        "PUPIL_STATUS relative_mm "
                        f"left={show(pupil_values[0])} right={show(pupil_values[1])} "
                        f"left_state={pupil_result.states[0].replace(' ', '_') if pupil_result else 'warming'} "
                        f"right_state={pupil_result.states[1].replace(' ', '_') if pupil_result else 'warming'} "
                        f"processing_ms={pupil_result.processing_ms:.1f} "
                        f"age_ms={pupil_result.age_ms:.1f} "
                        f"dropped_frames={pupil_result.dropped_frames}"
                        if pupil_result else "PUPIL_STATUS left=warming/invalid right=warming/invalid "
                        "left_state=warming right_state=warming",
                        flush=True,
                    )
                    last_pupil_status = time.monotonic()

            if last_sequence is not None and sequence > last_sequence + 1:
                sequence_gap += sequence - last_sequence - 1
            last_sequence = sequence
            frames += 1
            interval_bytes += HEADER.size + payload_size
            now = time.perf_counter()
            elapsed = now - fps_started
            if elapsed >= 1.0:
                fps = frames / elapsed
                throughput_mbps = interval_bytes / elapsed / 1_000_000
                frames = 0
                interval_bytes = 0
                fps_started = now

            if (tongue_still_session is not None or tongue_model_preview is not None or pupil_worker is not None) and now - transport_report_at >= 5.0:
                source_gaps, arrival_gaps = stream_gap_stats.summaries()
                print(
                    "TRANSPORT_QUALITY "
                    f"cameras={len(camera_ids)} fps={fps:.1f} MBps={throughput_mbps:.2f} "
                    f"source_p95_ms={source_gaps[0]:.1f} "
                    f"arrival_p95_ms={arrival_gaps[0]:.1f} "
                    f"extra_queue_p95_ms={stream_gap_stats.extra_queue_p95_ms():.1f} "
                    f"source_skips={sequence_gap} suspected_replays={frame_replay_stats.suspected_replays}",
                    flush=True,
                )
                transport_report_at = now

            model_image = None
            tongue_model_image = None
            open_source_image = None
            if live_model is not None:
                teacher_sample = (
                    label_recorder.nearest_sample(pc_monotonic_ns)
                    if label_recorder is not None else None
                )
                labels_live_for_model = bool(
                    label_recorder is not None and label_recorder.source_is_live()
                )
                prediction = live_model.predict(strip)
                model_image = live_model.render(
                    prediction,
                    teacher_sample,
                    pc_monotonic_ns,
                    labels_live_for_model,
                )
            if tongue_model_preview is not None:
                factory_sample = (
                    label_recorder.nearest_sample(pc_monotonic_ns)
                    if label_recorder is not None else None
                )
                tongue_inference_worker.submit(
                    stereo_mouth_strip(strip, camera_ids),
                    factory_sample,
                    label_recorder.schema_names if label_recorder else [],
                )
                tongue_prediction, tongue_model_image = tongue_inference_worker.latest()
                if tongue_prediction is not None and last_tongue_status is None:
                    last_tongue_status = now
                    last_tongue_completed = tongue_prediction.completed_frames
                elif tongue_prediction is not None and now - last_tongue_status >= 5.0:
                    inference_fps = (
                        (tongue_prediction.completed_frames - last_tongue_completed)
                        / (now - last_tongue_status)
                    )
                    print(
                        f"TONGUE_STATUS camera_fps={fps:.1f} inference_fps={inference_fps:.1f} "
                        f"inference_ms={tongue_prediction.inference_ms:.1f} "
                        f"pipeline_ms={tongue_prediction.pipeline_ms:.1f} "
                        f"result_age_ms={tongue_prediction.age_ms:.1f} "
                        f"dropped_frames={tongue_prediction.dropped_frames} "
                        f"requested_output={'on' if tongue_broadcaster.enabled else 'off'}", flush=True,
                    )
                    last_tongue_status = now
                    last_tongue_completed = tongue_prediction.completed_frames
                if (cheek_broadcaster is not None and tongue_prediction is not None
                        and now - last_cheek_status >= 2.0):
                    names = tongue_model_preview.target_names
                    left = float(tongue_prediction.values[names.index("cheekPuffLeft")])
                    right = float(tongue_prediction.values[names.index("cheekPuffRight")])
                    print(
                        f"CHEEK_CAMERA_STATUS left={left:.3f} right={right:.3f} "
                        f"inference_ms={tongue_prediction.inference_ms:.1f} "
                        f"pipeline_ms={tongue_prediction.pipeline_ms:.1f} "
                        f"dropped_frames={tongue_prediction.dropped_frames}", flush=True,
                    )
                    last_cheek_status = now
            # Queue tongue inference before optional MJPEG encoding. A browser
            # preview subscriber must not delay the current avatar prediction.
            shared.update(strip, camera_ids)
            if open_source_preview is not None:
                open_source_prediction = open_source_preview.predict(strip)
                factory_sample = (
                    label_recorder.nearest_sample(pc_monotonic_ns)
                    if label_recorder is not None else None
                )
                if stereo_eye_calibration is not None:
                    stereo_eye_calibration.add_sample(
                        open_source_prediction,
                        pc_monotonic_ns,
                        factory_sample=factory_sample,
                    )
                if hybrid_preview is not None:
                    hybrid_prediction = hybrid_preview.predict(
                        open_source_prediction,
                        factory_sample,
                        label_recorder.schema_names if label_recorder else [],
                    )
                    open_source_image = hybrid_preview.render(hybrid_prediction)
                else:
                    open_source_image = open_source_preview.render(
                        open_source_prediction
                    )
                if stereo_eye_calibration is not None:
                    open_source_image = stereo_eye_calibration.decorate(
                        open_source_image, open_source_prediction
                    )
            if not arguments.no_window:
                with shared.lock:
                    selected = shared.selected
                cv2.imshow(
                    window_name,
                    label_strip(
                        strip, camera_ids, fps, selected, sequence_gap,
                        rejected_torn, throughput_mbps,
                        capture_writer.frame_count if capture_writer else 0,
                        label_recorder.sample_count if label_recorder else 0,
                        frame_replay_stats.suspected_replays,
                        *stream_gap_stats.summaries(),
                        pupil_detections=pupil_result.detections if pupil_result else None,
                        pupil_values=pupil_values if pupil_tracker else None,
                    ),
                )
                if model_image is not None:
                    cv2.imshow(model_window_name, model_image)
                if tongue_model_image is not None:
                    cv2.imshow(tongue_model_window_name, tongue_model_image)
                if open_source_image is not None:
                    cv2.imshow(open_source_window_name, open_source_image)
                key = cv2.waitKeyEx(1)
                if key >= 0:
                    handle_key(shared, chr(key & 0xFF))
            if tongue_broadcaster is not None:
                for runtime_key in shared.take_runtime_keys():
                    if runtime_key == "t":
                        enabled = tongue_broadcaster.toggle()
                        print(
                            "EXPERIMENTAL_TONGUE_OUTPUT_ON"
                            if enabled else "EXPERIMENTAL_TONGUE_OUTPUT_OFF stock=restored"
                        )
            if stereo_eye_calibration is not None:
                latest_factory = (
                    label_recorder.nearest_sample(time.monotonic_ns())
                    if label_recorder is not None else None
                )
                factory_ready = bool(
                    label_recorder is not None
                    and label_recorder.sample_count >= 10
                    and label_recorder.source_change_sequence >= 3
                    and latest_factory is not None
                    and latest_factory.get("leftEyeIsValid", False)
                    and latest_factory.get("rightEyeIsValid", False)
                )
                for calibration_key in shared.take_calibration_keys():
                    stereo_eye_calibration.handle_key(
                        calibration_key, factory_ready=factory_ready
                    )
            if calibration_session is not None:
                labels_live = bool(
                    label_recorder is not None and label_recorder.source_is_live()
                )
                if labels_live != labels_live_reported:
                    print(
                        "LABEL_PREFLIGHT_OK source=changing"
                        if labels_live
                        else "LABEL_PREFLIGHT_WAITING source=frozen; "
                        "start SteamVR and VRCFT, then move your face and eyes"
                    )
                    labels_live_reported = labels_live
                for calibration_key in shared.take_calibration_keys():
                    if (
                        calibration_session.phase == "instruction"
                        and calibration_key.lower() in (" ", "\r", "\n")
                        and not labels_live
                    ):
                        action = "not_ready"
                    else:
                        action = calibration_session.handle_key(
                            calibration_key, pc_monotonic_ns
                        )
                    if action is not None:
                        play_cue(action)
                    if action == "completed":
                        calibration_completed = True
                        shared.running = False
                if calibration_session.update(pc_monotonic_ns):
                    play_cue("ready")
                step_index, step, phase, elapsed_active, remaining = (
                    calibration_session.status(pc_monotonic_ns)
                )
                cv2.imshow(
                    prompt_window_name,
                    prompt_image(
                        step_index, step, phase, elapsed_active, remaining,
                        labels_live=labels_live,
                        total_steps=len(calibration_session.steps),
                    ),
                )
            if tongue_still_session is not None:
                cheek_card = arguments.cheek_still_calibration or getattr(
                    tongue_still_session, "is_cheek_card", False)
                labels_ready_now = cheek_card or bool(
                    label_recorder is not None
                    and label_recorder.manual_tongue_reference_ready()
                )
                if (cheek_card, labels_ready_now) != manual_reference_reported:
                    print(
                        "LABEL_PREFLIGHT_OK mode=cheek-stills source=pose-cards native_labels=not_required"
                        if cheek_card else
                        "LABEL_PREFLIGHT_OK mode=manual-stills packets=fresh; "
                        "pose cards provide tongue targets"
                        if labels_ready_now else
                        "LABEL_PREFLIGHT_WAITING mode=manual-stills; "
                        "start the selected tracking app, SteamVR, and VRCFaceTracking"
                    )
                    manual_reference_reported = (cheek_card, labels_ready_now)
                for calibration_key in shared.take_calibration_keys():
                    if calibration_key == " " and not labels_ready_now:
                        action = "not_ready"
                        tongue_still_session.message = (
                            "Waiting for fresh factory reference packets from the selected tracking source."
                        )
                    else:
                        action = tongue_still_session.handle_key(calibration_key)
                    if action is not None:
                        play_cue(action)
                    if action == "completed":
                        calibration_completed = True
                        shared.running = False
                cv2.imshow(
                    prompt_window_name,
                    tongue_still_session.render(
                        strip, labels_ready_now,
                        factory_values_static=bool(
                            labels_ready_now and label_recorder is not None
                            and (
                                label_recorder.source_change_sequence < 3
                                or (label_recorder.source_unchanged_ms is not None
                                    and label_recorder.source_unchanged_ms > 3_000)
                            )
                        ),
                    ),
                )
            if (capture_writer is not None and arguments.record_seconds > 0
                    and capture_started_ns is not None
                    and (pc_monotonic_ns - capture_started_ns) / 1_000_000_000
                    >= arguments.record_seconds):
                shared.running = False
        capture_completed = True
    except StopRequested as stop:
        capture_completed = True
        print(f"STOP_REQUESTED {stop or 'camera preview closing'}", flush=True)
    finally:
        if companion is not None:
            companion.close()
        shared.running = False
        with shared.lock:
            shared.lock.notify_all()
        if connection is not None:
            connection.close()
        server.shutdown()
        server.server_close()
        if window_created:
            cv2.destroyAllWindows()
        if stereo_eye_calibration is not None:
            stereo_eye_calibration.close()
            if stereo_eye_calibration.result is not None:
                print(
                    "Stereo eye calibration complete: "
                    f"{stereo_eye_calibration.output_path}"
                )
        if tongue_inference_worker is not None:
            tongue_inference_worker.close()
        if cheek_broadcaster is not None:
            cheek_broadcaster.close()
            print("EXPERIMENTAL_CHEEK_CAMERA_OUTPUT_OFF native=restored", flush=True)
        if tongue_broadcaster is not None:
            tongue_broadcaster.close()
            print("EXPERIMENTAL_TONGUE_OUTPUT_OFF stock=restored")
        if pupil_worker is not None:
            pupil_worker.close()
            print("EXPERIMENTAL_PUPIL_OUTPUT_OFF stock=restored")
        elif pupil_broadcaster is not None:
            pupil_broadcaster.close()
            print("EXPERIMENTAL_PUPIL_OUTPUT_OFF stock=restored")
        if calibration_session is not None:
            calibration_session.finish(
                time.monotonic_ns(), completed=calibration_completed
            )
            state = "complete" if calibration_completed else "stopped early"
            print(f"Calibration session {state}: {calibration_session.path}")
        if tongue_still_session is not None:
            tongue_still_session.finish(completed=calibration_completed)
            state = "complete" if calibration_completed else "stopped early but recoverable"
            print(f"Manual lower-face session {state}: {tongue_still_session.path}")
        if capture_writer is not None:
            capture_writer.close(completed=capture_completed)
            state = "complete" if capture_completed else "recoverable but incomplete"
            print(
                f"Capture {state}: {capture_writer.frame_count} frames, "
                f"{capture_writer.path}"
            )
        if label_recorder is not None:
            label_recorder.close()
            if label_recorder.sample_count:
                if label_recorder.path is not None:
                    print(
                        f"Factory labels complete: {label_recorder.sample_count} samples, "
                        f"{len(label_recorder.schema_names)} expressions, "
                        f"{label_recorder.path}"
                    )
                else:
                    print(
                        f"Live factory labels received: {label_recorder.sample_count} "
                        f"samples, {len(label_recorder.schema_names)} expressions"
                    )
            else:
                print(
                    "WARNING: no factory reference labels were received from the selected tracking source. The camera capture "
                    "is valid but is not yet a supervised training dataset."
                )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
