"""Synthetic PE images exercise family admission; no proprietary code is copied."""
import hashlib
from pathlib import Path
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from driver_layout import analyse_driver, inspect_driver, DriverLayoutError, MAX_DRIVER_BYTES


PE = 0x80
OPTIONAL = PE + 24
TABLE = OPTIONAL + 240
TEXT, RDATA, RELOC, RSRC = 0x400, 0x600, 0xa00, 0xc00


def put(data, offset, fmt, *values):
    struct.pack_into(fmt, data, offset, *values)


def image():
    data = bytearray(0xe00)
    data[:2] = b"MZ"
    put(data, 60, "<I", PE)
    data[PE:PE + 4] = b"PE\0\0"
    put(data, PE + 4, "<HHIIIHH", 0x8664, 4, 12345, 0, 0, 240, 0x2022)
    put(data, OPTIONAL, "<H", 0x20b)
    put(data, OPTIONAL + 16, "<I", 0x1000)
    put(data, OPTIONAL + 24, "<Q", 0x180000000)
    put(data, OPTIONAL + 32, "<II", 0x1000, 0x200)
    put(data, OPTIONAL + 56, "<II", 0x5000, 0x400)
    put(data, OPTIONAL + 108, "<I", 16)
    sections = [(b".text", 0x100, 0x1000, 0x200, TEXT, 0x60000020),
                (b".rdata", 0x300, 0x2000, 0x400, RDATA, 0x40000040),
                (b".reloc", 0x100, 0x3000, 0x200, RELOC, 0x42000040),
                (b".rsrc", 0x200, 0x4000, 0x200, RSRC, 0x40000040)]
    for index, (name, size, rva, raw_size, raw, flags) in enumerate(sections):
        row = TABLE + index * 40
        data[row:row + 8] = name.ljust(8, b"\0")
        put(data, row + 8, "<IIII", size, rva, raw_size, raw)
        put(data, row + 36, "<I", flags)
    data[TEXT:TEXT + 0x100] = bytes(range(256))
    put(data, TEXT + 16, "<Q", 0x180001234)
    # One real DIR64 fixup and one padding entry.
    put(data, OPTIONAL + 112 + 5 * 8, "<II", 0x3000, 12)
    put(data, RELOC, "<IIHH", 0x1000, 12, 0xa010, 0)
    # CodeView identity and directory timestamp are pure build metadata.
    codeview = b"RSDS" + bytes(range(16)) + struct.pack("<I", 1) + b"driver.pdb\0"
    put(data, OPTIONAL + 112 + 6 * 8, "<II", 0x2000, 28)
    put(data, RDATA, "<IIHHIIII", 0, 12345, 0, 0, 2, len(codeview), 0x2040, RDATA + 0x40)
    data[RDATA + 0x40:RDATA + 0x40 + len(codeview)] = codeview
    # RT_VERSION -> name -> language -> VERSIONINFO leaf.
    put(data, OPTIONAL + 112 + 2 * 8, "<II", 0x4000, 0x200)
    for directory, name, target in [(0, 16, 0x80000020), (0x20, 1, 0x80000040), (0x40, 1033, 0x60)]:
        put(data, RSRC + directory + 12, "<HH", 0, 1)
        put(data, RSRC + directory + 16, "<II", name, target)
    put(data, RSRC + 0x60, "<IIII", 0x4080, 92, 0, 0)
    put(data, RSRC + 0x80, "<HHH", 92, 52, 0)
    key = "VS_VERSION_INFO\0".encode("utf-16le")
    data[RSRC + 0x86:RSRC + 0x86 + len(key)] = key
    put(data, RSRC + 0x80 + 40, "<I", 0xfeef04bd)
    return data


def family(data):
    return analyse_driver(bytes(data))["layoutSha256"]


class FamilyTests(unittest.TestCase):
    def test_metadata_changes_keep_family_but_not_loaded_identity(self):
        original = image()
        changed = image()
        put(changed, PE + 8, "<I", 99999)
        put(changed, OPTIONAL + 64, "<I", 0x13579)
        put(changed, RDATA + 4, "<I", 98765)
        changed[RDATA + 0x44:RDATA + 0x54] = bytes(range(16, 32))
        put(changed, RDATA + 0x54, "<I", 3)
        put(changed, RSRC + 0x80 + 48, "<I", 0x1000200)
        before, after = analyse_driver(original), analyse_driver(changed)
        self.assertEqual(before["layoutSha256"], after["layoutSha256"])
        self.assertNotEqual(before["liveImageIdentity"]["headerSha256"], after["liveImageIdentity"]["headerSha256"])
        self.assertNotEqual(before["liveImageIdentity"]["codeView"], after["liveImageIdentity"]["codeView"])

    def test_certificate_overlay_does_not_change_loaded_layout(self):
        original, changed = image(), image()
        put(changed, OPTIONAL + 112 + 4 * 8, "<II", len(changed), 16)
        changed.extend(b"certificate-data")
        self.assertEqual(family(original), family(changed))

    def test_instruction_changes_cannot_be_hidden_by_metadata(self):
        original, changed = image(), image()
        changed[TEXT + 128] ^= 1
        self.assertNotEqual(family(original), family(changed))

    def test_private_data_changes_are_not_ignored(self):
        original, changed = image(), image()
        changed[RDATA + 0x100] = 7
        self.assertNotEqual(family(original), family(changed))

    def test_loader_header_changes_are_not_ignored(self):
        original, changed = image(), image()
        put(changed, OPTIONAL + 16, "<I", 0x1010)
        self.assertNotEqual(family(original), family(changed))

    def test_debug_path_changes_are_conservatively_not_normalised(self):
        original, changed = image(), image()
        changed[RDATA + 0x58] = ord('x')
        self.assertNotEqual(family(original), family(changed))

    def test_non_version_resource_bytes_are_not_ignored(self):
        original, changed = image(), image()
        changed[RSRC + 0x1f0] = 42
        self.assertNotEqual(family(original), family(changed))

    def test_relocation_table_and_pointer_values_remain_in_family(self):
        original, changed = image(), image()
        put(changed, RELOC + 8, "<H", 0xa020)
        self.assertNotEqual(family(original), family(changed))
        changed = image()
        put(changed, TEXT + 16, "<Q", 0x180001235)
        self.assertNotEqual(family(original), family(changed))

    def test_loaded_code_proof_contains_only_verified_relocations(self):
        data = image()
        identity = analyse_driver(data)["liveImageIdentity"]
        code = identity["executableSections"]
        self.assertEqual(len(code), 1)
        self.assertEqual(code[0]["rva"], 0x1000)
        self.assertEqual(code[0]["relocations"], [16])
        self.assertEqual(code[0]["sha256"], hashlib.sha256(data[TEXT:TEXT + 0x200]).hexdigest())

    def test_unknown_full_hash_is_admitted_only_by_approved_family(self):
        data = image()
        with tempfile.TemporaryDirectory() as temporary:
            driver = Path(temporary) / "driver.dll"
            driver.write_bytes(data)
            expected = family(data)
            facts = inspect_driver(driver, {"pcDriverLayoutSha256s": [expected]})
            self.assertFalse(facts["knownHash"])
            self.assertTrue(facts["approvedLayoutMatched"])
            self.assertTrue(facts["compatible"])
            self.assertFalse(inspect_driver(driver, {})["compatible"])

    def test_inspected_exact_build_remains_admitted(self):
        data = image()
        with tempfile.TemporaryDirectory() as temporary:
            driver = Path(temporary) / "driver.dll"
            driver.write_bytes(data)
            facts = inspect_driver(driver, {"pcDriverSha256": hashlib.sha256(data).hexdigest()})
            self.assertTrue(facts["knownHash"])
            self.assertTrue(facts["compatible"])
            self.assertFalse(facts["approvedLayoutMatched"])


class MalformedTests(unittest.TestCase):
    def reject(self, mutate):
        data = image()
        mutate(data)
        with self.assertRaises(DriverLayoutError):
            analyse_driver(data)

    def test_missing_and_oversized_files_fail_with_controlled_error(self):
        with tempfile.TemporaryDirectory() as temporary:
            driver = Path(temporary) / "missing.dll"
            with self.assertRaises(DriverLayoutError):
                inspect_driver(driver, {})
        with self.assertRaises(DriverLayoutError):
            analyse_driver(b"MZ" + bytes(MAX_DRIVER_BYTES))

    def test_truncated_headers_and_sections_are_rejected(self):
        for length in (0, 63, 127, 511, 1500):
            with self.subTest(length=length), self.assertRaises(DriverLayoutError):
                analyse_driver(image()[:length])

    def test_wrong_architecture_and_optional_headers_are_rejected(self):
        self.reject(lambda data: put(data, PE + 4, "<H", 0x14c))
        self.reject(lambda data: put(data, OPTIONAL, "<H", 0x10b))
        self.reject(lambda data: put(data, PE + 20, "<H", 16))

    def test_bad_alignment_and_image_bounds_are_rejected(self):
        self.reject(lambda data: put(data, OPTIONAL + 36, "<I", 3))
        self.reject(lambda data: put(data, OPTIONAL + 56, "<I", 0xffffffff))
        self.reject(lambda data: put(data, OPTIONAL + 60, "<I", 32))

    def test_overlapping_raw_and_virtual_sections_are_rejected(self):
        self.reject(lambda data: put(data, TABLE + 40 + 20, "<I", TEXT))
        self.reject(lambda data: put(data, TABLE + 40 + 12, "<I", 0x1000))

    def test_debug_payload_cannot_claim_instruction_bytes(self):
        self.reject(lambda data: (put(data, RDATA + 20, "<II", 0x1000, TEXT),
                                  data.__setitem__(slice(TEXT, TEXT + 4), b"RSDS")))

    def test_debug_pointer_mismatch_and_aliasing_are_rejected(self):
        self.reject(lambda data: put(data, RDATA + 24, "<I", RDATA + 0x80))
        self.reject(lambda data: put(data, RDATA + 20, "<II", 0x2000, RDATA))

    def test_debug_directory_truncation_and_bad_codeview_are_rejected(self):
        self.reject(lambda data: put(data, OPTIONAL + 112 + 6 * 8 + 4, "<I", 27))
        self.reject(lambda data: data.__setitem__(RDATA + 0x40, 0))

    def test_resources_cannot_hide_code_or_out_of_bounds_bytes(self):
        self.reject(lambda data: put(data, RSRC + 0x60, "<I", 0x1000))
        self.reject(lambda data: put(data, RSRC + 0x60 + 4, "<I", 0xffffffff))
        self.reject(lambda data: put(data, TABLE + 3 * 40 + 36, "<I", 0x60000040))

    def test_resource_cycles_and_malformed_versioninfo_are_rejected(self):
        self.reject(lambda data: put(data, RSRC + 0x20 + 20, "<I", 0x80000020))
        self.reject(lambda data: put(data, RSRC + 0x80 + 40, "<I", 0))
        self.reject(lambda data: put(data, RSRC + 0x80, "<H", 65535))

    def test_certificate_range_cannot_hide_mapped_bytes(self):
        self.reject(lambda data: put(data, OPTIONAL + 112 + 4 * 8, "<II", TEXT, 16))

    def test_all_bss_sections_fail_with_controlled_error(self):
        def mutate(data):
            for index in range(4):
                put(data, TABLE + index * 40 + 16, "<II", 0, 0)
            put(data, OPTIONAL + 112 + 4 * 8, "<II", len(data) - 16, 16)
        self.reject(mutate)

    def test_relocation_blocks_and_masks_are_validated(self):
        self.reject(lambda data: put(data, RELOC + 4, "<I", 7))
        self.reject(lambda data: put(data, RELOC + 8, "<H", 0x3010))
        self.reject(lambda data: put(data, RELOC + 10, "<H", 0xa014))
        self.reject(lambda data: put(data, RELOC + 8, "<H", 0xafff))

    def test_writable_executable_sections_are_rejected(self):
        self.reject(lambda data: put(data, TABLE + 36, "<I", 0xe0000020))


if __name__ == "__main__":
    unittest.main()
