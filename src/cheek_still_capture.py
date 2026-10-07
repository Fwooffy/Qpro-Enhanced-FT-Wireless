"""Prompted stereo cheek examples for an experimental tongue-and-cheek model."""

import textwrap

import cv2
import numpy as np

from tongue_still_capture import (
    TONGUE_REFINEMENT_PROMPTS, TONGUE_STILL_PROMPTS,
    TongueStillCaptureSession, prompt,
)


CHEEK_TARGET_NAMES = ["cheekPuffLeft", "cheekPuffRight"]


def cheek_prompt(name: str, instruction: str, left: float = 0, right: float = 0):
    return prompt(
        name, instruction,
        dict(zip(CHEEK_TARGET_NAMES, (left, right))),
        context="cheek camera training", guide=f"Left {left:.2f} / Right {right:.2f}", captures=8,
    )


CHEEK_STILL_PROMPTS = [
    cheek_prompt("Relaxed cheeks, lips closed", "Relax both cheeks and your jaw. Keep your tongue inside."),
    cheek_prompt("Relaxed cheeks, lips parted", "Part your lips comfortably without puffing either cheek."),
    *[
        cheek_prompt(
            f"{side.title()} cheek {round(strength * 100)}%",
            f"Puff only your own {side} cheek to roughly {round(strength * 100)}% of your comfortable full puff. "
            "Keep the other cheek relaxed. Hold each still, relax briefly and repeat.",
            strength if side == "left" else 0, strength if side == "right" else 0,
        )
        for side in ("left", "right") for strength in (0.25, 0.5, 1.0)
    ],
    *[
        cheek_prompt(
            f"Both cheeks {round(strength * 100)}%",
            f"Puff both cheeks evenly to roughly {round(strength * 100)}% of full strength. Keep your jaw relaxed.",
            strength, strength,
        )
        for strength in (0.25, 0.5, 1.0)
    ],
    cheek_prompt("Smile, lips closed", "Smile with your lips closed. Do not puff your cheeks."),
    cheek_prompt("Smile, teeth visible", "Show your teeth in a comfortable smile without puffing."),
    cheek_prompt("Left smirk", "Raise only your left mouth corner, keeping both cheeks unpuffed."),
    cheek_prompt("Right smirk", "Raise only your right mouth corner, keeping both cheeks unpuffed."),
    cheek_prompt("Jaw open", "Open your jaw comfortably with both cheeks unpuffed."),
    cheek_prompt("Pucker", "Pucker your lips without puffing either cheek."),
    cheek_prompt("Cheeks sucked in", "Suck both cheeks inward gently. This is not a puff."),
    cheek_prompt("Tongue straight out", "Stick your tongue straight out without puffing your cheeks."),
    cheek_prompt("Tongue left", "Point your tongue left without puffing your cheeks."),
    cheek_prompt("Tongue right", "Point your tongue right without puffing your cheeks."),
]

# Keep the original curricula available for old journals and model tooling.
# Only the new lower-face session types append the camera cheek curriculum.
LOWER_FACE_REFINEMENT_PROMPTS = [*TONGUE_REFINEMENT_PROMPTS, *CHEEK_STILL_PROMPTS]
LOWER_FACE_STILL_PROMPTS = [*TONGUE_STILL_PROMPTS, *CHEEK_STILL_PROMPTS]
LOWER_FACE_SESSION_TYPES = frozenset(("lower-face-refinement-v1", "lower-face-stills-v1"))


def prompts_for_lower_face_session(session_type: str):
    if session_type == "lower-face-refinement-v1":
        return LOWER_FACE_REFINEMENT_PROMPTS
    if session_type == "lower-face-stills-v1":
        return LOWER_FACE_STILL_PROMPTS
    raise ValueError("Unsupported lower-face capture session")


class CheekStillCaptureSession(TongueStillCaptureSession):
    def __init__(self, path):
        super().__init__(path, prompts=CHEEK_STILL_PROMPTS,
                         session_type="cheek-stereo-stills-v1", title="Quest Pro tongue model: cheek training")

    def handle_key(self, key):
        if key.lower() == "k":
            self.message = "Every cheek pose is needed. Use Q to stop safely, or capture this card before advancing."
            return "not_ready"
        return super().handle_key(key)

    @staticmethod
    def _draw_pose_guide(image: np.ndarray, current, origin: tuple[int, int]) -> None:
        x, y = origin
        cv2.ellipse(image, (x + 165, y), (120, 110), 0, 0, 360, (160, 160, 160), 2)
        # Wearer-relative guide: follow the same side as your own hand.
        for key, label, offset in (("cheekPuffLeft", "YOUR LEFT", 80),
                                   ("cheekPuffRight", "YOUR RIGHT", 250)):
            value = current.targets[key]
            color = (80, 245, 120) if value > 0 else (130, 130, 130)
            label_width = cv2.getTextSize(label, cv2.FONT_HERSHEY_SIMPLEX, 0.58, 2)[0][0]
            cv2.putText(image, label, (x + offset - label_width // 2, y - 125),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.58, (235, 235, 235), 2, cv2.LINE_AA)
            cv2.circle(image, (x + offset, y + 15), round(12 + value * 30), color, 2)
            strength = f"{value:.2f}"
            strength_width = cv2.getTextSize(strength, cv2.FONT_HERSHEY_SIMPLEX, 0.58, 2)[0][0]
            cv2.putText(image, strength, (x + offset - strength_width // 2, y + 135),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.58, color, 2, cv2.LINE_AA)

    def render(self, strip, labels_ready=True, *, factory_values_static=False):
        image = super().render(strip, True)
        image[58:88, 750:] = 0
        cv2.putText(image, "POSE CARDS PROVIDE CHEEK LABELS", (760, 80),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.46, (80, 245, 120), 1, cv2.LINE_AA)
        # Cheek instructions can exceed the base tongue guide's two lines.
        image[475:574] = 0
        for line_index, line in enumerate(textwrap.wrap(self.current.instruction, width=100)[:3]):
            cv2.putText(image, line, (24, 500 + line_index * 30),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.60, (240, 240, 240), 1, cv2.LINE_AA)
        image[715:755] = 0
        image[675:711] = 0
        cv2.putText(image, "SPACE capture | ENTER next | X undo | B previous | Q stop safely",
                    (24, 700), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (170, 210, 255), 1, cv2.LINE_AA)
        cv2.putText(image, "Follow your own left/right: left cheek = left hand side; right cheek = right hand side.",
                    (24, 740), cv2.FONT_HERSHEY_SIMPLEX, 0.48, (170, 170, 170), 1, cv2.LINE_AA)
        cv2.putText(image, "Relax and repeat each pose; vary fit slightly between stills.",
                    (24, 761), cv2.FONT_HERSHEY_SIMPLEX, 0.44, (170, 170, 170), 1, cv2.LINE_AA)
        return image


class LowerFaceCaptureSession(CheekStillCaptureSession):
    """Capture tongue examples first, then independently labeled cheek cards."""

    def __init__(self, path, *, refinement: bool = False):
        session_type = "lower-face-refinement-v1" if refinement else "lower-face-stills-v1"
        self.tongue_card_count = len(TONGUE_REFINEMENT_PROMPTS if refinement else TONGUE_STILL_PROMPTS)
        TongueStillCaptureSession.__init__(
            self, path, prompts=prompts_for_lower_face_session(session_type),
            session_type=session_type,
            title="Lower face calibration: " + ("Quick refinement" if refinement else "Full dataset"),
        )

    @property
    def is_cheek_card(self) -> bool:
        return self.current_index >= self.tongue_card_count

    def handle_key(self, key):
        # Existing tongue cards retain their old optional-skip behavior. All
        # cheek cards are required to calibrate neutral and both individual sides.
        if self.is_cheek_card:
            return CheekStillCaptureSession.handle_key(self, key)
        return TongueStillCaptureSession.handle_key(self, key)

    @staticmethod
    def _draw_pose_guide(image, current, origin):
        if all(name in current.targets for name in CHEEK_TARGET_NAMES):
            CheekStillCaptureSession._draw_pose_guide(image, current, origin)
        else:
            TongueStillCaptureSession._draw_pose_guide(image, current, origin)

    def render(self, strip, labels_ready=True, *, factory_values_static=False):
        if self.is_cheek_card:
            return CheekStillCaptureSession.render(self, strip, True)
        return TongueStillCaptureSession.render(
            self, strip, labels_ready, factory_values_static=factory_values_static,
        )
