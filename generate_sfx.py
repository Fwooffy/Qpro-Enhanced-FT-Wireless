"""Generate the release's original, copyright-free interface tones."""

from __future__ import annotations

import math
import struct
import wave
from pathlib import Path


RATE = 44_100
OUTPUT = Path(__file__).resolve().parent / "SFX"


def write_tones(name: str, notes: list[tuple[float, float, float]]) -> None:
    samples = bytearray()
    for frequency, duration, volume in notes:
        count = round(RATE * duration)
        for index in range(count):
            progress = index / count
            envelope = math.sin(math.pi * progress) ** 2
            value = volume * envelope * math.sin(
                2 * math.pi * frequency * index / RATE
            )
            samples.extend(struct.pack("<h", round(value * 32767)))

    OUTPUT.mkdir(parents=True, exist_ok=True)
    with wave.open(str(OUTPUT / name), "wb") as sound:
        sound.setnchannels(1)
        sound.setsampwidth(2)
        sound.setframerate(RATE)
        sound.writeframes(samples)


if __name__ == "__main__":
    write_tones("succeed.wav", [(659.25, 0.15, 0.25), (880.0, 0.20, 0.25)])
    write_tones(
        "trainingComplete.wav",
        [(523.25, 0.15, 0.25), (659.25, 0.15, 0.25), (783.99, 0.30, 0.25)],
    )
    write_tones("warning.wav", [(392.0, 0.18, 0.22), (392.0, 0.18, 0.22)])
