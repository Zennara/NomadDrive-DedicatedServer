# Isolated terrain admission checks

Never install this probe in a public runtime. Build with
`dotnet build tests/TerrainGuardProbe -c Release`, alongside the candidate host
plugin. Use a prepared private runtime and a new directory below
`E:/Odin/scratch/nomad-drive/`. Set `ND_TERRAIN_GUARD_TEST_ROOT` to that directory
and `NOMADDRIVE_SERVER_DATA` to its `data` child through the supervisor config.

The native probe waits for a ready, settled world. It holds a real background
task while checking that grid updates and refreshes defer but apply callbacks
still run. It then tests the active-only yielding apply case, where the native
queue is empty but a routine is unfinished. It checks that deferred work drains
after release. Holds and waits are bounded; cleanup releases both held tasks.
Results are written to `terrain-guard.json` inside the test root.

Set `ND_TERRAIN_GUARD_STRESS=1` for an additional 60-second sample with two
temporary observers 8 and 16 tiles from the initial observer. It records frames,
maximum frame interval, and terrain exceptions while three regions stream.
Observers are removed afterward. This is synthetic region loading, not remote
client joining, and does not prove that every public-server stall is fixed.
