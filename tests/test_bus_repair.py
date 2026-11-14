"""Run explicitly with private local --save, --reference-zip and --claims inputs.

All mutations are in-memory copies. No save or claim file is written.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import struct
import sys
import unittest
import uuid
import zipfile
import zlib

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import repair_bus_pile as repair

parser = argparse.ArgumentParser(description=__doc__)
for name in ("save", "reference-zip", "claims"):
    parser.add_argument("--" + name, required=True)
args = parser.parse_args()


def append_entry(source, value):
    header, entries = repair.decode_save(source)
    key = b"repair-reference-test"
    raw = (header[:8] + struct.pack("<i", len(entries) + 1) +
           b"".join(original for _, _, original in entries) +
           struct.pack("<H", len(key)) + key + struct.pack("<i", len(value)) + value)
    payload = gzip.compress(raw, mtime=0)
    return b"EVL2\x01\x01" + struct.pack("<II", len(payload), zlib.crc32(payload)) + payload


class RepairTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = Path(args.save).read_bytes()
        cls.input_hash = hashlib.sha256(cls.source).hexdigest()
        cls.claims = json.loads(Path(args.claims).read_text(encoding="utf-8-sig"))
        with zipfile.ZipFile(args.reference_zip) as archive:
            cls.reference = archive.read("save.dat")
        cls.output, cls.report = repair.repair(cls.source, cls.reference, cls.claims)

    def test_exact_removal_and_byte_preservation(self):
        self.assertEqual(self.report["objectsBefore"], 837)
        self.assertEqual(self.report["objectsAfter"], 782)
        self.assertEqual(len(self.report["removedIds"]), 55)
        before = repair.decode_save(self.source)[1]
        after = repair.decode_save(self.output)[1]
        self.assertEqual([(k, v) for k, v, _ in before if k != "game.manifest"],
                         [(k, v) for k, v, _ in after if k != "game.manifest"])
        original = repair.parse_manifest(dict((k, v) for k, v, _ in before)["game.manifest"])[2]
        remaining = repair.parse_manifest(dict((k, v) for k, v, _ in after)["game.manifest"])[2]
        expected = [r["raw"] for r in original if r["guid"] not in self.report["removedIds"]]
        self.assertEqual([r["raw"] for r in remaining], expected)
        self.assertEqual(sum(r["asset"] == repair.BUS for r in remaining), 2)

    def test_crc_failure(self):
        changed = bytearray(self.source)
        changed[-1] ^= 1
        with self.assertRaisesRegex(ValueError, "CRC"):
            repair.repair(bytes(changed), self.reference, self.claims)

    def test_claimed_target_refused(self):
        claims = {"Claims": self.claims["Claims"] + [{"VehicleId": self.report["removedIds"][0]}]}
        with self.assertRaisesRegex(ValueError, "claimed"):
            repair.repair(self.source, self.reference, claims)

    def test_all_reference_encodings_refused(self):
        guid = self.report["removedIds"][0]
        parsed = uuid.UUID(hex=guid)
        for label, value in (("ASCII", guid.encode()), ("uppercase", guid.upper().encode()),
                             ("UTF16", guid.encode("utf-16le")),
                             ("UUID", parsed.bytes), ("UUID_LE", parsed.bytes_le)):
            with self.subTest(encoding=label):
                with self.assertRaisesRegex(ValueError, "referenced"):
                    repair.repair(append_entry(self.source, value), self.reference, self.claims)

    def test_truncation_and_wrong_reference_refused(self):
        with self.assertRaises(ValueError):
            repair.repair(self.source[:-1], self.reference, self.claims)
        with self.assertRaisesRegex(ValueError, "baseline"):
            repair.repair(self.source, self.source, self.claims)

    @classmethod
    def tearDownClass(cls):
        if hashlib.sha256(Path(args.save).read_bytes()).hexdigest() != cls.input_hash:
            raise AssertionError("Input save changed during read-only tests")


if __name__ == "__main__":
    unittest.main(argv=[sys.argv[0]], verbosity=2)
