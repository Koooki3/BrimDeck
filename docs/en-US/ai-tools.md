# Supported tools

Each column of the AI usage page can be set up separately in Settings › Quotas and usage:

- **Quota source**: the plan, quota usage and reset countdowns shown in the column.
- **Usage app**: the tokens and estimated cost for Today and Week (the last 7 days) at the bottom of the column, and its model details.
- **Display name and colors**: a custom name, and the theme, warning and critical colors.

The same tool can appear in several columns. It is read only once, and totals are never counted twice.

| Tool | Quota | Tokens and cost | What you need |
| --- | :---: | :---: | --- |
| Claude | ✓ | ✓ | Sign in to Claude Code or the Claude desktop app on this PC. Usage comes from Claude Code |
| Codex | ✓ | ✓ | Sign in to Codex on this PC |
| Cursor | ✓ | ✓ | Sign in to Cursor on this PC |
| Grok Bot | ✓ | | Sign in to the Grok Bot desktop app on this PC |
| Antigravity | ✓ | ✓ | The Antigravity desktop app must be running for its quota to be read |
| ZCode | ✓ | ✓ | Sign in to ZCode on this PC |
| DeepSeek Harness | ✓ | ✓ | Sign in to a DeepSeek account in the DeepSeek Harness desktop app or local web app, or save an API key there |
| Zhipu GLM, Z.ai GLM | ✓ | | Enter an API key in settings |
| DeepSeek | ✓ | | Enter an API key in settings |
| New API, Sub2API relay sites | ✓ | | Enter the site address and key in settings |
| Custom | ✓ | | Write a short JavaScript script |

Sources you enter by hand and Grok Bot have no local usage records; choose another tool as the usage app for Today and Week.

## Fetch quota online for Claude and Grok Bot

Rows whose quota source is Claude or Grok Bot can be expanded, and each row has its own "Fetch quota online" switch (on by default):

- **On**: the app's sign-in on this PC is used to read the account quota online. If a read fails, the last successful reading or the quota the desktop app recorded on this PC is shown, with its time.
- **Off**: no sign-in is read and nothing is sent; only the latest quota the desktop app recorded on this PC is shown. This data is marked as old and does not trigger quota alerts.

## Grok Bot

A Grok Bot row shows only the weekly allowance of the account signed in to the Grok Bot desktop app: the share used, the reset time and the plan name. Other Cursor quotas and on-demand spending are not shown.

- **Online**: read with the Grok Bot desktop app's own sign-in, not the Cursor editor's, and the sign-in is never renewed. If the sign-in has expired, open the Grok Bot desktop app.
- **Offline**: shows the weekly allowance the Grok Bot desktop app cached on this PC, timed when the desktop app last read it.
- When the account uses a pooled enterprise allowance, the share used is not shown.

## DeepSeek Harness

The DeepSeek Harness desktop app and local web app (`npx @deepseek-ai/dsh web`) share one data folder, so signing in to either is enough.

- **Quota**: when a DeepSeek account is signed in to DeepSeek Harness, shows that account's balance, granted balance and total cost, the same figures as the DeepSeek platform's web console, all for the whole account. Without a signed-in account, the API key saved on the DeepSeek Harness Models page is used, and only the balance and the granted balance can be shown. If the sign-in expires, sign in again in DeepSeek Harness.
- **Tokens and cost**: read from the DeepSeek Harness session records on this PC, covering the desktop app and the local web app. Costs are estimated from model prices; models missing from the price list are not priced, and you can add their prices in settings.

## Sources entered by hand

Choose Zhipu GLM, Z.ai GLM, DeepSeek, New API, Sub2API or Custom as the quota source, and the row expands so you can fill in what it needs.

- **Key**: stored on this PC, encrypted by Windows, and never written to the settings file. It is deleted when you remove the column or change its quota source.
- **Site address**: the home page address is enough. A trailing `/v1`, or the path of a console page you paste, is removed automatically.
- **Test connection**: reads the source once and shows the result and how long it took.
- **If a read fails**: the last successful reading stays on screen, and the tooltip on the name explains why.

### Zhipu GLM and Z.ai GLM

Enter the API key of your GLM Coding Plan. Shows the 5-hour and weekly limits and tool call counts; credit-based plans show credits used and the total. Team plans whose API returns no limits are not supported yet.

### DeepSeek

Enter an API key created on the DeepSeek platform. Shows the account balance and the granted part of it, in the account's currency (CNY or USD). When the balance is too low to call the API, the status says so.

### New API

Enter the site address and API key. Depending on the site and the key, shows the account balance or key balance, today's charges, and the running total of keys without a limit. Amounts are converted with the currency and exchange rate the site uses.

### Sub2API

Enter the site address and API key. Wallet mode shows the account balance and today's charges; subscription mode shows daily, weekly and monthly limits; limited keys show their limits for each period and the key balance. The tag next to the name shows the rate multiplier or the plan name.

If you pick New API for a Sub2API site, or the other way round, the result suggests the right one.

### Custom script

When you choose Custom, the code box is filled with a template that runs as is. The script defines `fetchUsage(ctx)` and returns the quotas to show:

```javascript
async function fetchUsage(ctx) {
  const res = await ctx.http.get(ctx.site + "/api/usage", {
    headers: { Authorization: "Bearer " + ctx.secrets.key }
  });
  if (res.status !== 200) throw new Error("Read failed: HTTP " + res.status);
  const data = res.json();
  return [
    { title: "This month", percent: data.used / data.limit * 100, value: "$" + data.used.toFixed(2), suffix: "used", resetAt: data.resetText }
  ];
}
```

`ctx` provides:

| Member | Description |
| --- | --- |
| `ctx.site`, `ctx.secrets.key` | The site address and API key you entered; empty strings when not filled in |
| `ctx.http.get(url, options)`, `ctx.http.post(url, options)` | Sends a request. `options` may contain `headers`, `timeout` (milliseconds) and `body` (POST only). Returns `{ status, headers, text(), json() }` |
| `ctx.log(text)` | Writes a log line shown by Test connection |
| `ctx.language`, `ctx.now` | The interface language (`"zh-CN"` or `"en-US"`) and the time of this read |

Each item of the returned array is one quota, shown in order:

| Field | Description |
| --- | --- |
| `title` | Required. The quota name |
| `percent` | Optional, 0–100. The share used; sets the length and color of the bar |
| `value`, `suffix` | Optional. The large figure and the small text to its right, such as `"$12.30"` and `"used"` |
| `note` | Optional. Shown at the right of the name's line |
| `resetAt` | Optional. Shown at the right of the figure's line, as written, such as `"in 2h 13m"` |

Items that do not meet these rules are skipped, and the result explains why. Errors thrown by the script are shown as they are.

Scripts run in a sandbox with no access to files, processes or .NET objects. Each read may take up to 10 seconds and send up to 16 requests, with requests and responses of up to 1 MB each.
