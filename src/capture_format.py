#!/usr/bin/env python3
"""Lossless, recoverable capture files for synchronized Quest Pro frames."""

from __future__ import annotations

import argparse
import struct
import time
import zlib
from collections import Counter, deque
from pathlib import Path


FILE_MAGIC = b"QPCAP1\0\0"
FRAME_MAGIC = b"QPFRM1\0\0"
FILE_VERSION = 1
FILE_HEADER = struct.Struct("<8sIIQQQQ16s")
FRAME_HEADER = struct.Struct("<8sIIQQ64s")
TRANSPORT_HEADER = struct.Struct("<8sIIQQIIIIIIQ")


class CaptureWriter:
    """Append exact transport frames and finalize the file when capture ends."""

    def __init__(self, path: str | Path) -> None:
        self.path = Path(path).resolve()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.created_wall_ns = time.time_ns()
        self.created_monotonic_ns = time.monotonic_ns()
        self.frame_count = 0
        self._file = self.path.open("xb", buffering=1024 * 1024)
        try:
            self._write_file_header(completed=False)
        except BaseException:
            try:
                self._file.close()
            except Exception:
                pass
            raise

    def _write_file_header(self, completed: bool) -> None:
        self._file.seek(0)
        self._file.write(
            FILE_HEADER.pack(
                FILE_MAGIC,
                FILE_VERSION,
                FILE_HEADER.size,
                self.created_wall_ns,
                self.created_monotonic_ns,
                self.frame_count,
                int(completed),
                b"\0" * 16,
            )
        )
        self._file.seek(0, 2)

    def write(
        self,
        transport_header: bytes,
        payload: bytes | memoryview,
        pc_monotonic_ns: int,
        pc_wall_ns: int,
    ) -> None:
        if len(transport_header) != TRANSPORT_HEADER.size:
            raise ValueError("Capture received an invalid transport header size")
        source_fields = TRANSPORT_HEADER.unpack(transport_header)
        if source_fields[9] != len(payload):
            raise ValueError("Capture payload size does not match its header")
        self._file.write(
            FRAME_HEADER.pack(
                FRAME_MAGIC,
                FRAME_HEADER.size,
                TRANSPORT_HEADER.size,
                pc_monotonic_ns,
                pc_wall_ns,
                transport_header,
            )
        )
        self._file.write(payload)
        self.frame_count += 1

    def close(self, completed: bool = True) -> None:
        if self._file.closed:
            return
        failure = None
        try:
            # Flush frame data before advertising a completed capture header.
            self._file.flush()
            self._write_file_header(completed=completed)
            self._file.flush()
        except BaseException as error:
            failure = error
        try:
            self._file.close()
        except BaseException:
            if failure is None:
                raise
        if failure is not None:
            raise failure


def scan_stereo_mouth_stills(path: str | Path) -> tuple[list[tuple[int, int]], int]:
    """Locate paired mouth stills in either the old face or new mouth layout."""
    capture_path = Path(path)
    layouts = {(0x0C, 800, 400), (0x1C, 1200, 400)}
    entries: list[tuple[int, int]] = []
    frame_width: int | None = None
    file_size = capture_path.stat().st_size
    with capture_path.open("rb") as capture:
        raw_header = capture.read(FILE_HEADER.size)
        if len(raw_header) != FILE_HEADER.size:
            raise ValueError("Capture file header is missing")
        magic, version, header_size, _created_wall, _created_mono, declared, _complete, _reserved = FILE_HEADER.unpack(raw_header)
        if magic != FILE_MAGIC or version != FILE_VERSION or header_size != FILE_HEADER.size:
            raise ValueError("Unrecognized capture file")
        while True:
            raw_frame = capture.read(FRAME_HEADER.size)
            if not raw_frame:
                break
            if len(raw_frame) != FRAME_HEADER.size:
                raise ValueError("Truncated capture frame header")
            frame_magic, record_size, source_size, pc_monotonic_ns, _pc_wall_ns, source_header = FRAME_HEADER.unpack(raw_frame)
            if (frame_magic != FRAME_MAGIC or record_size != FRAME_HEADER.size
                    or source_size != TRANSPORT_HEADER.size):
                raise ValueError("Invalid capture frame record")
            source = TRANSPORT_HEADER.unpack(source_header)
            width, height, stride, pixel_format, payload_size, mask = map(
                int, (source[5], source[6], source[7], source[8], source[9], source[10])
            )
            if (mask, width, height) not in layouts:
                raise ValueError("Tongue stills need cameras 2 and 3 in face or mouth mode")
            if stride != width or pixel_format != 1 or payload_size != width * height:
                raise ValueError("Unsupported tongue still frame layout")
            if frame_width is not None and width != frame_width:
                raise ValueError("Capture changes camera layout between tongue stills")
            frame_width = width
            entries.append((capture.tell(), int(pc_monotonic_ns)))
            capture.seek(payload_size, 1)
            if capture.tell() > file_size:
                raise ValueError("Truncated tongue still payload")
    if len(entries) != declared:
        raise ValueError(f"Capture declares {declared} frames but scanned {len(entries)}")
    if not entries or frame_width is None:
        raise ValueError("Capture contains no tongue stills")
    return entries, frame_width


def inspect_capture(path: str | Path) -> dict[str, object]:
    capture_path = Path(path).resolve()
    masks: Counter[int] = Counter()
    dimensions: Counter[tuple[int, int]] = Counter()
    first_pc_ns: int | None = None
    last_pc_ns: int | None = None
    first_sequence: int | None = None
    last_sequence: int | None = None
    source_skips = 0
    first_rejected_torn: int | None = None
    last_rejected_torn: int | None = None
    scanned_frames = 0
    truncated = False
    recent_payloads: deque[int] = deque(maxlen=12)
    exact_nonconsecutive_replays = 0

    with capture_path.open("rb") as capture:
        raw_file_header = capture.read(FILE_HEADER.size)
        if len(raw_file_header) != FILE_HEADER.size:
            raise ValueError("Capture is too short to contain a file header")
        (magic, version, header_size, created_wall_ns, _created_monotonic_ns,
         declared_frames, completed, _reserved) = FILE_HEADER.unpack(raw_file_header)
        if magic != FILE_MAGIC or version != FILE_VERSION or header_size != FILE_HEADER.size:
            raise ValueError("Unrecognized capture file")

        while True:
            raw_frame_header = capture.read(FRAME_HEADER.size)
            if not raw_frame_header:
                break
            if len(raw_frame_header) != FRAME_HEADER.size:
                truncated = True
                break
            (frame_magic, frame_header_size, source_header_size, pc_monotonic_ns,
             _pc_wall_ns, transport_header) = FRAME_HEADER.unpack(raw_frame_header)
            if (frame_magic != FRAME_MAGIC or frame_header_size != FRAME_HEADER.size
                    or source_header_size != TRANSPORT_HEADER.size):
                raise ValueError(f"Invalid frame record at frame {scanned_frames}")
            source_fields = TRANSPORT_HEADER.unpack(transport_header)
            payload_size = source_fields[9]
            payload = capture.read(payload_size)
            if len(payload) != payload_size:
                truncated = True
                break
            payload_fingerprint = zlib.crc32(payload)
            if (len(recent_payloads) >= 2
                    and payload_fingerprint != recent_payloads[-1]
                    and payload_fingerprint in list(recent_payloads)[:-1]):
                exact_nonconsecutive_replays += 1
            recent_payloads.append(payload_fingerprint)
            masks[source_fields[10]] += 1
            dimensions[(source_fields[5], source_fields[6])] += 1
            first_pc_ns = pc_monotonic_ns if first_pc_ns is None else first_pc_ns
            last_pc_ns = pc_monotonic_ns
            sequence = source_fields[3]
            rejected_torn = source_fields[11]
            if last_sequence is not None and sequence > last_sequence + 1:
                source_skips += sequence - last_sequence - 1
            first_sequence = sequence if first_sequence is None else first_sequence
            last_sequence = sequence
            first_rejected_torn = (
                rejected_torn if first_rejected_torn is None else first_rejected_torn
            )
            last_rejected_torn = rejected_torn
            scanned_frames += 1

    duration_seconds = (
        (last_pc_ns - first_pc_ns) / 1_000_000_000
        if first_pc_ns is not None and last_pc_ns is not None and scanned_frames > 1
        else 0.0
    )
    return {
        "path": capture_path,
        "size_bytes": capture_path.stat().st_size,
        "created_wall_ns": created_wall_ns,
        "declared_frames": declared_frames,
        "scanned_frames": scanned_frames,
        "completed": bool(completed),
        "truncated": truncated,
        "duration_seconds": duration_seconds,
        "average_fps": ((scanned_frames - 1) / duration_seconds
                        if duration_seconds > 0 else 0.0),
        "first_sequence": first_sequence,
        "last_sequence": last_sequence,
        "source_skips": source_skips,
        "exact_nonconsecutive_replays": exact_nonconsecutive_replays,
        "rejected_torn_delta": (
            last_rejected_torn - first_rejected_torn
            if first_rejected_torn is not None and last_rejected_torn is not None
            else 0
        ),
        "camera_masks": dict(masks),
        "dimensions": dict(dimensions),
    }


def camera_list(mask: int) -> str:
    return ",".join(str(camera_id) for camera_id in range(5) if mask & (1 << camera_id))


def main() -> int:
    parser = argparse.ArgumentParser(description="Inspect a Quest Pro .qpcap file")
    parser.add_argument("capture")
    arguments = parser.parse_args()
    summary = inspect_capture(arguments.capture)
    masks = ", ".join(
        f"[{camera_list(mask)}] x {count}"
        for mask, count in summary["camera_masks"].items()
    )
    dimensions = ", ".join(
        f"{width}x{height} x {count}"
        for (width, height), count in summary["dimensions"].items()
    )
    print(f"Capture: {summary['path']}")
    print(f"Complete: {summary['completed']}; truncated: {summary['truncated']}")
    print(
        f"Frames: {summary['scanned_frames']} scanned / "
        f"{summary['declared_frames']} declared"
    )
    print(
        f"Duration: {summary['duration_seconds']:.3f} s; "
        f"average: {summary['average_fps']:.2f} FPS"
    )
    print(
        f"Source sequence: {summary['first_sequence']}..{summary['last_sequence']}; "
        f"skipped: {summary['source_skips']}; "
        f"exact nonconsecutive replays: "
        f"{summary['exact_nonconsecutive_replays']}; "
        f"torn rejections during capture: {summary['rejected_torn_delta']}"
    )
    print(f"Cameras: {masks or 'none'}")
    print(f"Layouts: {dimensions or 'none'}")
    print(f"Size: {summary['size_bytes'] / 1_000_000:.2f} MB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
