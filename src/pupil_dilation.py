"""Experimental relative pupil-size estimates from Quest Pro eye cameras.

The camera stream has no calibrated millimetre scale. Each eye's first valid
frames establish a session baseline, represented as 5 mm for VRCFT animation.
These values are relative animation estimates, not clinical measurements.
"""

from __future__ import annotations

import socket
import struct
import time
from collections import deque
from collections.abc import Iterator
from dataclasses import dataclass
from functools import lru_cache
from statistics import median

import cv2
import numpy as np

from gpu_readback import GPUReadbackCancelled


CAMERA_WIDTH = 400
CAMERA_HEIGHT = 400
PUPIL_PACKET = struct.Struct("<4sBBHff")
PUPIL_MAGIC = b"QPDI"
_LARGE_KERNEL = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (7, 7))
_SMALL_KERNEL = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))
_PUPIL_QUANTILES = np.asarray((5, 10, 15, 20, 25, 30), dtype=np.float64) / 100.0
_MAX_PUPIL_MEDIAN = 45


def _error_summary(exc: Exception) -> str:
    lines = str(exc).splitlines()
    return f"{type(exc).__name__}: {lines[0] if lines else 'no further detail'}"


@dataclass(frozen=True)
class PupilDetection:
    center: tuple[float, float]
    diameter_px: float
    confidence: float


def _valid_eye_roi(roi: np.ndarray) -> bool:
    # Camera ROIs are strided views. OpenCV reduces them directly without
    # NumPy's slower strided passes or changing the intensity checks.
    low, high, _, _ = cv2.minMaxLoc(roi)
    # Median blur cannot create a value darker than the input minimum.
    return low <= _MAX_PUPIL_MEDIAN and high - low >= 18 and cv2.mean(roi)[0] >= 8


def detect_pupil(
    image: np.ndarray,
    previous_center: tuple[float, float] | None = None,
) -> PupilDetection | None:
    """Find a dark pupil despite small tracking-LED reflections."""
    if image.shape != (CAMERA_HEIGHT, CAMERA_WIDTH) or image.dtype != np.uint8:
        raise ValueError("Pupil detection expects one 400x400 grayscale eye frame")
    roi = image[75:355, 55:390]
    if not _valid_eye_roi(roi):
        return None
    # Median filtering removes bright headset LED glints inside the pupil.
    clean = cv2.medianBlur(roi, 7)
    return _detect_clean_pupil(clean, _morph_pupil_image(clean), previous_center)


def _thresholds(clean: np.ndarray) -> list[int]:
    # Median images contain exact uint8 levels. A 256-bin histogram provides
    # the same linearly interpolated percentiles without copying/partitioning
    # every ROI. Per-bin counts stay exact in float32 for the 280x335 ROI.
    counts = cv2.calcHist([clean], [0], None, [256], [0, 256]).ravel()
    cumulative = counts.cumsum(dtype=np.float64)
    lower_ranks, upper_ranks, fraction = _pupil_quantile_ranks(clean.size)
    lower = np.searchsorted(cumulative, lower_ranks, side="right").astype(np.float64)
    upper = np.searchsorted(cumulative, upper_ranks, side="right").astype(np.float64)
    difference = upper - lower
    # Match NumPy's stable linear interpolation on both halves of each bin.
    percentiles = np.where(
        fraction >= 0.5, upper - difference * (1.0 - fraction),
        lower + difference * fraction,
    )
    low = int(np.searchsorted(cumulative, 0, side="right"))
    return sorted(set(
        [low + 10, low + 20]
        + [int(value) for value in percentiles]
    ))


@lru_cache(maxsize=8)
def _pupil_quantile_ranks(size: int) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    ranks = _PUPIL_QUANTILES * (size - 1)
    lower = np.floor(ranks)
    return lower, np.ceil(ranks), ranks - lower


def _morph_pupil_image(clean: np.ndarray) -> np.ndarray:
    # Inverse thresholding swaps min and max. Grayscale opening/closing
    # therefore reproduces the binary closing/opening at every cutoff.
    opened = cv2.morphologyEx(clean, cv2.MORPH_OPEN, _LARGE_KERNEL)
    return cv2.morphologyEx(opened, cv2.MORPH_CLOSE, _SMALL_KERNEL)


def _threshold_masks(morphed: np.ndarray, thresholds: list[int]) -> Iterator[np.ndarray]:
    for threshold in thresholds:
        yield cv2.threshold(morphed, threshold, 255, cv2.THRESH_BINARY_INV)[1]


def _median_uint8_samples(samples: np.ndarray) -> float:
    # The selected ellipse pixels are disposable 1-D copies. Select the
    # middle values in place and widen before adding an even pair.
    middle = samples.size // 2
    if samples.size % 2:
        samples.partition(middle)
        return float(samples[middle])
    samples.partition((middle - 1, middle))
    return (int(samples[middle - 1]) + int(samples[middle])) / 2


def _detect_clean_pupil(
    clean: np.ndarray, morphed: np.ndarray,
    previous_center: tuple[float, float] | None,
) -> PupilDetection | None:
    x0, y0 = 55, 75
    best: tuple[float, PupilDetection] | None = None
    seen_contours: set[bytes] = set()
    previous_foreground = 0
    for dark in _threshold_masks(morphed, _thresholds(clean)):
        foreground = cv2.countNonZero(dark)
        if foreground == previous_foreground:
            # Increasing thresholds can only add pixels. Equal counts imply
            # identical masks even if the original percentiles differ.
            continue
        previous_foreground = foreground
        if foreground == dark.size:
            break
        contours, _ = cv2.findContours(dark, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        for contour in contours:
            if len(contour) < 5:
                continue
            area = cv2.contourArea(contour)
            if not 100 <= area <= 6500:
                continue
            # Several thresholds often produce the same boundary. Its score
            # uses the same median image, so evaluating it again adds no signal.
            key = contour.tobytes()
            if key in seen_contours:
                continue
            seen_contours.add(key)
            perimeter = cv2.arcLength(contour, True)
            circularity = 4 * np.pi * area / (perimeter * perimeter) if perimeter else 0
            if circularity < 0.42:
                continue
            (x, y), axes, angle = cv2.fitEllipse(contour)
            major, minor = max(axes), min(axes)
            if not 14 <= major <= 75 or minor < 10 or minor / major < 0.48:
                continue
            if not 15 <= x <= clean.shape[1] - 15 or not 15 <= y <= clean.shape[0] - 15:
                continue
            ellipse_area = np.pi * major * minor / 4
            fill = area / ellipse_area
            if not 0.60 <= fill <= 1.30:
                continue
            # The inner ellipse uses axes scaled by 0.70, so all its pixels
            # lie within 0.35 * major of its center. Include two pixels for
            # OpenCV's fixed-point rasterization and center rounding.
            inner_radius = int(np.ceil(major * 0.35)) + 2
            cx, cy = int(x), int(y)
            core = clean[max(0, cy - inner_radius):cy + inner_radius + 1,
                         max(0, cx - inner_radius):cx + inner_radius + 1]
            if cv2.minMaxLoc(core)[0] > _MAX_PUPIL_MEDIAN:
                continue
            # Require the fitted shape to be darker than its immediate annulus.
            # Only allocate masks around the ellipse, not the entire eye ROI.
            # Integer translation preserves OpenCV's rasterized ellipse.
            radius = int(np.ceil(major * 0.95)) + 3
            left, top = max(0, int(x) - radius), max(0, int(y) - radius)
            right, bottom = min(clean.shape[1], int(x) + radius + 1), min(clean.shape[0], int(y) + radius + 1)
            patch = clean[top:bottom, left:right]
            inner = np.zeros(patch.shape, np.uint8)
            local_center = (x - left, y - top)
            cv2.ellipse(inner, (local_center, (axes[0] * 0.70, axes[1] * 0.70), angle), 255, -1)
            inside = patch[inner != 0]
            if inside.size < 20:
                continue
            inside_median = _median_uint8_samples(inside)
            if inside_median > _MAX_PUPIL_MEDIAN:
                continue
            outer = np.zeros(patch.shape, np.uint8)
            cv2.ellipse(outer, (local_center, (axes[0] * 1.90, axes[1] * 1.90), angle), 255, -1)
            cv2.ellipse(outer, (local_center, (axes[0] * 1.20, axes[1] * 1.20), angle), 0, -1)
            ring = patch[outer != 0]
            if ring.size < 30:
                continue
            contrast = _median_uint8_samples(ring) - inside_median
            if contrast < 25:
                continue
            center = (float(x + x0), float(y + y0))
            prior_penalty = (
                0.15 * np.hypot(center[0] - previous_center[0], center[1] - previous_center[1])
                if previous_center is not None else 0
            )
            location_penalty = 0.07 * np.hypot(center[0] - 275, center[1] - 215)
            oversize_penalty = 1.5 * max(0, major - 48)
            score = (
                contrast + 20 * circularity + 7 * fill
                - prior_penalty - location_penalty - oversize_penalty
            )
            if best is None or score > best[0]:
                detection = PupilDetection(
                    center=center,
                    diameter_px=float(major),
                    confidence=min(contrast / 50 * circularity, 1.0),
                )
                best = (score, detection)
    return best[1] if best is not None else None


class RelativePupilEye:
    def __init__(self, baseline_frames: int = 12, sensitivity: float = 1.4) -> None:
        if not 1.0 <= sensitivity <= 3.0:
            raise ValueError("Pupil sensitivity must be between 1.0 and 3.0")
        self.sensitivity = sensitivity
        self._baseline_samples: deque[float] = deque(maxlen=baseline_frames)
        self._baseline_centers: deque[tuple[float, float]] = deque(maxlen=baseline_frames)
        # A five-frame median removes small contour changes magnified at 3x.
        self._recent_sizes: deque[float] = deque(maxlen=5)
        self._baseline: float | None = None
        self._baseline_center: tuple[float, float] | None = None
        self._smoothed: float | None = None
        self._invalid_frames = 0
        self.status = "warming"

    def _invalid(self, reason: str) -> float | None:
        self._recent_sizes.clear()
        if self._baseline is None:
            self._baseline_samples.clear()
            self._baseline_centers.clear()
            self.status = reason
            return None
        self._invalid_frames += 1
        if self._smoothed is None:
            self.status = reason
            return None
        if self._invalid_frames <= 5:
            self.status = f"{reason}; holding last estimate"
            return self._smoothed
        # Ease toward neutral during longer occlusion instead of jumping to 5.
        self._smoothed += 0.14 * (5.0 - self._smoothed)
        self.status = f"{reason}; easing to neutral"
        return self._smoothed if abs(self._smoothed - 5.0) >= 0.05 else None

    def update(self, detection: PupilDetection | None) -> float | None:
        if detection is None or detection.confidence < 0.20:
            return self._invalid("pupil not visible")
        if self._baseline_center is not None and np.hypot(
            detection.center[0] - self._baseline_center[0],
            detection.center[1] - self._baseline_center[1],
        ) > 35:
            return self._invalid("gaze outside calibrated area")
        self._recent_sizes.append(detection.diameter_px)
        size = median(self._recent_sizes)
        if self._baseline is None:
            self._baseline_samples.append(size)
            self._baseline_centers.append(detection.center)
            if len(self._baseline_samples) < self._baseline_samples.maxlen:
                self.status = f"warming {len(self._baseline_samples)}/{self._baseline_samples.maxlen}"
                return None
            centers = np.asarray(self._baseline_centers, dtype=np.float32)
            center_spread = np.ptp(centers, axis=0)
            size_spread = max(self._baseline_samples) - min(self._baseline_samples)
            baseline_samples = list(self._baseline_samples)
            half = len(self._baseline_samples) // 2
            size_trend = abs(
                median(baseline_samples[:half])
                - median(baseline_samples[half:])
            ) if half else 0.0
            if np.max(center_spread) > 20 or size_spread > 10 or size_trend > 4:
                self.status = "warming: hold gaze steady"
                return None
            self._baseline = median(self._baseline_samples)
            self._baseline_center = tuple(np.median(centers, axis=0))
        ratio = size / self._baseline
        if not 0.50 <= ratio <= 1.70:
            return self._invalid("size outside plausible range")
        self._invalid_frames = 0
        # Match the old gain near neutral, then ease toward the limits so a
        # high response setting still has visible range instead of flatlining.
        relative_change = self.sensitivity * (ratio - 1.0)
        span = 3.0
        estimate = float(5.0 + span * np.tanh(5.0 * relative_change / span))
        if self._smoothed is None:
            self._smoothed = estimate
        else:
            response = 0.32 - 0.06 * (self.sensitivity - 1.0)
            step = response * (estimate - self._smoothed)
            self._smoothed += min(0.27, max(-0.27, step))
        self.status = "tracking"
        return self._smoothed


class RelativePupilTracker:
    def __init__(self, sensitivity: float = 1.4, backend: str = "auto", device: str = "auto") -> None:
        if backend not in ("auto", "gpu", "cpu"):
            raise ValueError("Pupil backend must be auto, gpu, or cpu")
        self.eyes = (RelativePupilEye(sensitivity=sensitivity), RelativePupilEye(sensitivity=sensitivity))
        self.detections: tuple[PupilDetection | None, PupilDetection | None] = (None, None)
        self._previous_centers: list[tuple[float, float] | None] = [None, None]
        self._cpu_prepared: dict[int, tuple[np.ndarray, np.ndarray, np.ndarray]] = {}
        self.backend = "cpu"
        self.device = "cpu"
        self.device_name = "CPU"
        self.backend_notice = ""
        self._gpu = None
        if backend != "cpu":
            try:
                from pupil_gpu import TorchPupilPreprocessor
                self._gpu = TorchPupilPreprocessor(device)
                self.backend = self._gpu.backend
                self.device = self._gpu.device
                self.device_name = self._gpu.name
            except Exception as exc:
                self.backend_notice = f"GPU pupil processing unavailable; using CPU: {_error_summary(exc)}"

    def set_readback_cancelled(self, cancelled) -> None:
        if self._gpu is not None:
            self._gpu.readback_cancelled = cancelled

    def _detect_eyes(self, images: dict[int, np.ndarray]) -> dict[int, PupilDetection | None]:
        results: dict[int, PupilDetection | None] = {eye: None for eye in images}
        valid_rois = {}
        for eye, image in images.items():
            if image.shape != (CAMERA_HEIGHT, CAMERA_WIDTH) or image.dtype != np.uint8:
                raise ValueError("Pupil detection expects one 400x400 grayscale eye frame")
            roi = image[75:355, 55:390]
            if _valid_eye_roi(roi):
                valid_rois[eye] = roi
        if not valid_rois:
            return results
        eye_ids = list(valid_rois)
        prepared = None
        if self._gpu is not None:
            try:
                clean_batch, morphed_batch = self._gpu.prepare(list(valid_rois.values()))
                prepared = zip(clean_batch, morphed_batch)
            except GPUReadbackCancelled:
                # Stop is not a GPU failure. Do not start CPU fallback work
                # or change this tracker's backend while shutting down.
                raise
            except Exception as exc:
                # Discard the incomplete batch. Both eyes use one CPU path,
                # retaining their existing baselines and smoothing state.
                self.backend_notice = f"GPU pupil processing failed; switched to CPU: {_error_summary(exc)}"
                self._gpu = None
                self.backend = "cpu"
                self.device = "cpu"
                self.device_name = "CPU"
        if prepared is None:
            prepared = []
            for eye, roi in valid_rois.items():
                cached = self._cpu_prepared.get(eye)
                if cached is not None and cv2.norm(roi, cached[0], cv2.NORM_INF) == 0:
                    prepared.append((cached[1], cached[2]))
                    continue
                clean = cv2.medianBlur(roi, 7)
                morphed = _morph_pupil_image(clean)
                # Callers may reuse a mutable frame buffer. Keep a snapshot
                # of the ROI and rerun scoring and state updates on every frame.
                self._cpu_prepared[eye] = (roi.copy(), clean, morphed)
                prepared.append((clean, morphed))
        for eye, (clean, morphed) in zip(eye_ids, prepared):
            results[eye] = _detect_clean_pupil(clean, morphed, self._previous_centers[eye])
        return results

    def update(
        self, strip: np.ndarray, camera_ids: list[int]
    ) -> tuple[float | None, float | None]:
        results: list[float | None] = []
        detections: list[PupilDetection | None] = []
        images = {
            eye: strip[:, camera_ids.index(eye) * CAMERA_WIDTH:(camera_ids.index(eye) + 1) * CAMERA_WIDTH]
            for eye in (0, 1) if eye in camera_ids
        }
        found = self._detect_eyes(images)
        for eye_id in (0, 1):
            detection = found.get(eye_id)
            if detection is not None and detection.confidence >= 0.20:
                self._previous_centers[eye_id] = detection.center
            detections.append(detection)
            results.append(self.eyes[eye_id].update(detection))
        self.detections = (detections[0], detections[1])
        return results[0], results[1]


def encode_pupil_packet(values: tuple[float | None, float | None]) -> bytes:
    left, right = values
    flags = int(left is not None) | (int(right is not None) << 1)
    return PUPIL_PACKET.pack(PUPIL_MAGIC, 1, flags, 0, left or 0.0, right or 0.0)


class PupilBroadcaster:
    _interval = 1.0 / 24

    def __init__(self, port: int = 27277) -> None:
        self._address = ("127.0.0.1", port)
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._next_send = 0.0

    def send(self, values: tuple[float | None, float | None]) -> None:
        now = time.perf_counter()
        if now < self._next_send:
            return
        self._socket.sendto(encode_pupil_packet(values), self._address)
        # Keep time against the 24 Hz schedule. A late prediction should not
        # disqualify the next one. Allow at most a quarter-frame of credit.
        self._next_send = max(self._next_send + self._interval, now + 0.75 * self._interval)

    def close(self) -> None:
        try:
            self._socket.sendto(encode_pupil_packet((None, None)), self._address)
        finally:
            self._socket.close()
