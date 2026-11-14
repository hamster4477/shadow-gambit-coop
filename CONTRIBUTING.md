# Contributing / 参与开发

- Identify the variant and build in every report and change. Two-player and multiplayer are separate source trees for now.
- Keep game assets, generated bindings, account data, logs and binaries out of commits. Do not add decompiled game implementations.
- Preserve license and attribution information for externally sourced code. Do not assume a public download is permission to redistribute it.
- Run the selected tests and compile against your local game references when native code changes. Describe what was actually tested and what remains unverified.
- Prefer capturing the real native interaction sequence in a controlled test over inventing game API calls. Avoid unsafe changes to IL2CPP value-type hook signatures.
- Test claims/ownership, cancellation and disconnect handling whenever a new multiplayer path is added. A connected socket alone does not prove game synchronization.

提交代码即表示你有权按本项目 MIT 许可证提供自己的贡献。第三方代码需要保留原来的许可和署名；本条不要求或授予对游戏和第三方库的额外权利。
