# 影子诡局联机模组

[English](README.md) | **简体中文**

《影子诡局：被诅咒的海盗》非官方联机测试项目，基于 BepInEx IL2CPP。房主执行权威游戏逻辑，客机发送操作并同步状态、进度和检查点。仍有实机测试未覆盖的场景，请勿将其描述为稳定正式版。

## 版本

| 源码 | 基线 | 状态 |
| --- | --- | --- |
| `src/two-player` | `0.8.42 / 2026-11-14 标点功能` | 已合并最新双人修订，待双机复验 |
| `src/multiplayer` | `0.9.0 / MP1-20261113-unlimited-trial` | 独立多人试行版 |

所有参与电脑使用同一版本。两个源码目录是相互独立的替代实现，不要混装。

双人导出已补齐启动身份隔离、F7 恢复房主（F8 仍是原版读档键）、新启动时清理旧的客机等待请求、教程／回档场景恢复，以及接档失败的清理。多人线仍是独立的 MP1 基线，不是这些新修复的超集。

0.8.41 减少了 UDP 快照解析中的临时分配和无关的场景加载器查找，并修订了加载、过场与附身期间的发现视野接替和旧朝向状态。这些是孤立测试结果，不是实测帧率提升。

0.8.42 新增纯装饰的场景内标点：每人一个、无冷却、约五秒消失，房主青色、客机橙色。默认 `Alt+左键`、`Ctrl+E+左键`、反引号（Esc 下方），右键不受影响。标点不产生移动、攻击或原版视野指令，读档／换场景／断线会清空。默认提示音随源码提供（`src/two-player/media/bell_b.wav`），安装后位于 `ShadowGambitCoop/media/bell_b.wav`，**可以替换**：换成任意 PCM16 WAV 即可；文件缺失时标记照常显示，仅记录一次告警。该音频随模组分发，但不属于 MIT 授权的源码部分。

多人版不设固定房间人数上限，但实际容量受房主性能和带宽限制。关卡仍最多部署八名船员；没有空闲船员时旁观。目前单船员场景仍由房主控制。

多人传输层用模拟客户端验证过，包括 32 个客户端和 96 条 TCP 通道。**这不等于验证了 32 个同时运行的游戏实例；三台以上实机游玩仍未验证。**

## 构建

需要 Windows x64、PowerShell 7、本机已安装的游戏，以及匹配的 BepInEx/core、.NET 运行时和本机生成的 BepInEx/interop 引用。这些二进制**不包含在本仓库中**，请从各自上游项目获取并保留其许可与声明。

```powershell
pwsh -NoProfile -File .\scripts\Build.ps1 -Variant two-player -GameDirectory "X:\Games\Shadow Gambit The Cursed Crew"
pwsh -NoProfile -File .\scripts\Build.ps1 -Variant multiplayer -GameDirectory "X:\Games\Shadow Gambit The Cursed Crew"
```

构建只读取本机游戏依赖，输出到 `artifacts/build/<variant>/`；不会安装到游戏目录，也不会覆盖正在使用的插件。构建期检查确认接口签名，不能代替实际游戏验证。工具链与测试说明见 [BUILDING.md](BUILDING.md)。

## 测试

```powershell
pwsh -NoProfile -File .\scripts\Test.ps1
```

测试使用临时夹具和本机回环网络，某些环境需要放行本地套接字。原版接口的测试替身不能证明游戏内渲染、技能或场景行为正确。

初次多人实测建议一名房主加两名客机，依次检查独占选人、移动和技能、加入／退出、存读档、回船及剧情等待。问题反馈走 [Issue 模板](.github/ISSUE_TEMPLATE)；分享日志前请先检查脱敏结果。

## 网络

- 原生 TCP 与 UDP：本地端口 **38479**。
- HTTP 网关／HTTPS 穿透后端：本地 TCP **38477**。HTTPS 需要正确配置 TLS 端点；只有端口号不等于启用 TLS。
- 局域网搜房：UDP **38478**。Unity 桥接使用回环 UDP **38476**。
- 先开房主，再加入客机。单个房间端点可以服务多名客机，不必为每个客机单独开隧道。

原生 TCP 和 UDP 使用同一端口号但属于不同协议，TCP 不能直接替代 UDP。UDP 不可用时状态传输回退到 TCP。参与者应拥有相匹配的游戏版本，并拥有所选关卡需要的内容。

## 许可与发布范围

自有代码按 [MIT 许可证](LICENSE) 开源；再分发时请保留许可证与版权声明。MIT 授权**不**覆盖 Shadow Gambit、Unity、BepInEx、Il2CppInterop、.NET、Python 或其他第三方组件。

本仓库不含游戏程序、游戏资源、接口生成缓存、个人存档、联机地址历史或依赖二进制。另见 [第三方声明](THIRD_PARTY_NOTICES.md)、[发布审查](RELEASE_REVIEW.md) 和 [安全说明](SECURITY.md)。早期的离线测试安装包**尚未**取得公开再分发许可：第三方声明／源码安排与 Unity 库再分发仍需处理。

本项目与 Mimimi Games、Unity、Valve 无隶属关系，也未获其背书。游戏名称仅用于说明支持的对象，不授予其资源或商标的任何权利。

## 发布这个源码目录

如果要在别处重新发布本目录，请一并包含隐藏的 `.github`、`.gitignore` 和 `.gitattributes`。不要上传游戏目录、历史测试 ZIP、崩溃转储、存档文件或生成的 `artifacts` 目录。仓库已包含 MIT 许可文件，新建仓库时无需再生成另一份许可证。
