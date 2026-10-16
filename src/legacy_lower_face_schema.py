"""Read-only schemas for captures recorded before camera cheeks were retired.

These cards are never offered by the current capture UI. Their original labels
remain available so tongue preparation can reject mislabeled old journals and
exclude the cheek-only suffix from tongue supervision.
"""

from collections import Counter

from tongue_still_capture import (
    TONGUE_REFINEMENT_PROMPTS, TONGUE_STILL_PROMPTS, prompt,
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

# Frozen legacy curricula; new sessions use the tongue-only card lists.
LOWER_FACE_REFINEMENT_PROMPTS = [*TONGUE_REFINEMENT_PROMPTS, *CHEEK_STILL_PROMPTS]
LOWER_FACE_STILL_PROMPTS = [*TONGUE_STILL_PROMPTS, *CHEEK_STILL_PROMPTS]
LOWER_FACE_SESSION_TYPES = frozenset(("lower-face-refinement-v1", "lower-face-stills-v1"))


def prompts_for_lower_face_session(session_type: str):
    if session_type == "lower-face-refinement-v1":
        return LOWER_FACE_REFINEMENT_PROMPTS
    if session_type == "lower-face-stills-v1":
        return LOWER_FACE_STILL_PROMPTS
    raise ValueError("Unsupported lower-face capture session")


def validate_cheek_session(session: dict, frame_count: int | None = None) -> tuple[list[dict], int]:
    """Validate every journal label; return usable cheek samples and their offset.

    Combined captures retain their original tongue cards as a prefix. Their
    cheek suffix has its own targets: a tongue-out cheek card must never become
    a hidden-tongue example just because it lacks a visibility target.
    """
    session_type = session.get("sessionType")
    if session_type not in LOWER_FACE_SESSION_TYPES | {"cheek-stereo-stills-v1"} or session.get("completed") is not True:
        raise ValueError("Complete the cheek camera cards before training")
    expected_cards = (prompts_for_lower_face_session(session_type)
                      if session_type in LOWER_FACE_SESSION_TYPES else CHEEK_STILL_PROMPTS)
    cheek_offset = len(expected_cards) - len(CHEEK_STILL_PROMPTS)
    cards = session.get("prompts", [])
    if not isinstance(cards, list) or len(cards) != len(expected_cards) or any(
        not isinstance(card, dict) or card.get("name") != expected.name or
        card.get("targets") != expected.targets
        for card, expected in zip(cards, expected_cards)
    ):
        raise ValueError("The cheek session does not match the capture curriculum")

    def integer(value):
        if type(value) is not int:
            raise ValueError("Cheek frame and pose indices must be integers")
        return value

    skipped = session.get("skippedPrompts", [])
    if not isinstance(skipped, list) or any(
        not 0 <= integer(step) < len(cards) for step in skipped
    ):
        raise ValueError("The cheek session has invalid skipped-card indices")
    if any(step >= cheek_offset for step in skipped):
        raise ValueError("Recapture skipped cheek cards before training")
    samples = session.get("samples", [])
    if not isinstance(samples, list) or not samples or any(not isinstance(sample, dict) for sample in samples):
        raise ValueError("The cheek session contains no labeled pairs")
    frame_indices = [integer(sample.get("frameIndex")) for sample in samples]
    if any(index < 0 for index in frame_indices) or len(set(frame_indices)) != len(frame_indices) or (
        frame_count is not None and set(frame_indices) != set(range(frame_count))
    ):
        raise ValueError("Every cheek still must have one matching prompt label")
    for sample in samples:
        step = integer(sample.get("promptIndex"))
        if not 0 <= step < len(cards) or sample.get("targets") != expected_cards[step].targets or \
                sample.get("promptName") != expected_cards[step].name:
            raise ValueError("A captured cheek label does not match its pose card")
        if type(sample.get("excluded", False)) is not bool:
            raise ValueError("Cheek exclusion flags must be boolean")
    usable = [sample for sample in samples if sample["promptIndex"] >= cheek_offset and not sample.get("excluded")]
    counts = Counter(sample["promptIndex"] for sample in usable)
    for index, card in enumerate(CHEEK_STILL_PROMPTS):
        if counts[index + cheek_offset] < card.minimum_captures:
            raise ValueError(f"{card.name} needs at least {card.minimum_captures} usable stills")
    return usable, cheek_offset
