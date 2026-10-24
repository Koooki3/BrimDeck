# Data and privacy

BrimDeck reads only the sign-in state and usage records that apps already keep. It never changes, renews or ends their sign-ins, never uploads conversations, and collects no usage data.

## Data read

| Tool | Quota | Tokens and cost |
| --- | --- | --- |
| Claude | Reads the Claude Code sign-in (`%USERPROFILE%\.claude\.credentials.json`), or the Claude desktop app's sign-in when that is unavailable, and asks Anthropic for the account usage. Each row can turn off "Fetch quota online": such a row reads no sign-in and sends nothing, and shows only the quota the Claude desktop app recorded on this PC (`plan-usage-history.json`) | Reads the local Claude Code session records (`projects\**\*.jsonl`); regular chats in the Claude desktop app are not included |
| Codex | Reads the sign-in in `%USERPROFILE%\.codex\auth.json` and asks OpenAI for the account usage; if that fails, shows the quota from the session records and marks it as possibly out of date | Reads the local session records (`sessions` and `archived_sessions`) |
| Antigravity | Reads from the local service of the running Antigravity desktop app (127.0.0.1 only) | Reads the local conversation databases of the desktop app and the CLI under `%USERPROFILE%\.gemini` |
| Cursor | Reads the Cursor sign-in from `%APPDATA%\Cursor\User\globalStorage\state.vscdb` in read-only mode and asks Cursor for the account usage | Asks Cursor for the account usage details, including usage on other devices |
| Grok Bot | Reads the Grok Bot desktop app's own sign-in (`%APPDATA%\Grok Bot\sand-secrets.json`) and asks Cursor for that account's weekly allowance. Each row can turn off "Fetch quota online": such a row reads no sign-in and sends nothing, and shows only the weekly allowance the Grok Bot desktop app cached on this PC (`%APPDATA%\Grok Bot\sand-client-persistence`) | Not available |
| ZCode | Reads the ZCode sign-in and asks for the Coding Plan, Start Plan and MCP daily limits | Reads the local request records (`%USERPROFILE%\.zcode\cli\db\db.sqlite`) |
| DeepSeek Harness | Reads the DeepSeek Harness sign-in (`%USERPROFILE%\.dsh\.credentials.yaml`) and asks the DeepSeek platform for the account balance and total cost; without a signed-in account, uses the API key saved there to ask for the balance | Reads the local session records (`%USERPROFILE%\.dsh\sessions`) |
| Kimi Code | Reads the Kimi Code sign-in (`kimi-code.json` in `%USERPROFILE%\.kimi-code\credentials\`, or `kimi-code-env-…json` for the international site) and its service address and site settings (`config.toml` and the optional `region` file), and asks the chosen site (api.kimi.com or api.kimi.ai) for the plan quota and membership level; never renews the sign-in or changes any Kimi Code file | Reads the local session records (`%USERPROFILE%\.kimi-code\sessions`) |
| Zhipu GLM, Z.ai GLM, DeepSeek, Kimi Code API, New API, Sub2API, Custom | Uses the key and site address entered in settings; a Kimi Code API key is sent only to api.kimi.com and api.kimi.ai, and its quota is not written to `kimi-quota.json`. The key goes only over HTTPS or to this PC; a New API, Sub2API or Custom source with "Allow HTTP" on also sends it over HTTP, in plain text | Not available |

- When `CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `ZCODE_HOME`, `DSH_HOME` or `KIMI_CODE_HOME` is set to an absolute path, that folder is read instead.
- These account APIs and local data formats are not stable public interfaces. When they cannot be read, the panel shows the actual status and never makes up numbers.

## Files stored on this PC

All files are stored in `%LOCALAPPDATA%\BrimDeck`:

| File | Contents |
| --- | --- |
| `settings.json` | Settings; no sign-in tokens or API keys |
| `secrets.dat` | API keys you entered, encrypted by Windows for the current user |
| `model-prices.json`, `manual-model-prices.json` | Updated model prices, and prices you added |
| `moonshot-prices.json` | Moonshot AI endpoint prices from OpenRouter (Kimi K3, K3.1 Pro and K3.1 Pro Highspeed), used for Kimi Code costs |
| `claude-quota.json` | The last Claude quota read online (shares, reset times and the plan name), so per-model quotas such as Fable still show after a restart while the account API is rate limited; no sign-in token; the account is recorded only as a one-way hash of its id (never the id itself), and the file is used only while the same account is signed in; not used once older than 7 days, deleted when the sign-in is rejected or removed |
| `kimi-quota.json` | The last Kimi Code quota read online (shares, reset times, extra usage and the membership level), so it still shows, also after a restart, while the sign-in has expired or the API is temporarily unavailable; no sign-in token or other account details; the account is recorded only as a one-way hash of its id together with the service address (never the id itself), and the file is used only while the same account on the same site is signed in; not used once older than 7 days, deleted when the sign-in is removed or the account has no Kimi Code plan |
| `update-prompt.txt`, `updates\` | The last version you were told about, and downloaded installers |
| `crash.log` | Error records; cleared when it exceeds 1 MB |
| `notices\notices.dat`, `notices\icons\` | With dynamic notices on and kept: the recent notices (24 hours and at most 500 by default), encrypted for the current user with Windows encryption; application icons (from Windows, no notice content). **Kept by default in the `notices` folder of BrimDeck's install folder** (opened to the current user only), and here only when the install folder cannot be written to; the folder can be changed in settings, and uninstalling with "Delete user data" removes it |
| `notices.log` | Dynamic notices' error records: error types, codes and code locations only, never titles or bodies; rotated past 256 KB |

## Network and security

- Connects only to: each tool's official APIs, sites you enter in settings, the OpenRouter model list and the endpoint lists of Kimi models (model prices), lyrics services (LRCLIB, NetEase Cloud Music, QQ Music), the cover addresses players provide, and GitHub Releases (updates).
- Sign-in tokens are used only in memory and never written to disk; the sign-in caches of the Claude and Grok Bot desktop apps are decrypted in memory with Windows encryption for the current user.
- Custom scripts run in a sandbox with no access to files, processes or .NET objects; see the limits in [Supported tools](ai-tools.md#custom-script).
- The sound behind the equalizer is analyzed only in memory and never saved.
- Dynamic notices (off by default) read only the current user's Windows notifications and use no network; the local channel accepts only the current user on this PC. WSL notices are off by default; an agent is installed in a distribution only once you switch it on. Time notices (alarms, mail, event reminders) are sorted on this PC by the application's id and the notice's text; no mailbox, calendar or Clock data is read, and the list of time sources in the settings only checks whether those applications are installed. See [Dynamic notices](notices.md#privacy).

## NetEase Cloud Music full control

- Full control needs NetEase Cloud Music to open a local debugging port. When enabled from BrimDeck, the port is 127.0.0.1:21631.
- While the port is open, other programs on this PC and web pages in a browser may use it to control NetEase Cloud Music and read its page content and sign-in state.
- Before connecting, BrimDeck checks that the port listens only on this PC and belongs to NetEase Cloud Music, but this cannot remove the risk of the port itself.
- Through this port, BrimDeck reads the song that is playing, its progress, the playback mode and whether the current song is liked, and has NetEase Cloud Music act only when you select a control. When you select the heart, NetEase Cloud Music itself adds the song to, or removes it from, the liked songs of your account. To show the heart, BrimDeck takes only two results, whether NetEase Cloud Music is signed in and whether the current song is liked; it neither reads nor keeps your account details or your other songs.
- The port stays open until NetEase Cloud Music is started again the normal way: quitting BrimDeck, or a full control connection that fails (BrimDeck then only reads what is playing), does not close it.
- Turn it on only in an environment you trust. Starting NetEase Cloud Music the normal way ends full control and closes the port.
