# Isolated runtime health probe

Diagnostic plugin only. Never install on a live/public runtime or use a live data directory.

Build with `dotnet build tests/RuntimeHealthProbe -c Release`. References use the pinned installed game and server BepInEx core. Install the resulting `NomadDrive.RuntimeHealthProbe.dll` into an isolated prepared runtime's plugins directory. Set `ND_RUNTIME_HEALTH_TEST_ROOT` to a new directory under `E:/Odin/scratch/nomad-drive/` and configure its dedicated data directory as `<test root>/data`. Import a copy of the world first. Disable scheduled backups/restarts during this test.

After dedicated hosting reaches ready, the probe records body/collider counts, network object count, frame settings, and crowded cells. It measures a baseline, makes the initially dynamic bodies temporarily kinematic, measures again, then restores their velocities and dynamic state in a finally block and measures once more. It writes `runtime-health.json` into the isolated test directory. Stop the isolated host and restore the test runtime after collecting results.

Each measurement also records elapsed time and invocation counts around existing
`UnityEngine.PlayerLoop` systems using sibling timing callbacks. The callbacks
preserve the original systems and their order, and are removed when the probe is
destroyed. Parent and child timings overlap: do not add them together. Timings
that cross a measurement boundary are omitted. This works with the pinned game
build's stripped Unity assemblies, which do not expose `ProfilerRecorder.StartNew`
or `ProfilerRecorderHandle.GetAvailable`.

This changes physics in the disposable world copy. It is not a production optimization or a vehicle cleanup tool. Scripts that change body state during the measurement can affect the comparison. Frame and CPU samples include all process work and are diagnostic observations. Loop timings identify engine phases, not individual native functions or managed methods.

For terminal network recovery, also set `ND_VERIFY_NETWORK_RECOVERY=1`. Use a fresh private lobby with crash recovery enabled. After the supervisor reports ready and a manual backup succeeds, create `stop-network.once` in the test root. The probe consumes it and calls Mirror's native `StopHost()` once. Verify that the supervisor reports the verified checkpoint, changes the game PID and run directory, returns to ready on the same slot, and completes a graceful stop. Check the backup hash before and after recovery.

To exercise Windows sharing violations, open that disposable slot's `meta.json` with `FileShare.None` before writing the trigger. Wait for two checkpoint sharing-violation retries and verify the old PID is still running. Release the handle and check that the replacement reaches ready on the original checkpoint and completes a graceful stop.
