<div align="center">

<a href="README.md"><img src="https://img.shields.io/badge/English-0A84FF?style=for-the-badge" alt="English"></a>
<a href="README.zh-CN.md"><img src="https://img.shields.io/badge/%E7%AE%80%E4%BD%93%E4%B8%AD%E6%96%87-3A3A3C?style=for-the-badge" alt="简体中文"></a>

<img src="docs/icon.png" width="96" alt="BrimDeck icon">

# BrimDeck - A Dynamic Island for Windows

BrimDeck is a Dynamic Island for Windows 11 that brings your AI coding tool quotas, usage and the music you are playing together at the top of the screen.<br>
It stays collapsed at the top of the screen and opens only when you need it.<br>
A native Windows app built with C# and WPF, designed for performance and smoothness.

[![Download](https://img.shields.io/badge/Download-Windows%2011%20x64-9CB9FF?style=flat-square)](https://github.com/MoFeng2223/BrimDeck/releases/latest)
[![Latest release](https://img.shields.io/github/v/release/MoFeng2223/BrimDeck?style=flat-square&label=Latest&color=98DBB0)](https://github.com/MoFeng2223/BrimDeck/releases/latest)
[![License](https://img.shields.io/badge/License-Apache%202.0-E5A385?style=flat-square)](LICENSE)
![Interface languages](https://img.shields.io/badge/Interface-English%20%7C%20%E7%AE%80%E4%BD%93%E4%B8%AD%E6%96%87-B9A8FF?style=flat-square)

<p><a href="#features">Features</a> · <a href="#install">Install</a> · <a href="#quick-start">Quick start</a> · <a href="#privacy">Privacy</a> · <a href="#faq">FAQ</a> · <a href="#contributing">Contributing</a> · <a href="README.zh-CN.md">简体中文</a></p>

<img src="docs/en-US/images/hero.png" width="100%" alt="BrimDeck opens from the notch at the top of the screen into the AI usage panel, switches to the music page, then collapses">

</div>

## Why BrimDeck

- **All quotas in one place**: Quotas for Claude, Codex, Cursor, Antigravity and more are shown in a single panel, so you no longer open each website, command line or app to check them.
- **Quota checks made simple**: BrimDeck stays collapsed at the top of the screen. Move the pointer over it to open it, and move away to close it.
- **Media control**: Preview and control the song that is playing inline; the cover, equalizer and lyrics follow the music.
- **No interruptions in full screen**: Hides automatically while you play full-screen games or watch full-screen video, so it never opens by accident. Adjustable in settings.
- **Works out of the box**: Detects Claude, Codex, Cursor and other tools already signed in on this PC. No keys to enter and no extra sign-in.
- **Native Windows**: Built on .NET and WPF to deliver smooth animation with low resource use.

## Features

### Collapsed styles

BrimDeck sits at the top center of the screen and comes in three styles: Notch (default), Capsule and Indicator.

<table>
  <tr>
    <td align="center"><img src="docs/en-US/images/compact-notch.png" width="279" alt="Notch"><br>Notch (default)</td>
    <td align="center"><img src="docs/en-US/images/compact-capsule.png" width="258" alt="Capsule"><br>Capsule</td>
    <td align="center"><img src="docs/en-US/images/compact-line.png" width="128" alt="Indicator"><br>Indicator</td>
  </tr>
</table>

- Notch and Capsule show the highest quota usage of each tool as a ring, and preview the music that is playing with its cover, an equalizer, and the title or the current lyric line.

  <img src="docs/en-US/images/compact-notch-lyrics.png" width="279" alt="Notch showing a lyric line">

- The notch and capsule can show the time. It sits on the right when shown with the quota rings or music, and is centered when shown alone; its colors can be customized.

  <img src="docs/en-US/images/compact-clock.png" width="567" alt="Clock in the notch: Simple, Seven segment and Serif styles beside the quota rings, and the clock shown alone, centered">

- When a tool's quota usage rises to the warning (70%) or critical (90%) level, the collapsed view shows a notice for 5 seconds. You can turn this off in settings.

  <img src="docs/en-US/images/compact-alert.png" width="390" alt="Quota notice: Claude 5-hour 91% used">

- The size and position of the Notch, Capsule and Indicator can be adjusted in settings.

- By default it turns into the Indicator when a window is maximized, and hides during full-screen games or video so it never opens by accident.

- The Notch, Capsule, notices and the expanded panel follow a panel theme: System, Dark or Light, built-in open-source themes such as One Dark Pro, GitHub, Dracula and Night Owl, or a custom theme made with the [theme guide](docs/en-US/themes.md). Text and figures stay readable in every theme.

### AI usage

<img src="docs/en-US/images/usage.png" width="100%" alt="AI usage page: quotas of Claude, Codex, Cursor and Antigravity in four columns">

<img src="docs/en-US/images/details.png" width="100%" alt="Model details page: tokens and cost by app and model">

- One column per tool shows the plan, quota usage and reset countdowns, for up to 7 tools at once.
- The bottom of each column totals tokens and estimated cost for today and the last 7 days.
- Token and cost details are available for each model, filtered by tool and date range.

You can choose the quota providers and set the quota and usage source of each column separately. See [Supported tools](docs/en-US/ai-tools.md).

### System monitor

- The processor's usage large, with the last minute as a soft line beside it, and one column per logical processor, performance cores and efficiency cores in their own colors, each with a tick for its current frequency (point at a column for its figures); the GPU shows small beside them. Click it to show the GPU large instead: a column per engine (3D, copy, video decode and encode…), its dedicated memory and name on hover, and the processor small.
- Memory in use, cached and committed, and download and upload speed over the physical network adapters with the last minute as a line.
- The busiest apps by CPU or memory, with the processes of one program folded into one row, compact or detailed, in two columns on a wide panel; the list does not reshuffle when two apps trade places by a point. Click an app for its card.
- Drag the open panel's bottom edge, sides or corners to resize it, on every page (AI usage, system monitor and media); the layout follows step by step. The grips reach a little beyond the edges, handles appear as the pointer nears them, and the edge under the pointer lights in the theme's accent; the size follows the pointer on a soft spring.
- The notch and the capsule can show the readings too: ftop's strip of core columns, CPU, memory, GPU and network, three ring gauges, or readings of your choice, next to the quota rings and the music. They widen to fit; drag either end to set the width yourself (the line shown beside a maximized window too), double-click an end to let them fit again.
- Follows the panel theme by default, taking its colors from the theme in use; ftop's Sea, Graphite and Warm schemes are also offered, in their light or dark variant to suit the theme. The system is read only while the page or the readings are shown, and processes only while the page is open.

The page is adapted from [ftop](https://github.com/Nongfsq/ftop) by Nongfsq (MIT License), a system monitor for macOS.

### Music

<img src="docs/en-US/images/music.png" width="63%" alt="Music page: cover, title, lyrics, progress bar and playback controls">

- Shows song information, synced lyrics and playback progress, with playback controls, playback mode switching and seeking in supported players.
- Works with several players at once; switch between them or pin one.

Works with players that use Windows media controls, with extra support for some players. See [Supported players](docs/en-US/music-players.md).

### Dynamic notices

- When a notification arrives, the notch, capsule or indicator grows out of its place with the application's icon, title and summary; hover for the details, open the application or use the notice's buttons, and it returns to its shape. Notices take the panel theme.
- Receives Windows app notifications, desktop notifications in WSL (off by default, per distribution) and notices sent with `brim-notify`, such as `brim-notify run -- dotnet test`, which reports the result when the command ends.
- Never takes the focus or interrupts the open panel; nothing pops up while locked, in full screen, in a focus session or presenting, with one summary afterwards; recent notices are kept encrypted on this PC for 24 hours, which can be switched off. Off by default; see [Dynamic notices](docs/en-US/notices.md).

## Install

Requires Windows 11 (x64).

1. Download and run `BrimDeck-<version>-Setup.exe` from [Releases](https://github.com/MoFeng2223/BrimDeck/releases/latest).
2. If Windows shows "Windows protected your PC", select "More info", then "Run anyway".

When a new version is available, you are notified when you open the settings; it is downloaded, verified and installed from within the app.

## Quick start

1. After you install and start BrimDeck, the panel appears at the top center of the primary display, showing Claude and Codex by default.
2. Move the pointer over the panel to open it; move away or press Esc to close it.
3. Select the settings button at the top right of the open panel to add tools or change settings. You can also right-click the open panel, double-click the tray icon, or right-click the tray icon.

## Uninstall

Uninstall BrimDeck from Apps in Windows Settings.

## Privacy

- Reads only the sign-in state and usage records that apps already keep, and never changes, renews or ends their sign-ins.
- Sign-in tokens are used only in memory and never written to disk; API keys you enter are stored on this PC, encrypted by Windows.
- Goes online only to read quotas and usage, update model prices, fetch lyrics and covers, and check for updates. Conversations are never uploaded, and no usage data is collected.

For the files BrimDeck reads and the addresses it connects to, see [Data and privacy](docs/en-US/privacy.md).

## FAQ

**A tool shows "Not connected" or "Waiting for update". What should I do?**
Make sure the tool is signed in on this PC; for example, Antigravity needs its desktop app running for its quota to be read. When BrimDeck cannot read the data, it shows the actual status and never makes up numbers.

**Is the cost shown what I actually pay?**
No. Costs come from the amounts reported by each service or from public model prices, and are meant to show how much you use. Your actual subscription charges are on each provider's bill.

## Contributing

You are welcome to report problems, suggest features or ask for more tools and players in [Issues](https://github.com/MoFeng2223/BrimDeck/issues), and to contribute through pull requests.

## License

BrimDeck is licensed under the [Apache License 2.0](LICENSE). Third-party licenses are listed in [ThirdPartyNotices.txt](src/BrimDeck/ThirdPartyNotices.txt).

The system monitor page is adapted from [ftop](https://github.com/Nongfsq/ftop) by Nongfsq, licensed under the MIT License; thanks to its author.
