# Shadow Gambit Co-op

**English** | [简体中文](README.zh-CN.md)

Unofficial experimental co-op for **Shadow Gambit: The Cursed Crew**, using BepInEx IL2CPP. The host runs the authoritative game; guests send commands and receive state and checkpoint updates. Several scenarios are still untested in a real game session; do not describe this as a stable release.

## Versions

| Source | Baseline | Status |
| --- | --- | --- |
| `src/two-player` | `0.8.42 / 2026-11-14 visual pings` | Latest two-player fixes, gameplay retest pending |
| `src/multiplayer` | `0.9.0 / MP1-20261113-unlimited-trial` | Separate multiplayer experiment |

Keep the same variant and build on every participating PC. These directories are independent alternatives; do not mix their files.

The two-player export now includes host-role isolation, F7 host resume (F8 remains the native quickload key), pending guest-request retirement on a new launch, tutorial/scene-rewind recovery and checkpoint-failure cleanup. The multiplayer line is still the separate MP1 baseline, not a superset of these newer fixes.

Version 0.8.41 reduces temporary allocations in UDP snapshot parsing and unnecessary scene-loader lookups. It fixes observer-view handoff and stale facing updates across loading, cutscenes and possession. These are isolated test results, not a measured in-game frame-rate improvement.

Version 0.8.42 adds cosmetic, scene-scoped pings: one marker per player, no cooldown, about five seconds to live, host cyan / guest amber. Default bindings are `Alt+Left click`, `Ctrl+E+Left click` and the backquote key; right click is unchanged. Pings never issue movement, attack or view-cone commands, and clear across loads, scene changes and disconnects. The default bell ships with the sources at `src/two-player/media/bell_b.wav` and is installed to `ShadowGambitCoop/media/bell_b.wav`. It is **replaceable**: put any PCM16 WAV in its place, and if the file is missing the markers still work with a single logged warning. The audio file is distributed with the mod but is not part of the MIT-licensed source code.

The multiplayer variant has no fixed room cap, but practical capacity is limited by host performance and bandwidth. A mission still deploys at most eight crew members; spectators watch when no crew is idle. Single-crew scenes are still controlled by the host.

The multiplayer transport was exercised with simulated clients, including 32 clients and 96 TCP channels. **This is not validation of 32 simultaneous game instances. Three-or-more-PC gameplay remains unverified.**

## Install (players)

Playing does not need the build toolchain. Close the game, extract the release ZIP into the game folder, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\ShadowGambitCoop\Check-Environment.ps1
```

It reports what is present and what is missing. If BepInEx or Python is missing, install them once:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\ShadowGambitCoop\Install-Dependencies.ps1
```

That script downloads **only** BepInEx 6.0.0-be.682 (IL2CPP win-x64) from `builds.bepinex.dev` and CPython 3.13.9 embeddable from `python.org`, verifies pinned SHA-256 values before extracting, backs up anything it replaces, and rolls back on failure. It does not touch saves and does not start the game. This repository does **not** redistribute those binaries. Offline machines can pass `-OfflineCache <folder>` holding the two ZIPs.

After the first launch, BepInEx fetches the Unity base libraries itself, so that first launch needs network once.

## Build

Windows x64, PowerShell 7, a local game installation, and matching BepInEx/core, .NET runtime and locally generated BepInEx/interop references are required. Those binaries are **not included** in this repository. Obtain dependencies from their respective upstream projects and keep their notices.

```powershell
pwsh -NoProfile -File .\scripts\Build.ps1 -Variant two-player -GameDirectory "X:\Games\Shadow Gambit The Cursed Crew"
pwsh -NoProfile -File .\scripts\Build.ps1 -Variant multiplayer -GameDirectory "X:\Games\Shadow Gambit The Cursed Crew"
```

The build only reads the local game dependencies and writes to `artifacts/build/<variant>/`. It does not install into the game directory or overwrite the running plugin. The build-time checks confirm interface signatures; they do not replace in-game validation. See [BUILDING.md](BUILDING.md) for toolchain requirements and tests.

## Test

```powershell
pwsh -NoProfile -File .\scripts\Test.ps1
```

Tests use temporary fixtures and loopback networking; some environments require permission to open local sockets. Native test doubles do not prove in-game rendering, skill or scene correctness.

For a first multiplayer session use one host and two guests, then check exclusive crew selection, movement and skills, join/leave, save and load, returning to the ship and story waits in that order. Report issues through the [issue templates](.github/ISSUE_TEMPLATE) or by email to **hamster4477@qq.com**, and inspect the log collector's output for private data before sharing it.

## Network

- Native TCP and UDP: local port **38479**.
- HTTP gateway / HTTPS tunnel backend: local TCP **38477**. HTTPS requires a correctly configured TLS endpoint; this number alone does not enable TLS.
- LAN discovery: UDP **38478**. The Unity bridge uses loopback UDP **38476**.
- Host first, then guests. A single room endpoint can serve multiple guests; separate tunnels per guest are unnecessary.

Native TCP and UDP share the port number but are different protocols; TCP is not a drop-in substitute for UDP. When UDP is unavailable the state path falls back to TCP. Participants should run matching game versions and own the content the selected mission needs.

## License and release scope

Original project code is under the [MIT License](LICENSE). Preserve that license and copyright notice when redistributing it. The MIT grant does **not** cover Shadow Gambit, Unity, BepInEx, Il2CppInterop, .NET, Python, or other third-party components.

This repository contains no game executables, assets, generated binding caches, personal saves, connection address history or dependency binaries. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), [RELEASE_REVIEW.md](RELEASE_REVIEW.md) and [SECURITY.md](SECURITY.md). Earlier offline test installers are **not** cleared for public redistribution: missing third-party notices/source arrangements and Unity library redistribution need further work.

This project is not affiliated with or endorsed by Mimimi Games, Unity or Valve. The game's name identifies the supported game and does not grant any rights in its assets or trademarks.

## Publishing this source folder

If you re-publish this directory elsewhere, include the hidden `.github`, `.gitignore` and `.gitattributes` files. Do not upload your game directory, historical test ZIPs, crash dumps, save files or a generated `artifacts` directory. The MIT file is already present, so a new repository does not need a second license.
