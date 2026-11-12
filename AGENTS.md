# Release rules

These rules apply to this repository and override the global initial-major rule.

- Use `MAJOR.MINOR.PATCH.BUILD` in `VERSION`. Previews use major `0`;
  reserve major `1` for the first stable release, approved by Keagan.
- Increase minor for features and reset patch; increase patch for fixes.
  Increase build for every release, including documentation or maintenance.
  Build never resets, including when moving from preview to stable. Do not bump
  again for retries of one release.
- Keep the server version separate from the supported game version.
- Every release must include `compatibility.json` with the server version,
  game version, Steam app/build IDs, Unity version, game assembly SHA-256,
  BepInEx version, verification date, and validation document reference.
- Check that the manifest agrees with `VERSION`, the plugin's assembly hash
  gate, setup script pins, and actual tested game files. The BepInEx plugin
  attribute uses the first three server version components; assembly/file
  versions use all four. Do not infer compatibility with newer game builds.
- Every published release gets an annotated `vMAJOR.MINOR.PATCH.BUILD` tag on
  its exact commit and a GitHub release. Mark all `0.x.x.x` releases as
  prereleases. Include game compatibility and unverified behavior in the notes.
- Never move published tags or replace a published version's assets.
  Unpublished prototype commits are history, not retrospectively renamed releases.
- Publish only project source, our built CLI/plugin, compatibility metadata,
  and checksums. Never publish game/loader binaries, saves, runtime configs,
  credentials, raw runtime logs, or decompiled game sources.
- See `docs/RELEASES.md` for the release checklist. Publishing requires user
  authorization; use `AGENT_PUSH_OK=1` for authorized push/release commands.
