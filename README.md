# Shadow Gambit Co-op / 影子诡局联机模组

Unofficial experimental co-op for **Shadow Gambit: The Cursed Crew**, using BepInEx IL2CPP. The host runs the authoritative game; guests send commands and receive state and checkpoint updates.

《影子诡局：被诅咒的海盗》非官方联机测试项目。房主执行游戏逻辑，客机发送操作并同步状态、进度和检查点。仍有实机测试未覆盖的场景，请勿将其描述为稳定正式版。

## Versions / 版本

| Source | Baseline | Status |
| --- | --- | --- |
| `src/two-player` | `0.8.42 / 2026-11-14 visual pings` | Latest two-player fixes, gameplay retest pending / 已合并最新双人修订，待双机复验 |
| `src/multiplayer` | `0.9.0 / MP1-20261113-unlimited-trial` | Separate multiplayer experiment / 独立多人试行版 |

Keep the same variant and build on every participating PC. These directories are independent alternatives; do not mix their files.

The two-player export now includes host-role isolation, F7 host resume (F8 remains the native quickload key), pending guest-request retirement on a new launch, tutorial/scene-rewind recovery and checkpoint-failure cleanup. The multiplayer line is still the separate MP1 baseline, not a superset of these newer fixes.

Version 0.8.41 also reduces temporary allocations in UDP snapshot parsing and unnecessary scene-loader lookups. It fixes observer-view handoff and stale facing updates across loading, cutscenes and possession. These are isolated test results, not a measured in-game frame-rate improvement.

Version 0.8.42 adds cosmetic, scene-scoped pings: one marker per player, no cooldown, about five seconds, host cyan / guest amber. Default bindings are `Alt+Left click`, `Ctrl+E+Left click` and the backquote key; right click is unchanged. Pings never issue movement, attack or view-cone commands, and clear across loads, scene changes and disconnects. The **ping sound file is not distributed** under the MIT grant: without `ShadowGambitCoop/media/bell_b.wav` the markers still work and a single warning is logged.

0.8.42 在双人线新增纯装饰的场景内标点：每人一个、无冷却、约五秒，房主青色、客机橙色；默认 `Alt+左键`、`Ctrl+E+左键`、反引号，右键不受影响。标点不产生移动、攻击或原版视野指令，读档／换场景／断线会清空。**提示音文件不随 MIT 授权分发**：缺少 `ShadowGambitCoop/media/bell_b.wav` 时标记照常显示，仅记录一次告警。

截至 2026-11-14，双人导出已补齐启动身份、重连、教程回档和接档失败修订；0.8.41 进一步减少 UDP 解析分配和无关场景查找，并修订发现视野接替及换场后的旧朝向状态。多人版仍是独立实验分支。尚无本轮实机帧率结论；检查边界见 [VALIDATION.md](VALIDATION.md)。

所有参与电脑使用同一版本。两个源码目录分别构建，不要混装。多人版不设固定房间人数上限，但实际容量受房主性能和带宽限制。关卡仍最多部署八名船员；没有空闲船员时旁观。目前单船员场景仍由房主控制。

The multiplayer transport was exercised with simulated clients, including 32 clients and 96 TCP channels. **This is not validation of 32 simultaneous game instances. Three-or-more-PC gameplay remains unverified.**

## Build / 构建

Windows x64, PowerShell 7, a local game installation, and matching BepInEx/core, .NET runtime and locally generated BepInEx/interop references are required. Those binaries are **not included** in this repository. Obtain dependencies from their respective upstream projects and keep their notices.

```powershell
pwsh -NoProfile -File .\scripts\Build.ps1 -Variant two-player -GameDirectory "X:\Games\Shadow Gambit The Cursed Crew"
pwsh -NoProfile -File .\scripts\Build.ps1 -Variant multiplayer -GameDirectory "X:\Games\Shadow Gambit The Cursed Crew"
```

The build writes to `artifacts/build/<variant>/`. It does not install into the game directory. See [BUILDING.md](BUILDING.md) for toolchain requirements and tests.

构建只读取本机游戏依赖并输出到 `artifacts`，不会自动覆盖游戏插件。构建检查确认接口签名，不能代替实际游戏验证。

## Test / 测试

```powershell
pwsh -NoProfile -File .\scripts\Test.ps1
```

Tests use temporary fixtures and loopback networking; some environments require permission to open local sockets. Native test doubles do not prove in-game rendering, skill or scene correctness.

初次多人实测建议一名房主加两名客机，依次检查独占选人、移动和技能、加入/退出、存读档、回船及剧情等待。问题反馈模板见 GitHub Issues；分享日志前请检查脱敏结果。

## Network / 网络

- Native TCP and UDP: local port **38479**.
- HTTP gateway / HTTPS tunnel backend: local TCP **38477**. HTTPS requires a correctly configured TLS endpoint; this number alone does not enable TLS.
- LAN discovery: UDP **38478**. The Unity bridge uses loopback UDP **38476**.
- Host first, then guests. A single room endpoint can serve multiple guests; separate tunnels per guest are unnecessary.

原生 TCP 和 UDP 使用同一端口号、不同协议；TCP 不直接等于 UDP。UDP 不可用时走 TCP 状态传输。参与者应拥有相匹配的游戏版本和关卡所需内容。

## License and release scope / 许可与发布范围

Original project code is under the [MIT License](LICENSE). Preserve that license and copyright notice when redistributing it. The MIT grant does **not** cover Shadow Gambit, Unity, BepInEx, Il2CppInterop, .NET, Python, or other third-party components.

自有代码按 MIT 开源；第三方组件分别适用自己的许可证。此仓库不含游戏程序、资源、接口生成缓存、个人存档、联机地址历史或依赖二进制。

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), [RELEASE_REVIEW.md](RELEASE_REVIEW.md) and [SECURITY.md](SECURITY.md). Earlier offline test installers are **not** cleared for public redistribution: missing third-party notices/source arrangements and Unity library redistribution need further work.

This project is not affiliated with or endorsed by Mimimi Games, Unity or Valve. The game's name identifies the supported game and does not grant any rights in its assets or trademarks.

## Uploading to GitHub / 上传 GitHub

Upload the contents of this source folder, including hidden `.github`, `.gitignore` and `.gitattributes` files. Do not upload your game directory, historical test ZIPs, crash dumps, save files or generated `artifacts` directory.

只上传这个整理后的目录。可以新建空仓库后通过网页上传文件，或使用 GitHub Desktop 添加此目录。仓库已包含 MIT 文件，创建仓库时无需再生成另一份许可证。
