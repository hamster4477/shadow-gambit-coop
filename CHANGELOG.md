# Changelog

Release ordering on GitHub follows the commit a tag points at, not the upload time. Entries live here so the two variant lines can be read in one place.

## Two-player line

| Version | Date | Contents |
| --- | --- | --- |
| `v0.8.42` | 2026-11-14 | Scene-scoped visual pings (one marker per player, five seconds, host cyan / guest amber, replaceable bell). Carries the 0.8.39 host-role, 0.8.40 checkpoint/rewind and 0.8.41 performance/enemy-view work. **No two-PC test yet.** |
| `v0.8.41` | 2026-11-14 | Lower temporary allocations in UDP snapshot parsing, fewer unnecessary scene-loader lookups, observer-view handoff and stale facing fixes. Dense-scene paging limit remains. |
| `v0.8.40` | 2026-11-14 | Checkpoint/rewind recovery, first-account live import, retryable result publication, launch-request retirement, installer process-lifecycle fix. |
| `v0.8.39` | 2026-11-13 | Host/guest identity cleanup for an explicit host launch; F6 protection for the host. |

Unreleased work and open issues are tracked in the repository issues, not here.

## Multiplayer trial line

| Version | Date | Contents |
| --- | --- | --- |
| `v0.9.0-mp1` | 2026-11-14 | Independent multiplayer (3+ players) experiment, MP1 baseline. **Does not include** the 0.8.39–0.8.42 two-player fixes except the shared Steam-account constant fix. Never played on more than two PCs. |

The two lines share the plugin GUID and DLL file name, so a single install can only hold one of them.

---

# 更新日志

GitHub 的发布列表按 tag 所指提交的时间排序，而不是上传时间。把两条线放在一起，方便对照。

## 双人线

| 版本 | 日期 | 内容 |
| --- | --- | --- |
| `v0.8.42` | 2026-11-14 | 场景内标点（每人一个、五秒、房主青色／客机橙色、提示音可替换）。包含 0.8.39 启动身份、0.8.40 接档／回档、0.8.41 性能与敌人视野修订。**尚无双机实测。** |
| `v0.8.41` | 2026-11-14 | 减少 UDP 快照解析临时分配、减少无关场景加载器查找、修订发现视野接替与换场旧朝向。密集场景分页限制仍在。 |
| `v0.8.40` | 2026-11-14 | 检查点／回档恢复、首次账号实时导入、可重试结果发布、启动请求清理、安装器进程生命周期修正。 |
| `v0.8.39` | 2026-11-13 | 显式房主启动时清理客机身份；保护房主的 F6。 |

未发布的改动和未结问题记录在仓库 issues 里，不写在本文件。

## 多人试行线

| 版本 | 日期 | 内容 |
| --- | --- | --- |
| `v0.9.0-mp1` | 2026-11-14 | 独立多人（3 人以上）实验，MP1 基线。**不包含** 0.8.39–0.8.42 的双人修复，只带上共用的 Steam 账号常量修复。从未在超过两台机器上玩过。 |

两条线共用插件 GUID 和 DLL 文件名，同一次安装只能放其中一个。
