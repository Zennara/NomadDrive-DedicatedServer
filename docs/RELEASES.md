# Versioning and releases

The server uses `MAJOR.MINOR.PATCH.BUILD`, independently of Nomad Drive's version.
Preview releases use major `0` and GitHub's prerelease flag. The first stable
release will use `1.0.0.BUILD`, retaining the next build counter, after Keagan
approves it. Four-part versions are the
project's convention, not strict three-part Semantic Versioning.

Features increase minor and reset patch. Fixes increase patch. Every release
increases build, including maintenance-only releases. Build never resets, even
when the major version changes from preview to stable.
Changes that break configuration, commands, or plugin interfaces must be called
out in release notes, including during preview development.

A game update does not automatically change the server's major version. Review
the new game code and dependencies, update the exact compatibility pins, and test
the adapter before claiming support. A compatibility repair can be a patch when
server interfaces remain compatible. Explicitly state when an old game build is
no longer supported. Keep older releases available for their recorded game builds.

## Release checklist

1. Update `VERSION`, `compatibility.json`, and the plugin's three-part BepInEx
   version attribute. Assembly/file versions come from `VERSION` automatically.
2. Verify the manifest against the tested Steam app/build, displayed game version,
   Unity version, assembly hash, and setup/loader pins. Update `docs/VALIDATION.md`
   with passed checks and remaining limitations; a successful build is not a
   gameplay acceptance test.
3. Run the supervisor tests and `scripts/build.ps1` against a stopped, prepared
   runtime. Verify both builds and the package's assembly/file versions.
4. Inspect tracked history and package entries before first public publication.
   Include only this project's source, built CLI/plugin, compatibility manifest,
   and checksums. Do not bundle the game, BepInEx, saves, or raw logs.
5. Commit the release version as `chore(release): vX.X.X.X`. Package that exact
   commit; compare packaged DLL hashes against the verified build outputs.
6. With user authorization, create the annotated `vX.X.X.X` tag, push the commit
   and tag with `AGENT_PUSH_OK=1`, and create the GitHub release with its ZIP,
   `compatibility.json`, and `SHA256SUMS.txt`. Mark major-zero releases prerelease.
7. Release notes must state the game version, Steam app/build IDs, Unity and
   BepInEx versions, exact assembly hash, changes, verification, and untested areas.
   Verify the remote tag, prerelease state, and uploaded asset hashes afterward.

The first published preview is `0.1.0.2`. Earlier local prototype commits used
`1.0.0.0` and `1.0.1.1` before the preview-major policy was settled. They were never
published or tagged. Their original version files remain unchanged in history;
they are not stable releases and will not receive misleading replacement tags.
