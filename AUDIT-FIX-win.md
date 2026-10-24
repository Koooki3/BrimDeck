# 审查修复报告：win 组

分支：`worktree-agent-a6587b43f6720f550`（基于 1f64d35）
提交：b774f94、6b70d00、e23b115、63f49d7，以及本报告所在的最后一个提交

## 条目处理结果

| 条目 | 结果 | 说明 |
|---|---|---|
| perf-6 Windows 轮询 | 已修 | 未确认最新 id 时，全量读取间隔从 3 秒开始；每次读取没有新通知就翻倍，最长 15 秒（FullInterval）。唤醒、重置时回到 3 秒。逻辑在 Core `WindowsNoticeList.UnknownWait`。 |
| perf-11 NoticeIcons | 已修（第 4 小项跳过） | 16×16 缩放取样移到后台线程，UI 线程上的 `MusicVisuals.Accent` 只处理 16×16 图，结果和原来相同（`Readable` 的缓存不是线程安全的，所以最后一步仍在 UI 线程）。LRU 改用节点字典，复杂度 O(1)。调色板预先解析，回退色和用户颜色缓存在有上限的并发字典里。跳过的第 4 小项：`aumid:` 先读本地缓存。这样会让应用更新 logo 后继续显示旧图，属于行为改变。 |
| sec-L2 管道限流 | 已修 | 所有类型的消息先在管道线程上经过 `IntakeGate`（10 秒内 300 条，高于 `NoticeCenter.Admit` 的 200 条，所以通知仍由 Admit 决定），超出时直接回复 `rate-limited`，不再派发到 UI 线程。 |
| sec-L2 回写超时 / life-L11 | 已修 | 回复（包括 `bad-frame`）用 idle 令牌，并重置为 10 秒。 |
| sec-L2 管道名被抢注 | 部分（检测与报告） | `FirstPipeInstance` 失败时，用 `CurrentUserOnly` 客户端连一次：返回 `UnauthorizedAccessException` 说明名字被其他用户占用，`Error` 显示"通道名称被另一个用户占用。"。随机盐方案要同时改 Protocol 的 `PipeName` 和 Notify 客户端，见"需要协调"。 |
| sec-L6 图标缓存上限 / ICO 帧数 | 已修 | 新写入图标后修剪 icons\：按最后写入时间只保留 200 个（复用已有图标时刷新时间），并清掉超过 1 分钟的残留临时文件。ICO 头部声明超过 64 帧、或解码器帧数超过 64 时不解码。 |
| sec-M1（图标部分） | 已修 | `TryWrite` 写随机名 `.icon.<guid>.tmp`（`FileMode.CreateNew`），再 `File.Move(..., overwrite)`。icons 目录是重解析点时不写；目标是链接时会被替换，不会写穿。顺带完成了 quality 的"NoticeIcons 原子写"。 |
| core-M5 唤醒后丢失 | 源侧已修，需要协调接线 | 唤醒后的第一次读取改用 `WindowsNoticeBaseline.Resume`：`_known` 里没有的 id 就是上次读取之后到达的，通过新事件 `WindowsNoticeSource.Missed` 交出（按时间从旧到新）。启动或授权后的基线行为不变。还没读取过就唤醒，按普通基线处理。接线见下。 |
| life-L2 开关重建管道冲突 | 已修 | `NoticePipeServer.Dispose` 记下旧监听器全部结束的任务，新服务器的监听器先等它（最多 2 秒）再用 `FirstPipeInstance`。创建失败的重试从 200 毫秒起指数退避到 5 秒（原来固定 5 秒）。 |
| life-L5（Windows 部分） | 已修 | 循环捕获所有异常：设置 `_dirty`，放开监听器（下一步重建），报告 Failed。不在预期列表里的异常写 notices.log，同一条消息只写一次。`GetNotificationsAsync` 加 15 秒超时。 |
| life-L15（Windows 部分） | 已修 | 每个 Run 的 CTS 在循环结束时释放；唤醒信号量改为每个 Run 一个，旧循环不会吃掉新循环的唤醒；`LaunchAsync` 多捕获 `InvalidOperationException`。 |
| life-T1（IsAppId / 列表差分） | 已补测试 | `IsAppId` 移到 Core（`WindowsNoticeList.IsAppId`），列表差分抽成 `WindowsNoticeList.Compare`，两者都有测试。 |
| life-T2 快捷键冲突 | 已补测试 | 冲突规则代码 `HotkeyText.Plan`（Core）本身正确，未改。"App.ApplyHotkeys 在通知关闭时传空串"在 App.xaml.cs，不归本组，未测。 |
| life-T3 路径拒绝 | 已补测试 | 函数在 Core，只加测试。报告列出的拒绝用例全部通过，另外覆盖了 `\\?\UNC\`、`//server`、超长、控制字符。重解析点检查在 App 侧（`NoticeIcons.ThroughLinks`/`ReadImageFile`），测试项目只引用 Core，无法直接测。发现但未改：`IsLinuxIcon("")` 返回 true（`IconRef` 先挡掉了空串，无实际影响）。 |
| quality 死代码 | 无 | quality.md 没有列本组文件的死代码，自查也没有发现无引用的成员。旧的 `UnknownInterval`、`_known`/`_newest`/`_confirmed`/`_baseline`、`_wake` 已随重构删除。 |
| life-L14（附带） | 准备 | `NoticeQuiet.SessionLocked()` 改为 `internal`，AmbientGlow 的负责组可以直接复用。 |

## 需要协调

1. **core-M5 接线**（NoticeCenter.cs、NoticeModel.cs / NoticeHub.cs 不归本组）：
   - 在 `NoticeInput` 加 `public bool Missed { get; init; }`，`NoticeHub.Late` 开头加 `if (input.Missed) return true;`，让它记录为未读但不弹出（结果为 `late`）。
   - 在 `NoticeCenter` 构造里，仿照 `Received` 订阅：
     `_windows.Missed += input => _dispatcher.BeginInvoke(() => { if (_enabled && Settings.NoticeWindowsSource) Receive(NoticeTimeSources.Classify(input, TimeZoneInfo.Local) with { Missed = true }); });`
   - 未接线时 `Missed` 没有订阅者，行为和修复前一样（这些通知仍然不会记录）。
2. **管道名随机盐（sec-L2 后半）**：要改 `BrimDeck.Protocol.NoticeProtocol.PipeName`、`BrimDeck.Notify` 客户端和 WSL helper 路径，范围超出本组。目前只做了检测与报告。
3. **测试注册**：`AuditWinTests.Run` 在 `HotkeyTests.Run` 的末尾调用，这样不用改共享的 `tests/BrimDeck.Tests/Program.cs`。合并时可以移到 Program.cs。
4. **新增的 Core 文件**：`src/BrimDeck.Core/Notices/WindowsNoticeList.cs`、`src/BrimDeck.Core/Notices/IntakeGate.cs`，没有修改现有的 Core 文件。`IntakeGate` 是通用的滑动窗口，`NoticeCenter.Admit` 以后可以改用它（使用单调时钟，并且已有测试，对应 life-T1 中的 Admit 用例）。

## 新增测试

- `tests/BrimDeck.Tests/AuditWinTests.cs`：Windows 列表差分（基线、新增、移除、同名替换、唤醒补记、唤醒不削弱待处理的基线、撤销权限、不可读通知只读一次、重复 id、按时间排序）、退避序列 3→6→12→15→15 秒、IsAppId 的接受与拒绝、IntakeGate 窗口与互相独立、Windows/Linux 图标路径拒绝。
- `tests/BrimDeck.Tests/HotkeyTests.cs`：通知快捷键对隐藏/展开让位、三者相同、写法不同仍算重复、未设置/无效、无效的前者不占组合。

## 构建与测试

- `dotnet build BrimDeck.sln -c Release`：0 个警告，0 个错误。
- `BrimDeck.Tests`：1507 checks passed（基线 1481，新增 26 项）。
- `BrimDeck.MediaTests`：83 native media checks passed。
- 按要求没有启动 BrimDeck，管道、Windows 通知读取和图标写入没有做实机验证。
