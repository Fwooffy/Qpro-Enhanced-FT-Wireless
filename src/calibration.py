#!/usr/bin/env python3
"""Guided whole-face calibration plan and session metadata."""

from __future__ import annotations

import json
import math
import textwrap
import threading
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path

import cv2
import numpy as np


@dataclass(frozen=True)
class CalibrationStep:
    name: str
    instruction: str
    seconds: float
    targets: dict[str, float] = field(default_factory=dict)
    tags: tuple[str, ...] = ()
    pattern: str = "free"


STEPS = [
    CalibrationStep("Neutral", "Relax your whole face and look straight ahead.", 5),
    CalibrationStep("Look left", "Keep your head still. Move both eyes slowly left, center, left.", 4),
    CalibrationStep("Look right", "Keep your head still. Move both eyes slowly right, center, right.", 4),
    CalibrationStep("Look up", "Keep your head still. Move both eyes slowly up, center, up.", 4),
    CalibrationStep("Look down", "Keep your head still. Move both eyes slowly down, center, down.", 4),
    CalibrationStep("Natural blinks", "Blink normally several times, returning fully open each time.", 5),
    CalibrationStep("Slow blinks", "Slowly close and reopen both eyes through the full range.", 5),
    CalibrationStep("Left wink", "Repeat a left-eye wink while keeping the right eye open.", 4),
    CalibrationStep("Right wink", "Repeat a right-eye wink while keeping the left eye open.", 4),
    CalibrationStep("Eyes wide", "Open both eyes wide, relax, and repeat slowly.", 4),
    CalibrationStep("Eye squint", "Squint both eyes, relax, and repeat without closing fully.", 4),
    CalibrationStep("Brows up", "Raise both eyebrows from neutral to maximum and back, slowly.", 5),
    CalibrationStep("Inner brows", "Raise the inner ends of both eyebrows, relax, and repeat.", 5),
    CalibrationStep("Outer brows", "Raise the outer ends of both eyebrows, relax, and repeat.", 5),
    CalibrationStep("Brows down", "Lower and pinch both eyebrows as if frowning, then relax.", 5),
    CalibrationStep("Left brow", "Raise mainly the left eyebrow, relax, and repeat if possible.", 4),
    CalibrationStep("Right brow", "Raise mainly the right eyebrow, relax, and repeat if possible.", 4),
    CalibrationStep("Jaw open", "Open your jaw gradually to maximum, close, and repeat.", 5),
    CalibrationStep("Jaw left", "Move your jaw left from center and back several times.", 4),
    CalibrationStep("Jaw right", "Move your jaw right from center and back several times.", 4),
    CalibrationStep("Jaw forward", "Push your lower jaw forward, relax, and repeat.", 4),
    CalibrationStep("Smile", "Smile from neutral to maximum and back several times.", 5),
    CalibrationStep("Frown", "Pull both mouth corners down, relax, and repeat.", 5),
    CalibrationStep("Smile sides", "Alternate a left-sided and right-sided smile slowly.", 5),
    CalibrationStep("Pucker", "Pucker your lips tightly, relax, and repeat.", 5),
    CalibrationStep("Funnel O", "Make a large rounded OH shape, relax, and repeat.", 5),
    CalibrationStep("Stretch", "Stretch your lips wide horizontally, relax, and repeat.", 5),
    CalibrationStep("Press lips", "Press your lips together firmly, relax, and repeat.", 5),
    CalibrationStep("Suck lips", "Roll or suck both lips inward, release, and repeat.", 5),
    CalibrationStep("Upper lip", "Raise your upper lip and wrinkle your nose, then relax.", 5),
    CalibrationStep("Lower lip", "Pull your lower lip down, relax, and repeat.", 5),
    CalibrationStep("Cheeks puff", "Puff both cheeks, release, then alternate sides if possible.", 5),
    CalibrationStep("Cheeks suck", "Suck both cheeks inward, release, and repeat.", 5),
    CalibrationStep("Tongue out", "Extend your tongue gradually, retract it, and repeat.", 5),
    CalibrationStep("Tongue motion", "Move your visible tongue left, right, up, and down.", 6),
    CalibrationStep("Vowel sequence", "Repeat slowly: EE, AH, OH, OO, then return to neutral.", 8),
    CalibrationStep("Natural speech", "Speak naturally and clearly for this entire step.", 10),
    CalibrationStep("Combined motion", "Mix gaze, blinks, brows, smiles, and speech naturally.", 10),
    CalibrationStep("Final neutral", "Relax your face and look straight ahead again.", 5),
]


class CalibrationSession:
    def __init__(
        self,
        path: str | Path,
        steps: list[CalibrationStep] | None = None,
        session_type: str = "whole-face-v1",
    ) -> None:
        self.path = Path(path).resolve()
        self.steps = list(steps if steps is not None else STEPS)
        if not self.steps:
            raise ValueError("A calibration session needs at least one step")
        self.session_type = session_type
        self.started_monotonic_ns: int | None = None
        self.started_wall_ns: int | None = None
        self.finished_monotonic_ns: int | None = None
        self.completed = False
        self.current_index = 0
        self.phase = "instruction"
        self.phase_started_ns: int | None = None
        self.active_started_ns: int | None = None
        self.events: list[dict[str, object]] = []
        self.skipped_steps: set[int] = set()

    @property
    def total_seconds(self) -> float:
        return sum(step.seconds for step in self.steps)

    def start(self, now_monotonic_ns: int) -> None:
        if self.started_monotonic_ns is None:
            self.started_monotonic_ns = now_monotonic_ns
            self.started_wall_ns = time.time_ns()
            self.phase_started_ns = now_monotonic_ns
            self.events.append(
                {
                    "event": "session_started",
                    "step": self.current_index,
                    "monotonicNs": now_monotonic_ns,
                }
            )

    def status(
        self, now_monotonic_ns: int
    ) -> tuple[int, CalibrationStep, str, float, float]:
        self.start(now_monotonic_ns)
        step = self.steps[min(self.current_index, len(self.steps) - 1)]
        elapsed = (
            (now_monotonic_ns - self.active_started_ns) / 1e9
            if self.active_started_ns is not None
            and self.phase in ("active", "ready")
            else 0.0
        )
        remaining = max(0.0, step.seconds - elapsed)
        return self.current_index, step, self.phase, elapsed, remaining

    def update(self, now_monotonic_ns: int) -> bool:
        """Return True once when recommended coverage is reached."""
        _index, step, phase, elapsed, _remaining = self.status(now_monotonic_ns)
        if phase == "active" and elapsed >= step.seconds:
            self.phase = "ready"
            self.phase_started_ns = now_monotonic_ns
            self.events.append(
                {
                    "event": "recommended_coverage_reached",
                    "step": self.current_index,
                    "monotonicNs": now_monotonic_ns,
                }
            )
            return True
        return False

    def handle_key(self, key: str, now_monotonic_ns: int) -> str | None:
        self.start(now_monotonic_ns)
        normalized = key.lower()
        if normalized in (" ", "\r", "\n"):
            if self.phase == "instruction":
                self.phase = "active"
                self.phase_started_ns = now_monotonic_ns
                self.active_started_ns = now_monotonic_ns
                self.events.append(
                    {
                        "event": "step_started",
                        "step": self.current_index,
                        "monotonicNs": now_monotonic_ns,
                    }
                )
                return "started"
            if self.phase == "active":
                return "not_ready"
            if self.phase == "ready":
                self.events.append(
                    {
                        "event": "step_finished",
                        "step": self.current_index,
                        "monotonicNs": now_monotonic_ns,
                    }
                )
                self.current_index += 1
                self.active_started_ns = None
                self.phase_started_ns = now_monotonic_ns
                if self.current_index >= len(self.steps):
                    self.current_index = len(self.steps) - 1
                    self.phase = "finished"
                    self.completed = True
                    return "completed"
                self.phase = "instruction"
                return "advanced"
        elif normalized == "r" and self.phase in ("active", "ready"):
            self.phase = "active"
            self.phase_started_ns = now_monotonic_ns
            self.active_started_ns = now_monotonic_ns
            self.events.append(
                {
                    "event": "step_restarted",
                    "step": self.current_index,
                    "monotonicNs": now_monotonic_ns,
                }
            )
            return "restarted"
        elif normalized == "k" and self.phase in ("instruction", "active", "ready"):
            self.events.append(
                {
                    "event": "step_skipped",
                    "step": self.current_index,
                    "monotonicNs": now_monotonic_ns,
                }
            )
            self.skipped_steps.add(self.current_index)
            self.current_index += 1
            self.active_started_ns = None
            self.phase_started_ns = now_monotonic_ns
            if self.current_index >= len(self.steps):
                self.current_index = len(self.steps) - 1
                self.phase = "finished"
                self.completed = True
                return "completed"
            self.phase = "instruction"
            return "skipped"
        elif normalized in ("b", "\x08"):
            self.current_index = max(0, self.current_index - 1)
            self.phase = "instruction"
            self.phase_started_ns = now_monotonic_ns
            self.active_started_ns = None
            self.events.append(
                {
                    "event": "step_back",
                    "step": self.current_index,
                    "monotonicNs": now_monotonic_ns,
                }
            )
            return "back"
        return None

    def finish(self, now_monotonic_ns: int, completed: bool) -> None:
        self.finished_monotonic_ns = now_monotonic_ns
        self.completed = completed
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(
            json.dumps(
                {
                    "version": 1,
                    "sessionType": self.session_type,
                    "completed": completed,
                    "startedMonotonicNs": self.started_monotonic_ns,
                    "startedWallNs": self.started_wall_ns,
                    "finishedMonotonicNs": self.finished_monotonic_ns,
                    "minimumRecommendedSeconds": self.total_seconds,
                    "userPaced": True,
                    "skippedSteps": sorted(self.skipped_steps),
                    "events": self.events,
                    "steps": [asdict(step) for step in self.steps],
                },
                indent=2,
            ),
            encoding="utf-8",
        )


def play_cue(cue: str) -> None:
    def worker() -> None:
        try:
            import winsound
        except ImportError:
            return
        try:
            notes = {
                "started": [(660, 100)],
                "ready": [(880, 140), (1100, 180)],
                "advanced": [(620, 90)],
                "restarted": [(520, 90), (520, 90)],
                "back": [(500, 120)],
                "not_ready": [(420, 110)],
                "completed": [(660, 120), (880, 120), (1100, 220)],
                "skipped": [(420, 100), (620, 130)],
            }.get(cue, [(700, 100)])
            for frequency, duration in notes:
                winsound.Beep(frequency, duration)
        except (OSError, RuntimeError):
            winsound.MessageBeep(winsound.MB_OK)

    threading.Thread(target=worker, name="calibration-cue", daemon=True).start()


def prompt_image(
    index: int,
    step: CalibrationStep,
    phase: str,
    elapsed: float,
    remaining: float,
    labels_live: bool = True,
    total_steps: int | None = None,
) -> np.ndarray:
    image = np.zeros((650, 1100, 3), dtype=np.uint8)
    cv2.putText(
        image,
        f"STEP {index + 1}/{total_steps or len(STEPS)}   {step.name}",
        (35, 60),
        cv2.FONT_HERSHEY_SIMPLEX,
        1.15,
        (80, 255, 120),
        2,
        cv2.LINE_AA,
    )
    cv2.putText(
        image,
        "LABELS LIVE" if labels_live else "LABELS FROZEN",
        (845, 58),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.6,
        (80, 255, 120) if labels_live else (80, 80, 255),
        2,
        cv2.LINE_AA,
    )
    lines = textwrap.wrap(step.instruction, width=58)
    for line_index, line in enumerate(lines[:3]):
        cv2.putText(
            image,
            line,
            (35, 125 + line_index * 48),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.85,
            (240, 240, 240),
            2,
            cv2.LINE_AA,
        )
    if not labels_live:
        status = "LABELS FROZEN: Start face tracking, then move your face and eyes."
        status_color = (80, 80, 255)
    elif phase == "instruction":
        status = "Read calmly. Press SPACE or ENTER when you are ready to begin."
        status_color = (80, 220, 255)
    elif phase == "active":
        status = (
            f"Perform slowly. Audio cue in {remaining:4.1f}s. "
            "Wait for the cue | R = restart"
        )
        status_color = (80, 255, 120)
    elif phase == "ready":
        status = "Coverage reached. Continue if useful, or press SPACE for the next step."
        status_color = (80, 220, 255)
    else:
        status = "Calibration complete."
        status_color = (80, 255, 120)
    cv2.putText(
        image, status, (35, 285), cv2.FONT_HERSHEY_SIMPLEX, 0.65,
        status_color, 2, cv2.LINE_AA,
    )
    if step.pattern == "pulse":
        cv2.putText(
            image,
            "METER 0 = FULLY NEUTRAL / RETRACTED    METER 1 = FULL NAMED POSE",
            (35, 245), cv2.FONT_HERSHEY_SIMPLEX, 0.57,
            (80, 220, 255), 2, cv2.LINE_AA,
        )
    if step.pattern == "pulse" and phase in ("active", "ready"):
        activation = target_activation(step, elapsed)
        cv2.putText(
            image,
            f"TARGET INTENSITY  {activation:0.2f}   match the meter smoothly",
            (35, 365), cv2.FONT_HERSHEY_SIMPLEX, 0.62,
            (80, 220, 255), 2, cv2.LINE_AA,
        )
        cv2.rectangle(image, (35, 390), (1035, 425), (90, 90, 90), 1)
        cv2.rectangle(
            image, (36, 391), (36 + round(998 * activation), 424),
            (80, 220, 255), -1,
        )
    cv2.putText(
        image,
        "Controls: SPACE/ENTER start or advance | R restart | K skip impossible shape | BACKSPACE previous | Q quit",
        (35, 325),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.55,
        (190, 190, 190),
        1,
        cv2.LINE_AA,
    )

    marker_offsets = {
        "Look left": (-360, 0),
        "Look right": (360, 0),
        "Look up": (0, -210),
        "Look down": (0, 210),
    }
    center = (550, 485)
    offset = marker_offsets.get(step.name, (0, 0))
    amount = (
        abs(math.sin(math.pi * elapsed / 2.0))
        if phase in ("active", "ready") and step.name in marker_offsets
        else 0.0
    )
    marker = (
        round(center[0] + offset[0] * amount),
        round(center[1] + offset[1] * amount),
    )
    cv2.line(image, (center[0] - 18, center[1]), (center[0] + 18, center[1]),
             (80, 80, 80), 1, cv2.LINE_AA)
    cv2.line(image, (center[0], center[1] - 18), (center[0], center[1] + 18),
             (80, 80, 80), 1, cv2.LINE_AA)
    cv2.circle(image, marker, 22, (0, 230, 255), 3, cv2.LINE_AA)
    cv2.circle(image, marker, 5, (255, 255, 255), -1, cv2.LINE_AA)
    marker_note = (
        "Follow the moving target with your eyes; keep your head still."
        if step.name in marker_offsets and phase in ("active", "ready")
        else "The target moves automatically during gaze steps."
    )
    cv2.putText(
        image, marker_note, (245, 625), cv2.FONT_HERSHEY_SIMPLEX, 0.6,
        (170, 170, 170), 1, cv2.LINE_AA,
    )
    return image


def target_activation(step: CalibrationStep, elapsed: float) -> float:
    """Deterministic neutral/ramp/hold/ramp label for prompted tongue motion."""
    if step.pattern != "pulse":
        return 1.0
    cycle = 4.0
    phase = max(0.0, float(elapsed)) % cycle
    if phase < 0.6:
        return 0.0
    if phase < 1.4:
        amount = (phase - 0.6) / 0.8
        return 0.5 - 0.5 * math.cos(math.pi * amount)
    if phase < 2.6:
        return 1.0
    if phase < 3.4:
        amount = (phase - 2.6) / 0.8
        return 0.5 + 0.5 * math.cos(math.pi * amount)
    return 0.0
