# 审查修复报告：core 组

分支：`worktree-agent-a1b2f50a03ec46833`（基于 1f64d35）
提交：`13cf1bf`（Core 与测试、文档）→ `aefd133`（App 侧：NoticeCenter / NoticeStorage / SettingsNotices / 通知页），本报告另有一个提交。
注意：`13cf1bf` 单独无法编译（`Hub.Persist` 签名改为 `Action<bool>`，NoticeCenter 在下一个提交中跟进），请两个提交一起合并。

## 构建与测试
- `dotnet build BrimDeck.sln -c Release`：0 警告 0 错误
- BrimDeck.Tests：1521 通过（基线 1481，新增 40）
- BrimDeck.MediaTests：83 通过
- 已清理 bin/obj

## 条目处理

### correctness-core.md
| 条目 | 结果 |
|---|---|
| core-H1 | 已修。FireDue 推进时设 `ReceivedAt = now`（移到顶部的那些）；Prune 按 `Max(ReceivedAt, UpdatedAt)`，所以 StopTimer、Snooze、错过的提醒也从变化时刻起算。`NoticeTimeTests` 中“Once given, it ages out as usual”改为：响起时仍在，保留时间过后才删除。 |
| core-H2 | 主机侧已修：按 InstanceKey 更新时要求 AppKey 相同；WSL 来源只匹配 `SourcePresent` 仍为真的通知（重启后从历史加载的都为 false，因此旧编号不会被覆盖）。不依赖 session 固定为 "server"/"monitor"。**需要协调**：agent 每次启动在 Session 中带随机 nonce（wsl 组已做，`server-<hex>`/`monitor-<hex>`，不含冒号，`NativeId` 取最后一段仍成立）。agent 重启但 BrimDeck 未重启时，同一应用的同一编号仍可能命中旧通知，只能靠 nonce 解决。 |
| core-M1 / C02 | 已修：上条的 AppKey 检查；CLI 的 InstanceKey 改为 `cli:{distro}:{name}:{eventId}`；空白 `--id` 用 `Blank()` 后生成 Guid。**需要协调**（Protocol 组）：`NotifyCommandLine` 对空 `--id`/`-r` 报错，或 `Validate` 归一化为 null；WSL agent `_replies.TryAdd("")`。 |
| core-M2 | 主机侧已修（应 wsl 组协调）：`FromAgent` 对 `notices-off`、`distro-off`、`Validate` 失败，给带 EventId 的 cli 消息回复 Rejected。`WslNoticeBridge.ReadAsync` 中 Validate 失败的 `continue` 不在本组文件。 |
| core-M4 | 已修：新增 `Unavailable`（IOException/UnauthorizedAccess、盘或网络位置不在）与 `Unreadable`（magic、解密、JSON）区分。只有读到（Loaded/Empty）才允许写；Unavailable 在 10 分钟维护、ApplySettings 或设置页“重试”时重读，重读结果与本次会话收到的通知合并。设置页只对 Unreadable 提供“清空并重建”。 |
| core-M5 | core 侧已修（应总管协调）：`NoticeInput.Missed`，`NoticeHub.Late` 开头 `if (input.Missed) return true;`，有测试。NoticeCenter 中已写好 `OnMissed(NoticeInput)`，**待合并时接线**：`_windows.Missed += input => _dispatcher.BeginInvoke(() => OnMissed(input));` |
| core-M6 | 已修：`ClockMoved`（TimeChanged 与唤醒都会调用）中 `TimeZoneInfo.ClearCachedData()`，并递增 `NoticeCenter.Epoch` 触发通知页整页重绘。 |
| core-L1 | 已修：`NoticeClock.Alerting(timing, now)`，开始超过 `MissedLimit`（10 分钟）后才收到的原生日程不算紧急；Hub.Receive 与 NoticeCenter.Item 使用它。 |
| core-L2 | 已修：MaxCount 裁剪跳过待提醒/运行中的项；每天/每周清空改用 `Hub.ClearDone`。手动“清空”和“退出时清空”仍清空全部（符合其说明）。 |
| core-L3 | 已修：Dispose 先停管道、Windows 源、WSL，再“退出时清空”和最后保存；之后 `Receive` 返回 `shutting-down`，管道回复 Rejected `shutting-down`，FromAgent 忽略。 |
| core-L4 | 已修：见 life-L1 的换目录方案（新目录已有文件先合并；读不了的旧文件原地保留；换目录后状态随新目录重置）。 |
| core-L5、L6 | 跳过：不在本组文件（WslNoticeBridge、NotifyCommandLine）。 |
| core-L7 | 已修：静默期间按 Key（notice.Id）去重计数。 |
| core-L8 | 跳过：报告中注明是设计取舍（倒计时需跨重启持久化）；平移方案风险大于收益。 |

### lifecycle-tests.md
| 条目 | 结果 |
|---|---|
| life-L1 | 已修：`NoticeHistoryPlan.For`（Core，纯函数，有测试）+ `NoticeCenter.UseHistoryFolder` 执行。重新开启保存且已读取 → 写盘；已读取时换目录 → 读入新目录文件合并、写入、写成功后再删旧文件；未读取时换目录 → 移动（`NoticeStorage.Move`，新目录已有文件则不动）；关闭保存时换目录 → 不写。 |
| life-T4 | 已修：断言固定为 `file:C:\icons\a.png`。 |
| 其他（L2–L15, T1–T3, T5–T7） | 不在本组范围或文件。 |

### security.md
| 条目 | 结果 |
|---|---|
| sec-M1 | 已修：`NoticeStorage.Trusted`（重解析点、属主须为当前用户/Administrators/SYSTEM）。`Choose()` 对已存在的安装目录 `notices` 不可信时回退到 `%LOCALAPPDATA%`；`Prepare` 对自选目录不可信时返回 false，状态 `Refused`，不读不写，设置页提示并提供“默认位置”。无属主信息的盘（FAT/exFAT）：自选目录放行，默认目录回退。`NoticeHistory.Save` 临时文件改为 `.notices.<guid>.tmp` + `FileMode.CreateNew`，失败时删除；`Delete` 同时清理残留临时文件。未做：已存在且属当前用户的默认目录再次 MakePrivate（每次启动改 ACL，收益小）；图标写入 `NoticeIcons.TryWrite` 不在本组文件（**需要协调**）。 |
| sec-L4 | 已修（Core 侧）：`NoticeText.Clean` 去掉 Cf 格式字符（保留 ZWJ）、U+2028/2029 转为换行（标题中转为空格），用于标题、正文、应用名；`FromPipe` 只接受 `IsDistroName` 合格的发行版名（WSL 经 `--stdio` 带 WSL_DISTRO_NAME 的正常路径不受影响，已有测试覆盖）。**需要协调**（Protocol 组）：`NoticeWire.Clip` 同样处理。紧凑态角标建议未做（视觉改动）。 |
| sec-L5 | 已修：紧急通知不受 3 次/10 秒限制，但另有 `UrgentLimit = 3` 次/分钟，超出的排队（高优先级可等 5 分钟）；单个真实闹钟照常立即响。NextDeadline 按各项最早可开始时间计算，无忙循环。 |
| sec-L6 | 已修（notices.dat 部分）：超过 32 MB 视为 Unreadable，不读入内存。图标缓存上限、ICO 帧数不在本组文件。 |

### performance.md
| 条目 | 结果 |
|---|---|
| perf-7 | 已修：Hub 按 Id 字典索引；显示中通知的进度刷新首次立即、之后每 250 ms 合并一次；Hub 引起的 `Changed` 节流到 250 ms（首次立即）；进度/已读等次要变化 12 s 后保存，新通知/删除仍 2 s；`NoticeHistory.Save` 不再二次复制；`Flush(true)` 只在退出保存时使用；通知页在列表顺序不变时只替换 Revision 变化的行。NoticeWindow 的 toast 原地更新不在本组文件（**需要协调**，已通过 250 ms 合并减少调用）。 |
| perf-8 | 已修：回调中先 MarkAllViewed 再清除排队标志。 |
| perf-9 | 已修：notices.dat 的读取、解密和旧目录迁移移到后台，完成后与已到达通知合并；读完前不写。`DefaultFolder` 的写探测仍在 UI 线程（仅首次，很小）。 |

### quality.md
- 已删：`NoticeHub.ForgetApps`、`AttentionCoordinator.IsHeld`。
- 未删：`NoticeLook.StatusColor`（在 NoticeVisuals.cs，不在本组文件，**需要协调**）；`NoticeHistory.PlainProtector` 改 internal 需改 Core 的 InternalsVisibleTo，未做。

## 新增测试
`tests/BrimDeck.Tests/AuditCoreTests.cs`（由 `NoticeCoreTests.Run` 调用，未改 Program.cs）：保留时间（停表、计时器响后）、不同 -s 同 --id、空白 --id、WSL 编号冲突与重启后、发行版名校验、补录日程、Missed、条数上限与定期清空保留待提醒项、Persist 次要标记、Load 合并、紧急限流与单个闹钟、静默汇总去重、Clean 文本、历史文件（随机临时文件不跟随旧名、占用时 Unavailable、超大文件、缺失盘符、残留临时文件清理）、NoticeHistoryPlan 各情形。

## 需要协调汇总
1. wsl 组：agent Session 带随机 nonce（已做）；`WslNoticeBridge.ReadAsync` 中 Validate 失败要回复。
2. 合并时接线 `_windows.Missed → OnMissed`。
3. Protocol 组：`NoticeWire.Clip` 去格式字符；`NotifyCommandLine` 拒绝空 `--id`/`-r`。
4. NoticeIcons：`TryWrite` 随机临时名 + 原子替换、缓存上限、ICO 帧数上限。
5. NoticeWindow：toast 原地更新进度；`NoticeLook.StatusColor` 死代码。
