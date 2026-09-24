#!/usr/bin/env python3
"""Read Quest Pro detector eye vectors from supported tracking-engine builds."""

from __future__ import annotations

import argparse
import base64
import collections
import json
import math
import queue
import re
import struct
import subprocess
import threading
import time
from dataclasses import asdict, dataclass, replace
from pathlib import Path

import cv2
import numpy as np

from native_eye_probe import gaze_panel, put_text, summary
from native_eye_pupil_probe import KernelToPcMonotonicClock


ENGINE_PATH = "/odm/lib64/libtrackingengines.so"
TRACE_ROOT = "/sys/kernel/tracing"
TRACE_INSTANCE = "qpro_raw_eye"
TRACE_GROUP = "qpro_raw_eye"

@dataclass(frozen=True)
class EngineProbeProfile:
    size: int
    offset: int
    arguments: str
    sha256: str | None = None


# The first profile is the previously supported 51483620027600340 engine.
# The second is 51503870024400340. Its hook is after the detector has stored
# the three visual-axis floats in EyeData; x19 still points to that EyeData.
ENGINE_PROFILES = (
    EngineProbeProfile(
        47_724_232,
        0xB63FE4,
        "x=+0x30(%sp):x32 y=+0x34(%sp):x32 z=+0x38(%sp):x32 "
        "tag=+0x0(%x19):x32",
    ),
    EngineProbeProfile(
        47_418_280,
        0xB1F3E8,
        "x=+0x300(%x19):x32 y=+0x304(%x19):x32 z=+0x308(%x19):x32 "
        "tag=+0x0(%x19):x32",
        "0fb6f54a3e190bec791d757ea18d32a8ecc1af4a861992d04b1703c93293cd03",
    ),
)

TRACE_SAMPLE = re.compile(
    r"(?P<time>\d+\.\d+): qpro_(?P<eye>left|right): .*?"
    r"x=0x(?P<x>[0-9a-fA-F]+) y=0x(?P<y>[0-9a-fA-F]+) "
    r"z=0x(?P<z>[0-9a-fA-F]+) valid=(?P<valid>\d+)"
)
PREMERGE_SAMPLE = re.compile(
    r"(?P<time>\d+\.\d+): qpro_inputs: .*?"
    r"ax=0x(?P<ax>[0-9a-fA-F]+) ay=0x(?P<ay>[0-9a-fA-F]+) "
    r"az=0x(?P<az>[0-9a-fA-F]+) av=(?P<av>\d+) "
    r"bx=0x(?P<bx>[0-9a-fA-F]+) by=0x(?P<by>[0-9a-fA-F]+) "
    r"bz=0x(?P<bz>[0-9a-fA-F]+) bv=(?P<bv>\d+)"
)
DETECTOR_SAMPLE = re.compile(
    r"(?P<time>\d+\.\d+): detector_output: .*?"
    r"x=0x(?P<x>[0-9a-fA-F]+) y=0x(?P<y>[0-9a-fA-F]+) "
    r"z=0x(?P<z>[0-9a-fA-F]+) tag=0x(?P<tag>[0-9a-fA-F]+)"
)


def float_from_trace_hex(value: str) -> float:
    return struct.unpack("<f", struct.pack("<I", int(value, 16)))[0]


def vector_yaw_pitch(vector: tuple[float, float, float]) -> tuple[float, float]:
    x, y, z = vector
    yaw = math.degrees(math.atan2(x, z))
    pitch = math.degrees(math.atan2(-y, math.hypot(x, z)))
    return yaw, pitch


def angular_separation_degrees(
    left: tuple[float, float, float], right: tuple[float, float, float]
) -> float:
    left_array = np.asarray(left, dtype=np.float64)
    right_array = np.asarray(right, dtype=np.float64)
    denominator = float(np.linalg.norm(left_array) * np.linalg.norm(right_array))
    if denominator <= 1e-12:
        return float("nan")
    cosine = float(np.clip(np.dot(left_array, right_array) / denominator, -1.0, 1.0))
    return math.degrees(math.acos(cosine))


@dataclass(frozen=True)
class RawEyeSample:
    pc_monotonic_ns: int
    kernel_time_s: float
    left_valid: bool
    right_valid: bool
    left_vector: tuple[float, float, float]
    right_vector: tuple[float, float, float]

    @property
    def valid(self) -> bool:
        return self.left_valid and self.right_valid

    @property
    def left_angles(self) -> tuple[float, float]:
        return vector_yaw_pitch(self.left_vector)

    @property
    def right_angles(self) -> tuple[float, float]:
        return vector_yaw_pitch(self.right_vector)

    @property
    def disparity(self) -> float:
        return self.right_angles[0] - self.left_angles[0]

    @property
    def pitch_difference(self) -> float:
        return self.right_angles[1] - self.left_angles[1]

    @property
    def angular_separation(self) -> float:
        return angular_separation_degrees(self.left_vector, self.right_vector)


class TracePairParser:
    """Pair adjacent left/right events and remove three publisher duplicates."""

    def __init__(self) -> None:
        self._left: tuple[float, bool, tuple[float, float, float]] | None = None
        self._last_signature: tuple[object, ...] | None = None

    def parse(self, line: str, pc_monotonic_ns: int) -> RawEyeSample | None:
        match = TRACE_SAMPLE.search(line)
        if not match:
            return None
        kernel_time = float(match.group("time"))
        valid = match.group("valid") == "1"
        vector = tuple(
            float_from_trace_hex(match.group(axis)) for axis in ("x", "y", "z")
        )
        if match.group("eye") == "left":
            self._left = (kernel_time, valid, vector)  # type: ignore[assignment]
            return None
        if self._left is None:
            return None
        left_time, left_valid, left_vector = self._left
        self._left = None
        if abs(kernel_time - left_time) > 0.002:
            return None
        signature = (left_valid, valid, left_vector, vector)
        if signature == self._last_signature:
            return None
        self._last_signature = signature
        return RawEyeSample(
            pc_monotonic_ns=pc_monotonic_ns,
            kernel_time_s=(left_time + kernel_time) / 2.0,
            left_valid=left_valid,
            right_valid=valid,
            left_vector=left_vector,
            right_vector=vector,  # type: ignore[arg-type]
        )


class PreMergeParser:
    """Decode and deduplicate the two inputs to the binocular merge routine."""

    def __init__(self) -> None:
        self._last_signature: tuple[object, ...] | None = None

    def parse(self, line: str, pc_monotonic_ns: int) -> RawEyeSample | None:
        match = PREMERGE_SAMPLE.search(line)
        if not match:
            return None
        input_a = tuple(
            float_from_trace_hex(match.group("a" + axis)) for axis in ("x", "y", "z")
        )
        input_b = tuple(
            float_from_trace_hex(match.group("b" + axis)) for axis in ("x", "y", "z")
        )
        input_a_valid = match.group("av") == "1"
        input_b_valid = match.group("bv") == "1"
        signature = (input_a_valid, input_b_valid, input_a, input_b)
        if signature == self._last_signature:
            return None
        self._last_signature = signature
        return RawEyeSample(
            pc_monotonic_ns=pc_monotonic_ns,
            kernel_time_s=float(match.group("time")),
            left_valid=input_b_valid,
            right_valid=input_a_valid,
            left_vector=input_b,  # type: ignore[arg-type]
            right_vector=input_a,  # type: ignore[arg-type]
        )


class DetectorOutputParser:
    """Pair tag-0/tag-1 outputs computed by VisualAxisDetector."""

    def __init__(self) -> None:
        self._pending: dict[int, tuple[float, tuple[float, float, float]]] = {}

    def parse(self, line: str, pc_monotonic_ns: int) -> RawEyeSample | None:
        match = DETECTOR_SAMPLE.search(line)
        if not match:
            return None
        eye = int(match.group("tag"), 16) & 0xFF
        if eye not in (0, 1):
            return None
        kernel_time = float(match.group("time"))
        vector = tuple(
            float_from_trace_hex(match.group(axis)) for axis in ("x", "y", "z")
        )
        self._pending[eye] = (kernel_time, vector)  # type: ignore[assignment]
        if 0 not in self._pending or 1 not in self._pending:
            return None
        left_time, left_vector = self._pending[0]
        right_time, right_vector = self._pending[1]
        if abs(right_time - left_time) > 0.004:
            older = 0 if left_time < right_time else 1
            del self._pending[older]
            return None
        self._pending.clear()
        return RawEyeSample(
            pc_monotonic_ns=pc_monotonic_ns,
            kernel_time_s=(left_time + right_time) / 2.0,
            left_valid=True,
            right_valid=True,
            left_vector=left_vector,
            right_vector=right_vector,
        )
class PersistentAdbRootShell:
    """One live Magisk shell for tracefs control commands.

    On the headset's Magisk build, a short `adb shell su -c ...` operation can
    complete on-device while its PC client never receives process completion.
    Keeping one interactive root shell and delimiting replies with unique
    markers avoids that per-command lifecycle race.
    """

    def __init__(self, adb: str) -> None:
        self.adb = adb
        self._lines: queue.Queue[str] = queue.Queue()
        self._counter = 0
        self._process = subprocess.Popen(
            [adb, "shell", "su"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        self._thread = threading.Thread(target=self._read, daemon=True)
        self._thread.start()
        ready = self.run("id", timeout=8.0)
        if "uid=0(root)" not in ready.stdout:
            self.close()
            raise RuntimeError("Magisk did not provide an interactive root shell")

    def _read(self) -> None:
        assert self._process.stdout is not None
        for line in self._process.stdout:
            self._lines.put(line.rstrip("\r\n"))

    @staticmethod
    def _normalize(command: str) -> str:
        # These quotes are needed when commands are passed as adb arguments to
        # `su -c`; inside an already-root interactive shell they would quote
        # the operators rather than perform redirection/piping.
        return (
            command.replace(" '>>' ", " >> ")
            .replace(" '>' ", " > ")
            .replace(" '|' ", " | ")
        )

    def run(
        self, command: str, *, check: bool = True, timeout: float = 8.0
    ) -> subprocess.CompletedProcess[str]:
        if self._process.poll() is not None or self._process.stdin is None:
            raise RuntimeError("The persistent Magisk root shell exited")
        self._counter += 1
        marker = f"__QPRO_ROOT_DONE_{self._counter:08d}__"
        normalized = self._normalize(command)
        self._process.stdin.write(
            f"{normalized}\nqpro_status=$?\necho {marker}:$qpro_status\n"
        )
        self._process.stdin.flush()
        output: list[str] = []
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise RuntimeError(
                    f"Timed out waiting for persistent headset root shell: {command}"
                )
            try:
                line = self._lines.get(timeout=min(remaining, 0.25))
            except queue.Empty:
                if self._process.poll() is not None:
                    raise RuntimeError("The persistent Magisk root shell exited")
                continue
            if line.startswith(marker + ":"):
                try:
                    return_code = int(line.rsplit(":", 1)[1])
                except ValueError:
                    return_code = 1
                text = "\n".join(output)
                result = subprocess.CompletedProcess(
                    [self.adb, "shell", "su"], return_code, text
                )
                if check and return_code != 0:
                    raise subprocess.CalledProcessError(
                        return_code, result.args, output=text
                    )
                return result
            output.append(line)

    def close(self) -> None:
        if self._process.poll() is None:
            try:
                if self._process.stdin is not None:
                    self._process.stdin.write("exit\n")
                    self._process.stdin.flush()
                self._process.wait(timeout=2.0)
            except (BrokenPipeError, subprocess.TimeoutExpired):
                self._process.terminate()
                try:
                    self._process.wait(timeout=1.0)
                except subprocess.TimeoutExpired:
                    self._process.kill()
        self._thread.join(timeout=1.0)


class RawTraceEyeReader:
    def __init__(self, adb: str) -> None:
        self.adb = adb
        self.samples: queue.Queue[RawEyeSample] = queue.Queue(maxsize=512)
        self.errors: queue.Queue[str] = queue.Queue(maxsize=16)
        self._process: subprocess.Popen[str] | None = None
        self._thread: threading.Thread | None = None
        self._stopping = False
        self._configured = False
        self._clock = KernelToPcMonotonicClock()
        self._root_shell: PersistentAdbRootShell | None = None
        self.engine_profile: EngineProbeProfile | None = None

    @property
    def instance_path(self) -> str:
        return f"{TRACE_ROOT}/instances/{TRACE_INSTANCE}"

    def _adb_root(
        self, command: str, *, check: bool = True, timeout: float = 8.0
    ) -> subprocess.CompletedProcess[str]:
        if self._root_shell is None:
            self._root_shell = PersistentAdbRootShell(self.adb)
        try:
            return self._root_shell.run(command, check=check, timeout=timeout)
        except subprocess.CalledProcessError as error:
            detail = (error.output or "").strip()
            message = f"Headset root command failed: {command}"
            if detail:
                message += f"\nHeadset response: {detail}"
            raise RuntimeError(message) from error
        except RuntimeError:
            if check:
                raise
            return subprocess.CompletedProcess(
                [self.adb, "shell", "su"], 124, ""
            )

    def _write_event(self, event: str) -> None:
        encoded = base64.b64encode(event.encode("utf-8")).decode("ascii")
        self._adb_root(
            f"echo {encoded} '|' base64 -d '>>' {TRACE_ROOT}/uprobe_events"
        )

    def _cleanup(self) -> None:
        if self._root_shell is None:
            return
        instance = self.instance_path
        self._adb_root(f"echo 0 '>' {instance}/tracing_on", check=False)
        for name in ("detector_output", "qpro_inputs", "qpro_left", "qpro_right"):
            self._adb_root(
                f"echo 0 '>' {instance}/events/{TRACE_GROUP}/{name}/enable",
                check=False,
            )
            self._adb_root(
                f"echo '-:{TRACE_GROUP}/{name}' '>' {TRACE_ROOT}/uprobe_events",
                check=False,
            )
        # A process interrupted during reader.start() used to orphan its
        # headset-side `cat trace_pipe`. That reader holds the instance open,
        # so a later run could neither remove nor recreate qpro_raw_eye. The
        # persistent root shell makes it safe to stop the exact reader here;
        # then retry until tracefs has actually released the directory. Resolve
        # the individual cat PID instead of using pkill: the latter previously
        # stranded some wireless adb shells.
        self._adb_root(
            "for qpro_pid in $(pidof cat); do "
            f"grep -q '{instance}/trace_pipe' /proc/$qpro_pid/cmdline "
            "&& kill $qpro_pid; done",
            check=False,
            timeout=2.0,
        )
        removed = False
        for _ in range(8):
            self._adb_root(
                f"echo 1 '>' {instance}/free_buffer", check=False, timeout=2.0
            )
            result = self._adb_root(
                f"rmdir {instance}; test ! -d {instance}",
                check=False,
                timeout=2.0,
            )
            if result.returncode == 0:
                removed = True
                break
            time.sleep(0.15)
        if not removed:
            raise RuntimeError(
                "Could not release the previous headset eye-trace workspace. "
                "Stop any other independent-gaze preview and try again."
            )
        self._configured = False

    def start(self) -> None:
        state = subprocess.run(
            [self.adb, "get-state"], stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, text=True, timeout=5, check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        if state.returncode != 0 or state.stdout.strip() != "device":
            raise RuntimeError("No authorized Quest was found over ADB")
        self._root_shell = PersistentAdbRootShell(self.adb)
        size_result = self._adb_root(f"stat -c %s {ENGINE_PATH}")
        try:
            engine_size = int(size_result.stdout.strip().splitlines()[-1])
        except (ValueError, IndexError) as error:
            raise RuntimeError("Could not verify the headset tracking-engine build") from error
        profile = next((item for item in ENGINE_PROFILES if item.size == engine_size), None)
        if profile is None:
            raise RuntimeError(
                f"Unsupported tracking-engine size ({engine_size}); "
                "do not use firmware-specific probe offsets"
            )
        if profile.sha256 is not None:
            hash_result = self._adb_root(f"sha256sum {ENGINE_PATH}")
            actual_hash = hash_result.stdout.strip().split()[0].lower()
            if actual_hash != profile.sha256:
                raise RuntimeError(
                    "Tracking-engine hash differs from the validated build; "
                    "do not use firmware-specific probe offsets"
                )
        self.engine_profile = profile

        self._cleanup()
        self._adb_root(f"mkdir {self.instance_path}")
        try:
            self._write_event(
                f"p:{TRACE_GROUP}/detector_output {ENGINE_PATH}:0x{profile.offset:x} "
                f"{profile.arguments}"
            )
            self._adb_root(
                f"echo 1 '>' {self.instance_path}/events/{TRACE_GROUP}/detector_output/enable"
            )
            self._adb_root(f"echo 1 '>' {self.instance_path}/tracing_on")
            self._configured = True
            remote_command = f"su -c 'cat {self.instance_path}/trace_pipe'"
            self._process = subprocess.Popen(
                [self.adb, "shell", remote_command],
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            self._thread = threading.Thread(target=self._read, daemon=True)
            self._thread.start()
        except Exception:
            try:
                self._cleanup()
            finally:
                if self._root_shell is not None:
                    self._root_shell.close()
                    self._root_shell = None
            raise

    def _read(self) -> None:
        assert self._process is not None and self._process.stdout is not None
        parser = DetectorOutputParser()
        try:
            for line in self._process.stdout:
                if self._stopping:
                    break
                arrival_monotonic_ns = time.monotonic_ns()
                sample = parser.parse(line, arrival_monotonic_ns)
                if sample is None:
                    continue
                sample = replace(
                    sample,
                    pc_monotonic_ns=self._clock.translate(
                        sample.kernel_time_s, arrival_monotonic_ns
                    ),
                )
                try:
                    self.samples.put_nowait(sample)
                except queue.Full:
                    try:
                        self.samples.get_nowait()
                    except queue.Empty:
                        pass
                    self.samples.put_nowait(sample)
        except Exception as error:
            try:
                self.errors.put_nowait(str(error))
            except queue.Full:
                pass

    def close(self) -> None:
        self._stopping = True
        if self._process is not None and self._process.poll() is None:
            self._process.terminate()
            try:
                self._process.wait(timeout=1.0)
            except subprocess.TimeoutExpired:
                self._process.kill()
        if self._thread is not None:
            self._thread.join(timeout=1.0)
        if self._root_shell is not None:
            self._cleanup()
            self._root_shell.close()
            self._root_shell = None


def save_capture(
    path: Path, samples: list[RawEyeSample], profile: EngineProbeProfile
) -> None:
    payload = {
        "format": "qpro-visual-axis-detector-output-v1",
        "created_unix_ns": time.time_ns(),
        "engine_size": profile.size,
        "probe_offset": profile.offset,
        "eye_mapping": "EyeData tag byte 0=left, 1=right",
        "samples": [asdict(sample) for sample in samples],
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", required=True)
    parser.add_argument("--headless-seconds", type=float, default=0.0)
    parser.add_argument("--calibration-overlay", default="")
    parser.add_argument("--calibration-output", default="")
    parser.add_argument("--gaze-seconds", type=int, default=60)
    parser.add_argument("--convergence-seconds", type=int, default=80)
    parser.add_argument("--title", default="Quest Pro VisualAxisDetector eye probe")
    parser.add_argument(
        "--notice",
        default="READ ONLY kernel trace - no trackingservice injection or pause",
    )
    parser.add_argument(
        "--instruction",
        default=(
            "Drift only the right eye or close one eye; the other detector "
            "output must remain independent."
        ),
    )
    arguments = parser.parse_args()

    reader = RawTraceEyeReader(arguments.adb)
    reader.start()
    calibration = None
    if arguments.calibration_overlay:
        if arguments.headless_seconds > 0:
            reader.close()
            raise ValueError("Calibration cannot run in headless mode")
        if not arguments.calibration_output:
            reader.close()
            raise ValueError("Calibration requires --calibration-output")
        from visual_axis_calibration import VisualAxisCalibrationController

        calibration = VisualAxisCalibrationController(
            arguments.calibration_overlay,
            arguments.calibration_output,
            gaze_seconds=arguments.gaze_seconds,
            convergence_seconds=arguments.convergence_seconds,
        )
        try:
            calibration.start()
        except Exception:
            reader.close()
            raise
    if arguments.headless_seconds > 0:
        captured: list[RawEyeSample] = []
        deadline = time.monotonic() + arguments.headless_seconds
        try:
            while time.monotonic() < deadline:
                try:
                    captured.append(reader.samples.get(timeout=0.25))
                except queue.Empty:
                    pass
            if not reader.errors.empty():
                raise RuntimeError(reader.errors.get_nowait())
            if not captured:
                raise RuntimeError("The detector trace produced no paired eye samples")
            print(json.dumps({
                "samples": len(captured),
                "rate_hz": len(captured) / arguments.headless_seconds,
                "latest": asdict(captured[-1]),
                "left_angles": captured[-1].left_angles,
                "right_angles": captured[-1].right_angles,
                "disparity_deg": captured[-1].disparity,
                "pitch_difference_deg": captured[-1].pitch_difference,
                "angular_separation_deg": captured[-1].angular_separation,
            }))
            return 0
        finally:
            reader.close()

    latest: RawEyeSample | None = None
    samples: list[RawEyeSample] = []
    recent: collections.deque[RawEyeSample] = collections.deque(maxlen=1200)
    sample_rate = 0.0
    rate_samples = 0
    rate_started = time.monotonic()
    saved_message = ""
    window = "Quest Pro VisualAxisDetector eye probe"
    cv2.namedWindow(window, cv2.WINDOW_NORMAL)
    cv2.resizeWindow(window, 1180, 760)
    try:
        while True:
            while True:
                try:
                    sample = reader.samples.get_nowait()
                except queue.Empty:
                    break
                latest = sample
                samples.append(sample)
                recent.append(sample)
                rate_samples += 1
                if calibration is not None:
                    calibration.add_axis_sample(
                        sample.left_angles,
                        sample.right_angles,
                        sample.pc_monotonic_ns,
                        kernel_time_s=sample.kernel_time_s,
                    )
            if not reader.errors.empty():
                raise RuntimeError(reader.errors.get_nowait())
            now = time.monotonic()
            elapsed = now - rate_started
            if elapsed >= 1.0:
                sample_rate = rate_samples / elapsed
                rate_samples = 0
                rate_started = now

            image = np.zeros((760, 1180, 3), dtype=np.uint8)
            put_text(image, arguments.title, (24, 40),
                     (245, 245, 245), 0.9, 2)
            put_text(
                image,
                f"computed before EyeData publication/fusion | paired {sample_rate:.1f} Hz",
                (24, 72), (170, 170, 170), 0.5,
            )
            put_text(
                image,
                arguments.notice,
                (24, 100), (80, 235, 120), 0.52, 1,
            )
            if latest is None:
                put_text(image, "Waiting for detector eye frames...", (360, 340),
                         (50, 175, 255), 0.7, 1)
            else:
                gaze_panel(image, (24, 150), "LEFT DETECTOR OUTPUT (tag 0)",
                           latest.left_angles, (255, 210, 45))
                gaze_panel(image, (410, 150), "RIGHT DETECTOR OUTPUT (tag 1)",
                           latest.right_angles, (245, 70, 235))
                put_text(image, "Detector-level candidate values", (805, 150),
                         (230, 230, 230), 0.62, 1)
                put_text(image, f"horizontal R-L  {latest.disparity:+.3f} deg",
                         (805, 194), (80, 235, 120), 0.58, 1)
                put_text(image, f"vertical R-L    {latest.pitch_difference:+.3f} deg",
                         (805, 228), (80, 235, 120), 0.50, 1)
                put_text(image, f"ray separation {latest.angular_separation:.3f} deg",
                         (805, 262), (80, 235, 120), 0.50, 1)
                put_text(image, f"valid L {int(latest.left_valid)}  R {int(latest.right_valid)}",
                         (805, 300), (210, 210, 210), 0.48)
                left = latest.left_vector
                right = latest.right_vector
                put_text(image, f"L ({left[0]:+.3f}, {left[1]:+.3f}, {left[2]:+.3f})",
                         (805, 342), (170, 170, 170), 0.43)
                put_text(image, f"R ({right[0]:+.3f}, {right[1]:+.3f}, {right[2]:+.3f})",
                         (805, 372), (170, 170, 170), 0.43)

            cv2.line(image, (24, 445), (1156, 445), (60, 60, 60), 1)
            if calibration is None:
                put_text(image, "Physical independence test", (24, 490),
                         (235, 235, 235), 0.65, 1)
                put_text(image, arguments.instruction,
                         (24, 535), (190, 190, 190), 0.5)
                put_text(image,
                         "Expected: LEFT stays nearly fixed while RIGHT moves. They must not move together.",
                         (24, 570), (80, 235, 120), 0.5)
                put_text(image,
                         "2. Close either eye. The closed eye may freeze or become invalid, but must not mirror the open eye.",
                         (24, 620), (190, 190, 190), 0.5)
                if recent:
                    cutoff = time.monotonic_ns() - 10_000_000_000
                    disparity = [item.disparity for item in recent
                                 if item.pc_monotonic_ns >= cutoff]
                    put_text(image, "10-second disparity: " + summary(disparity),
                             (24, 675), (80, 235, 120), 0.46)
                controls = "S saves detector vectors. Q quits and removes tracepoints."
            else:
                phase, message, count = calibration.status()
                color = (80, 235, 120) if phase == "done" else (50, 175, 255)
                if phase == "error":
                    color = (70, 70, 255)
                put_text(
                    image,
                    f"IN-VR LOCAL VISUAL-AXIS CALIBRATION [{phase.upper()}] | {count} samples",
                    (24, 490), color, 0.58, 1,
                )
                put_text(image, message, (24, 535), (210, 210, 210), 0.46, 1)
                put_text(
                    image,
                    "Space starts the stage shown in VR. Follow the target naturally; move your head slowly during both stages.",
                    (24, 580), (190, 190, 190), 0.47,
                )
                put_text(
                    image,
                    "The two eye mappings remain isolated. Near/far vergence is evaluated only after both rays are mapped.",
                    (24, 620), (80, 235, 120), 0.47,
                )
                controls = "SPACE starts | R resends tutorial | Q quits and restores the stock model"
            put_text(image, controls, (24, 725), (160, 160, 160), 0.48)
            if saved_message:
                put_text(image, saved_message, (650, 725), (80, 235, 120), 0.4)
            cv2.imshow(window, image)
            key = cv2.waitKeyEx(10)
            if key < 0:
                continue
            character = chr(key & 0xFF).lower()
            if character == "q":
                break
            if calibration is not None:
                calibration.handle_key(character)
            if character == "s":
                stamp = time.strftime("%Y%m%d-%H%M%S")
                path = Path("calibration") / f"detector-eye-probe-{stamp}.json"
                assert reader.engine_profile is not None
                save_capture(path, samples, reader.engine_profile)
                saved_message = f"Saved {path.resolve()}"
    finally:
        if calibration is not None:
            calibration.close()
        reader.close()
        cv2.destroyAllWindows()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
