"""Identify an inspected Virtual Desktop driver layout without pinning a build ID.

Only PE build/debug identity and VERSIONINFO payloads are normalised. Instructions,
private data, section geometry, imports and relocation tables remain part of the
approved family hash. This is a compatibility proof, not an Authenticode verifier.
"""
from __future__ import annotations

import hashlib
from pathlib import Path
import struct


MAX_DRIVER_BYTES = 32 * 1024 * 1024
MAX_IMAGE_BYTES = 128 * 1024 * 1024
MAX_HEADERS_BYTES = 256 * 1024
FAMILY_FORMAT = b"qpro-vd-pc-layout-v1\0"


class DriverLayoutError(ValueError):
    """The file cannot establish a supported PE64 layout."""


class _Image:
    def __init__(self, data: bytes):
        if not 64 <= len(data) <= MAX_DRIVER_BYTES or data[:2] != b"MZ":
            raise DriverLayoutError("The Windows hand driver is not a bounded PE image.")
        self.data = data = bytes(data)
        self.pe = self.u32(60)
        if not 64 <= self.pe <= MAX_HEADERS_BYTES or self.take(self.pe, 4) != b"PE\0\0":
            raise DriverLayoutError("The Windows hand driver has invalid PE headers.")
        coff = self.pe + 4
        machine, count = self.unpack("<HH", coff)
        optional_size = self.u16(coff + 16)
        if machine != 0x8664 or not 1 <= count <= 96 or optional_size != 240:
            raise DriverLayoutError("The Windows hand driver requires an AMD64 PE32+ layout.")
        self.optional = coff + 20
        self.take(self.optional, optional_size)
        if self.u16(self.optional) != 0x20b or self.u32(self.optional + 108) != 16:
            raise DriverLayoutError("The Windows hand driver has unsupported optional headers.")
        self.image_base = self.unpack("<Q", self.optional + 24)[0]
        section_alignment, file_alignment = self.unpack("<II", self.optional + 32)
        self.image_size, self.header_size = self.unpack("<II", self.optional + 56)
        if (not 512 <= file_alignment <= 65536 or file_alignment & (file_alignment - 1)
                or not file_alignment <= section_alignment <= 65536
                or section_alignment & (section_alignment - 1)
                or not 0 < self.image_size <= MAX_IMAGE_BYTES
                or self.image_size % section_alignment
                or not 0 < self.header_size <= min(len(data), MAX_HEADERS_BYTES)
                or self.header_size % file_alignment):
            raise DriverLayoutError("The Windows hand driver has invalid image bounds or alignment.")
        table = self.optional + optional_size
        if table + count * 40 > self.header_size:
            raise DriverLayoutError("The Windows hand driver section table is truncated.")
        self.sections = []
        names, raw_ranges, virtual_ranges = set(), [], []
        for index in range(count):
            row = table + index * 40
            name = self.take(row, 8).split(b"\0", 1)[0]
            size, rva, raw_size, raw = self.unpack("<IIII", row + 8)
            flags = self.u32(row + 36)
            if (not name or name in names or any(value < 32 or value > 126 for value in name)
                    or rva % section_alignment or rva < self.header_size
                    or max(size, raw_size) == 0 or rva + max(size, raw_size) > self.image_size):
                raise DriverLayoutError("The Windows hand driver has an invalid section map.")
            names.add(name)
            virtual_ranges.append((rva, rva + max(size, raw_size)))
            if raw_size:
                if raw < self.header_size or raw % file_alignment or raw_size % file_alignment:
                    raise DriverLayoutError("The Windows hand driver has invalid raw section alignment.")
                self.take(raw, raw_size)
                raw_ranges.append((raw, raw + raw_size))
            self.sections.append(dict(name=name, rva=rva, size=size, raw=raw, rawSize=raw_size,
                                      flags=flags, bytes=bytearray(self.take(raw, raw_size))))
        for ranges in (raw_ranges, virtual_ranges):
            ordered = sorted(ranges)
            if any(left[1] > right[0] for left, right in zip(ordered, ordered[1:])):
                raise DriverLayoutError("The Windows hand driver contains overlapping sections.")
        if not raw_ranges:
            raise DriverLayoutError("The Windows hand driver has no file-backed sections.")
        certificate, certificate_size = self.directory(4)
        if bool(certificate) != bool(certificate_size):
            raise DriverLayoutError("The Windows hand driver has an incomplete certificate range.")
        if certificate_size:
            self.take(certificate, certificate_size)
            if certificate < max(end for _, end in raw_ranges):
                raise DriverLayoutError("The Windows hand driver certificate overlaps mapped data.")
        self.exclusions = []

    def take(self, offset, size):
        if offset < 0 or size < 0 or offset + size > len(self.data):
            raise DriverLayoutError("The Windows hand driver contains a truncated PE range.")
        return self.data[offset:offset + size]

    def unpack(self, fmt, offset):
        return struct.unpack(fmt, self.take(offset, struct.calcsize(fmt)))

    def u16(self, offset):
        return self.unpack("<H", offset)[0]

    def u32(self, offset):
        return self.unpack("<I", offset)[0]

    def directory(self, index):
        return self.unpack("<II", self.optional + 112 + index * 8)

    def section_at(self, rva, size):
        matches = [section for section in self.sections
                   if section["rva"] <= rva and rva + size <= section["rva"] + section["rawSize"]]
        if size <= 0 or len(matches) != 1:
            raise DriverLayoutError("The Windows hand driver directory is outside file-backed image data.")
        return matches[0]

    def file_offset(self, rva, size):
        section = self.section_at(rva, size)
        return section["raw"] + rva - section["rva"]

    def exclude(self, rva, size):
        section = self.section_at(rva, size)
        if section["flags"] & 0x20000000 or section["flags"] & 0x80000000:
            raise DriverLayoutError("The Windows hand driver metadata overlaps executable or writable data.")
        start, end = rva, rva + size
        if any(start < other_end and other_start < end for other_start, other_end in self.exclusions):
            raise DriverLayoutError("The Windows hand driver has aliased metadata exclusions.")
        self.exclusions.append((start, end))
        offset = rva - section["rva"]
        section["bytes"][offset:offset + size] = bytes(size)

    def debug_identity(self):
        rva, size = self.directory(6)
        if not rva and not size:
            return []
        if not rva or not 28 <= size <= 28 * 64 or size % 28:
            raise DriverLayoutError("The Windows hand driver has an invalid debug directory.")
        offset = self.file_offset(rva, size)
        records = []
        for index in range(size // 28):
            entry = offset + index * 28
            kind, payload_size, payload_rva, payload_raw = self.unpack("<IIII", entry + 12)
            if payload_size:
                if self.file_offset(payload_rva, payload_size) != payload_raw:
                    raise DriverLayoutError("The Windows hand driver has inconsistent debug pointers.")
                # Other debug formats retain all payload bytes, not just their type.
                self.section_at(payload_rva, payload_size)
            self.exclude(rva + index * 28 + 4, 4)
            if kind == 2:
                if not 25 <= payload_size <= 4096:
                    raise DriverLayoutError("The Windows hand driver has invalid CodeView metadata.")
                payload = self.take(payload_raw, payload_size)
                if payload[:4] != b"RSDS" or payload[-1:] != b"\0":
                    raise DriverLayoutError("The Windows hand driver has unsupported CodeView metadata.")
                records.append(dict(rva=payload_rva, guid=payload[4:20].hex(),
                                    age=struct.unpack("<I", payload[20:24])[0]))
                self.exclude(payload_rva + 4, 20)
        return records

    def version_resources(self):
        rva, size = self.directory(2)
        if not rva and not size:
            return
        if not rva or not 16 <= size <= 4 * 1024 * 1024:
            raise DriverLayoutError("The Windows hand driver has an invalid resource directory.")
        base = self.file_offset(rva, size)
        resource_section = self.section_at(rva, size)
        if resource_section["flags"] & (0x20000000 | 0x80000000):
            raise DriverLayoutError("The Windows hand driver resource directory is executable or writable.")
        visited, payloads = set(), set()

        def resource_range(offset, length):
            if offset < 0 or length < 0 or offset + length > size:
                raise DriverLayoutError("The Windows hand driver resource tree is out of bounds.")
            return base + offset

        def entries(offset):
            resource_range(offset, 16)
            if offset in visited:
                raise DriverLayoutError("The Windows hand driver has a cyclic resource tree.")
            visited.add(offset)
            named, numeric = self.unpack("<HH", base + offset + 12)
            count = named + numeric
            if count > 512:
                raise DriverLayoutError("The Windows hand driver has too many resource entries.")
            resource_range(offset + 16, count * 8)
            return [self.unpack("<II", base + offset + 16 + index * 8) for index in range(count)]

        def versions(offset, depth):
            if depth > 2:
                raise DriverLayoutError("The Windows hand driver version tree is too deep.")
            for _, target in entries(offset):
                if target & 0x80000000:
                    versions(target & 0x7fffffff, depth + 1)
                    continue
                leaf = resource_range(target, 16)
                data_rva, length, _, reserved = self.unpack("<IIII", leaf)
                if reserved or not 6 <= length <= 65536 or data_rva < rva or data_rva + length > rva + size:
                    raise DriverLayoutError("The Windows hand driver version data is invalid.")
                if (data_rva, length) in payloads:
                    raise DriverLayoutError("The Windows hand driver aliases version payloads.")
                payloads.add((data_rva, length))
                payload = self.take(self.file_offset(data_rva, length), length)
                declared, value_length, kind = struct.unpack("<HHH", payload[:6])
                key = "VS_VERSION_INFO\0".encode("utf-16le")
                value_offset = (6 + len(key) + 3) & ~3
                if (not value_offset + 52 <= declared <= length or value_length != 52 or kind != 0
                        or payload[6:6 + len(key)] != key
                        or payload[value_offset:value_offset + 4] != b"\xbd\x04\xef\xfe"
                        or any(payload[declared:])):
                    raise DriverLayoutError("The Windows hand driver has malformed VERSIONINFO data.")
                self.exclude(data_rva, length)

        for name, target in entries(0):
            if name == 16:
                if not target & 0x80000000:
                    raise DriverLayoutError("The Windows hand driver version root is not a directory.")
                versions(target & 0x7fffffff, 0)

    def code_identity(self):
        executable = [section for section in self.sections if section["flags"] & 0x20000000]
        if not executable or any(section["flags"] & 0x80000000 or not section["rawSize"] for section in executable):
            raise DriverLayoutError("The Windows hand driver executable section layout is unsupported.")
        relocations = {section["rva"]: [] for section in executable}
        rva, size = self.directory(5)
        if bool(rva) != bool(size):
            raise DriverLayoutError("The Windows hand driver relocation directory is incomplete.")
        if size:
            offset = self.file_offset(rva, size)
            cursor = 0
            seen, covered = set(), set()
            while cursor < size:
                if cursor + 8 > size:
                    raise DriverLayoutError("The Windows hand driver relocation block is truncated.")
                page, length = self.unpack("<II", offset + cursor)
                if page % 4096 or length < 8 or length % 2 or cursor + length > size:
                    raise DriverLayoutError("The Windows hand driver relocation block is invalid.")
                for item in range(cursor + 8, cursor + length, 2):
                    value = self.u16(offset + item)
                    kind, target = value >> 12, page + (value & 4095)
                    if not kind:
                        continue
                    if kind != 10:
                        raise DriverLayoutError("The Windows hand driver requires unsupported relocation fixups.")
                    section = self.section_at(target, 8)
                    if any(target + byte in covered for byte in range(8)):
                        raise DriverLayoutError("The Windows hand driver contains overlapping relocations.")
                    seen.add(target)
                    covered.update(range(target, target + 8))
                    if len(seen) > 65536:
                        raise DriverLayoutError("The Windows hand driver has too many relocations.")
                    if section["rva"] in relocations:
                        relocations[section["rva"]].append(target - section["rva"])
                cursor += length
        return [dict(rva=section["rva"], size=section["rawSize"],
                     sha256=hashlib.sha256(self.take(section["raw"], section["rawSize"])).hexdigest(),
                     relocations=sorted(relocations[section["rva"]])) for section in executable]


def analyse_driver(data: bytes) -> dict:
    """Return an immutable layout-family hash and proof for the loaded image."""
    image = _Image(data)
    identity = dict(sizeOfImage=image.image_size, sizeOfHeaders=image.header_size,
                    headerSha256=hashlib.sha256(data[:image.header_size]).hexdigest(),
                    preferredImageBase=hex(image.image_base), codeView=image.debug_identity(),
                    executableSections=image.code_identity())
    image.version_resources()
    headers = bytearray(data[:image.header_size])
    headers[image.pe + 8:image.pe + 12] = bytes(4)  # COFF build timestamp.
    headers[image.optional + 64:image.optional + 68] = bytes(4)  # PE checksum.
    security = image.optional + 112 + 4 * 8
    headers[security:security + 8] = bytes(8)  # File-only certificate overlay.
    digest = hashlib.sha256(FAMILY_FORMAT)
    digest.update(headers)
    for section in image.sections:
        digest.update(section["bytes"])
    return dict(layoutSha256=digest.hexdigest(), liveImageIdentity=identity)


def inspect_driver(driver_path: Path, profile: dict) -> dict:
    """Read once; exact known builds or an approved code/layout family may proceed."""
    try:
        with Path(driver_path).open("rb") as stream:
            data = stream.read(MAX_DRIVER_BYTES + 1)
    except OSError as error:
        raise DriverLayoutError("The Windows hand driver could not be read.") from error
    facts = analyse_driver(data)
    driver_hash = hashlib.sha256(data).hexdigest()
    known = driver_hash in (profile.get("pcDriverSha256"), *profile.get("additionalPcDriverSha256s", []))
    family = facts["layoutSha256"] in profile.get("pcDriverLayoutSha256s", [])
    return dict(driverHash=driver_hash, knownHash=known, approvedLayoutMatched=family,
                compatible=known or family, **facts)
