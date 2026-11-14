# Isolated voice room probe

Diagnostic observer only. Never install in a public or live runtime.

Build with `dotnet build tests/VoiceRoomProbe/VoiceRoomProbe.csproj -c Release`.
Override `GameDirectory` and `BepInExDirectory` with MSBuild properties when needed.
The project defaults to the installed pinned game and retained isolated loader.

Before launching an isolated supervisor, install the compiled probe and candidate
hosting plugin in that stopped scratch runtime. Set `ND_VOICE_TEST_ROOT` to a
new directory below `E:/Odin/scratch/nomad-drive/`. Configure `dataDirectory` as
exactly `<test root>/data`, `lobby.public: false`, and `lobby.maxPlayers: 15` (voice enabled) or `63` (capacity-preserving guard).
The supervisor supplies its own `NOMADDRIVE_SERVER_CONFIG` startup snapshot.
The probe refuses a public configuration, different capacity, live runtime path,
missing runtime marker, or linked ancestors. Use disposable world data.

Run normally and inspect `<test root>/voice-room.json`. Allow 180 seconds after
plugin initialization. Stop the isolated supervisor using its normal saved stop.
Remove the diagnostic plugin before any release packaging.

`roomCreationPassed` is true only when the real native creation call received
the configured human count plus one host identity and `EnableRTCRoom: true`, EOS returns a lobby with that expected member limit,
and native `GetRTCRoomName` returns success with a nonempty room name. The probe
also queries `IsRTCRoomConnected` for the host, without forcing a join or changing
device, mute, or room options. It completes early if room creation passes and
the host's connection query reports connected; otherwise it records a timeout
after observing the full interval. Missing or false fields are failures or
incomplete evidence, never a pass. Inspect both `roomCreationPassed` and
`hostRTCConnected`; a creation pass alone does not prove a connected host.

The game SDK's stripped `LobbyDetailsInfo.RTCRoomEnabled` property has no getter.
The probe uses the native room lookup result instead of reading private fields.
Room names, lobby IDs and account identifiers are not written into the evidence.

`mediaVerified` always remains false. A successful room allocation or connection
does not establish microphone capture, participant audio delivery, spatialization,
range, or a 63-human voice capacity. Final acceptance requires two real vanilla
clients with working microphones in the private candidate lobby: verify mutual
nearby hearing, intended distance falloff, mute/unmute, and reconnect. Record
that separately from the room probe.

Backend evidence on 2026-11-14: enabling RTC for 64 identities returned
`LobbyInvalidSession`: conference rooms require <=16 max players. The source now
retains a capacity-preserving voice guard with an explicit warning. Use private
15-human configuration to verify successful room creation; a guarded 63-human
configuration intentionally cannot pass the room-creation assertion.
