# Building and testing / 构建与测试

## Prerequisites

- Windows x64 and PowerShell 7 (`pwsh`). The existing compiler script uses Roslyn shipped with PowerShell 7.
- A legitimate local installation of Shadow Gambit: The Cursed Crew.
- Matching BepInEx IL2CPP dependencies installed locally and a generated `BepInEx/interop` directory. The tested baseline was BepInEx `6.0.0-be.682` with Unity `2021.3.25f1`; compatibility with newer framework versions is not established.
- `dotnet`, `BepInEx/core` and `BepInEx/interop` in the supplied game directory. They are read as references, not copied into the repository or output.
- The Windows .NET Framework C# compiler for the GUI launcher.
- Python 3.13 for the gateway and Python tests. This source repository does not bundle Python.

已有旧版框架并不代表它仍获得安全维护。升级运行时需要单独做兼容性验证；本次源码整理没有修改游戏运行时版本。

## Build commands

Run from the repository root:

```powershell
pwsh -NoProfile -File .\scripts\Build.ps1 -Variant two-player -GameDirectory "X:\Games\Shadow Gambit The Cursed Crew"
```

Use `-Variant multiplayer` for MP1. Files are materialized under `artifacts/build/<variant>`, and the DLL is staged there. The launcher is built in the same isolated output. The native hook audit checks the compiled plugin against local interop metadata. The script never launches the game or changes its installed plugin.

源码目录本身保持干净。请勿将游戏的 DLL、数据、生成的 `interop` 或解包内容提交到 GitHub。

## Tests

```powershell
pwsh -NoProfile -File .\scripts\Test.ps1 -Python python
```

The selected suite covers ownership, observer selection, per-player command tickets, checkpoint barriers, lobby coordination, page delivery, and real local HTTP/TCP/UDP transport with a fake game service. Native gameplay APIs are replaced by test doubles where stated.

As of 2026-11-14 the selected suite also runs the two-player launch-role, pending-request isolation, process lifecycle, scene following, live-import, enemy-rewind and bootstrap regression checks. Run only this group with:

```powershell
pwsh -NoProfile -File .\scripts\Test-TwoPlayer.ps1
```

The live-import tests generate synthetic ZIP data at runtime; no real save files are required or included. Steam account conversion checks use synthetic account IDs and preserve the public SteamID64 base constant. Launcher VBS end-to-end testing additionally requires Windows Script Host; the current sandbox denies CScript settings access, so direct PowerShell fixture results do not prove that entire launcher chain.

双人新增检查会由总测试脚本一并执行；接档夹具是合成字节，不是玩家存档。脚本构建和原版接口签名检查不等同于两台游戏实测。

`send_move.py` and the gateway's legacy browser controller belong to the old 0.2 diagnostic workflow. They are not the normal player entry point. Use the Steam launch dialog; for the current two-player line F7 resumes hosting, F6 reconnects a guest, F11 disconnects, and F8 remains the original game's quickload key.

The two-player test wrapper also runs snapshot assembly, production UDP parsing, scene-lookup lifecycle and pursuit/observer presentation checks. The pursuit fixture now compiles against current test doubles. These checks do not launch Unity or establish correct in-game rendering. The default UDP parsing test is self-contained; its optional `-Benchmark` mode needs the separately retained development baseline and is not part of this source export.

原生游戏验证仍需匹配版本的多台电脑，尤其是复杂技能、剧情、中途加入、角色复活、教程暂离和连续换场景。

## Creating a mod-only package

```powershell
pwsh -NoProfile -File .\scripts\Package-Mod.ps1 -Variant two-player -GameDirectory "X:\Games\Shadow Gambit The Cursed Crew"
```

The package contains only the project's compiled plugin, its GUI launcher, gateway/launch scripts, and project notices. It excludes BepInEx, Doorstop, .NET, Python, Unity libraries, generated interop assemblies, and game assets. Those prerequisites must already exist on the target machine.

这个包是已有依赖环境的模组更新包，不是完整一键安装器。公开发布前仍应阅读发行审查说明，确认所发布源码对应二进制，并在 Release 中明确测试状态。
