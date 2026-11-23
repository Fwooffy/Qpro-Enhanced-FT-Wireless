#!/usr/bin/env python3
"""Independent low-latency filtering for left/right eye signals."""

from __future__ import annotations

import collections
import math

import numpy as np


def _alpha(cutoff_hz: np.ndarray, elapsed_s: float) -> np.ndarray:
    rate = 2.0 * math.pi * cutoff_hz * elapsed_s
    return rate / (rate + 1.0)


class OneEuroVectorFilter:
    """Timestamp-aware One Euro filter for one vector-valued signal."""

    def __init__(
        self,
        dimensions: int,
        min_cutoff_hz: float = 1.5,
        beta: float = 0.08,
        derivative_cutoff_hz: float = 1.0,
    ) -> None:
        if dimensions < 1:
            raise ValueError("dimensions must be positive")
        self.dimensions = dimensions
        self.min_cutoff_hz = float(min_cutoff_hz)
        self.beta = float(beta)
        self.derivative_cutoff_hz = float(derivative_cutoff_hz)
        self._value: np.ndarray | None = None
        self._derivative = np.zeros(dimensions, dtype=np.float64)
        self._time_s: float | None = None

    def reset(self) -> None:
        self._value = None
        self._derivative.fill(0.0)
        self._time_s = None

    def update(self, value: np.ndarray, timestamp_s: float) -> np.ndarray:
        current = np.asarray(value, dtype=np.float64)
        if current.shape != (self.dimensions,):
            raise ValueError(
                f"expected a {self.dimensions}-component vector, got {current.shape}"
            )
        if self._value is None or self._time_s is None:
            self._value = current.copy()
            self._time_s = float(timestamp_s)
            return current.copy()

        elapsed = float(timestamp_s) - self._time_s
        self._time_s = float(timestamp_s)
        if not math.isfinite(elapsed) or elapsed <= 1e-6:
            return self._value.copy()
        elapsed = min(elapsed, 0.25)

        raw_derivative = (current - self._value) / elapsed
        derivative_alpha = _alpha(
            np.full(self.dimensions, self.derivative_cutoff_hz), elapsed
        )
        self._derivative += derivative_alpha * (raw_derivative - self._derivative)
        cutoff = self.min_cutoff_hz + self.beta * np.abs(self._derivative)
        value_alpha = _alpha(cutoff, elapsed)
        self._value += value_alpha * (current - self._value)
        return self._value.copy()


class IndependentEyeFilter:
    """Three-frame median plus distinct adaptive filters for the two eyes."""

    def __init__(
        self,
        dimensions: int,
        median_window: int = 3,
        min_cutoff_hz: float = 1.5,
        beta: float = 0.08,
        derivative_cutoff_hz: float = 1.0,
    ) -> None:
        if median_window < 1 or median_window % 2 == 0:
            raise ValueError("median_window must be a positive odd number")
        self.dimensions = dimensions
        self.median_window = median_window
        self.left_history: collections.deque[np.ndarray] = collections.deque(
            maxlen=median_window
        )
        self.right_history: collections.deque[np.ndarray] = collections.deque(
            maxlen=median_window
        )
        arguments = (dimensions, min_cutoff_hz, beta, derivative_cutoff_hz)
        self.left = OneEuroVectorFilter(*arguments)
        self.right = OneEuroVectorFilter(*arguments)

    def reset(self) -> None:
        self.left_history.clear()
        self.right_history.clear()
        self.left.reset()
        self.right.reset()

    def _update_eye(
        self,
        value: np.ndarray,
        timestamp_s: float,
        valid: bool,
        history: collections.deque[np.ndarray],
        eye_filter: OneEuroVectorFilter,
    ) -> np.ndarray:
        if not valid:
            history.clear()
            eye_filter.reset()
            return np.zeros(self.dimensions, dtype=np.float64)
        history.append(value.copy())
        median = np.median(np.stack(history), axis=0)
        return eye_filter.update(median, timestamp_s)

    def update(
        self,
        left: np.ndarray,
        right: np.ndarray,
        timestamp_s: float,
        *,
        left_valid: bool = True,
        right_valid: bool = True,
    ) -> tuple[np.ndarray, np.ndarray]:
        left_value = np.asarray(left, dtype=np.float64)
        right_value = np.asarray(right, dtype=np.float64)
        expected = (self.dimensions,)
        if left_value.shape != expected or right_value.shape != expected:
            raise ValueError(f"both eye values must have shape {expected}")
        return (
            self._update_eye(left_value, timestamp_s, left_valid, self.left_history, self.left),
            self._update_eye(right_value, timestamp_s, right_valid, self.right_history, self.right),
        )
