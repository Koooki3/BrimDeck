# 支持的工具

AI 用量页的每一列可以在"设置 › 配额与统计"中分别配置：

- **配额来源**：决定这一列显示的套餐、配额使用率与重置倒计时。
- **统计应用**：决定这一列底部"今日"与"七日"的 Token 用量、费用估算，以及模型明细。
- **显示名称与颜色**：可以自定义名称、主题色、预警色与告警色。

同一个工具可以出现在多列中，读取时只请求一次，合计也不会重复计算。

| 工具 | 配额 | Token 用量与费用 | 需要的准备 |
| --- | :---: | :---: | --- |
| Claude | ✓ | ✓ | 在本机登录 Claude Code 或 Claude 桌面版。用量统计来自 Claude Code |
| Codex | ✓ | ✓ | 在本机登录 Codex |
| Cursor | ✓ | ✓ | 在本机登录 Cursor |
| Grok Bot | ✓ | | 在本机登录 Grok Bot 桌面版 |
| Antigravity | ✓ | ✓ | 读取配额时需要 Antigravity 桌面版正在运行 |
| ZCode | ✓ | ✓ | 在本机登录 ZCode |
| DeepSeek Harness | ✓ | ✓ | 在 DeepSeek Harness 桌面版或本地 Web 版中登录 DeepSeek 账户，或在其中保存 API 密钥 |
| 智谱 GLM、Z.ai GLM | ✓ | | 在设置中填写 API 密钥 |
| DeepSeek | ✓ | | 在设置中填写 API 密钥 |
| New API、Sub2API 中转站 | ✓ | | 在设置中填写站点地址和密钥 |
| 自定义 | ✓ | | 编写一段 JavaScript 脚本 |

手动填写的来源与 Grok Bot 没有本地用量记录，"今日"和"七日"可以另选一个工具统计。

## Claude 与 Grok Bot 的"联网获取额度"

配额来源为 Claude 或 Grok Bot 的行可以展开，每一行可以单独开关"联网获取额度"（默认开启）：

- **开启**：使用该应用在本机的登录，联网读取账户额度。读取失败时，显示最近一次成功读取的额度或桌面版在本机记录的额度，并标明时间。
- **关闭**：不读取登录，也不联网，只显示桌面版在本机记录的最新额度。这些数据标为旧数据，不触发额度提醒。

## Grok Bot

Grok Bot 一行只显示 Grok Bot 桌面版登录账户的每周额度：已用比例、重置时间与套餐名称，不显示 Cursor 的其他配额与按量计费金额。

- **联网**：使用 Grok Bot 桌面版自己的登录读取，不使用 Cursor 编辑器的登录，也不续期登录。登录失效时，请打开 Grok Bot 桌面版。
- **不联网**：显示 Grok Bot 桌面版在本机缓存的每周额度，时间为桌面版上次读取的时间。
- 账户使用企业共享额度时，不显示已用比例。

## DeepSeek Harness

DeepSeek Harness 桌面版与本地 Web 版（`npx @deepseek-ai/dsh web`）共用同一个数据目录，两者任选其一登录即可。

- **配额**：在 DeepSeek Harness 中登录了 DeepSeek 账户时，显示该账户的余额、赠送余额与累计消费金额，与 DeepSeek 开放平台网页显示的数字一致，均按整个账户计算。未登录账户时，使用在 DeepSeek Harness 模型页面保存的 API 密钥，只能显示余额与赠送余额。登录失效时，请在 DeepSeek Harness 中重新登录。
- **Token 用量与费用**：读取本机的 DeepSeek Harness 会话记录，包含桌面版与本地 Web 版。费用按模型单价估算；单价表中没有的模型不计算费用，可以在设置中手动添加单价。

## 手动填写的来源

在"配额来源"中选择智谱 GLM、Z.ai GLM、DeepSeek、New API、Sub2API 或自定义后，该行会展开，填写所需内容即可。

- **密钥**：使用 Windows 的加密功能保存在本机，不写入设置文件。删除该列或更换配额来源时，密钥一并删除。
- **站点地址**：填写站点首页地址即可。末尾的 `/v1`，以及直接粘贴的控制台页面路径，会自动去掉。
- **测试连接**：立即读取一次，显示读取结果与耗时。
- **读取失败时**：保留最近一次成功读取的数据，名称的悬停提示中会说明失败原因。

### 智谱 GLM 与 Z.ai GLM

填写 GLM Coding Plan 的 API 密钥。显示 5 小时与每周额度，以及工具调用次数；积分制套餐显示已用积分与总积分。不返回额度的团队套餐暂不支持。

### DeepSeek

填写在 DeepSeek 开放平台创建的 API 密钥。显示账户余额与其中的赠送余额，金额按账户的货币（人民币或美元）显示。余额不足以调用 API 时，状态中会提示。

### New API

填写站点地址与 API 密钥。按站点和密钥的情况，显示账户余额或密钥余额、今日扣费，以及无上限密钥的累计消费。金额按站点设置的货币与汇率换算。

### Sub2API

填写站点地址与 API 密钥。钱包模式显示账户余额与今日扣费，订阅模式显示每日、每周、每月额度，限额密钥显示各时间段额度与密钥余额。名称旁的标签显示倍率或套餐名。

选错 New API 与 Sub2API 时，读取结果会提示改用另一种。

### 自定义脚本

选择"自定义"后，代码框会预先填入可直接运行的模板。脚本需定义 `fetchUsage(ctx)`，返回要显示的配额数组：

```javascript
async function fetchUsage(ctx) {
  const res = await ctx.http.get(ctx.site + "/api/usage", {
    headers: { Authorization: "Bearer " + ctx.secrets.key }
  });
  if (res.status !== 200) throw new Error("读取失败：HTTP " + res.status);
  const data = res.json();
  return [
    { title: "本月", percent: data.used / data.limit * 100, value: "$" + data.used.toFixed(2), suffix: "已用", resetAt: data.resetText }
  ];
}
```

`ctx` 提供：

| 成员 | 说明 |
| --- | --- |
| `ctx.site`、`ctx.secrets.key` | 填写的站点地址与 API 密钥，未填写时为空字符串 |
| `ctx.http.get(url, options)`、`ctx.http.post(url, options)` | 发送请求。`options` 可包含 `headers`、`timeout`（毫秒）与 `body`（仅 POST）。返回 `{ status, headers, text(), json() }` |
| `ctx.log(text)` | 写一行日志，在"测试连接"中显示 |
| `ctx.language`、`ctx.now` | 界面语言（`"zh-CN"` 或 `"en-US"`）与本次读取的时间 |

返回数组中的每一项是一条配额，按顺序显示：

| 字段 | 说明 |
| --- | --- |
| `title` | 必填。配额名称 |
| `percent` | 选填，0–100。已用比例，决定进度条长度与颜色 |
| `value`、`suffix` | 选填。大号数字及其右侧的小字，例如 `"$12.30"` 与 `"已用"` |
| `note` | 选填。与名称同一行，靠右显示 |
| `resetAt` | 选填。与数字同一行，靠右显示，直接显示原文，例如 `"2 时 13 分后"` |

不符合要求的项会被跳过，并在读取结果中说明原因。脚本抛出的错误会原样显示。

脚本在沙盒中运行，无法访问文件、进程或 .NET 对象。每次读取最长 10 秒，最多发送 16 个请求，请求与响应各不超过 1 MB。
