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

## Where the other answers are

| Question | File |
| --- | --- |
| What is this? | [README](README.md) |
| What changed between builds? | [CHANGELOG](CHANGELOG.md) |
| How do I build it? | [BUILDING](BUILDING.md) |
| What has actually been checked? | [VALIDATION](VALIDATION.md) |
| Which dependencies, under which licenses? | [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md) |
