# Isolated runtime health probe

Diagnostic plugin only. Never install on a live/public runtime or use a live data directory.

Build with `dotnet build tests/RuntimeHealthProbe -c Release`. References use the pinned installed game and server BepInEx core. Install the resulting `NomadDrive.RuntimeHealthProbe.dll` into an isolated prepared runtime's plugins directory. Set `ND_RUNTIME_HEALTH_TEST_ROOT` to a new directory under `E:/Odin/scratch/nomad-drive/` and configure its dedicated data directory as `<test root>/data`. Import a copy of the world first. Disable scheduled backups/restarts during this test.

After dedicated hosting reaches ready, the probe records body/collider counts, network object count, frame settings, and crowded cells. It measures a baseline, makes the initially dynamic bodies temporarily kinematic, measures again, then restores their velocities and dynamic state in a finally block and measures once more. It writes `runtime-health.json` into the isolated test directory. Stop the isolated host and restore the test runtime after collecting results.

This changes physics in the disposable world copy. It is not a production optimization or a vehicle cleanup tool. Scripts that change body state during the measurement can affect the comparison. Frame and CPU samples include all process work and are diagnostic observations, not per-method profiling.
