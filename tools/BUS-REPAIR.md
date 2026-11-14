# Audited bus-pile repair

`repair_bus_pile.py` prepares a new output directory and never edits its inputs.
It supports only EVL2 v1 gzip with a version-16 game manifest. Do not run it
against a newer format or use it as a general vehicle deletion tool.

The authorized incident has 57 saved school buses. A trusted earlier backup has
two of those IDs. The exact 55-ID difference is the removal set. The tool refuses
claimed targets, targets outside the audited pile, targets with attachment slots
or parents, and references in surviving objects, player records, blob references
or other save entries. All surviving object records and other entries retain
their original bytes. Only the manifest count/removed records, its entry length,
gzip envelope CRC/length and metadata content hash/size change.

Supply private local paths explicitly:

```text
python tools/repair_bus_pile.py --save <save.dat> --meta <meta.json> --claims <claims.json> --reference-zip <earlier-backup.zip> --expected-sha256 <audited-input-hash> --output <new-output-directory>
python tests/test_bus_repair.py --save <save.dat> --claims <claims.json> --reference-zip <earlier-backup.zip>
```

Stop hosting first. Back up the entire world and claims before applying anything.
Verify the prepared copy in an isolated native host, then stop that host. Recheck
the live input hash, claims hash, absence of a newer autosave, and stopped-process
state before installing the prepared save and metadata. Preserve a recovery copy
and verify every other live world file stayed unchanged. Do not publish private
reports, saves, runtime configurations or player identifiers.

Native evidence: retained `evilsave.cs:6238,6333,5630,5023` and
`vehicle-claims-audit/game.cs:89228,89392`. Generation and save timestamps stay
unchanged; the local loader does not require changing them for this repair.
