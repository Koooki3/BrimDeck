# 审查修复报告：ui 组

分支：`worktree-agent-a95124eb164e9ae5c`（基于 1f64d35）。最终提交见文末。

## 处理结果

### correctness-ui.md
| 条目 | 结果 | 说明 |
| --- | --- | --- |
| ui-1 日期弹窗首帧前关闭卡住 | 已修 | ModelDetails.ShowModelPopup：ContentRendered 只订阅一次，且 `!left && popup.IsVisible` 时才 Soft.Enter；离场后加 `Soft.Quick + 250ms` 兜底计时器，窗口仍在就直接 Close（Soft 本身未改）。 |
| ui-2 HoverZone.Opacity 争用 | 已修 | 提示遮盖改为 `_islandCovered` + `HoverZone.Visibility`，启动/关机继续只用 Opacity，两者独立、同时满足才显示；`wasCovered` 读自己的状态。启动期间（`BootHoldsPointer`）到达的提示先挂起，启动播完再 Present（结束/更新时同步挂起项）；`CanShowNotices` 排除 `_poweringOff`；PowerOff 开始时 `NoticeWindow.Vanish()` 立即收起提示；启动中不再触发 reveal 落地动画。 |
| ui-3 首个提示按 DPI 1 构建 | 已修 | 未显示时按目标显示器缩放 Dress；Show/Place 之后再 Dress（look 按 dpi 缓存，未变零成本）；处理 DpiChanged。 |
| ui-4 不抓鼠标的控件按压卡住 | 已修 | Frame 中若按压未松开且物理左键已抬起（GetAsyncKeyState，考虑左右键互换）则 LiftOff(false)；键盘按压、触摸/笔按压、预览不走此检查。 |
| ui-5 _pen2 未随 DPI 重建 | 已修 | DressPens 末尾 `_pen2 = null`。 |
| ui-6 AmbientGlow | 不在本组 | AmbientGlow 属 perf 组。 |
| ui-7 实时倒计时停住 | 已修 | ToastTexts 返回 `Live`，BuildToast 用同一次结果设置 `_liveLine`；删除 KeepLiveLine。 |
| ui-8 日期弹窗裁剪滞后 | 已修 | InteractionFx.ClipTo：裁剪矩形按去掉平移的布局位置计算，并共用选择器自身的 TranslateTransform（必要时预先放一个，Soft.Enter/Leave 会复用），每帧随滑动移动。 |
| ui-9 MonitorStrip | 不在本组 | |
| ui-10 键盘按下无按压反馈 | 部分已修 / 需要协调 | FxSurface 现在对焦点控件的 Space（按住期间）和 Enter（按下即完成）显示主题按压，覆盖面板、提示和日期弹窗。模板里 PressLayer 的键盘按压需要在 Feedback.cs（perf 组）里订阅 IsPressed，见“需要协调”。 |

### lifecycle-tests.md
| 条目 | 结果 | 说明 |
| --- | --- | --- |
| L12 隐藏路径关机无兜底 | 已修 | Sink 中启动 `total + 500ms` 的 DispatcherTimer，与 hop.Completed 共用同一个收尾 `Gone()`（只执行一次，且校验 `boot == _boot && _poweringOff`）。 |
| L13 退出动画期间的设置写入丢失 | 需要协调 | 修复在 App.xaml.cs（不在本组），补丁见下。 |
| T6 FxPreview DPI 检查不在 CI | 部分 | FxPreview 无参数运行即执行 100/125/150/175% 的全部像素与挂钩检查，失败时退出码 1，可直接当自动检查用：`dotnet build tests/BrimDeck.FxPreview -c Release` 后 `dotnet run --project tests/BrimDeck.FxPreview -c Release --no-build`（本次运行全部 PASS，退出码 0）。加进 CI 需改 `.github/workflows/ci.yml`，见“需要协调”。 |
| T7 产品代码中的测试钩子 | 已修 | FxSurface 与 ModelDatePicker 的时钟/调色板/系统主题/时长/条件/是否动画合并为 `PreviewHooks`（新文件 InteractionFx.Preview.cs，`Preview` 属性在应用里为 null），预览专用的只读成员也移到该文件；ModelDatePicker 的按钮访问器（DayButton/Cell/ApplyButton/CancelButton/PreviousButton/Field）全部删除，DatePreview 改为按无障碍名称查找。 |

### performance.md
| 条目 | 结果 | 说明 |
| --- | --- | --- |
| perf-7 提示卡片每次进度更新整树重建 | 已修（toast 部分） | NoticeWindow.Update：构建依据（图标、应用、类别、强调色、尾部类型、是否有摘要、尺寸、外观）不变时只改标题/摘要文字、计数、进度环和倒计时环；只有文字变化时才重新播报（UIA）。展开的详情卡片仍整卡重建（只在详情打开时发生），未改。 |
| perf-8 MarkAllViewed 二次重建 | 已修 | 回调中先 MarkAllViewed 再清除排队标志。 |
| perf-13 悬停静止时订阅全局 LayoutUpdated | 跳过 | 报告本身认为可接受；改为按控件订阅收益小、易漏掉滚动/缩放引起的位移。 |

### quality.md
| 条目 | 结果 | 说明 |
| --- | --- | --- |
| P1.1 Track/Animate/Set 两套、轨道字面量两份 | 已修 | 新增 `Tracks`（Timed/Animate/SetImmediate），MainWindow 与 NoticeWindow 共用，`Track` 统一为 `NoticeMotion.Track`。面板开合的轨道写进 NoticeMotion 的 `Panel*` 常量（唯一来源），提示的 Surface/ArriveTint/Return*/Landing 直接引用它们；`ArriveContent (.2,.85)` 与面板 `PanelOpenContent (.12,.8)` 取值不同，各自保留、并排定义。所有取值与原来一致（新测试逐项核对）。注：NoticeMotion.cs 位于 Core/Notices，按“同一处定义”的要求在此修改，只增加常量并把字面量改为引用，未改任何取值或 Length。 |
| P1.2 自建逐帧钩子 | 已修（本组文件） | FxSurface 与 ModelDatePicker 改用 `Motion.Start`（只调用，未改 MonitorDraw.cs），静止后 step 返回 false 自动摘钩。MusicMarquee 不在本组。 |
| P1.8 EnsureDressed 重复计算时长 | 已修 | 方法末尾只算一次；三处 `?? 360` 收拢为 `AnimationLength`。`DeckSettings.DefaultAnimationDuration` 需改 Settings.cs，未做。 |
| P2 死代码/残留 | 已修 | FxShapes.Clip 改由 FxMark 使用（不再只有测试用）；删除 CancelButton、`_dressedPalette`、`_light`（亮边直接用白色笔）、InteractionFx 关于“不透明卡片”的历史说明；ThemeMotion 的 `Trail` 改名 `ParticleStrength`（文件键仍为 `trail`，保留只读别名 `Trail` 供 PanelThemes.Template.cs 与 ThemeMotionTests 读取）；ThemeMotion 顶部注释改为“只有描线呼吸”。AmbientFrame.Glow 通道的删除涉及 AmbientGlow，未做。 |
| P3 NoticeBadges.Wpf | 已修 | 删除，改用 PanelTheme.ToColor（tests/BrimDeck.GlyphPreview/Program.cs 一行随之修改）。 |
| P5 NoticeWindow.TextButton 悬停 | 已修 | 主题悬停标记会画在这些按钮上，若再加 Feedback.Fade 会有两层悬停，因此不加；改为只有在 InteractionFx 不画悬停（动画关闭、高对比度、主题无 hover）时才换底色，任何时候只有一种悬停效果。新增 `InteractionFx.AnswersHover(window)`。 |
| 文档 themes.md 缺 motion | 已修 | docs/en-US 与 docs/zh-CN 的 themes.md 新增“动效（motion）”一节：ambient 各项、hover/press/focus/particles/trail/pace/steps/color/secondary/highlight 的取值、范围、旧名、安全上限、何时不动、system 主题的替换规则，与 theme-template.json 一致；模板里 breathe 的注释同步为只有描线。 |

## 需要协调
1. **L13（App.xaml.cs）**：`OnExit` 中在 `FinishWriting()` 之前加 `if (_save.IsEnabled) FlushSettings();`，并把 `Notices?.Dispose()` 移到这次 flush 之前，使退出动画期间 `QueueSave()` 的写入（NoticeLastCleared、过期的 NoticesPausedUntil）落盘。
2. **ui-10（Feedback.cs，perf 组）**：订阅 `ButtonBase.IsPressedProperty` 变化（DependencyPropertyDescriptor.AddValueChanged，或 KeyDown/KeyUp 时也调用 Update），使模板 PressLayer 在键盘按下时显示。
3. **T6（.github/workflows/ci.yml）**：在 windows 作业中加一步 `dotnet build tests/BrimDeck.FxPreview -c Release` + `dotnet run --project tests/BrimDeck.FxPreview -c Release --no-build`（无参数只做检查、不写 PNG，失败退出码 1）。
4. **Trail 别名**：PanelThemes.Template.cs（`motion.Trail`）与 tests/BrimDeck.Tests/ThemeMotionTests.cs（`Trail:` 属性模式）改读 `ParticleStrength` 后，可删除 ThemeMotion 上的 `Trail` 别名。
5. **共享文件**：tests/BrimDeck.Tests/Program.cs 增加了一行 `AuditUiTests.Run(Check);`；src/BrimDeck.Core/Notices/NoticeMotion.cs 增加了 Panel* 常量（见 P1.1），合并时注意与 core 组的改动。

## 新增测试
- tests/BrimDeck.Tests/AuditUiTests.cs（6 项）：提示的开合轨道与面板的 Panel* 常量相同；面板各轨道取值与原字面量一致；ArriveContent 保留自己的取值；ParticleStrength 与文件键 trail 一致、system 外观没有粒子。
- WPF 相关修复（ui-2/3/4/5/7/8/10、L12、perf-7）依赖窗口与渲染，没有单元测试；FxPreview 的全部检查（含 125/150/175% 像素对齐、关闭动画/高对比度不挂帧、Reset/Detach 后不挂帧）与日期选择器预览均已重新运行通过。

## 构建与测试
- `dotnet build BrimDeck.sln -c Release`：0 警告 0 错误。
- `dotnet run --project tests/BrimDeck.Tests -c Release --no-build`：1487 项通过（基线 1481 + 新增 6）。
- `dotnet run --project tests/BrimDeck.MediaTests -c Release --no-build`：83 项通过。
- `dotnet run --project tests/BrimDeck.FxPreview -c Release --no-build`：全部 PASS，退出码 0；`-- --date <folder>` 三个主题的日期选择器预览生成正常、检查通过。
- 未启动 BrimDeck，未实机验证动画观感。
