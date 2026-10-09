"""Bound pupil work to the newest eye pair without delaying camera reception."""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass

import numpy as np

from pupil_dilation import PupilBroadcaster, RelativePupilTracker
from gpu_readback import GPUReadbackCancelled


@dataclass(frozen=True)
class PupilResult:
    values: tuple[float | None, float | None]
    detections: tuple[object | None, object | None]
    states: tuple[str, str]
    backend: str
    device: str
    notice: str | None
    processing_ms: float
    age_ms: float
    dropped_frames: int
    completed_at: float
    device_name: str = "CPU"
    worker_cpu_ms: float = 0.0


class PupilInferenceWorker:
    """One pending pair, one worker, and no valid output from stale frames.

    The socket reader retains an immutable frame payload. If detection falls
    behind, replacing the pending pair avoids processing a queue of old eyes.
    A stalled feed expires independently of the detector's occlusion hold.
    """

    maximum_age_seconds = 0.5

    def __init__(self, tracker: RelativePupilTracker, broadcaster: PupilBroadcaster) -> None:
        self.tracker = tracker
        self.broadcaster = broadcaster
        self._condition = threading.Condition()
        self._pending: tuple[np.ndarray, list[int], float] | None = None
        self._latest: PupilResult | None = None
        self._error: BaseException | None = None
        self._dropped = 0
        self._running = True
        self._broadcaster_lock = threading.Lock()
        self._broadcaster_closed = False
        self._stop_event = threading.Event()
        set_cancelled = getattr(tracker, "set_readback_cancelled", None)
        if set_cancelled is not None:
            set_cancelled(self._stop_event.is_set)
        self._thread = threading.Thread(target=self._run, name="pupil-inference", daemon=True)
        self._thread.start()

    def submit(self, strip: np.ndarray, camera_ids: list[int]) -> None:
        with self._condition:
            if not self._running:
                return
            if self._pending is not None:
                self._dropped += 1
            self._pending = (strip, list(camera_ids), time.perf_counter())
            self._condition.notify()

    def latest(self) -> PupilResult | None:
        with self._condition:
            if self._error is not None:
                raise RuntimeError("Pupil inference worker failed") from self._error
            result = self._latest
        if result is None or (
            time.perf_counter() - result.completed_at + result.age_ms / 1000
            > self.maximum_age_seconds
        ):
            return None
        return result

    def close(self) -> None:
        self._stop_event.set()
        with self._condition:
            self._running = False
            self._pending = None
            self._condition.notify_all()
        # Immediately clear the override even if a GPU kernel has stalled.
        # The same lock serializes sends and close, so it cannot publish a
        # result after the final invalid packet or close the socket twice.
        self._close_broadcaster()
        if self._thread.ident is not None:
            self._thread.join(timeout=2.0)

    def _send(self, values: tuple[float | None, float | None]) -> None:
        with self._broadcaster_lock:
            if not self._broadcaster_closed:
                self.broadcaster.send(values)

    def _close_broadcaster(self) -> None:
        with self._broadcaster_lock:
            if not self._broadcaster_closed:
                self._broadcaster_closed = True
                self.broadcaster.close()

    def _run(self) -> None:
        last_input = time.perf_counter()
        try:
            while True:
                with self._condition:
                    self._condition.wait_for(lambda: not self._running or self._pending is not None, timeout=0.1)
                    if not self._running:
                        return
                    item = self._pending
                    self._pending = None
                if item is None:
                    if time.perf_counter() - last_input > self.maximum_age_seconds:
                        self._send((None, None))
                    continue
                strip, camera_ids, submitted_at = item
                last_input = submitted_at
                started = time.perf_counter()
                cpu_started = time.thread_time()
                if started - submitted_at > self.maximum_age_seconds:
                    self._send((None, None))
                    continue
                values = self.tracker.update(strip, camera_ids)
                worker_cpu_ms = (time.thread_time() - cpu_started) * 1000
                finished = time.perf_counter()
                stale = finished - submitted_at > self.maximum_age_seconds
                result = PupilResult(
                    (None, None) if stale else values,
                    (None, None) if stale else self.tracker.detections,
                    ("processing too slow", "processing too slow") if stale else
                    (self.tracker.eyes[0].status, self.tracker.eyes[1].status),
                    self.tracker.backend, str(self.tracker.device), self.tracker.backend_notice,
                    (finished - started) * 1000, (finished - submitted_at) * 1000,
                    self._dropped, finished, getattr(self.tracker, "device_name", "CPU"),
                    worker_cpu_ms,
                )
                with self._condition:
                    if not self._running:
                        return
                    self._latest = result
                    self._send(result.values)
        except GPUReadbackCancelled as error:
            if not self._stop_event.is_set():
                with self._condition:
                    self._error = error
        except BaseException as error:
            with self._condition:
                self._error = error
        finally:
            self._close_broadcaster()
            set_cancelled = getattr(self.tracker, "set_readback_cancelled", None)
            if set_cancelled is not None:
                set_cancelled(None)
