#!/usr/bin/env python3
"""Live preview and fail-safe VRCFT output for the stereo tongue model."""

from __future__ import annotations

import time
import socket
import struct
import threading
from dataclasses import dataclass, replace
from pathlib import Path

import cv2
import numpy as np
import torch

if torch.version.hip:
    # Windows MIOpen HIPRTC cannot compile these tongue-model BatchNorm kernels.
    torch.backends.cudnn.enabled = False

from train_tongue_model import create_model
from qpro_gpu import validated_torch_device_name
from label_capture import AmbiguousTongueSourceError, native_tongue_out
from tongue_image_processing import preprocess_stereo_images, resolve_input_preprocessing
from gpu_readback import GPUReadbackCancelled, copy_to_cpu
from model_checkpoint import load_model_checkpoint


TONGUE_PACKET = struct.Struct("<4sBBH12f")
TONGUE_MAGIC = b"QPTO"
TONGUE_VERSION = 1
# Match TongueTimeoutMs in the VRCFT bridge. A late frame must not reacquire
# the tongue override after the bridge has already restored native tracking.
TONGUE_MAX_PIPELINE_MS = 300.0
TONGUE_MAX_NATIVE_AGE_MS = 300.0


def _native_tongue_reference(
    sample: dict[str, object] | None, names: list[str], now_ns: int
) -> tuple[float, str]:
    """Use only a recent, valid lower-face sample for the visibility gate.

    The label recorder retains its nearest packet after a source disconnects.
    Its old TongueOut value must not keep affecting a new camera pose. Missing
    evidence contributes zero to the existing calibrated weighted formula;
    changing that formula would need a separately validated camera threshold.
    """
    if sample is None or "TongueOut" not in names:
        return 0.0, "unavailable"
    try:
        arrival = sample["arrivalMonotonicNs"]
        flags = sample["faceFlags"]
        if (not isinstance(arrival, int) or isinstance(arrival, bool)
                or not isinstance(flags, int) or isinstance(flags, bool)
                or not flags & 1):
            return 0.0, "invalid"
        age_ms = (now_ns - arrival) / 1_000_000.0
        if not 0.0 <= age_ms <= TONGUE_MAX_NATIVE_AGE_MS:
            return 0.0, "stale"
        native = native_tongue_out(sample, names)
        if native is None:
            return 0.0, "invalid"
        return native, "available"
    except AmbiguousTongueSourceError:
        return 0.0, "source-unknown"
    except (KeyError, IndexError, TypeError, ValueError, OverflowError):
        return 0.0, "invalid"


def tongue_output_state(prediction: "TonguePrediction", enabled: bool) -> str:
    """Describe what the module can receive, rather than only the toggle."""
    if not enabled:
        return "off"
    if (not np.isfinite(prediction.pipeline_ms)
            or not 0.0 <= prediction.pipeline_ms <= TONGUE_MAX_PIPELINE_MS):
        return "stale"
    return "visible" if prediction.visible else "hidden"


def format_tongue_status(
    prediction: "TonguePrediction", *, camera_fps: float,
    inference_fps: float, enabled: bool,
) -> str:
    """Keep the periodic Activity result useful when output is not visible."""
    return (
        f"TONGUE_STATUS camera_fps={camera_fps:.1f} inference_fps={inference_fps:.1f} "
        f"inference_ms={prediction.inference_ms:.1f} "
        f"worker_cpu_ms={prediction.worker_cpu_ms:.1f} "
        f"pipeline_ms={prediction.pipeline_ms:.1f} "
        f"result_age_ms={prediction.age_ms:.1f} "
        f"dropped_frames={prediction.dropped_frames} "
        f"requested_output={'on' if enabled else 'off'} "
        f"output_state={tongue_output_state(prediction, enabled)} "
        f"native_status={prediction.native_status} "
        f"fused_visibility={prediction.fused_visibility:.3f}"
    )


def _prepare_inference_model(model: torch.nn.Module, device: torch.device) -> torch.nn.Module:
    """Fold fixed normalization into convolutions on this loaded eval copy.

    The training architecture and checkpoint bytes retain their original
    layers. Inference uses full float32 precision and the same learned running
    statistics, with fewer kernels and intermediate image tensors per frame.
    """
    model.eval()

    def fold(module: torch.nn.Module) -> None:
        for child in module.children():
            fold(child)
        if isinstance(module, torch.nn.Sequential):
            for index in range(len(module) - 1):
                convolution, normalization = module[index], module[index + 1]
                if (
                    isinstance(convolution, torch.nn.Conv2d)
                    and isinstance(normalization, torch.nn.BatchNorm2d)
                    and normalization.running_mean is not None
                    and normalization.running_var is not None
                ):
                    module[index] = torch.nn.utils.fuse_conv_bn_eval(
                        convolution, normalization
                    )
                    module[index + 1] = torch.nn.Identity()

    fold(model)
    model.to(device)
    if device.type == "cpu":
        # This layout speeds up the CPU convolution backend without changing
        # the stereo input contract or global thread settings used by pupils.
        model.to(memory_format=torch.channels_last)
    return model


def vrcft_tongue_values(
    prediction: "TonguePrediction", target_names: list[str]
) -> np.ndarray:
    """Map ten model heads to VRCFT's twelve detailed tongue expressions."""
    values = {
        name: float(prediction.values[index])
        for index, name in enumerate(target_names)
    }
    if not prediction.visible:
        return np.zeros(12, dtype=np.float32)
    horizontal = float(np.clip(values.get("horizontal", 0.0), -1.0, 1.0))
    vertical = float(np.clip(values.get("vertical", 0.0), -1.0, 1.0))
    twist = float(np.clip(values.get("twist", 0.0), -1.0, 1.0))
    # Visibility is confidence that the tongue is present, not how far it
    # extends. Using it as TongueOut makes a visible tip jump to full extension.
    tongue_out = float(np.clip(values.get("extension", 0.0), 0.0, 1.0))
    return np.asarray(
        [
            tongue_out,
            max(vertical, 0.0),
            max(-vertical, 0.0),
            max(-horizontal, 0.0),
            max(horizontal, 0.0),
            float(np.clip(values.get("roll", 0.0), 0.0, 1.0)),
            float(np.clip(values.get("bend_down", 0.0), 0.0, 1.0)),
            float(np.clip(values.get("curl_up", 0.0), 0.0, 1.0)),
            float(np.clip(values.get("squish", 0.0), 0.0, 1.0)),
            float(np.clip(values.get("flat", 0.0), 0.0, 1.0)),
            max(-twist, 0.0),
            max(twist, 0.0),
        ],
        dtype=np.float32,
    )


def encode_tongue_packet(values: np.ndarray, enabled: bool) -> bytes:
    values = np.asarray(values, dtype=np.float32)
    if values.shape != (12,):
        raise ValueError("A VRCFT tongue packet needs exactly twelve values")
    return TONGUE_PACKET.pack(
        TONGUE_MAGIC, TONGUE_VERSION, int(enabled), 0,
        *[float(np.clip(value, 0.0, 1.0)) for value in values],
    )


def smooth_tongue_output(
    previous: np.ndarray, target: np.ndarray, elapsed_seconds: float
) -> np.ndarray:
    """Bound per-packet expression changes, including a visibility-gate drop.

    The model's visibility decision is binary, but avatar TongueOut is an
    analog expression. A short hold protects isolated misses; this slew limit
    makes genuine entry and retraction gradual after that hold expires.
    """
    # Limit a stalled frame's jump without adding another long motion filter.
    elapsed = min(max(elapsed_seconds, 0.0), 0.04)
    maximum_rise = elapsed / 0.10
    maximum_fall = elapsed / 0.12
    return np.clip(
        previous + np.clip(target - previous, -maximum_fall, maximum_rise),
        0.0, 1.0,
    ).astype(np.float32)


class TongueBroadcaster:
    """Opt-in UDP override; disabled or stale packets restore stock tracking."""

    def __init__(self, port: int = 27276, *, enabled: bool = False) -> None:
        self.enabled = bool(enabled)
        self._address = ("127.0.0.1", int(port))
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._lock = threading.Lock()
        self._closed = False
        self._stale = False
        self._last_values: np.ndarray | None = None
        self._last_sent = 0.0
        # The camera can supply up to 72 FPS. Leave enough scheduling margin
        # to publish each new prediction instead of halving faster sessions.
        self._minimum_interval = 1.0 / 90.0
        self._first_interval = 1.0 / 24.0
        self._keepalive_interval = 0.20

    def toggle(self) -> bool:
        # The preview key and inference thread can run concurrently. The OFF
        # packet must follow any prediction already being published.
        with self._lock:
            if self._closed:
                return False
            self.enabled = not self.enabled
            if not self.enabled:
                self._send(np.zeros(12, dtype=np.float32), enabled=False)
                self._last_values = None
                self._last_sent = 0.0
                self._stale = False
            return self.enabled

    def send_prediction(
        self, prediction: "TonguePrediction", target_names: list[str]
    ) -> None:
        with self._lock:
            if not self.enabled or self._closed:
                return
            # A delayed frame must not renew an override after VRCFT has
            # already restored the native source.
            age = prediction.pipeline_ms
            if not np.isfinite(age) or not 0.0 <= age <= TONGUE_MAX_PIPELINE_MS:
                if not self._stale:
                    self._send(np.zeros(12, dtype=np.float32), enabled=False)
                self._stale = True
                self._last_values = None
                self._last_sent = 0.0
                return
            self._stale = False
            self._publish_prediction(prediction, target_names)

    def _publish_prediction(
        self, prediction: "TonguePrediction", target_names: list[str]
    ) -> None:
        target = vrcft_tongue_values(prediction, target_names)
        now = time.perf_counter()
        elapsed = (
            now - self._last_sent
            if self._last_values is not None else self._first_interval
        )
        if self._last_values is not None and elapsed < self._minimum_interval:
            return
        previous = (
            self._last_values if self._last_values is not None
            else np.zeros(12, dtype=np.float32)
        )
        values = smooth_tongue_output(previous, target, elapsed)
        changed = (
            self._last_values is None
            or float(np.max(np.abs(values - self._last_values))) >= 0.002
        )
        if not changed and elapsed < self._keepalive_interval:
            return
        self._send(values, enabled=True)
        self._last_values = values.copy()
        self._last_sent = now

    def _send(self, values: np.ndarray, *, enabled: bool) -> None:
        self._socket.sendto(encode_tongue_packet(values, enabled), self._address)

    def close(self) -> None:
        with self._lock:
            if self._closed:
                return
            self.enabled = False
            self._closed = True
            try:
                self._send(np.zeros(12, dtype=np.float32), enabled=False)
            finally:
                self._socket.close()


@dataclass(frozen=True)
class TonguePrediction:
    values: np.ndarray
    native_tongue_out: float
    fused_visibility: float
    visible: bool
    inference_ms: float
    pipeline_ms: float = 0.0
    dropped_frames: int = 0
    age_ms: float = 0.0
    completed_frames: int = 0
    native_status: str = "unavailable"
    worker_cpu_ms: float = 0.0


class TongueVisibilityHold:
    """Bridge brief visibility misses without making a new hidden pose visible."""

    def __init__(self, hold_seconds: float = 0.22) -> None:
        self.hold_seconds = hold_seconds
        self.visible = False
        self._last_confident_at: float | None = None
        self._last_confident_values: np.ndarray | None = None
        self._last_confident_strength = 0.0

    def update(
        self, strength: float, threshold: float, values: np.ndarray, now: float
    ) -> tuple[bool, float, np.ndarray]:
        cutoff = threshold - 0.08 if self.visible else threshold
        if strength >= cutoff:
            self.visible = True
            self._last_confident_at = now
            self._last_confident_values = values.copy()
            self._last_confident_strength = strength
            return True, strength, values
        if (
            self.visible and self._last_confident_at is not None
            and now - self._last_confident_at <= self.hold_seconds
            and self._last_confident_values is not None
        ):
            return True, self._last_confident_strength, self._last_confident_values.copy()
        self.visible = False
        return False, strength, values


class TongueMotionFilter:
    """Steady small fluctuations while letting deliberate movements catch up.

    The slider defines smoothing at 24 FPS. Converting that response to elapsed
    time keeps faster camera sessions from changing its feel. Visibility and
    cheek heads retain ordinary smoothing; the movement boost is tongue-only.
    """

    def __init__(self, smoothing: float, target_names: list[str]) -> None:
        self.smoothing = float(np.clip(smoothing, 0.05, 1.0))
        self._motion = np.asarray([
            name not in {"visibility", "cheekPuffLeft", "cheekPuffRight"}
            for name in target_names
        ])
        self._values: np.ndarray | None = None
        self._updated_at: float | None = None

    def update(self, values: np.ndarray, now: float) -> np.ndarray:
        values = np.asarray(values, dtype=np.float32)
        if values.shape != self._motion.shape or not np.all(np.isfinite(values)):
            raise ValueError("Tongue model returned invalid expression values")
        if self._values is None or self.smoothing == 1.0:
            self._values = values.copy()
        else:
            # Ignore sub-6% jitter when adapting. A large pose change uses a
            # faster response, then returns to the user's steady-pose setting.
            movement = np.clip((np.abs(values - self._values) - 0.06) / 0.30, 0.0, 1.0)
            fast_alpha = max(self.smoothing, 0.90)
            alpha = self.smoothing + (fast_alpha - self.smoothing) * movement * self._motion
            elapsed = min(max(now - self._updated_at, 0.001), 0.10)
            alpha = 1.0 - np.power(1.0 - alpha, elapsed * 24.0)
            self._values += alpha * (values - self._values)
        self._updated_at = now
        return self._values.copy()


class LiveTongueModelPreview:
    def __init__(
        self,
        checkpoint_path: str | Path,
        device_name: str = "auto",
        direction_checkpoint_path: str | Path | None = None,
        smoothing: float = 0.35,
        visibility_mode: str = "weighted",
        camera_weight: float | None = None,
    ) -> None:
        self.checkpoint_path = Path(checkpoint_path).resolve()
        device_name = validated_torch_device_name(torch, device_name)
        self.device = torch.device(device_name)
        checkpoint = load_model_checkpoint(self.checkpoint_path)
        self.target_names = list(checkpoint["targetNames"])
        self.image_size = int(checkpoint["imageSize"])
        self.input_preprocessing = resolve_input_preprocessing(checkpoint)
        self.architecture = str(checkpoint.get("architecture", "legacy-late-fusion-v1"))
        self.model = create_model(self.architecture, self.target_names)
        self.model.load_state_dict(checkpoint["modelState"])
        self.model = _prepare_inference_model(self.model, self.device)
        self.direction_checkpoint_path: Path | None = None
        self.direction_model = None
        self.direction_image_size = self.image_size
        self.direction_input_preprocessing = self.input_preprocessing
        if direction_checkpoint_path:
            self.direction_checkpoint_path = Path(direction_checkpoint_path).resolve()
            direction_checkpoint = load_model_checkpoint(self.direction_checkpoint_path)
            direction_names = list(direction_checkpoint["targetNames"])
            cheek_names = ["cheekPuffLeft", "cheekPuffRight"]
            if direction_names != self.target_names and direction_names != self.target_names + cheek_names:
                raise ValueError(
                    "Visibility and direction checkpoints use different target schemas"
                )
            direction_architecture = str(
                direction_checkpoint.get("architecture", "legacy-late-fusion-v1")
            )
            self.direction_image_size = int(direction_checkpoint["imageSize"])
            self.direction_input_preprocessing = resolve_input_preprocessing(
                direction_checkpoint
            )
            self.direction_model = create_model(
                direction_architecture, direction_names
            )
            self.direction_model.load_state_dict(direction_checkpoint["modelState"])
            self.direction_model = _prepare_inference_model(self.direction_model, self.device)
            self.target_names = direction_names
        gate = checkpoint.get("visibilityGate", {})
        self.camera_weight = float(
            gate.get("cameraWeight", 0.5) if camera_weight is None else camera_weight
        )
        self.threshold = float(gate.get("threshold", 0.44))
        self.smoothing = float(np.clip(smoothing, 0.05, 1.0))
        if visibility_mode not in {"camera", "native", "weighted", "agreement"}:
            raise ValueError(f"Unsupported tongue visibility mode: {visibility_mode}")
        self.visibility_mode = visibility_mode
        self._motion_filter = TongueMotionFilter(self.smoothing, self.target_names)
        self._visibility_hold = TongueVisibilityHold()

    def reset_temporal_state(self) -> None:
        """Discard rejected frame history before accepting another camera pose."""
        self._motion_filter = TongueMotionFilter(self.smoothing, self.target_names)
        self._visibility_hold = TongueVisibilityHold(self._visibility_hold.hold_seconds)

    def _inputs(
        self, strip: np.ndarray, image_size: int, preprocessing: str = "raw-v1"
    ) -> torch.Tensor:
        cameras = np.empty((2, image_size, image_size), dtype=np.uint8)
        for view in range(2):
            panel = strip[:, view * 400:(view + 1) * 400]
            cameras[view] = cv2.resize(
                panel, (image_size, image_size), interpolation=cv2.INTER_AREA
            )
        # Match the cache/trainer order. Contrast is part of the checkpoint's
        # input contract, so legacy weights continue to receive raw pixels.
        cameras = preprocess_stereo_images(cameras, preprocessing)
        normalized = cameras.astype(np.float32)[None] / 255.0
        return torch.from_numpy(normalized).to(self.device)

    def set_readback_cancelled(self, cancelled) -> None:
        self._readback_cancelled = cancelled

    def predict(
        self,
        strip: np.ndarray,
        factory_sample: dict[str, object] | None,
        factory_names: list[str],
    ) -> TonguePrediction:
        if strip.shape not in ((400, 800), (400, 1200)):
            raise ValueError(
                "Tongue preview requires cameras 2 and 3 in a 400x800 mouth "
                f"or 400x1200 face strip, got {strip.shape}"
            )
        started = time.perf_counter()
        inputs = self._inputs(strip, self.image_size, self.input_preprocessing)
        with torch.inference_mode():
            model_values = self.model(inputs)[0]
            if self.direction_model is not None:
                if (
                    self.direction_image_size == self.image_size
                    and self.direction_input_preprocessing == self.input_preprocessing
                ):
                    direction_inputs = inputs
                else:
                    direction_inputs = self._inputs(
                        strip, self.direction_image_size,
                        self.direction_input_preprocessing,
                    )
                # Preserve the float32 gate value if a custom direction head
                # emits a lower-precision tensor. Released models use float32.
                direction_values = self.direction_model(direction_inputs)[0].float().clone()
                visibility_index = self.target_names.index("visibility")
                direction_values[visibility_index] = model_values[visibility_index]
                model_values = direction_values
            # One owned readback covers both heads on the current stream.
            # Sleeping while it finishes avoids a driver wait occupying a
            # CPU core and lets Stop cancel before any output is published.
            values = copy_to_cpu(
                model_values.float(), cancelled=getattr(self, "_readback_cancelled", None),
            ).numpy()
        completed_at = time.perf_counter()
        inference_ms = (completed_at - started) * 1000.0
        smoothed = self._motion_filter.update(values, completed_at)
        native, native_status = _native_tongue_reference(
            factory_sample, factory_names, time.monotonic_ns()
        )
        visibility = float(smoothed[self.target_names.index("visibility")])
        if self.visibility_mode == "camera":
            fused = visibility
        elif self.visibility_mode == "native":
            fused = native
        elif self.visibility_mode == "agreement":
            fused = min(visibility, native)
        else:
            fused = self.camera_weight * visibility + (1.0 - self.camera_weight) * native
        visible, fused, output_values = self._visibility_hold.update(
            fused, self.threshold, smoothed, completed_at
        )
        output_values = output_values.copy()
        # A tongue visibility hold must not freeze an unrelated cheek pose.
        for name in ("cheekPuffLeft", "cheekPuffRight"):
            if name in self.target_names:
                index = self.target_names.index(name)
                output_values[index] = smoothed[index]
        return TonguePrediction(
            values=output_values.copy(),
            native_tongue_out=native,
            fused_visibility=fused,
            visible=visible,
            inference_ms=inference_ms,
            native_status=native_status,
        )

    def render(
        self, prediction: TonguePrediction, *, output_enabled: bool = False
    ) -> np.ndarray:
        height = max(760, 205 + len(self.target_names) * 46 + 60)
        image = np.zeros((height, 1100, 3), dtype=np.uint8)
        cv2.putText(image, "Quest Pro personalized lower-face preview", (24, 42),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.86, (240, 240, 240), 2, cv2.LINE_AA)
        output_text = (
            "EXPERIMENTAL VRCFT TONGUE OUTPUT ON - T disables immediately"
            if output_enabled
            else "Stock tongue output active; T enables the tongue override"
        )
        cv2.putText(
            image, output_text, (24, 76), cv2.FONT_HERSHEY_SIMPLEX, 0.55,
            (80, 235, 120) if output_enabled else (50, 175, 255), 1, cv2.LINE_AA,
        )
        status = "TONGUE VISIBLE" if prediction.visible else "TONGUE RETRACTED"
        color = (80, 235, 120) if prediction.visible else (170, 170, 170)
        cv2.putText(image, status, (24, 118), cv2.FONT_HERSHEY_SIMPLEX, 0.68,
                    color, 2, cv2.LINE_AA)
        visibility = float(prediction.values[self.target_names.index("visibility")])
        summary = (
            f"camera {visibility:.2f}  native {prediction.native_tongue_out:.2f}  "
            f"{self.visibility_mode} {prediction.fused_visibility:.2f} / threshold {self.threshold:.2f}  "
            f"infer {prediction.inference_ms:.1f} ms  "
            f"pipeline {prediction.pipeline_ms:.1f} ms  dropped {prediction.dropped_frames}"
        )
        cv2.putText(image, summary, (24, 151), cv2.FONT_HERSHEY_SIMPLEX, 0.49,
                    (190, 220, 255), 1, cv2.LINE_AA)
        if self.direction_checkpoint_path is not None:
            cv2.putText(
                image,
                "ENSEMBLE: clean manual visibility gate + dense motion directions",
                (24, 177), cv2.FONT_HERSHEY_SIMPLEX, 0.46,
                (120, 225, 255), 1, cv2.LINE_AA,
            )
        bar_x, bar_width = 260, 560
        for row, (name, value) in enumerate(zip(self.target_names, prediction.values)):
            y = 205 + row * 46
            cv2.putText(image, name, (24, y + 9), cv2.FONT_HERSHEY_SIMPLEX, 0.52,
                        (225, 225, 225), 1, cv2.LINE_AA)
            cv2.rectangle(image, (bar_x, y - 9), (bar_x + bar_width, y + 15),
                          (55, 55, 55), 1)
            if name in {"horizontal", "vertical", "twist"}:
                center = bar_x + bar_width // 2
                cv2.line(image, (center, y - 8), (center, y + 14), (90, 90, 90), 1)
                endpoint = center + round((bar_width / 2) * float(value))
                cv2.rectangle(image, (min(center, endpoint), y - 5),
                              (max(center, endpoint), y + 11), (245, 90, 225), -1)
            else:
                endpoint = bar_x + round(bar_width * float(np.clip(value, 0, 1)))
                cv2.rectangle(image, (bar_x, y - 5), (endpoint, y + 11),
                              (80, 235, 120), -1)
            cv2.putText(image, f"{float(value):+.2f}", (845, y + 9),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.49, (220, 220, 220), 1, cv2.LINE_AA)
        cv2.putText(
            image,
            "T toggles VRCFT output | Q quits and restores stock tongue automatically.",
            (24, height - 30), cv2.FONT_HERSHEY_SIMPLEX, 0.48, (170, 170, 170), 1, cv2.LINE_AA,
        )
        return image


class TongueInferenceWorker:
    """Runs inference latest-frame-first so brief stalls cannot build latency."""

    def __init__(
        self,
        preview: LiveTongueModelPreview,
        broadcaster: TongueBroadcaster,
        render_preview: bool = True,
        cheek_broadcaster=None,
    ) -> None:
        self.preview = preview
        self.broadcaster = broadcaster
        self.render_preview = render_preview
        self.cheek_broadcaster = cheek_broadcaster
        self._condition = threading.Condition()
        self._pending: tuple[
            np.ndarray, dict[str, object] | None, list[str], float
        ] | None = None
        self._latest: tuple[TonguePrediction, np.ndarray | None] | None = None
        self._error: BaseException | None = None
        self._running = True
        self._dropped_frames = 0
        self._completed_frames = 0
        self._completed_at = 0.0
        self._preview_image: np.ndarray | None = None
        self._preview_rendered_at = 0.0
        self._preview_interval = 1.0 / 12.0
        self._output_state: str | None = None
        self._next_stale_warning_at = 0.0
        self._reported_stale = False
        self._reported_native_source_unknown = False
        self._stop_event = threading.Event()
        set_cancelled = getattr(preview, "set_readback_cancelled", None)
        if set_cancelled is not None:
            set_cancelled(self._stop_event.is_set)
        self._thread = threading.Thread(
            target=self._run, name="tongue-inference", daemon=True
        )
        self._thread.start()

    def submit(
        self,
        strip: np.ndarray,
        factory_sample: dict[str, object] | None,
        factory_names: list[str],
    ) -> None:
        with self._condition:
            if not self._running:
                return
            if self._pending is not None:
                self._dropped_frames += 1
            # The ndarray keeps its immutable payload bytes alive. Replacing
            # this one-slot pending item intentionally discards stale work.
            self._pending = (
                strip, factory_sample, list(factory_names), time.perf_counter()
            )
            self._condition.notify()

    def latest(self) -> tuple[TonguePrediction | None, np.ndarray | None]:
        with self._condition:
            if self._error is not None:
                raise RuntimeError("Tongue inference worker failed") from self._error
            if self._latest is None:
                return None, None
            prediction, image = self._latest
            return replace(
                prediction, age_ms=max(0.0, time.perf_counter() - self._completed_at) * 1000.0
            ), image

    def close(self) -> None:
        self._stop_event.set()
        with self._condition:
            self._running = False
            self._pending = None
            self._condition.notify_all()
        # Startup-path tests intentionally replace Thread.start; a receiver
        # failure before connection must still clean up without joining a
        # thread that never began.
        if self._thread.ident is not None:
            self._thread.join(timeout=2.0)

    def _run(self) -> None:
        try:
            while True:
                with self._condition:
                    while self._running and self._pending is None:
                        self._condition.wait()
                    if not self._running:
                        return
                    strip, factory_sample, factory_names, submitted_at = self._pending
                    self._pending = None
                    dropped = self._dropped_frames
                cpu_started = time.thread_time()
                prediction = self.preview.predict(
                    strip, factory_sample, factory_names
                )
                prediction = replace(
                    prediction,
                    pipeline_ms=(time.perf_counter() - submitted_at) * 1000.0,
                    dropped_frames=dropped,
                    worker_cpu_ms=(time.thread_time() - cpu_started) * 1000,
                )
                # Stop may arrive while a GPU call is still running. Serialize
                # publication with close so that call cannot renew an override
                # after the receiver has requested shutdown.
                with self._condition:
                    if not self._running:
                        return
                    if (
                        not np.isfinite(prediction.pipeline_ms)
                        or not 0.0 <= prediction.pipeline_ms <= TONGUE_MAX_PIPELINE_MS
                    ):
                        # predict() updates smoothing and the visibility hold.
                        # A rejected slow pose must not reappear in a subsequent
                        # fresh hidden frame through either of those histories.
                        self.preview.reset_temporal_state()
                    self.broadcaster.send_prediction(
                        prediction, self.preview.target_names
                    )
                    output_state = tongue_output_state(prediction, self.broadcaster.enabled)
                    # Hidden is an ordinary tongue pose. Report only slow-frame
                    # transitions and recovery, rather than every appearance.
                    if (output_state == "stale" and self._output_state != "stale"
                            and time.perf_counter() >= self._next_stale_warning_at):
                        print(
                            "WARNING: TONGUE_OUTPUT_STALE "
                            f"pipeline_ms={prediction.pipeline_ms:.1f} "
                            f"limit_ms={TONGUE_MAX_PIPELINE_MS:.0f}. "
                            "Camera tongue output paused because this frame is too old; "
                            "the module uses native tongue values. Check the selected GPU "
                            "runtime and reduce other camera work before retrying.",
                            flush=True,
                        )
                        self._next_stale_warning_at = time.perf_counter() + 5.0
                        self._reported_stale = True
                    elif output_state != "stale" and self._reported_stale:
                        print(
                            f"TONGUE_OUTPUT_RECOVERED state={output_state}. "
                            "Fresh camera results are available again.", flush=True,
                        )
                        self._reported_stale = False
                    self._output_state = output_state
                    if (self.broadcaster.enabled
                            and prediction.native_status == "source-unknown"
                            and not self._reported_native_source_unknown):
                        print(
                            "WARNING: TONGUE_NATIVE_SOURCE_UNKNOWN. The factory "
                            "label feed does not identify its streaming app; its "
                            "native TongueOut reference is omitted. Close "
                            "VRCFaceTracking, update the matching Qpro module "
                            "in First-time setup, then reopen it and retry.", flush=True,
                        )
                        self._reported_native_source_unknown = True
                    if self.cheek_broadcaster is not None:
                        self.cheek_broadcaster.send_prediction(prediction, self.preview.target_names)
                    self._completed_frames += 1
                    self._completed_at = time.perf_counter()
                    prediction = replace(prediction, completed_frames=self._completed_frames)
                    self._latest = (prediction, self._preview_image)
                # Avatar output follows every prediction; rendering diagnostic
                # bars only needs 12 FPS and must not consume the camera rate.
                now = time.perf_counter()
                if self.render_preview and (
                    self._preview_image is None
                    or now - self._preview_rendered_at >= self._preview_interval
                ):
                    self._preview_image = self.preview.render(
                        prediction, output_enabled=self.broadcaster.enabled
                    )
                    self._preview_rendered_at = now
                with self._condition:
                    if not self._running:
                        return
                    self._latest = (prediction, self._preview_image)
        except GPUReadbackCancelled as error:
            if not self._stop_event.is_set():
                with self._condition:
                    self._error = error
                    self._running = False
                    self._condition.notify_all()
        except BaseException as error:
            with self._condition:
                self._error = error
                self._running = False
                self._condition.notify_all()
        finally:
            set_cancelled = getattr(self.preview, "set_readback_cancelled", None)
            if set_cancelled is not None:
                set_cancelled(None)
