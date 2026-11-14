# Third-party dependencies / 第三方依赖声明

The MIT license in this repository covers independently authored project code. It does not replace any dependency license. This source export and its mod-only packaging script do not include the components below. Versions describe the audited test baseline, not a recommendation to use outdated runtimes indefinitely.

本项目 MIT 许可证仅覆盖自有代码。下列组件不包含在源码包或纯模组更新包中；使用或再次分发它们时应分别遵守原许可证。版本来自测试环境核对，不能理解为所有修订版都适用同一条款。

| Component / 组件 | Audited version / 核对版本 | License / 许可 | Primary evidence / 原始依据 |
| --- | --- | --- | --- |
| BepInEx | 6.0.0-be.682+910844a982dbb126d0c6544847424bc42fb7849c | LGPL-2.1 | [upstream 1](https://raw.githubusercontent.com/BepInEx/BepInEx/910844a982dbb126d0c6544847424bc42fb7849c/LICENSE) |
| Il2CppInterop | 1.4.6-ci.389 | LGPL-3.0-only | [upstream 1](https://nuget.bepinex.dev/v3/package/il2cppinterop.runtime/1.4.6-ci.389/il2cppinterop.runtime.1.4.6-ci.389.nupkg), [upstream 2](https://raw.githubusercontent.com/BepInEx/Il2CppInterop/v1.4.5/Directory.Build.props), [upstream 3](https://raw.githubusercontent.com/BepInEx/Il2CppInterop/v1.4.5/COPYING.LESSER), [upstream 4](https://raw.githubusercontent.com/BepInEx/Il2CppInterop/v1.4.5/LICENSE) |
| UnityDoorstop | 4.0.0 | LGPL-2.1 | [upstream 1](https://raw.githubusercontent.com/NeighTools/UnityDoorstop/v4.0.0/LICENSE) |
| HarmonyX | 2.10.1 | MIT | [upstream 1](https://raw.githubusercontent.com/BepInEx/HarmonyX/v2.10.1/LICENSE) |
| MonoMod | 22.05.01.01 | MIT | [upstream 1](https://raw.githubusercontent.com/MonoMod/MonoMod/v22.05.01.01/LICENSE) |
| Mono.Cecil | 0.11.4.0 | MIT | [upstream 1](https://raw.githubusercontent.com/jbevain/cecil/0.11.4/LICENSE.txt) |
| AsmResolver | 5.1.0 | MIT | [upstream 1](https://raw.githubusercontent.com/Washi1337/AsmResolver/v5.1.0/LICENSE.md) |
| Iced | 1.18.0+2e21c4c1299f9f90b8fce53acb4710b6d474e46d | MIT | [upstream 1](https://raw.githubusercontent.com/icedland/iced/2e21c4c1299f9f90b8fce53acb4710b6d474e46d/LICENSE.txt) |
| Cpp2IL family | 2022.1.0-development.866+f87ccd188ec032cbba27763bf10ba43bb25eb4e8 | MIT | [upstream 1](https://raw.githubusercontent.com/SamboyCoding/Cpp2IL/f87ccd188ec032cbba27763bf10ba43bb25eb4e8/LICENSE) |
| AssetRipper.VersionUtilities | 1.3.1+3e5ee806808370156f641019e278b30a80ab5062 | MIT | [upstream 1](https://api.nuget.org/v3-flatcontainer/assetripper.versionutilities/1.3.1/assetripper.versionutilities.nuspec) |
| Disarm | 2022.1.0-master.26+cb8877f0dc1eecd6cbfba219991f8b00be2d41bc | MIT | [upstream 1](https://api.nuget.org/v3-flatcontainer/disarm/2022.1.0-master.26/disarm.nuspec) |
| SemanticVersioning | 2.0.2 | MIT | [upstream 1](https://api.nuget.org/v3-flatcontainer/semanticversioning/2.0.2/semanticversioning.nuspec), [upstream 2](https://raw.githubusercontent.com/adamreeve/semver.net/master/LICENSE) |
| Capstone.NET binding | 2.1.0 | MIT | [upstream 1](https://api.nuget.org/v3-flatcontainer/gee.external.capstone/2.1.0/gee.external.capstone.nuspec), [upstream 2](https://raw.githubusercontent.com/9ee1/Capstone.NET/master/LICENSE) |
| Dobby | unknown bundled revision | Apache-2.0 | [upstream 1](https://raw.githubusercontent.com/jmpews/Dobby/master/LICENSE) |
| .NET runtime | 6.0.7 @0ec02c8c96e2eda06dc5b5edfdbdba0f36415082 | MIT AND third-party notices | [upstream 1](https://raw.githubusercontent.com/dotnet/runtime/v6.0.7/LICENSE.TXT), [upstream 2](https://raw.githubusercontent.com/dotnet/runtime/v6.0.7/THIRD-PARTY-NOTICES.TXT) |
| Python embedded Windows distribution | 3.13.9 | PSF-2.0 AND bundled third-party notices | [upstream 1](https://raw.githubusercontent.com/python/cpython/v3.13.9/LICENSE), [upstream 2](https://docs.python.org/3.13/license.html) |
| Unity base libraries | Unity 2021.3.25 reference/base libraries | NOT CLEARED |  |

## If redistributing dependencies / 如需打包第三方依赖

- MIT/BSD-style components: keep their actual copyright and license texts; a link alone is not a substitute for required notices.
- Apache-2.0 components: retain the license and applicable notices, identify modifications, and check the exact upstream revision.
- LGPL components: preserve applicable license/GPL texts and notices, supply the required corresponding source or a compliant source-access arrangement, and retain the user’s applicable replacement/relinking rights. Which mechanism applies depends on the exact license version and distribution method.
- .NET and Python include other components with additional notices; do not strip their upstream notice files.
- Unity libraries and game files have separate terms. A public download URL or a mod framework license is not a general grant to mirror engine/game binaries.

独立的 MIT 应用动态引用 LGPL 库，不等于必须把所有应用代码改成 GPL；但也不免除被分发库及组合发行适用的义务。本仓库不提供对游戏内容或引擎库的额外许可。

## Evidence limits / 核对边界

Exact Il2CppInterop 1.4.6-ci.389 NuGet metadata specifies LGPL-3.0-only. Its historical root LICENSE contains GPL text alongside COPYING.LESSER; do not label it plain GPL based on the root file alone. Doorstop 4.0.0 is LGPL-2.1; the old Doorstop 3 CC0 label does not apply.

The precise Dobby revision and complete corresponding-source delivery for the old offline installer remain unresolved. See [RELEASE_REVIEW.md](RELEASE_REVIEW.md). This inventory records evidence and remaining work; it is not permission from the game or engine rights holders.
