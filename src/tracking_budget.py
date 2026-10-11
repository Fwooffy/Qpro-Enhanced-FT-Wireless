"""Per-feature inference budgets, separate from camera transport and capture.

Responsive keeps the existing processing cadence. Lower CPU profiles reduce
only pupil sampling; tongue and camera-cheek predictions still see every pair
the worker can process. A lower rate changes temporal response and warm-up,
so it is an explicit performance choice rather than a model accuracy fix.
"""

from __future__ import annotations

import math
from dataclasses import dataclass


PROFILE_RATES = {
    "responsive": (0.0, 0.0),
    "balanced": (0.0, 12.0),
    "low-cpu": (0.0, 8.0),
}


def validate_processing_fps(value: float) -> float:
    """Zero follows camera arrivals; positive budgets retain override freshness."""
    value = float(value)
    if not math.isfinite(value) or (value != 0.0 and not 5.0 <= value <= 120.0):
        raise ValueError("Processing FPS must be 0 (camera cadence) or between 5 and 120")
    return value


@dataclass(frozen=True)
class TrackingBudget:
    profile: str
    tongue_fps: float
    pupil_fps: float


def resolve_tracking_budget(
    profile: str = "responsive", *,
    tongue_fps: float | None = None, pupil_fps: float | None = None,
) -> TrackingBudget:
    if profile not in PROFILE_RATES:
        raise ValueError(f"Unknown tracking profile: {profile}")
    default_tongue, default_pupil = PROFILE_RATES[profile]
    return TrackingBudget(
        profile,
        validate_processing_fps(default_tongue if tongue_fps is None else tongue_fps),
        validate_processing_fps(default_pupil if pupil_fps is None else pupil_fps),
    )


class InferenceCadence:
    """Space worker starts without accumulating missed slots or queued frames.

    Call while holding the worker's condition lock. Pending frames stay in the
    worker's existing one-slot buffer during waits and retain their original
    arrival timestamp. The first frame is immediate; slow inference does not
    create a catch-up burst. Waiting does not consume a worker CPU core.
    """

    def __init__(self, maximum_fps: float = 0.0) -> None:
        maximum_fps = validate_processing_fps(maximum_fps)
        self._interval = 1.0 / maximum_fps if maximum_fps else 0.0
        self._last_started: float | None = None

    @property
    def limited(self) -> bool:
        return self._interval > 0.0

    def delay(self, now: float) -> float:
        if self._last_started is None or self._interval == 0.0:
            return 0.0
        return max(0.0, self._last_started + self._interval - now)

    def started(self, now: float) -> None:
        self._last_started = now
