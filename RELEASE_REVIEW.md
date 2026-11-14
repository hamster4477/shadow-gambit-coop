# Publication review / 发布审查

Reviewed 2026-11-13. This records technical and license-document checks; it is not authorization from game or engine rights holders.

## Source repository / 源码仓库

The maintainer selected MIT for the project's independently authored code. The curated export contains source, launch scripts, selected tests and documentation. No game executable, game assets, generated `BepInEx/interop` bindings, Unity DLLs, third-party runtime binaries, saves, room histories, logs or dumps are included.

源码保留双人版和多人试行版两条代码线。已移除个人账号测试值，未发现明显嵌入的私钥或活动连接凭据。静态审查不能保证每段代码的历史来源；引入第三方实现时仍须单独核对并保留署名。

The build scripts now accept an explicit local game directory and stage output without installing. The export also corrects incomplete public-log redaction. These changes are source-export changes; they were not silently installed over either tested game build.

## Historical full offline installer / 旧完整离线测试包

The MP1 full installer audited here contains 366 payload files, including 66 Unity base-library DLLs. Only the Python license file was retained. Required license/notice material for BepInEx, Il2CppInterop, Doorstop and multiple other dependencies is missing. Corresponding-source arrangements for applicable LGPL components are also incomplete.

**Do not publish that old offline test ZIP unchanged as a cleared public release.**

旧包还未核实 Unity 托管基础库的独立再分发授权。未查到授权依据不等于判定已经违法，但不能因此声称整包可自由分发。公开源码和纯模组包排除了这些二进制。

Primary evidence:

- [BepInEx exact baseline license](https://raw.githubusercontent.com/BepInEx/BepInEx/910844a982dbb126d0c6544847424bc42fb7849c/LICENSE).
- [Il2CppInterop historical LGPL text](https://raw.githubusercontent.com/BepInEx/Il2CppInterop/v1.4.5/COPYING.LESSER); exact ci.389 package metadata is listed in `third-party-dependencies.json`.
- [Doorstop 4.0.0 license](https://raw.githubusercontent.com/NeighTools/UnityDoorstop/v4.0.0/LICENSE).
- [Unity Software Terms](https://unity.com/legal/editor-terms-of-service/software). The terms applicable to the originally acquired Unity version and any particular redistribution grant still require confirmation.
- [Shadow Gambit official Modding Tool announcement](https://www.shadowgambit.com/modding-tool/). It describes sharing missions made with the built-in tool, not blanket permission to redistribute engine libraries or every external plugin.

## Before publishing binaries / 发布二进制前

1. Publish the corresponding project source for the exact build and identify whether it is the two-player or multiplayer trial.
2. Package only project files unless dependency redistribution obligations and game/engine rights have been resolved separately.
3. Include the MIT license, dependency notices and accurate prerequisites. Downloading dependencies automatically does not remove their license obligations.
4. Preserve exact upstream versions and hashes. The old full installer's source/notice requirements are not fixed merely by adding this report.
5. Label remaining native-gameplay validation honestly. MP1's simulated clients are not proof that that many real game instances run correctly.

多人版仍需三台以上电脑实测。当前单船员场景仍由房主操控；不设房间人数硬上限不代表性能无限。不要把原始日志、转储或存档当作公开 Release 的附件。
