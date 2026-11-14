# Contributing

**English** | [简体中文](CONTRIBUTING.zh-CN.md)

For **what this project is and what it does, read [README](README.md)** and [CHANGELOG](CHANGELOG.md). This file only covers how to help.

This is a one-person hobby project. There is no maintainer team, no roadmap and no review process for outside code. Nothing here is waiting on a pull request.

## What actually helps

- **Test reports from real games.** The biggest gap is two-PC play. A report needs the collected logs from **both** PCs out of the **same** session, the build you ran, the role that broke, and how you connected.
- **The real native call sequence** when something misbehaves. That is what turns a bug into something fixable instead of something guessed at.
- **Checked, redacted logs** plus the map, skill and scene where it went wrong.

## What is not needed

- Large refactors, dependency bumps, formatting-only or comment-only patches.
- Changes to IL2CPP hook signatures or native call sequences without evidence from an actual run.
- Re-uploaded game assets, generated interop assemblies, saves, logs or module binaries.

Use the [issue templates](.github/ISSUE_TEMPLATE) for reports. For anything sensitive — full room links, account details, crash dumps, whole log bundles — use the address in [SECURITY.md](SECURITY.md) instead of a public thread.

## If you do send code

Say which variant and build it targets; `src/two-player` and `src/multiplayer` are separate trees. Keep the license and attribution of any third-party code. Run the selected tests and compile against your local game references, then describe what was actually tested and what remains unverified.

Submitting code means you have the right to provide it under this project's MIT license. Third-party code keeps its own license and attribution; that grants no extra rights in the game or in third-party libraries.

## Want to help with the code

It is possible, but be clear about what it is: bespoke IL2CPP hook work against one specific game build, with no public API and no documentation of the game's internals. Hook signatures are checked at build time; a wrong one crashes the game instead of failing politely.

Start here:

1. Read [BUILDING.md](BUILDING.md). You need Windows, the game itself, PowerShell 7, BepInEx core and locally generated `BepInEx/interop` assemblies.
2. Build with `pwsh -NoProfile -File .\scripts\Build.ps1 -Variant two-player -GameDirectory "<game folder>"` and run `pwsh -NoProfile -File .\scripts\Test.ps1`.
3. Watch out: `src/two-player/build-probe.ps1` lists every source file by name. A new `.cs` file is silently ignored until it is added to that list, and the build still succeeds.

Three places where the work is genuinely stuck, roughly in order of value:

| Task | Why it matters |
| --- | --- |
| A compact facing/view channel for dense scenes | Only four guard rows fit per snapshot inside a 2048-character budget, while a view lease lasts 0.55 s. With around 64 nearby guards, one rotation takes longer than the lease, so distant guards visibly jump. A fix has to avoid starving ordinary guards, keep protocol compatibility and recover after packet loss. |
| Porting 0.8.39–0.8.42 into `src/multiplayer` | That tree is still the MP1 baseline: none of the host-role, rewind, performance or ping work is in it. |
| Turning the skill and scene matrix into fixtures | [VALIDATION.md](VALIDATION.md) records what is checked; the playable-but-untested combinations are the gap, and each one that becomes a fixture reduces future breakage. |

Open an issue describing what you intend to change and why **before** writing a large patch. Small, testable changes are far easier to accept than rewrites, and a change that cannot be tested here is hard to justify.

## Where the other answers are

| Question | File |
| --- | --- |
| What is this? | [README](README.md) |
| What changed between builds? | [CHANGELOG](CHANGELOG.md) |
| How do I build it? | [BUILDING](BUILDING.md) |
| What has actually been checked? | [VALIDATION](VALIDATION.md) |
| Which dependencies, under which licenses? | [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md) |
