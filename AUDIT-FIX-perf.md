# 审查修复：perf 组

分支：`worktree-agent-a325c517be530765a`（从 `1f64d35` 开始）。提交见文末。

## 条目处理结果

| 条目 | 结果 | 说明 |
|---|---|---|
| perf-1 环境光每 tick 重新模糊阴影 | 已修 | `MainWindow.xaml`：`IslandShadow` 外包 `ShadowHost`（只挂 `BitmapCache`，效果仍在子元素上）。`MainWindow.Motion.cs` 的 `FitShadowCache` 按显示缩放设 `RenderAtScale`，并让 `ShadowBounds`（透明矩形）把缓存边界外扩到整设备像素（`ceil((blur+depth+2)·scale)/scale`）。岛尺寸变化时暂时去掉缓存，尺寸静止 250 ms 后恢复（缓存在持续缩放时更贵，见测量）。自适应 tick **未做**：赛博朋克的 drift 每个收起 tick 都移动 0.006 宽（阈值 0.0015），每个 tick 都必须提交，放慢只会让漂移步子变粗（可见变化）；没有提交的 tick 本来就不渲染，省不下东西。 |
| perf-1 第 3 点（扫描线 Visibility） | 已修 | `_sweep` 常驻 Visible，只改 Opacity。 |
| perf-2 收起时窗口是页面尺寸 | 跳过（附数据） | 离屏分层窗口 760×256 与 300×70 跑同样的环境光：无缓存 2.31% / 3.00%，有缓存 2.25% / 2.08%（各 3 轮平均，噪声约 ±0.6%）。没有可测的收益，且缩窗牵涉 `Place()`/DPI/悬停带，风险大于收益。 |
| perf-3 MusicMarquee 静止期与高刷 | 已修 | 改用 `Motion.Start`（q-P1.2，同一刷新节流：≤180 Hz 每帧、更高隔帧；按 `Motion.FrameTime` 即 RenderingTime 推进，和以前一样）。首尾 1.5 s 静止期退订，`PromptTimer` 在静止结束前 0.1 s 唤醒；`MarqueeScroll.Wait()` 让唤醒后的第一步覆盖剩余静止时间，位置与逐帧绘制逐帧相同（有测试）。**没有**把 144 Hz 再降到 72/60：`Motion` 的注释记录了 144 Hz 隔帧会让三成步子不均，且用户要求动效与监控页一致；按设备像素取整写 `_shift.X` 会改变 Animated 文本的亚像素平滑，属可见变化，也未做。 |
| perf-4 显示器关闭未锁屏 | 已修 | `MotionEnvironment` 用隐藏的 `HwndSource` 注册 `GUID_SESSION_DISPLAY_STATUS`（不是 CONSOLE：远程桌面会话不会因本机显示器睡眠被误判为关闭），`DisplayOff` 进 `MotionConditions`/`MotionGate.Ambient`；`MainWindow.EnvironmentChanged` 同时更新音乐（滚动、均衡器节拍、`PresentMedia` 音频电平、配额环）与读数需求（页面和收起行）。注册后系统会立刻发一次当前状态（离线验证：收到 value=1）。显示器真正熄灭的路径无法离线验证。 |
| perf-5 `_stripWanted` | 已修 | 删掉 `_stripWanted`；需求改为 `MonitorStrip.Showing`（有读数被接纳或仍在淡出）。淡出结束后下一次采样即释放需求。代价：读数放不下超过 10 s（`SystemSampler.Linger`）后再放得下时，最初约 0.3 s（`PrimeMilliseconds`）显示“—”，与淡入重叠。这是总管指定的修法。 |
| perf-10 MonitorStrip 每次采样分配 | 已修 | `FigureText` 用 span + `Dictionary.GetAlternateLookup<ReadOnlySpan<char>>` 缓存非数字部分宽度（实测每次调用 3445 B → 0 B）；`Presence()` 去掉 LINQ；轨道笔、简略图标笔、箭头笔按实例缓存。`Readings()` 的 List 保留（调用方持有返回值）。 |
| perf-14 QuotaCarousel 关闭动画仍 60 fps | 已修 | 关闭动画时不再建离散关键帧 Storyboard，改为在两个跳变点之间用 `PromptTimer` 等待（`Turn`），`Elapsed` 交接保持。开动画路径不变。 |
| ui-6 Apply 用 Halt | 已修 | `Apply()` 先 `Stop()`；`Halt` 合并进 `Stop`。 |
| ui-9 >28 线程核心柱溢出 | 已修 | `CompactSpacing.CoreColumns/CoreRange`：放不下时相邻核心合并为一列，显示其中最忙的核心（颜色取该核心的组）。放得下时与原来完全相同。 |
| life-L14 RDP 后永远锁定 | 已修 | 启动时、Console/RemoteConnect 时、显示器重新点亮时用 WTS 查询锁屏状态。 |
| q-P1.2 自造逐帧时钟 | 已修（本组范围） | MusicMarquee 改用 `Motion.Start`；`MotionTicker` 保留（低频环境光），与 `Motion` 同放 `Motion.cs`。ModelDatePicker / InteractionFx 不在本组。 |
| q-P1.3 一条“能不能动”规则 | 已修 | `MotionConditions.Allowed(animations, system, highContrast)` 是唯一规则；`Soft.Enabled` 用它（**加入了高对比度**），`MotionGate` 用 `MotionAllowed`；`Motion.Enabled` 改为 `Soft.Enabled`；`MainWindow.Music.cs`/`MonitorPanel.cs` 的手写检查全部换成 `Soft.Enabled`。影响：高对比度下所有经 `Soft.Enabled` 的动画（含其他组文件里的调用）都不再播放，这是审查的建议。 |
| q-P1.4 四份 glide + 两份截图 | 已修 | `Soft.Glide`（double，含容差）与 `Soft.Glide(SolidColorBrush, Color)`；`PageHandover.Glide` 改为转调；`PageHandover.Snapshot` 给 `Capture` 和 `PopupMotion.Leave` 共用。`MusicControlButton.Animate` 暂留一行转调，供 `MainWindow.CoverFocus.cs` 使用（见协调）。 |
| q-P1.6 动画时长常量 | 部分（本组范围） | `AnimationLength.Default/Min/Max/Clamp/Ms` 放在 `ThemeMotion.cs`，`MotionTiming.From` 已改用；其余散落处不在本组（见协调）。 |
| q-P1.6 MonitorStrip:168 舍入 | 已修 | `MidpointRounding.AwayFromZero`。 |
| q-P2 AmbientGlow 死代码与旧注释 | 已修 | 删 `_shadow/_glow/_shadowBase`、构造参数 `shadow`、`AmbientFrame.Glow`（`PeakFlashes` 少一个通道）、`MotionTicker.Ticks/Running`、`AmbientGlow.Running`；注释改为“只有描边呼吸”。 |
| perf 报告 SystemParameterChanged 未经 dispatcher | 已修 | 改为 `BeginInvoke`。 |
| ui-10（总管追加）键盘按压无反馈 | 已修 | `Feedback` 订阅 KeyDown/KeyUp（handledEventsToo）与 LostKeyboardFocus，按下空格时 `IsPressed` 为真即显示 PressLayer；仍只有一层，按同一状态计算，鼠标路径不变。Enter 本来就不经过 IsPressed（立即 Click），与旧模板一致。WPF 交互无法在 Core 测试里覆盖，未加测试。 |

## 测量（离线，方法与数据）

方法：scratchpad 里的探针程序（不入库）按 MainWindow 的结构搭出赛博朋克岛：`IslandBorder` + `DropShadowEffect(#FF2A6D, blur 18, depth 0, .6)` + 漂移反射渐变描边，`AmbientMotion(6.5,.22,11,16,.07,41,.1)`，66 ms Render 优先级计时器，阈值与 AmbientGlow 相同。窗口为 `AllowsTransparency` 分层窗口，放在屏幕上但不可见（Window.Opacity=1/255，WS_EX_TRANSPARENT|NOACTIVATE），显示器约 144 Hz（帧间隔中位数 6.9 ms），100% 缩放。CPU = 进程 TotalProcessorTime / 墙钟，3 s 预热后测 20–25 s。视觉对比用非分层窗口 + `PrintWindow(PW_RENDERFULLCONTENT)` 截硬件渲染结果，逐像素比较。

视觉（缓存 vs 直接绘制，t=3.3 s 与 40.9 s 两帧）：
- 岛放在整设备像素上、`ShadowBounds` 外扩到整设备像素：100%、125%、150%、175%、200% 全部 **0 个像素不同**。
- 不外扩时：125% 有约 5300 个边缘像素差（最大 44/255），150%/175% 在岛位置非整像素时也有差异——这就是 `ShadowBounds` 的由来。BrimDeck 窗口开启了 UseLayoutRounding，岛位置本来就在整设备像素上。

CPU（% 单核）：

| 场景 | 无缓存 | 有缓存 |
|---|---|---|
| 环境光 15 fps，窗口 760×256 | 1.87 / 2.50 / 2.56 | 2.38 / 2.87 / 1.50 |
| 环境光 15 fps，窗口 300×70 | 3.37 / 2.75 / 2.88 | 1.81 / 2.69 / 1.75 |
| 岛内文字每帧移动（模拟滚动标题，无静止期） | 17.75 / 17.13 / 15.44 | 11.94 / 11.50 / 9.19 |
| 岛连续缩放（模拟展开/收起） | 23.75 / 21.56 / 15.61 | 28.20 / 22.11 / 13.43 |

滚动标题带首尾静止期的完整周期（100 DIP 溢出：1.5 s + 4.2 s 滑动 + 1.5 s），阴影已缓存：

| 场景 | 第 1 轮 | 第 2 轮 | 第 3 轮 | 平均 |
|---|---|---|---|---|
| 静止期也每帧挂钩（修复前） | 9.06 | 6.17 | 6.25 | 7.16 |
| 静止期退订（修复后） | 7.42 | 6.02 | 4.61 | 6.02 |
| 修复前且无阴影缓存（对照） | 9.69 | 6.64 | 6.87 | 7.73 |

注意：第 2、3 轮期间本机显示器的帧间隔从 6.9 ms 变成 13.9 ms（动态刷新率），所以各轮之间不能直接比，只比同一轮内的配置。

结论：在本机（100%）上环境光本身的重模糊并不贵（约 2%，噪声内），真正的大头是**岛内任何逐帧变化都会让阴影重新模糊**：滚动标题每帧移动时缓存省下约 35%（16.8% → 10.9%）；带静止期的真实周期里，退订静止期再省约 16%。持续缩放时缓存的结果不稳定（第一轮更贵 19%，第三轮更省），保守起见尺寸变化期间去掉缓存。

其他：
- `MonitorStrip.FigureText`：旧写法每次调用平均 1208 字节（LINQ 迭代器、拼接字符串、FormattedText），新写法 0 字节（GC.GetAllocatedBytesForCurrentThread，7 种典型读数各 1000 次）。每秒每个读数一次。
- MarqueeScroll / AmbientMotion.At / MotionTicker.Tick：每帧零分配（值类型与预分配列表）。
- 静止期退订：100 DIP 溢出的标题一个周期 3 s 静止 + 4.2 s 滑动，约 42% 的帧不再请求；空 Rendering 订阅本身约 0.6% 单核（本机 144 Hz）。
- 显示器状态注册：探针用同样的隐藏 HwndSource 注册 `GUID_SESSION_DISPLAY_STATUS`，立即收到 `PBT_POWERSETTINGCHANGE`，数据长度 4、值 1（亮）。

## 新增测试

`tests/BrimDeck.Tests/AuditPerfTests.cs`（24 项，已在 `Program.cs` 注册）：MarqueeScroll 等待静止期后与逐帧推进结果一致、迟到唤醒仍限步、Pause 不计时、末端静止计数；核心柱在 8–256 核下列宽 ≥1 DIP、不超宽、覆盖所有核心；DisplayOff 只停环境光；统一的动画规则；AnimationLength 与设置默认值一致、MotionTiming 夹取一致。

## 构建与测试

- `dotnet build BrimDeck.sln -c Release`：0 警告 0 错误。
- `BrimDeck.Tests`：1505 项全部通过（基线 1481 + 新增 24）。
- `BrimDeck.MediaTests`：83 项全部通过。

## 需要协调

1. `tests/BrimDeck.Tests/ThemeMotionTests.cs`（非本组文件）：删除 `AmbientFrame.Glow` 后必须把第 98 行模式里的 `Glow: 1, ` 去掉，否则编译不过。已做了这一处最小改动。
2. `tests/BrimDeck.Tests/Program.cs`：加了一行 `AuditPerfTests.Run(Check);`，其他组可能也在同处加行，合并时注意。
3. `Native/NoticeQuiet.cs`：`SessionLocked()` 与 `MotionEnvironment.SessionLocked()`（internal）现在是同一段 WTS 查询，建议 NoticeQuiet 改为调用后者并删除自己的副本。
4. q-P1.6 其余位置改用 `AnimationLength`：`Settings.cs:242/426`（默认值和 Clamp）、`InteractionFx.cs:132/201/209`、`InteractionFx.Particles.cs:45`、`ModelDatePicker.cs:210`、`DatePickerMotion.cs:19`（含局部 `Ms`）、`NoticeMotion.cs:42`。
5. `MainWindow.CoverFocus.cs:23-24` 改用 `Soft.Glide(...)`，然后删除 `MusicControlButton.Animate` 转调。
6. q-P1.3 手写的 `Settings.Animations && SystemParameters.ClientAreaAnimation` 还在其他组文件里：`MainWindow.CoverFocus.cs:22`、`MainWindow.Hotkeys.cs:59`、`MainWindow.xaml.cs:517`、`MainWindow.SourceMenu.cs:53/70`、`UsageSynchronization.cs:85`、`SettingsApplications.cs:83` 等，建议改为 `Soft.Enabled`。
7. `Templates/theme-template.json:104` 注释仍写“the rim and glow dim”，应改为只有描边呼吸。
8. `MainWindow.Motion.cs` 不在两组的清单里；因 AmbientGlow 构造参数与阴影缓存必须改它，本组改了。

## 提交

- a9bf02b 环境光：阴影改为缓存位图、显示器关闭时停止、清理死代码
- 7508ceb 滚动标题、配额环与系统读数：去掉空转帧和看不见的采样
- d834e4d 动效规则统一：高对比度不动、滑动与截图各一份、键盘按压反馈
- 21d14f5 补充 perf 组测试
- 最后一条：本报告（见 git log）
