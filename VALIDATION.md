# Export validation / 导出验证

Checked through 2026-11-14 against the local reference installation. These results describe this source export, not a stable-release certification.

## 2026-11-14 dependency installer / 依赖安装器

- `Check-Environment.ps1` and `Install-Dependencies.ps1` are new. The installer downloads BepInEx `6.0.0-be.682` (IL2CPP win-x64) and CPython `3.13.9` embeddable, verifies pinned SHA-256 values, refuses archives containing absolute or `..` paths, extracts to a staging folder, backs up what it replaces, and rolls back on failure. Neither script writes saves or starts the game.
- Pinned hashes were read from the published artifacts, not copied from a previous package: BepInEx `087B337D…6572C` (34,073,982 bytes) and CPython `91D828C2…402A` (10,926,675 bytes). The long-standing local `loader.zip` hashes to the **same** BepInEx value, which confirms the older full package used this exact official build.
- Run in an isolated fake game folder: detection reported every missing item and exited 1; `-DryRun` changed nothing; installation from a local cache created `winhttp.dll`, `doorstop_config.ini`, `BepInEx/core`, `dotnet/` and a working embedded Python 3.13.9; the extracted `winhttp.dll` is byte-identical to the copy inside the official archive. A second run correctly reported "nothing to install" and exited 0.
- A deliberately tampered Python archive was rejected with the expected and actual digests, the file was deleted, and no Python folder was created. Rollback left no partial files behind.
- The real network branch was exercised once end to end (no cache): both downloads completed and the same files were installed in about 100 seconds.
- Checking the real game installation returns ready: loader present, plugin present, 157 interop assemblies, 66 Unity libraries, bundled Python.
- **Not tested:** a machine that has never had BepInEx, an actual game launch after installing, and BepInEx's own Unity-libraries download on first launch. Those need a clean machine and a real game start.

新增的两个脚本已在隔离目录和真实游戏目录验证：检测、离线安装、真实下载、幂等重跑、篡改拒绝与回退均符合预期；同时确认旧 `loader.zip` 与官方 BepInEx 包同哈希。尚未在从未装过 BepInEx 的机器上真正启动游戏。

## 2026-11-14 version 0.8.42 / 标点功能与导出同步

- The ping feature was ported from the finalized two-player working source without editing production code: `CoopPings.cs`, `PingWire.cs`, `CoopPingVisual.cs`, `ping_broker.py`, the three ping test scripts and `test_ping.py`. `MoveProbe.cs` and `lobby_broker.py` were replaced with their 0.8.42 versions; in `lobby_broker.py` the only difference from 0.8.41 is the `ping` operation routing and its import.
- **A build from this exported layout reproduces the shipped plugin exactly**: 958,976 bytes, SHA-256 `73D790F1776B999B52BA35327D7925F997958D4571F908E6B77A48EE7AD1AF75`, identical to the DLL installed in the game and to the payload inside the 0.8.42 update package. The 89 native hook/signature checks and GUI launcher compilation passed.
- Ping checks run from the exported layout: protocol and lifetime **60**, production input suppression **27**, broker **8**, and decoding of the bundled `src/two-player/media/bell_b.wav` (48 kHz mono PCM16, 21,813 frames, non-silent). The decode check accepts any PCM16 WAV placed at that path, so a replaced sound is validated instead of the original one.
- `runtime-files.json` now lists `ping_broker.py` and `media/bell_b.wav`, and `package_mod.py` accepts `.wav`. The first entry fixed a real packaging defect: `lobby_broker.py` imports `ping_broker` at runtime, so a package built from the previous list would have shipped a missing module. The rebuilt mod-only ZIP (45 entries) was inspected and contains both files.
- `test_ping.py` now creates its own `test-results/` directory. That directory is git-ignored, so a fresh clone previously failed in `setUp` before any assertion ran; the same fix was applied to the working-tree copy.
- `source-inventory.json` was regenerated from the tracked file list (**395 entries**) so it covers the ping sources; the previous file predated them.
- **No two-PC result exists.** Marker rendering, host/guest colours, the sound cue and input suppression in the real game are unverified. The 60/27/8 figures are simulated checks against test doubles.

本轮把 0.8.42 标点功能完整并入导出，并从导出目录独立构建出与已发布 DLL 逐字节相同的插件；60／27／8 项均为替身检查，双机画面、声音和输入抑制仍无实测结论。

## 2026-11-14 version 0.8.41 / 传输、查找与视野修订

- The six changed production files were copied from the finalized two-player working source: `MoveProbe.cs`, `LanStateFeed.cs`, `SnapshotTransport.cs`, `GuardPursuit.cs`, `SceneLookup.cs` and `MissionEntrySync.cs`. Export-specific build-directory adaptations, privacy filtering and synthetic live-import fixtures were retained. The multiplayer branch was not changed.
- Independent build from this exported layout passed **89 native hook/signature checks** and GUI launcher compilation. The compiled plugin reports **0.8.41**. Build outputs are excluded from the source archive.
- Tests run from the exported source: snapshot assembly **48**, production UDP parsing **12**, scene-lookup lifecycle **24**, pursuit/observer presentation **659** (including per-frame paging budget assertions): **743 checks passed**. Socket/game objects are simulated; no game process, save or production room was changed by these checks.
- In the retained development allocation benchmark, complete UDP snapshots of 1,024 / 8,192 / 32,768 bytes allocated 8,808 / 84,520 / 337,688 bytes before and 6,184 / 63,752 / 254,712 bytes after the change: **29.79% / 24.57% / 24.57% lower allocation**. CPU timings were noisy and did not establish a CPU-speed improvement. There is no measured in-game frame-rate or bandwidth claim. The 20 Hz request interval, 48-packet processing budget and validation rules remain unchanged.
- In 30 simulated seconds at 60 Hz, ordinary-task scene-loader lookup requests fell from **3,600 to 0**; actual native-search callbacks under the existing negative cache fell from **62 to 0**. Cached active-object validation uses 100 rather than 200 `gameObject` reads per 100 validations. Cache expiry and inactive-object semantics were preserved.
- Observer policy tests cover late UI initialization, handoff between detecting enemies, scene/loading/cutscene cleanup, ownership transitions, and stale facing data after a possession changes the AI object. Within the unchanged four-row/2 KB budget, a current detector now receives a priority slot chosen by its last successful send, while ordinary rotation is retained. At 20 snapshots/s with 64 ordinary guards, one active detector updates every **0.050 s**, or two detectors every **0.100 s**. These results assume the normal packet budget and no packet loss.
- A dense-scene limit remains: ordinary guards reach **0.800 s** between updates with no active detectors and **1.100 s** in the detector-priority fixture, longer than the **0.55 s** view lease. High density, reduced packet space and network loss can still produce stepped or stale views. The tests do not establish rendered appearance or real animation timing.

本轮改动已在导出目录独立构建并执行上述检查；没有重新跑此前全部联机测试，也没有进行双机游戏或实际帧率测试。仍需实测发现视野、敌人朝向、连续换场和网络抖动下的表现。仅提供 Windows 源码和构建入口，没有经过验证的 Mac 支持包。

## 2026-11-14 two-player maintenance audit / 双人收尾审查

- Exported two-player source is now **0.8.40**. Host/guest role isolation, F7 host resume, tutorial/scene rewind handling, first-account live import, asynchronous load failure cleanup and retryable result publication have been merged. Build-directory adaptations and the exported privacy filter were retained.
- A new explicit launch retires old guest `join-room` / `return-menu` requests. Steam's replacement game process still preserves the same launch's pending handoff. Local verification requests remain available for maintainers.
- The public SteamID64 base constant was restored in both source variants. It had been incorrectly replaced during privacy cleanup; actual personal account IDs in test fixtures remain synthetic.
- Independent exported build succeeded; **89 native hook/signature checks** and GUI launcher compilation passed. The resulting mod-only package reports **0.8.40**, with ZIP CRC and every payload SHA-256 verified. Version metadata is read from the selected source, rather than hardcoded.
- Targeted production-policy checks: launch roles **37**, pending launch request isolation **23**, process lifecycle **14**, scene follow **65**, live import **53**, enemy rewind context **15**, bootstrap recovery **76**, and account arithmetic **6**: **289 total**. Native loader callbacks are simulated. Live import now generates synthetic ZIP contents; no real save is needed.
- Public diagnostic redaction: **109 checks each** under Windows PowerShell 5.1 and PowerShell 7.
- Exported real gateway lifetime: **3 tests passed**, including normal/abnormal parent exit under a forced cp1252 output environment. The initial sandbox attempt was blocked by Windows socket error 10013; the authorized loopback rerun passed.
- Full VBS launcher-chain verification was **not completed** in this environment: CScript could not load its settings because access was denied. The direct PowerShell-to-fixture launcher stage passed in the working tree. No actual Steam/game session was launched in this audit.

This is not a fresh two-PC gameplay pass. Host menu access after switching from guest, tutorial rewind/late join, continuous scene changes and the skill regression matrix still require real games. Multiplayer remains the separate MP1 baseline; only the Steam account constant fix was applied to that export during this audit.

本轮不改游戏安装或存档。双人最新修订已进入公开源码；不能把 289 项模拟策略检查和 89 项接口签名检查表述为双机玩法全部通过。多人自动测试的历史结果如下，未在本轮重做整套多人测试。

## 2026-11-13 baseline / 原导出验证

- Both source variants compiled independently from the exported layout. Each passed the 89 native hook/signature checks; both GUI launchers compiled.
- Multiplayer source checks passed: ownership 522, spectator 21, page/mapping 1,387, checkpoint barrier 13, planning retirement 6, lobby 72.
- Python lobby/broker tests: 27 passed. Python real loopback transport: 4 passed, exercising up to 32 simulated clients and 96 TCP channels.
- C# production loopback transport/context checks: 154 passed. Command-payload validation was stubbed for the tested STEP path.
- Public log redaction: 109 checks passed under both Windows PowerShell 5.1 and PowerShell 7, including inspection of a generated diagnostic ZIP.
- Both mod-only packages passed ZIP CRC and payload SHA-256 checks. They exclude third-party runtime and game binaries.

The exported log collector improves privacy filtering; this change is included in the rebuilt mod-only packages. The existing game installation was not changed by this release-preparation task.

两条源码都已独立编译，所列自动检查通过。模拟客户端和原生接口签名检查不能代替双机或多机游戏验证。多人版尚未完成三台以上电脑实测；复杂技能、剧情和连续换场景等仍需实际游玩核对。此历史批次未包含旧追击夹具；0.8.41 已修订夹具并执行相关测试，见上文。

The source ZIP contains no saves, live room configuration, game executable/assets, generated game bindings, Unity base libraries or bundled runtime. `source-inventory.json` records the SHA-256 of its exported files, excluding the inventory itself. Generated build output, test staging/runtime and caches are excluded from the inventory and ZIP; any local `artifacts/` output is not uploadable source.
