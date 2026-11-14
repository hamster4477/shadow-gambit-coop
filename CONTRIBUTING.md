# Contributing

**English** | [简体中文](CONTRIBUTING.zh-CN.md)

- Identify the variant and build in every report and change. Two-player and multiplayer are separate source trees for now.
- Keep game assets, generated bindings, account data, logs and binaries out of commits. Do not add decompiled game implementations.
- Preserve license and attribution information for externally sourced code. Do not assume a public download is permission to redistribute it.
- Run the selected tests and compile against your local game references when native code changes. Describe what was actually tested and what remains unverified.
- Prefer capturing the real native interaction sequence in a controlled test over inventing game API calls. Avoid unsafe changes to IL2CPP value-type hook signatures.
- Test claims/ownership, cancellation and disconnect handling whenever a new multiplayer path is added. A connected socket alone does not prove game synchronization.
- Keep user-facing text available in both Chinese and English, and keep the split language documents (`README.md` / `README.zh-CN.md` and the matching pairs) in sync when behaviour changes.

Submitting code means you have the right to provide your contribution under this project's MIT license. Third-party code must keep its original license and attribution; this section neither requires nor grants any extra rights in the game or third-party libraries.
