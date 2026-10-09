#!/usr/bin/env python3
"""Receive timestamped factory reference labels over localhost UDP."""

from __future__ import annotations

import argparse
import collections
import json
import math
import socket
import threading
import time
from pathlib import Path


NATIVE_TONGUE_MAPPING = "source-flags-v2"
TRACKING_SOURCES = ("VirtualDesktop", "SteamLink")
_STANDARD_TONGUE_CHANNELS = (
    "TongueTipInterdental", "TongueTipAlveolar", "TongueFrontDorsalPalate",
    "TongueMidDorsalPalate", "TongueBackDorsalVelar", "TongueOut", "TongueRetreat",
)


def require_current_tongue_cache(metadata: dict[str, object]) -> None:
    """Older scalar native labels cannot prove which VD layout they used."""
    if metadata.get("nativeTongueMapping") != NATIVE_TONGUE_MAPPING:
        raise ValueError(
            "This prepared tongue cache uses an older native TongueOut mapping. "
            "Regenerate the cache from its original .qpcap, .qplabel.jsonl and "
            ".qpsession.json files before training. The Hub rebuilds it when "
            "you retry training; command-line users should rerun the matching "
            "prepare_tongue_stills.py or prepare_tongue_training.py command. "
            "Existing trained checkpoints remain usable."
        )


class AmbiguousTongueSourceError(ValueError):
    """A legacy packet uses flags whose meaning depends on its producer."""


def native_tongue_out(
    sample: dict[str, object], names: list[str], *, tracking_source: str | None = None,
) -> float | None:
    """Read a scalar tongue reference without rewriting raw expressions.

    VD's alternate tongue layout uses slot 63 for TongueOut while the label
    stream retains the 70 XR_FB channel names. Older Steam Link packets also
    set flag 2 but still use the named TongueOut slot, so that flag alone is
    insufficient. An explicit override is only for legacy capture sidecars.
    """
    flags = sample.get("faceFlags", 1)
    if not isinstance(flags, int) or isinstance(flags, bool) or not flags & 1:
        return None
    declared_source = sample.get("trackingSource")
    if declared_source is not None and declared_source not in TRACKING_SOURCES:
        raise ValueError("The factory tongue sample has an unsupported trackingSource")
    if tracking_source is not None and tracking_source not in TRACKING_SOURCES:
        raise ValueError("The native tongue source override is unsupported")
    if (declared_source is not None and tracking_source is not None
            and declared_source != tracking_source):
        raise ValueError("The native tongue source override conflicts with the recorded trackingSource")
    source = declared_source or tracking_source
    if flags & 2 and source is None:
        raise AmbiguousTongueSourceError(
            "The factory TongueOut layout is ambiguous: this older label file "
            "does not identify Virtual Desktop or Steam Link. Regenerate its "
            "cache with --tracking-source VirtualDesktop or --tracking-source "
            "SteamLink, choosing the app used for that recording. For a new "
            "capture, update the matching Qpro module and record again."
        )
    if flags & 2 and source == "VirtualDesktop":
        if (len(names) != 70 or tuple(names[63:]) != _STANDARD_TONGUE_CHANNELS
                or len(set(names)) != 70):
            raise ValueError(
                "The alternate Virtual Desktop tongue layout needs the full "
                "70-channel factory schema. Restart the matching Qpro module "
                "and record the capture again."
            )
        index = 63
    else:
        if names.count("TongueOut") != 1:
            raise ValueError("The factory schema needs exactly one TongueOut channel")
        index = names.index("TongueOut")
    values = sample.get("values")
    if not isinstance(values, (list, tuple)) or len(values) != len(names):
        raise ValueError("The factory tongue sample does not match its channel schema")
    try:
        value = float(values[index])
    except (TypeError, ValueError, OverflowError) as error:
        raise ValueError("The factory TongueOut reference is not a number") from error
    if not math.isfinite(value) or not 0.0 <= value <= 1.0:
        raise ValueError("The factory TongueOut reference must be a finite value in 0..1")
    return value


class LabelSidecarRecorder:
    def __init__(self, path: str | Path | None, port: int = 27274) -> None:
        self.path = Path(path).resolve() if path is not None else None
        if self.path is not None:
            self.path.parent.mkdir(parents=True, exist_ok=True)
        self.port = port
        self.sample_count = 0
        self.invalid_count = 0
        self.schema_names: list[str] = []
        self.schema_tracking_source: str | None = None
        self.last_sample_monotonic_ns: int | None = None
        self.source_change_sequence = 0
        self.source_unchanged_ms: float | None = None
        self._recent_lock = threading.Lock()
        self._recent_samples: collections.deque[dict[str, object]] = (
            collections.deque(maxlen=512)
        )
        self._file = (
            self.path.open("x", encoding="utf-8", buffering=1024 * 1024)
            if self.path is not None else None
        )
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._socket.bind(("127.0.0.1", port))
        self._socket.settimeout(0.2)
        self._running = True
        self._write(
            {
                "type": "file",
                "version": 1,
                "createdWallNs": time.time_ns(),
                "createdMonotonicNs": time.monotonic_ns(),
                "udpPort": port,
            }
        )
        self._thread = threading.Thread(
            target=self._receive_loop,
            name="vrcft-label-receiver",
            daemon=True,
        )
        self._thread.start()

    def _write(self, value: dict[str, object]) -> None:
        if self._file is None:
            return
        self._file.write(json.dumps(value, separators=(",", ":"), allow_nan=False))
        self._file.write("\n")

    def _receive_loop(self) -> None:
        last_flush = time.monotonic()
        while self._running:
            try:
                data, address = self._socket.recvfrom(65507)
            except socket.timeout:
                continue
            except OSError:
                break
            if address[0] != "127.0.0.1":
                self.invalid_count += 1
                continue
            arrival_monotonic_ns = time.monotonic_ns()
            arrival_wall_ns = time.time_ns()
            try:
                message = json.loads(data)
                if not isinstance(message, dict) or message.get("v") != 1:
                    raise ValueError("unsupported message")
                message_type = message.get("type")
                if message_type == "schema":
                    names = message.get("names")
                    if (not isinstance(names, list) or not names
                            or len(names) > 512
                            or any(not isinstance(name, str) or not name
                                   for name in names)):
                        raise ValueError("invalid schema")
                    source = message.get("trackingSource")
                    if source is not None and source not in TRACKING_SOURCES:
                        raise ValueError("invalid tracking source")
                    self.schema_names = names
                    self.schema_tracking_source = source
                    self._write(
                        {
                            "type": "schema",
                            "arrivalMonotonicNs": arrival_monotonic_ns,
                            "arrivalWallNs": arrival_wall_ns,
                            "names": names,
                            **({"trackingSource": source} if source else {}),
                        }
                    )
                elif message_type == "sample":
                    # A source-tagged schema cannot certify an untagged packet
                    # from another producer sharing this localhost port.
                    source = message.get("trackingSource")
                    if source is not None and source not in TRACKING_SOURCES:
                        raise ValueError("invalid tracking source")
                    if (source is not None and self.schema_tracking_source is not None
                            and source != self.schema_tracking_source):
                        raise ValueError("sample source does not match schema")
                    values = message.get("values")
                    if (not isinstance(values, list)
                            or len(values) > 512
                            or (self.schema_names
                                and len(values) != len(self.schema_names))):
                        raise ValueError("invalid values")
                    numeric_values = [float(value) for value in values]
                    if any(not math.isfinite(value) for value in numeric_values):
                        raise ValueError("non-finite value")
                    sequence = int(message["sequence"])
                    qpc = int(message["qpc"])
                    qpc_frequency = int(message["qpcFrequency"])
                    if sequence <= 0 or qpc < 0 or qpc_frequency <= 0:
                        raise ValueError("invalid timing")
                    source_change_sequence = int(
                        message.get("sourceChangeSequence", 0)
                    )
                    source_unchanged_ms = float(
                        message.get("sourceUnchangedMs", -1.0)
                    )
                    if source_change_sequence < 0 or source_unchanged_ms < -1.0:
                        raise ValueError("invalid source freshness")
                    sample_record: dict[str, object] = {
                        "type": "sample",
                        "arrivalMonotonicNs": arrival_monotonic_ns,
                        "arrivalWallNs": arrival_wall_ns,
                        "sourceSequence": sequence,
                        "sourceQpc": qpc,
                        "sourceQpcFrequency": qpc_frequency,
                        "sourceUtcUnixMs": int(message["utcUnixMs"]),
                        "sourceChangeSequence": source_change_sequence,
                        "sourceUnchangedMs": source_unchanged_ms,
                        "values": numeric_values,
                        **({"trackingSource": source} if source else {}),
                        "faceFlags": int(message.get("faceFlags", 0)),
                        "isEyeFollowingBlendshapesValid": bool(
                            message.get("isEyeFollowingBlendshapesValid", False)
                        ),
                        "leftEyeIsValid": bool(message.get("leftEyeIsValid", False)),
                        "rightEyeIsValid": bool(message.get("rightEyeIsValid", False)),
                        "leftEyeConfidence": float(
                            message.get("leftEyeConfidence", 0.0)
                        ),
                        "rightEyeConfidence": float(
                            message.get("rightEyeConfidence", 0.0)
                        ),
                    }
                    for field_name, expected_count in (
                        ("leftEyeOrientation", 4),
                        ("leftEyePosition", 3),
                        ("rightEyeOrientation", 4),
                        ("rightEyePosition", 3),
                    ):
                        source_values = message.get(field_name, [0.0] * expected_count)
                        if (not isinstance(source_values, list)
                                or len(source_values) != expected_count):
                            raise ValueError(f"invalid {field_name}")
                        converted = [float(value) for value in source_values]
                        if any(not math.isfinite(value) for value in converted):
                            raise ValueError(f"non-finite {field_name}")
                        sample_record[field_name] = converted
                    numeric_metadata = (
                        float(sample_record["leftEyeConfidence"]),
                        float(sample_record["rightEyeConfidence"]),
                    )
                    if any(not math.isfinite(value) for value in numeric_metadata):
                        raise ValueError("non-finite eye confidence")
                    self._write(sample_record)
                    self.sample_count += 1
                    self.last_sample_monotonic_ns = arrival_monotonic_ns
                    self.source_change_sequence = source_change_sequence
                    self.source_unchanged_ms = source_unchanged_ms
                    with self._recent_lock:
                        self._recent_samples.append(sample_record)
                else:
                    raise ValueError("unknown message type")
            except (KeyError, TypeError, ValueError, json.JSONDecodeError):
                self.invalid_count += 1
            now = time.monotonic()
            if now - last_flush >= 1.0 and self._file is not None:
                self._file.flush()
                last_flush = now

    def label_age_seconds(self) -> float | None:
        if self.last_sample_monotonic_ns is None:
            return None
        return (time.monotonic_ns() - self.last_sample_monotonic_ns) / 1_000_000_000

    def source_is_live(
        self, minimum_changes: int = 3, maximum_unchanged_seconds: float = 3.0
    ) -> bool:
        label_age = self.label_age_seconds()
        return bool(
            self.source_change_sequence >= minimum_changes
            and self.source_unchanged_ms is not None
            and 0.0 <= self.source_unchanged_ms
            <= maximum_unchanged_seconds * 1000.0
            and label_age is not None
            and label_age <= 1.0
        )

    def manual_tongue_reference_ready(self) -> bool:
        """Manual pose cards need fresh factory packets, not changing face values.

        Facial hair, a bandage, or a neutral pose can leave Meta's blendshapes
        unchanged while the user can still label exact camera stills by hand.
        Keep the packet age and TongueOut schema checks so a disconnected
        VRCFT bridge cannot silently produce an unsupervised capture.
        """
        age = self.label_age_seconds()
        return bool(
            self.sample_count >= 10
            and "TongueOut" in self.schema_names
            and age is not None
            and 0.0 <= age <= 1.0
        )

    def nearest_sample(self, monotonic_ns: int) -> dict[str, object] | None:
        with self._recent_lock:
            if not self._recent_samples:
                return None
            sample = min(
                self._recent_samples,
                key=lambda value: abs(
                    int(value["arrivalMonotonicNs"]) - monotonic_ns
                ),
            )
            return dict(sample)

    def close(self) -> None:
        if not self._running:
            return
        self._running = False
        self._socket.close()
        self._thread.join(timeout=1.0)
        if self._file is not None:
            self._write(
                {
                    "type": "end",
                    "completedWallNs": time.time_ns(),
                    "samples": self.sample_count,
                    "invalidDatagrams": self.invalid_count,
                }
            )
            self._file.flush()
            self._file.close()


def inspect_sidecar(path: str | Path) -> dict[str, object]:
    sidecar_path = Path(path).resolve()
    schemas = 0
    samples = 0
    invalid_lines = 0
    completed = False
    first_arrival: int | None = None
    last_arrival: int | None = None
    names: list[str] = []
    with sidecar_path.open("r", encoding="utf-8") as sidecar:
        for line in sidecar:
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                invalid_lines += 1
                continue
            record_type = record.get("type")
            if record_type == "schema":
                schemas += 1
                names = record.get("names", [])
            elif record_type == "sample":
                samples += 1
                arrival = int(record["arrivalMonotonicNs"])
                first_arrival = arrival if first_arrival is None else first_arrival
                last_arrival = arrival
            elif record_type == "end":
                completed = True
    duration = (
        (last_arrival - first_arrival) / 1_000_000_000
        if first_arrival is not None and last_arrival is not None and samples > 1
        else 0.0
    )
    return {
        "path": sidecar_path,
        "schemas": schemas,
        "samples": samples,
        "expressions": len(names),
        "invalid_lines": invalid_lines,
        "completed": completed,
        "duration_seconds": duration,
        "average_hz": ((samples - 1) / duration if duration > 0 else 0.0),
    }


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Inspect a Qpro factory reference label sidecar"
    )
    parser.add_argument("sidecar")
    arguments = parser.parse_args()
    summary = inspect_sidecar(arguments.sidecar)
    print(f"Labels: {summary['path']}")
    print(f"Complete: {summary['completed']}; invalid lines: {summary['invalid_lines']}")
    print(
        f"Samples: {summary['samples']} at {summary['average_hz']:.2f} Hz; "
        f"duration: {summary['duration_seconds']:.3f} s"
    )
    print(
        f"Schemas: {summary['schemas']}; "
        f"Factory expressions: {summary['expressions']}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
