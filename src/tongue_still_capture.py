#!/usr/bin/env python3
"""User-paced, exact-frame stereo tongue dataset capture."""

from __future__ import annotations

import json
import textwrap
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path

import cv2
import numpy as np

from capture_format import CaptureWriter


@dataclass(frozen=True)
class TongueStillPrompt:
    name: str
    instruction: str
    targets: dict[str, float] = field(default_factory=dict)
    context: str = "relaxed jaw"
    guide: str = "Tongue fully hidden"
    recommended_captures: int = 6
    minimum_captures: int = 4


def prompt(
    name: str,
    instruction: str,
    targets: dict[str, float] | None = None,
    *,
    context: str = "relaxed jaw",
    guide: str = "Tongue fully hidden",
    captures: int = 6,
) -> TongueStillPrompt:
    return TongueStillPrompt(
        name=name,
        instruction=instruction,
        targets=targets or {},
        context=context,
        guide=guide,
        recommended_captures=captures,
        minimum_captures=max(3, captures - 2),
    )


def visible(**targets: float) -> dict[str, float]:
    return {"visibility": 1.0, **targets}


# Definitions follow VRCFT Unified Expressions. Horizontal is wearer-relative:
# positive/right and negative/left. Vertical is positive/up and negative/down.
TONGUE_STILL_PROMPTS = [
    # Hard negatives are deliberately varied. They teach visibility separately
    # from jaw, lips, teeth, cheeks, and ordinary speech.
    prompt("Neutral, lips closed", "Relax your whole lower face; keep the tongue fully inside."),
    prompt("Neutral, lips parted", "Part your lips slightly while the tongue stays behind the teeth.", context="lips slightly parted"),
    prompt("Smile, lips closed", "Hold a broad closed-mouth smile; tongue fully hidden.", context="closed-mouth smile"),
    prompt("Smile, teeth visible", "Show your teeth in a broad smile; tongue stays behind them.", context="smile + teeth"),
    prompt("Jaw half open", "Open your jaw halfway with the tongue resting inside.", context="jaw half open"),
    prompt("Jaw fully open", "Open as wide as comfortable without showing your tongue.", context="jaw wide"),
    prompt("Pucker", "Pucker your lips firmly; tongue stays inside.", context="pucker"),
    prompt("Funnel / O", "Make a large rounded O shape; tongue remains hidden.", context="rounded lips"),
    prompt("Pressed lips", "Press both lips together firmly; tongue stays inside.", context="lip press"),
    prompt("Mouth left", "Pull your mouth toward your left; tongue hidden.", context="asymmetric mouth"),
    prompt("Mouth right", "Pull your mouth toward your right; tongue hidden.", context="asymmetric mouth"),
    prompt("Cheeks puffed", "Puff both cheeks while keeping the tongue hidden.", context="cheek puff"),
    prompt("Cheeks sucked", "Suck both cheeks inward while keeping the tongue hidden.", context="cheek suck"),
    prompt("Teeth / lower lip down", "Expose the lower teeth by pulling the lower lip down; tongue hidden.", context="teeth visible"),
    prompt("Speech: EE", "Hold an EE mouth shape; tongue stays inside.", context="speech / teeth"),
    prompt("Speech: AH", "Hold an AH mouth shape; tongue stays inside.", context="speech / open jaw"),
    prompt("Speech: OH", "Hold an OH mouth shape; tongue stays inside.", context="speech / rounded lips"),

    # Extension strength is explicit rather than inferred from a delayed meter.
    prompt("Tongue tip visible (25%)", "Show only the very tip. Hold completely still for each capture.",
           visible(extension=0.25), guide="TongueOut 0.25", captures=8),
    prompt("Tongue halfway out (50%)", "Extend straight forward to about half of your comfortable maximum.",
           visible(extension=0.50), guide="TongueOut 0.50", captures=8),
    prompt("Tongue mostly out (75%)", "Extend straight forward to about three quarters of maximum.",
           visible(extension=0.75), guide="TongueOut 0.75", captures=8),
    prompt("Tongue fully out (100%)", "Extend straight forward as far as is comfortable.",
           visible(extension=1.0), guide="TongueOut 1.00", captures=8),
    prompt("Tongue out, lips closed around it", "Gently close your lips around a fully extended tongue.",
           visible(extension=1.0), context="closed lips", guide="TongueOut 1.00", captures=8),
    prompt("Tongue out, teeth visible", "Show your teeth while holding the tongue fully extended.",
           visible(extension=1.0), context="teeth visible", guide="TongueOut 1.00", captures=8),
    prompt("Tongue out, jaw wide", "Open wide and hold the tongue fully extended.",
           visible(extension=1.0), context="jaw wide", guide="TongueOut 1.00", captures=8),
    prompt("Tongue out while smiling", "Smile broadly while holding the tongue fully extended.",
           visible(extension=1.0), context="smile", guide="TongueOut 1.00", captures=8),

    # Cardinal and diagonal pointing. Capture moderate and full variants so the
    # signed axes can interpolate rather than only seeing endpoints.
    prompt("Tongue left (50%)", "Point the visible tip halfway toward your left.",
           visible(extension=0.75, horizontal=-0.5), guide="TongueLeft 0.50"),
    prompt("Tongue left (100%)", "Point the visible tip as far toward your left as comfortable.",
           visible(extension=0.75, horizontal=-1.0), guide="TongueLeft 1.00"),
    prompt("Tongue right (50%)", "Point the visible tip halfway toward your right.",
           visible(extension=0.75, horizontal=0.5), guide="TongueRight 0.50"),
    prompt("Tongue right (100%)", "Point the visible tip as far toward your right as comfortable.",
           visible(extension=0.75, horizontal=1.0), guide="TongueRight 1.00"),
    prompt("Tongue up (50%)", "Point the visible tip halfway upward.",
           visible(extension=0.75, vertical=0.5), guide="TongueUp 0.50"),
    prompt("Tongue up (100%)", "Point the visible tip as far upward as comfortable.",
           visible(extension=0.75, vertical=1.0), guide="TongueUp 1.00"),
    prompt("Tongue down (50%)", "Point the visible tip halfway downward.",
           visible(extension=0.75, vertical=-0.5), guide="TongueDown 0.50"),
    prompt("Tongue down (100%)", "Point the visible tip as far downward as comfortable.",
           visible(extension=0.75, vertical=-1.0), guide="TongueDown 1.00"),
    prompt("Tongue upper-left", "Point diagonally up and toward your left.",
           visible(extension=0.75, horizontal=-1.0, vertical=1.0), guide="Up + Left"),
    prompt("Tongue upper-right", "Point diagonally up and toward your right.",
           visible(extension=0.75, horizontal=1.0, vertical=1.0), guide="Up + Right"),
    prompt("Tongue lower-left", "Point diagonally down and toward your left.",
           visible(extension=0.75, horizontal=-1.0, vertical=-1.0), guide="Down + Left"),
    prompt("Tongue lower-right", "Point diagonally down and toward your right.",
           visible(extension=0.75, horizontal=1.0, vertical=-1.0), guide="Down + Right"),

    # Fixed intermediate positions reveal whether the model can resolve X and
    # Y together. A sweep would give every captured frame the same label even
    # though the tip moves, so each card asks for one held pose.
    prompt("Tongue upper-left (half diagonal)", "Hold the visible tip halfway up and halfway toward your left.",
           visible(extension=0.75, horizontal=-0.5, vertical=0.5), guide="Up 0.50 + Left 0.50"),
    prompt("Tongue upper-right (half diagonal)", "Hold the visible tip halfway up and halfway toward your right.",
           visible(extension=0.75, horizontal=0.5, vertical=0.5), guide="Up 0.50 + Right 0.50"),
    prompt("Tongue lower-left (half diagonal)", "Hold the visible tip halfway down and halfway toward your left.",
           visible(extension=0.75, horizontal=-0.5, vertical=-0.5), guide="Down 0.50 + Left 0.50"),
    prompt("Tongue lower-right (half diagonal)", "Hold the visible tip halfway down and halfway toward your right.",
           visible(extension=0.75, horizontal=0.5, vertical=-0.5), guide="Down 0.50 + Right 0.50"),
    prompt("Tongue fully up, half left", "Hold the tip fully upward and halfway toward your left, visible in both views.",
           visible(extension=0.75, horizontal=-0.5, vertical=1.0), guide="Up 1.00 + Left 0.50"),
    prompt("Tongue fully up, half right", "Hold the tip fully upward and halfway toward your right, visible in both views.",
           visible(extension=0.75, horizontal=0.5, vertical=1.0), guide="Up 1.00 + Right 0.50"),
    prompt("Tongue fully down, half left", "Hold the tip fully downward and halfway toward your left, visible in both views.",
           visible(extension=0.75, horizontal=-0.5, vertical=-1.0), guide="Down 1.00 + Left 0.50"),
    prompt("Tongue fully down, half right", "Hold the tip fully downward and halfway toward your right, visible in both views.",
           visible(extension=0.75, horizontal=0.5, vertical=-1.0), guide="Down 1.00 + Right 0.50"),
    prompt("Tongue far left, half up", "Hold the tip far toward your left and halfway upward, visible in both views.",
           visible(extension=0.75, horizontal=-1.0, vertical=0.5), guide="Left 1.00 + Up 0.50"),
    prompt("Tongue far right, half up", "Hold the tip far toward your right and halfway upward, visible in both views.",
           visible(extension=0.75, horizontal=1.0, vertical=0.5), guide="Right 1.00 + Up 0.50"),
    prompt("Tongue far left, half down", "Hold the tip far toward your left and halfway downward, visible in both views.",
           visible(extension=0.75, horizontal=-1.0, vertical=-0.5), guide="Left 1.00 + Down 0.50"),
    prompt("Tongue far right, half down", "Hold the tip far toward your right and halfway downward, visible in both views.",
           visible(extension=0.75, horizontal=1.0, vertical=-0.5), guide="Right 1.00 + Down 0.50"),

    # The visible tip is hardest to distinguish when extension is small or
    # lips, teeth, and fit change. Match these conditions to hidden cards above.
    prompt("Tongue tip with smile and teeth", "Show only a clearly visible centered tip while smiling with teeth visible.",
           visible(extension=0.25), context="smile + teeth", guide="TongueOut 0.25", captures=8),
    prompt("Tongue tip with jaw half open", "Keep the jaw halfway open and show only a clearly visible centered tip.",
           visible(extension=0.25), context="jaw half open", guide="TongueOut 0.25", captures=8),
    prompt("Tongue tip with rounded lips", "Make a comfortable rounded mouth while showing a centered tip in both camera views.",
           visible(extension=0.25), context="rounded lips", guide="TongueOut 0.25", captures=8),
    prompt("Small headset-fit change, tongue hidden", "Make a small comfortable headset-fit adjustment between stills; keep the tongue hidden.",
           context="fit variation"),
    prompt("Small headset-fit change, tongue visible", "Repeat small comfortable fit adjustments with the tongue centered and clearly visible in both views.",
           visible(extension=0.75), context="fit variation", guide="TongueOut 0.75"),

    # Keep the two tip-curvature poses that remain visible from the lower
    # cameras. Roll/squish/flat/twist were removed: the Quest Pro views do not
    # expose those shapes consistently enough for honest supervision.
    prompt("Bend tongue downward", "Arch the tongue upward and then bend the tip downward.",
           visible(extension=0.75, bend_down=1.0), guide="TongueBendDown 1.00"),
    prompt("Curl tongue upward", "Arch the tongue downward and then curl the tip upward.",
           visible(extension=0.75, curl_up=1.0), guide="TongueCurlUp 1.00"),

    # A small, balanced clothing pair teaches that an edge entering the camera
    # is not itself a tongue, without dominating the full curriculum.
    prompt("Clothing edge, tongue hidden", "Let a shirt or collar edge barely enter the bottom of the views. Keep the tongue fully hidden.", context="environment hard negative"),
    prompt("Clothing edge, tongue visible", "Keep the same shirt or collar edge barely visible while extending the tongue straight forward.", visible(extension=0.75), context="matched environment positive", guide="TongueOut 0.75"),
]


# A compact follow-up set for real-world mistakes discovered after the broad
# calibration. It deliberately pairs nuisance conditions with matching visible
# tongue poses so the network cannot learn that a shifted headset or shirt edge
# always means "hidden".
TONGUE_CORRECTION_PROMPTS = [
    prompt("Slight smile, tongue hidden", "Hold the exact small smile that caused false positives. Keep the tongue fully inside; vary smile strength slightly between captures.", context="targeted slight-smile negative", captures=10),
    prompt("Slight smile, lips parted", "Smile gently with lips just parted and tongue behind the teeth. Vary the opening slightly.", context="targeted smile negative", captures=10),
    prompt("Smile transition, tongue hidden", "Capture several different points between neutral and a broad smile. The tongue must remain hidden for every still.", context="variable smile negative", captures=10),
    prompt("Shirt edge barely visible", "Keep a neutral mouth and deliberately reproduce the shirt edge entering the bottom of either camera. Vary how much is visible.", context="environment hard negative", captures=12),
    prompt("Shirt edge plus slight smile", "Keep the tongue hidden while combining the troublesome shirt edge with a small smile.", context="environment + smile negative", captures=10),
    prompt("Shirt edge plus open jaw", "Let the shirt edge enter view while opening the jaw to several comfortable amounts. Tongue stays hidden.", context="environment + jaw negative", captures=8),
    prompt("Headset shifted upward", "Move the headset slightly higher than normal; sample neutral, parted, and slight-smile mouths with tongue hidden.", context="fit-shift negative", captures=8),
    prompt("Headset shifted downward", "Move the headset slightly lower than normal; sample neutral, parted, and slight-smile mouths with tongue hidden.", context="fit-shift negative", captures=8),
    prompt("Headset shifted left/right", "Shift the headset a little left and right between captures. Keep the tongue hidden and include a few small smiles.", context="fit-shift negative", captures=10),
    prompt("Chin tucked, tongue hidden", "Tuck your chin so clothing approaches the camera view. Vary jaw opening and keep the tongue fully hidden.", context="pose/environment negative", captures=10),
    prompt("Speech and lip motion", "Capture varied EE, AH, OH, pucker, and lip-press shapes. Tongue remains hidden in every still.", context="mixed expression negative", captures=12),

    prompt("Tongue tip with slight smile", "Show only the tongue tip while holding a small smile. Include small natural fit variations.", visible(extension=0.25), context="matched smile positive", guide="TongueOut 0.25", captures=10),
    prompt("Tongue fully out with slight smile", "Hold a small smile with the tongue fully extended.", visible(extension=1.0), context="matched smile positive", guide="TongueOut 1.00", captures=10),
    prompt("Tongue tip with shirt visible", "Show only the tongue tip while deliberately keeping the shirt edge barely in view.", visible(extension=0.25), context="matched environment positive", guide="TongueOut 0.25", captures=10),
    prompt("Tongue out with shirt visible", "Keep the shirt edge in view while holding the tongue straight forward at roughly 75% extension.", visible(extension=0.75), context="matched environment positive", guide="TongueOut 0.75", captures=12),
    prompt("Tongue out, shifted headset", "Extend straight forward while repeating small up/down/left/right headset-fit shifts between captures.", visible(extension=0.75), context="matched fit-shift positive", guide="TongueOut 0.75", captures=12),

    prompt("Direction left (75%)", "Hold the visible tip at roughly three-quarters of your comfortable wearer-left range for every capture.", visible(extension=0.75, horizontal=-0.75), guide="TongueLeft 0.75", captures=12),
    prompt("Direction right (75%)", "Hold the visible tip at roughly three-quarters of your comfortable wearer-right range for every capture.", visible(extension=0.75, horizontal=0.75), guide="TongueRight 0.75", captures=12),
    prompt("Direction up (75%)", "Hold the visible tip at roughly three-quarters of your comfortable upward range for every capture.", visible(extension=0.75, vertical=0.75), guide="TongueUp 0.75", captures=12),
    prompt("Direction down (75%)", "Hold the visible tip at roughly three-quarters of your comfortable downward range for every capture.", visible(extension=0.75, vertical=-0.75), guide="TongueDown 0.75", captures=12),
    prompt("Direction upper-left (75%)", "Hold a fixed three-quarter diagonal upper-left pose for every capture.", visible(extension=0.75, horizontal=-0.75, vertical=0.75), guide="Up + Left 0.75", captures=10),
    prompt("Direction upper-right (75%)", "Hold a fixed three-quarter diagonal upper-right pose for every capture.", visible(extension=0.75, horizontal=0.75, vertical=0.75), guide="Up + Right 0.75", captures=10),
    prompt("Direction lower-left (75%)", "Hold a fixed three-quarter diagonal lower-left pose for every capture.", visible(extension=0.75, horizontal=-0.75, vertical=-0.75), guide="Down + Left 0.75", captures=10),
    prompt("Direction lower-right (75%)", "Hold a fixed three-quarter diagonal lower-right pose for every capture.", visible(extension=0.75, horizontal=0.75, vertical=-0.75), guide="Down + Right 0.75", captures=10),
]


# A wearer-friendly quick refinement. Every card needs at most six intentional
# stills and concentrates on the outputs the lower cameras can actually see.
# The broad full dataset remains available for uncommon expressions and fit.
TONGUE_REFINEMENT_PROMPTS = [
    prompt("Neutral and natural mouth", "Capture relaxed, slightly parted, and small-smile poses. Keep the tongue fully hidden in every still.", context="ordinary mouth negatives", captures=6),
    prompt("Jaw motion, tongue hidden", "Hold a few comfortable jaw openings with lips closed or parted. Keep the tongue behind the teeth.", context="jaw-motion negatives", captures=6),
    prompt("Open mouth, tongue hidden", "Open your mouth comfortably and show teeth if desired, but keep the tongue fully inside.", context="open-mouth negatives", captures=6),
    prompt("Smile and teeth, tongue hidden", "Smile naturally, then show your teeth in some captures while keeping the tongue fully hidden.", context="smile and teeth hard negatives", captures=6),

    prompt("Straight tongue, 25%", "Show only the centered tip with no intentional up/down or left/right component.", visible(extension=0.25), context="neutral-axis anchor", guide="TongueOut 0.25; X/Y zero", captures=6),
    prompt("Straight tongue, 50%", "Extend halfway straight ahead with the tip centered vertically and horizontally.", visible(extension=0.50), context="neutral-axis anchor", guide="TongueOut 0.50; X/Y zero", captures=6),
    prompt("Straight tongue, 100%", "Extend straight ahead as far as is comfortable while keeping the tip centered.", visible(extension=1.0), context="neutral-axis anchor", guide="TongueOut 1.00; X/Y zero", captures=6),
    prompt("Tongue tip with smile", "Keep a small smile while showing only the tongue tip. Hold each pose before pressing Space.", visible(extension=0.25), context="matched smile positive", guide="TongueOut 0.25", captures=6),
    prompt("Tongue left", "Point the visible tip firmly toward your left and hold it still.", visible(extension=0.75, horizontal=-0.85), guide="TongueLeft 0.85", captures=6),
    prompt("Tongue right", "Point the visible tip firmly toward your right and hold it still.", visible(extension=0.75, horizontal=0.85), guide="TongueRight 0.85", captures=6),
    prompt("Tongue up", "Point the visible tip firmly upward and hold it still.", visible(extension=0.75, vertical=0.85), guide="TongueUp 0.85", captures=6),
    prompt("Tongue down", "Point the visible tip firmly downward and hold it still.", visible(extension=0.75, vertical=-0.85), guide="TongueDown 0.85", captures=6),
    prompt("Tongue upper-left", "Point diagonally up and toward your left. Use a comfortable jaw opening.", visible(extension=0.75, horizontal=-0.75, vertical=0.75), guide="Up + Left 0.75", captures=6),
    prompt("Tongue upper-right", "Point diagonally up and toward your right. Use a comfortable jaw opening.", visible(extension=0.75, horizontal=0.75, vertical=0.75), guide="Up + Right 0.75", captures=6),
    prompt("Tongue lower-left", "Point diagonally down and toward your left. Use a comfortable jaw opening.", visible(extension=0.75, horizontal=-0.75, vertical=-0.75), guide="Down + Left 0.75", captures=6),
    prompt("Tongue lower-right", "Point diagonally down and toward your right. Use a comfortable jaw opening.", visible(extension=0.75, horizontal=0.75, vertical=-0.75), guide="Down + Right 0.75", captures=6),
]


# A focused follow-up for mixed-axis errors and lower-face occlusion. The first
# ten cards retain the original extreme arc targets. Moderate corners provide
# another fixed point on both axes, rather than an ambiguously labeled sweep.
# Facial-hair cards pair each hidden pose with a visible pose under the same
# jaw/fit condition. On visible cards, adjust fit safely and retake any still
# where hair obscures the tip in either camera panel.
TONGUE_ARC_PROMPTS = [
    prompt("Fully up, far left", "Point fully upward and as far toward your left as comfortable. Hold the fixed pose before each capture.", visible(extension=0.75, horizontal=-1.0, vertical=1.0), guide="Up 1.00 + Left 1.00", captures=6),
    prompt("Fully up, half left", "Point fully upward while moving only halfway toward your left. This is up 100%, left 50%.", visible(extension=0.75, horizontal=-0.5, vertical=1.0), guide="Up 1.00 + Left 0.50", captures=6),
    prompt("Fully up, centered", "Point fully upward with no intentional left or right component.", visible(extension=0.75, horizontal=0.0, vertical=1.0), guide="Up 1.00; X zero", captures=6),
    prompt("Fully up, half right", "Point fully upward while moving only halfway toward your right. This is up 100%, right 50%.", visible(extension=0.75, horizontal=0.5, vertical=1.0), guide="Up 1.00 + Right 0.50", captures=6),
    prompt("Fully up, far right", "Point fully upward and as far toward your right as comfortable. Hold the fixed pose before each capture.", visible(extension=0.75, horizontal=1.0, vertical=1.0), guide="Up 1.00 + Right 1.00", captures=6),
    prompt("Fully down, far left", "Point fully downward and as far toward your left as comfortable. Hold the fixed pose before each capture.", visible(extension=0.75, horizontal=-1.0, vertical=-1.0), guide="Down 1.00 + Left 1.00", captures=6),
    prompt("Fully down, half left", "Point fully downward while moving only halfway toward your left. This is down 100%, left 50%.", visible(extension=0.75, horizontal=-0.5, vertical=-1.0), guide="Down 1.00 + Left 0.50", captures=6),
    prompt("Fully down, centered", "Point fully downward with no intentional left or right component.", visible(extension=0.75, horizontal=0.0, vertical=-1.0), guide="Down 1.00; X zero", captures=6),
    prompt("Fully down, half right", "Point fully downward while moving only halfway toward your right. This is down 100%, right 50%.", visible(extension=0.75, horizontal=0.5, vertical=-1.0), guide="Down 1.00 + Right 0.50", captures=6),
    prompt("Fully down, far right", "Point fully downward and as far toward your right as comfortable. Hold the fixed pose before each capture.", visible(extension=0.75, horizontal=1.0, vertical=-1.0), guide="Down 1.00 + Right 1.00", captures=6),

    prompt("Mid diagonal upper-left", "Hold the tip halfway up and halfway toward your left; keep the tongue visible in both panels.", visible(extension=0.75, horizontal=-0.5, vertical=0.5), guide="Up 0.50 + Left 0.50", captures=6),
    prompt("Mid diagonal upper-right", "Hold the tip halfway up and halfway toward your right; keep the tongue visible in both panels.", visible(extension=0.75, horizontal=0.5, vertical=0.5), guide="Up 0.50 + Right 0.50", captures=6),
    prompt("Mid diagonal lower-left", "Hold the tip halfway down and halfway toward your left; keep the tongue visible in both panels.", visible(extension=0.75, horizontal=-0.5, vertical=-0.5), guide="Down 0.50 + Left 0.50", captures=6),
    prompt("Mid diagonal lower-right", "Hold the tip halfway down and halfway toward your right; keep the tongue visible in both panels.", visible(extension=0.75, horizontal=0.5, vertical=-0.5), guide="Down 0.50 + Right 0.50", captures=6),

    prompt("Facial hair, neutral, tongue hidden", "Relax your mouth with facial hair in its normal position. Keep the tongue behind the teeth; vary the lips slightly.", context="facial hair / neutral", captures=6),
    prompt("Facial hair, neutral, tongue tip", "Keep the same relaxed jaw and hair position. Show only the centered tip, clearly visible in both panels.", visible(extension=0.25), context="facial hair / neutral", guide="TongueOut 0.25; X/Y zero", captures=6),
    prompt("Facial hair, smile, tongue hidden", "Smile slightly and vary the lip opening. Keep the tongue fully inside despite any moustache or beard shadow.", context="facial hair / smile", captures=6),
    prompt("Facial hair, smile, tongue tip", "Use the same slight smile and lip opening while showing the centered tongue tip in both panels.", visible(extension=0.25), context="facial hair / smile", guide="TongueOut 0.25; X/Y zero", captures=6),
    prompt("Facial hair, open jaw, tongue hidden", "Open the jaw comfortably at a few strengths. Keep the tongue behind the teeth while hair and chin remain in view.", context="facial hair / open jaw", captures=6),
    prompt("Facial hair, open jaw, tongue out", "Use the same comfortable jaw opening and extend the tongue straight forward, visible in both panels.", visible(extension=0.75), context="facial hair / open jaw", guide="TongueOut 0.75; X/Y zero", captures=6),
    prompt("Facial hair, fit variation, tongue hidden", "Make only small safe headset-fit changes between stills. Keep the tongue hidden and hair naturally in view.", context="facial hair / fit variation", captures=6),
    prompt("Facial hair, fit variation, tongue out", "Repeat those small headset-fit changes with the tongue centered and visible in both panels. If hair hides the tip, adjust fit safely and retake the still.", visible(extension=0.75), context="facial hair / fit variation", guide="TongueOut 0.75; X/Y zero", captures=6),
]


class TongueStillCaptureSession:
    """Captures only requested frames and journals every decision immediately."""

    def __init__(
        self,
        path: str | Path,
        *,
        prompts: list[TongueStillPrompt] | None = None,
        session_type: str = "tongue-stereo-stills-v1",
        title: str = "Quest Pro manual stereo tongue capture",
    ) -> None:
        self.path = Path(path).resolve()
        self.prompts = list(prompts or TONGUE_STILL_PROMPTS)
        self.session_type = session_type
        self.title = title
        self.current_index = 0
        self.pending_capture = False
        self.completed = False
        self.started_monotonic_ns = time.monotonic_ns()
        self.started_wall_ns = time.time_ns()
        self.finished_monotonic_ns: int | None = None
        self.samples: list[dict[str, object]] = []
        self.skipped_prompts: set[int] = set()
        self.message = "Hold the requested pose, then press SPACE once per still."
        self._save()

    @property
    def current(self) -> TongueStillPrompt:
        return self.prompts[self.current_index]

    def active_samples(self, prompt_index: int | None = None) -> list[dict[str, object]]:
        index = self.current_index if prompt_index is None else prompt_index
        return [
            sample for sample in self.samples
            if int(sample["promptIndex"]) == index and not sample.get("excluded", False)
        ]

    def handle_key(self, key: str) -> str | None:
        normalized = key.lower()
        if normalized == " ":
            if self.pending_capture:
                self.message = "Capture already queued; keep holding the pose."
                return "not_ready"
            self.pending_capture = True
            self.message = "Capturing the next synchronized stereo frame..."
            return "started"
        if normalized in ("\r", "\n"):
            count = len(self.active_samples())
            if count < self.current.minimum_captures:
                self.message = (
                    f"Need {self.current.minimum_captures - count} more still(s) before advancing."
                )
                return "not_ready"
            self.current_index += 1
            self.pending_capture = False
            if self.current_index >= len(self.prompts):
                self.current_index = len(self.prompts) - 1
                self.completed = True
                self.message = "Dataset capture complete."
                self._save()
                return "completed"
            self.message = "New card: form the pose, hold still, then press SPACE."
            self._save()
            return "advanced"
        if normalized == "k":
            self.skipped_prompts.add(self.current_index)
            self.current_index += 1
            self.pending_capture = False
            if self.current_index >= len(self.prompts):
                self.current_index = len(self.prompts) - 1
                self.completed = True
                self._save()
                return "completed"
            self.message = "Card skipped."
            self._save()
            return "skipped"
        if normalized in ("b", "\x08"):
            self.current_index = max(0, self.current_index - 1)
            self.pending_capture = False
            self.message = "Returned to the previous card; existing stills are preserved."
            self._save()
            return "back"
        if normalized == "x":
            for sample in reversed(self.samples):
                if (int(sample["promptIndex"]) == self.current_index
                        and not sample.get("excluded", False)):
                    sample["excluded"] = True
                    self.message = "Last still excluded non-destructively."
                    self._save()
                    return "back"
            self.message = "There is no still to undo on this card."
            return "not_ready"
        return None

    def consume_frame(
        self,
        writer: CaptureWriter,
        transport_header: bytes,
        payload: bytes | memoryview,
        pc_monotonic_ns: int,
        pc_wall_ns: int,
    ) -> bool:
        if not self.pending_capture or self.completed:
            return False
        frame_index = writer.frame_count
        writer.write(transport_header, payload, pc_monotonic_ns, pc_wall_ns)
        prompt_value = self.current
        self.samples.append(
            {
                "frameIndex": frame_index,
                "promptIndex": self.current_index,
                "promptName": prompt_value.name,
                "targets": dict(prompt_value.targets),
                "context": prompt_value.context,
                "pcMonotonicNs": pc_monotonic_ns,
                "pcWallNs": pc_wall_ns,
                "excluded": False,
            }
        )
        self.pending_capture = False
        count = len(self.active_samples())
        if count >= prompt_value.recommended_captures:
            self.message = "Recommended coverage reached. ENTER advances; SPACE adds another."
        else:
            self.message = (
                f"Still saved. Add {prompt_value.recommended_captures - count} more with tiny natural variation."
            )
        self._save()
        return True

    def finish(self, completed: bool | None = None) -> None:
        self.finished_monotonic_ns = time.monotonic_ns()
        if completed is not None:
            self.completed = completed
        self._save()

    def _save(self) -> None:
        payload = {
            "version": 1,
            "sessionType": self.session_type,
            "completed": self.completed,
            "startedMonotonicNs": self.started_monotonic_ns,
            "startedWallNs": self.started_wall_ns,
            "finishedMonotonicNs": self.finished_monotonic_ns,
            "currentPrompt": self.current_index,
            "skippedPrompts": sorted(self.skipped_prompts),
            "samples": self.samples,
            "prompts": [asdict(value) for value in self.prompts],
            "capturePolicy": "one exact synchronized stereo pair per Space press",
            "semantics": "VRCFT Unified Expressions; wearer-relative directions",
        }
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temporary = self.path.with_suffix(self.path.suffix + ".tmp")
        temporary.write_text(json.dumps(payload, indent=2), encoding="utf-8")
        temporary.replace(self.path)

    def render(
        self, strip: np.ndarray, labels_ready: bool, *,
        factory_values_static: bool = False,
    ) -> np.ndarray:
        image = np.zeros((820, 1280, 3), dtype=np.uint8)
        current = self.current
        cv2.putText(image, self.title, (24, 42),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.88, (245, 245, 245), 2, cv2.LINE_AA)
        cv2.putText(
            image, f"CARD {self.current_index + 1}/{len(self.prompts)}  {current.name}",
            (24, 82), cv2.FONT_HERSHEY_SIMPLEX, 0.72, (80, 245, 120), 2, cv2.LINE_AA,
        )
        status = (
            "FACTORY PACKETS READY" if labels_ready
            else "WAITING FOR VRCFT FACTORY PACKETS"
        )
        cv2.putText(image, status, (760, 80), cv2.FONT_HERSHEY_SIMPLEX, 0.46,
                    (80, 245, 120) if labels_ready else (80, 80, 255), 1, cv2.LINE_AA)

        # Show the exact two inputs that will be written by the next key press.
        for view, label in ((0, "camera 2 / left-face view"), (1, "camera 3 / right-face view")):
            panel = strip[:, view * 400:(view + 1) * 400]
            panel = cv2.resize(panel, (300, 300), interpolation=cv2.INTER_AREA)
            panel = cv2.cvtColor(panel, cv2.COLOR_GRAY2BGR)
            x = 24 + view * 324
            image[110:410, x:x + 300] = panel
            cv2.putText(image, label, (x, 432), cv2.FONT_HERSHEY_SIMPLEX, 0.45,
                        (190, 190, 190), 1, cv2.LINE_AA)

        self._draw_pose_guide(image, current, origin=(775, 245))
        cv2.putText(image, f"Official target: {current.guide}", (700, 425),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.58, (80, 220, 255), 2, cv2.LINE_AA)
        cv2.putText(image, f"Mouth context: {current.context}", (700, 458),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.52, (215, 215, 215), 1, cv2.LINE_AA)
        for line_index, line in enumerate(textwrap.wrap(current.instruction, width=75)[:2]):
            cv2.putText(image, line, (24, 500 + line_index * 34),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.60, (240, 240, 240), 1, cv2.LINE_AA)

        count = len(self.active_samples())
        cv2.putText(
            image,
            f"Saved on this card: {count}/{current.recommended_captures} recommended "
            f"({current.minimum_captures} minimum)",
            (24, 590), cv2.FONT_HERSHEY_SIMPLEX, 0.62,
            (80, 220, 255) if count < current.recommended_captures else (80, 245, 120),
            2, cv2.LINE_AA,
        )
        cv2.putText(image, self.message, (24, 635), cv2.FONT_HERSHEY_SIMPLEX,
                    0.56, (220, 220, 220), 1, cv2.LINE_AA)
        if factory_values_static:
            cv2.putText(
                image, "Face values are static; these pose cards provide the tongue labels.",
                (24, 668), cv2.FONT_HERSHEY_SIMPLEX, 0.48,
                (80, 220, 255), 1, cv2.LINE_AA,
            )
        cv2.putText(image, "SPACE capture one still | ENTER next | X undo | B previous | K skip | Q stop safely",
                    (24, 700), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (170, 210, 255), 1, cv2.LINE_AA)
        cv2.putText(image, "Keep tongue visible in both views. Adjust fit if facial hair or tape hides it.",
                    (24, 740), cv2.FONT_HERSHEY_SIMPLEX, 0.52, (170, 170, 170), 1, cv2.LINE_AA)
        cv2.putText(image, "Semantics: docs.vrcft.io - Unified Expressions / Parameters",
                    (24, 780), cv2.FONT_HERSHEY_SIMPLEX, 0.44, (130, 130, 130), 1, cv2.LINE_AA)
        return image

    @staticmethod
    def _draw_pose_guide(
        image: np.ndarray, current: TongueStillPrompt, origin: tuple[int, int]
    ) -> None:
        x, y = origin
        cv2.ellipse(image, (x + 165, y), (150, 75), 0, 0, 360, (150, 150, 150), 3)
        if not current.targets.get("visibility", 0.0):
            cv2.line(image, (x + 55, y), (x + 275, y), (190, 190, 190), 4)
            cv2.putText(image, "TONGUE HIDDEN", (x + 78, y + 12),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.48, (170, 170, 170), 1, cv2.LINE_AA)
            return
        horizontal = float(current.targets.get("horizontal", 0.0))
        vertical = float(current.targets.get("vertical", 0.0))
        extension = float(current.targets.get("extension", 0.75))
        tip = (round(x + 165 + horizontal * 100), round(y + 35 - vertical * 62))
        half_width = round(35 + 24 * current.targets.get("flat", 0.0)
                           - 13 * current.targets.get("squish", 0.0))
        length = round(38 + extension * 44)
        points = np.asarray([
            (x + 165 - half_width, y + 5), (x + 165 + half_width, y + 5),
            (tip[0] + half_width // 2, tip[1] + length // 2), tip,
            (tip[0] - half_width // 2, tip[1] + length // 2),
        ], dtype=np.int32)
        cv2.fillPoly(image, [points], (105, 120, 235))
        cv2.polylines(image, [points], True, (170, 190, 255), 2, cv2.LINE_AA)
        if horizontal or vertical:
            end = (round(x + 165 + horizontal * 130), round(y - vertical * 100))
            cv2.arrowedLine(image, (x + 165, y), end, (80, 245, 120), 3, cv2.LINE_AA)
        shape = next(
            (name for name in ("roll", "bend_down", "curl_up", "squish", "flat", "twist")
             if abs(float(current.targets.get(name, 0.0))) > 0.1),
            None,
        )
        if shape:
            cv2.putText(image, shape.replace("_", " ").upper(), (x + 110, y + 112),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.48, (80, 220, 255), 1, cv2.LINE_AA)
