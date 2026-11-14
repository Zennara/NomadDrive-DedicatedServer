"""Prepare a byte-preserving repair in a NEW output directory. Never edits inputs.

Pinned to the audited Nomad Drive format 16 / EVL2 v1 gzip save. The targets are
the 55 school-bus IDs added since a known two-bus backup, not a spatial guess.
"""
import argparse
import gzip
import hashlib
import io
import json
import math
from pathlib import Path
import struct
import uuid
import zipfile
import zlib

BUS = "df321578632574b4a9d3394dbb489933"
MAX_BYTES = 256 * 1024 * 1024


def require(ok, message):
    if not ok:
        raise ValueError(message)


class Reader:
    def __init__(self, data):
        self.data, self.pos = data, 0

    def take(self, count):
        require(0 <= count <= len(self.data) - self.pos, "Truncated or invalid field")
        start = self.pos
        self.pos += count
        return self.data[start:self.pos]

    def number(self, code="i"):
        return struct.unpack("<" + code, self.take(struct.calcsize("<" + code)))[0]

    def count(self, maximum=200000):
        count = self.number()
        require(0 <= count <= maximum, "Invalid count")
        return count

    def string(self):
        length = 0
        for shift in range(0, 35, 7):
            value = self.number("B")
            length |= (value & 127) << shift
            if value < 128:
                require(length <= MAX_BYTES, "Oversized string")
                return self.take(length).decode("utf-8")
        raise ValueError("Invalid BinaryReader string length")

    def blob(self):
        return self.take(self.count(MAX_BYTES))

    def player(self):
        self.string(); self.string(); self.take(24); self.blob()
        self.string(); self.string(); self.take(1)
        for _ in range(self.count(1024)):
            self.string()
        self.take(2); self.string()


def decode_save(data):
    require(len(data) >= 14 and data[:6] == b"EVL2\x01\x01", "Unsupported envelope")
    size, crc = struct.unpack_from("<II", data, 6)
    payload = data[14:]
    require(size == len(payload) and zlib.crc32(payload) == crc, "Envelope length/CRC mismatch")
    with gzip.GzipFile(fileobj=io.BytesIO(payload)) as stream:
        raw = stream.read(MAX_BYTES + 1)
    require(len(raw) <= MAX_BYTES, "Inflated save too large")
    reader = Reader(raw)
    require(reader.take(4) == b"EVIL", "Missing inner header")
    reader.take(4)  # Preserve the native inner version exactly.
    entries = []
    for _ in range(reader.count(10000)):
        start = reader.pos
        key = reader.take(reader.number("H")).decode("utf-8")
        value = reader.blob()
        entries.append((key, value, raw[start:reader.pos]))
    require(reader.pos == len(raw), "Trailing inner data")
    require(len({key for key, _, _ in entries}) == len(entries), "Duplicate entry keys")
    require(sum(key == "game.manifest" for key, _, _ in entries) == 1, "Missing manifest")
    return raw[:12], entries


def parse_manifest(data):
    reader = Reader(data)
    require(reader.number() == 16, "Unsupported game manifest")
    reader.take(28)
    has_host = reader.number("B")
    require(has_host in (0, 1), "Invalid host flag")
    if has_host:
        reader.player()
    count_offset = reader.pos
    records = []
    for _ in range(reader.count()):
        start = reader.pos
        guid, asset = reader.string(), reader.string()
        reader.take(4)
        position = struct.unpack("<fff", reader.take(12))
        reader.take(16)
        parent = reader.string()
        reader.take(2)
        contributors = {}
        for _ in range(reader.count(512)):
            key, value = reader.string(), reader.blob()
            require(key not in contributors, "Duplicate contributor")
            contributors[key] = value
        records.append(dict(guid=guid, asset=asset, position=position, parent=parent,
                            contributors=contributors, raw=data[start:reader.pos]))
    tail_offset = reader.pos
    for _ in range(reader.count()):
        reader.string()
    for _ in range(reader.count(10000)):
        reader.player()
    reader.take(reader.count() * 4)
    reader.take(8)
    require(reader.pos == len(data), "Trailing manifest data")
    require(len({item["guid"] for item in records}) == len(records), "Duplicate object IDs")
    return count_offset, tail_offset, records


def repair(current, reference, claims):
    header, entries = decode_save(current)
    _, reference_entries = decode_save(reference)
    manifest = next(value for key, value, _ in entries if key == "game.manifest")
    old_manifest = next(value for key, value, _ in reference_entries if key == "game.manifest")
    count_offset, tail_offset, records = parse_manifest(manifest)
    _, _, old_records = parse_manifest(old_manifest)
    old_buses = {r["guid"] for r in old_records if r["asset"] == BUS}
    buses = {r["guid"] for r in records if r["asset"] == BUS}
    require(len(old_buses) == 2 and len(buses) == 57 and old_buses <= buses, "Unexpected bus baseline")
    targets = buses - old_buses
    require(len(targets) == 55, "Expected exactly 55 additional buses")
    require(not targets.intersection(c["VehicleId"] for c in claims["Claims"]), "Target is claimed")
    kept = [r for r in records if r["guid"] not in targets]
    removed = [r for r in records if r["guid"] in targets]
    for record in removed:
        x, y, z = record["position"]
        require(all(math.isfinite(v) for v in (x, y, z)) and
                150 <= x <= 172 and 65 <= y <= 82 and 532 <= z <= 547, "Target outside audited pile")
        vehicle = Reader(record["contributors"]["vehicle"])
        require(vehicle.number("B") == 1 and vehicle.number() == 0, "Target has vehicle slots")
        require(not record["parent"], "Target is attached")
    preserved = (manifest[:count_offset], manifest[tail_offset:], *(r["raw"] for r in kept),
                 *(value for key, value, _ in entries if key != "game.manifest"))
    for guid in targets:
        parsed = uuid.UUID(hex=guid)
        text_needles = (guid.encode(), guid.encode("utf-16le"))
        binary_needles = (parsed.bytes, parsed.bytes_le)
        require(not any(needle in section.lower() for section in preserved for needle in text_needles) and
                not any(needle in section for section in preserved for needle in binary_needles),
                "Target still referenced in preserved data")
    new_manifest = (manifest[:count_offset] + struct.pack("<i", len(kept)) +
                    b"".join(r["raw"] for r in kept) + manifest[tail_offset:])
    _, _, parsed_kept = parse_manifest(new_manifest)
    require([r["raw"] for r in parsed_kept] == [r["raw"] for r in kept], "Surviving record changed")
    raw = bytearray(header)
    for key, value, original in entries:
        if key != "game.manifest":
            raw.extend(original)
        else:
            name = key.encode()
            raw.extend(struct.pack("<H", len(name)) + name + struct.pack("<i", len(new_manifest)) + new_manifest)
    payload = gzip.compress(bytes(raw), mtime=0)
    output = b"EVL2\x01\x01" + struct.pack("<II", len(payload), zlib.crc32(payload)) + payload
    _, checked = decode_save(output)
    require([(k, v) for k, v, _ in checked if k != "game.manifest"] ==
            [(k, v) for k, v, _ in entries if k != "game.manifest"], "Other save entry changed")
    return output, {"removedIds": sorted(targets), "objectsBefore": len(records),
                    "objectsAfter": len(kept), "remainingBusIds": sorted(old_buses),
                    "remainingRecordsByteIdentical": True, "otherEntriesByteIdentical": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("save", "meta", "claims", "reference-zip", "expected-sha256", "output"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    source = Path(args.save).read_bytes()
    require(hashlib.sha256(source).hexdigest() == args.expected_sha256.lower(), "Input hash changed")
    meta = json.loads(Path(args.meta).read_text(encoding="utf-8-sig"))
    claims = json.loads(Path(args.claims).read_text(encoding="utf-8-sig"))
    require(meta["contentHash"] == args.expected_sha256.lower() and meta["contentSize"] == len(source), "Metadata mismatch")
    with zipfile.ZipFile(args.reference_zip) as archive:
        require(archive.namelist().count("save.dat") == 1, "Ambiguous reference ZIP")
        reference = archive.read("save.dat")
    repaired, report = repair(source, reference, claims)
    report.update(beforeSha256=hashlib.sha256(source).hexdigest(), afterSha256=hashlib.sha256(repaired).hexdigest(),
                  claimsSha256=hashlib.sha256(Path(args.claims).read_bytes()).hexdigest())
    meta["contentHash"], meta["contentSize"] = report["afterSha256"], len(repaired)
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=False)
    (output / "save.dat").write_bytes(repaired)
    (output / "meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
    (output / "repair-report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key not in ("removedIds", "remainingBusIds")}))


if __name__ == "__main__":
    main()
