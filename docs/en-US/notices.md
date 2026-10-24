# Dynamic notices

When a notification arrives, the notch, capsule or indicator grows out of its place and shows the application's icon, title and summary; hover for about a quarter of a second or click to see the details, and a few seconds later it returns to its shape. Windows' own banners and notification center stay as they are; BrimDeck shows its own copy alongside. Everything is processed on this PC, without any cloud service.

## Turning them on

Settings › Notices › Dynamic notices › Turn on dynamic notices (off by default). Then:

- The open panel gains a "Recent notices" page (bell icon, with a red dot while there are notices you have not seen; a notice that popped up but that you did not hover, open or see on the page still counts as unseen). It lists the notices kept, where each can be expanded, opened in its application or deleted; notices can be paused or cleared.
- Notices take the [panel theme](themes.md): they share the surface, gradient, scan lines, rim, shadow or glow and text colors of the notch, capsule, indicator and open panel. A glowing theme flashes once as a notice arrives. An application's color only marks a small dot, the progress ring and its initial, adjusted to read clearly on the theme.

## Sources

| Source | What it is | Default |
|---|---|---|
| Windows app notifications | The current user's notifications in Windows' notification center: source, icon, time and text. Messages inside applications are not read, and the notification center is never cleared or changed. | On |
| brim-notify | `BrimDeck.Notify.exe` on Windows and `brim-notify` in WSL send notices directly. | On |
| WSL desktop notifications | Standard desktop notifications (freedesktop D-Bus, such as `notify-send`) in the chosen distributions. | Off, per distribution |

### Windows: polling and event mode

As an ordinary desktop program BrimDeck gets no change events from Windows, so it **polls**: every 2.5 seconds it checks for a new notification (about 2.5 ms of CPU) and reads the whole list every 15 seconds; a new notice usually appears within 0.2–2.6 seconds. Measured idle cost: about 0.36% of one core.

With the **identity package** registered it switches to **event mode**, with practically no idle cost. `identity\Register-NoticeIdentity.ps1` in BrimDeck's folder registers, for the current user, a package that holds only a manifest and uses the install folder as its external location: nothing is copied or moved and no administrator rights are needed. Windows registers such an unsigned package only in Developer Mode; without it, keep polling — everything works the same. Note that Developer Mode is a system-wide setting: while it is on, any unsigned app package can be installed on this PC, not only BrimDeck's identity package; unless the small saving of event mode matters to you, leave it off. Uninstalling BrimDeck removes the registration of the user who uninstalls it; upgrading keeps it. Other Windows users of the same PC who registered it run `Register-NoticeIdentity.ps1 -Remove` themselves (Windows does not let an ordinary account remove another user's package).

```powershell
powershell -ExecutionPolicy Bypass -File "<BrimDeck folder>\identity\Register-NoticeIdentity.ps1"
```

If Windows has not yet decided whether BrimDeck may read notifications, the settings page shows "Allow access". A refusal can be changed in Windows Settings › Privacy & security › Notifications.

### Windows: application names, icons and categories

The application id in a Windows notification does not always say which application sent it. BrimDeck handles these cases:

- **Web apps installed from Edge** (such as WhatsApp Web or Facebook): named after the site, for example "WhatsApp", not Microsoft Edge. Each site is a separate application with its own rules. A trailing line that repeats the site's address is dropped.
- **Site push notifications in Chrome and other browsers**: the browser adds the site's address as the last line. BrimDeck drops that line and tells sites apart, for example "Gmail · Google Chrome", so you can mute one site. Notifications with only two lines are left as they are.
- **Desktop programs without a Start menu shortcut**: the name and icon they registered are used (registry `HKCU\Software\Classes\AppUserModelId`; the icon only from a local file).
- **Common programs Windows gives no name for**: Bilibili, DingTalk, Gemini, classic Outlook and others take a name BrimDeck knows; Electron programs installed with Squirrel (Discord, Slack) are named after their program.
- **Tray balloons of older programs**: shown as "Tray app" when Windows gives no name, rather than a long number; desktop programs known only by their path are named after the program file.
- **Default categories**: Windows notifications carry no category. Notices from the Claude, Codex (ChatGPT) and Gemini desktop apps and Cursor are marked "Task"; Windows system notices, Windows Security, NVIDIA App, Adobe Creative Cloud, Clash Party/Clash Verge, Snipping Tool, Lenovo and Intel utilities and similar are marked "Info"; chat, mail and browsers stay "Message".
- **Calls**: a notice from a chat application (QQ, WeChat, Teams, WhatsApp, Discord and others) or Phone Link that is a voice or video call ("Incoming call", "is calling you", "邀请你进行语音通话"…) gets the call mark and shows at once like an alarm (unless the application is set to low priority); a missed call does not count.
- **Replaced notifications**: when an application updates its notification (QQ gathering new messages into one, for example), Windows removes the old one and adds a new one. If both come from the same application with the same title in the same reading, BrimDeck updates the existing notice instead of keeping two.
- **Emoji**: WPF draws no color fonts, so emoji are monochrome; sequences joined from several emoji (a family, the rainbow flag) may show as a box, and flags as two letters. This is a platform limit; the text itself is unaffected.

### WSL

When you switch a distribution on under "WSL (off by default)", BrimDeck:

- copies its notice agent to `~/.local/share/brimdeck/brimdeck-agent` of the distribution's default user, with `bridge.json` and a version stamp. The agent is built for x64 and ARM64, each with glibc (Ubuntu, Debian, Fedora and most others) and with musl (Alpine); BrimDeck picks the build by the distribution's `uname -m` and program loader. A distribution none of them runs on (32-bit ARM, RISC-V, or a C library at another place) is shown as not supported in the settings and is not tried again for 30 minutes;
- adds `~/.local/bin/brim-notify`. It is updated only while its content is exactly what BrimDeck wrote; any other file of that name, or a symbolic link, is left alone and shown as a conflict in the settings (then use `~/.local/share/brimdeck/brimdeck-agent notify`). Every file is written to a new temporary file and renamed into place, so nothing is written through a link;
- starts the agent through `wsl.exe` only while the distribution **already runs**. A distribution is never started for notices, WSL's configuration is not changed and the system's `notify-send` is not replaced. WSL 1 and WSL 2 distributions both work;
- the agent is itself a `wsl.exe` session, and while it runs WSL does not stop the distribution or its virtual machine. So the agent looks once a minute: once the user has had no process in the distribution for 5 minutes other than the agent, WSL's init and relay processes and the session plumbing (`systemd --user`, `(sd-pam)`, `dbus-daemon`/`dbus-broker`) — terminals and background jobs both count — it ends by itself, and WSL stops the distribution and frees its memory as usual. The settings show that the agent ended. It connects again when the distribution has stopped and runs again; if the distribution keeps running for another reason, BrimDeck starts the agent again at most once every 30 minutes, never in a loop.

The agent looks for the session bus in this order: `DBUS_SESSION_BUS_ADDRESS`, `$XDG_RUNTIME_DIR/bus`, `/run/user/<uid>/bus` (both only when owned by the user), and `~/.dbus/session-bus/<machine-id>-<display>` (a bus X11 autolaunched where there is no systemd). While there is none it looks again every 3 seconds.

The agent adapts to the session bus:

| Situation | What happens |
|---|---|
| No notification service | The agent becomes the session's standard notification service; the application's buttons appear in the details and are passed back to it |
| Another service, watching allowed | The agent watches without replacing it; that service's buttons are not offered |
| Another service, watching not allowed | Shown as a limitation; `brim-notify` still works |
| No session bus | Shown as a limitation; `brim-notify` still works |

As the notification service the agent says `body-markup`: markup in a body shows as plain text (tags removed, `&lt;` and the like decoded), while titles always show as sent. Notifications with an `x-canonical-private-synchronous`, `synchronous` or `x-dunst-stack-tag` hint (volume, brightness) update one notice per application and tag; those with the `transient` hint show as usual but are not kept with the saved notices. An icon that is a symbolic link within `/usr/share/icons` or `/usr/share/pixmaps`, leading only within them, shows as the file it leads to (Debian and Ubuntu install many theme icons that way); any other link is still not read.

To remove it: switch the distribution off in settings, then delete `~/.local/share/brimdeck` and `~/.local/bin/brim-notify` in the distribution.

## brim-notify

```text
brim-notify "Build finished" --body "Tests passed"   show a notice
brim-notify run -- dotnet test                      run a command, then show its result (exit code and time)
brim-notify "Deploying" -r deploy --progress 40       the same -r key updates the same notice (task progress)
brim-notify status                                  check that BrimDeck receives notices
```

Options: `-b/--body`, `-s/--source` (the source's name), `-c/--category` (message, task, success, warning, error, info), `-p/--priority` (low, normal, high — a hint; only "High" chosen by you for the application raises it), `-i/--icon` (a local icon file; a relative path is from the current folder, and in WSL a name without `/` that is no file in the current folder is looked up as an icon name), `--id` (shown once however often it is retried). On Windows the program is `BrimDeck.Notify.exe` in BrimDeck's folder.

Exit codes: 0 delivered (or kept quiet by your rules), 1 usage error (a message over 64 KB even with its texts cut to BrimDeck's limits included), 2 refused (a `stop` that finds no timer, stopwatch or event with that key included), 3 BrimDeck is not running, dynamic notices are off, or it did not answer in time (the notice may still show); `run` returns the program's own exit code. Titles and bodies are cut to BrimDeck's limits before they are sent.

## Time notices

Alarms, timers, countdowns, mail and event reminders are recognized and shown by kind:

| Kind | From | Shown |
|---|---|---|
| Alarm | Windows Clock's notifications for alarms, finished timers and finished focus sessions | High priority; "Open app" returns to Clock |
| Timer running (stopwatch) | `brim-notify stopwatch` | "Running 12:03", live; can be stopped |
| Countdown | `brim-notify timer` | "24:59 left" with a ring, live; shows again when it ends (high priority); can be cancelled or reminded again in 5 minutes |
| Alarm | Microsoft To Do reminders | As above |
| Mail | New-mail notifications of Outlook (new and classic), Mail and Calendar, Thunderbird, Foxmail and others; notifications of web mail in a browser (Gmail, Outlook on the web, QQ Mail, NetEase Mail and others); WSL desktop notifications with the `email` category | Ordinary notice |
| Event | Reminders of Microsoft 365 Calendar and Mail and Calendar; Outlook notifications that read as reminders ("In 15 minutes", "Now", or a line starting with "3:00 PM - 3:30 PM"); Teams meeting reminders and "meeting started"; Google Calendar reminders in a browser and web mail notifications that read as reminders; `brim-notify event` | High priority, "Starts in 14 min", "In progress"; for `brim-notify event` BrimDeck itself reminds before the start |

- Notices are sorted by the application's id (AUMID) and the notice's text, in memory on this PC; the original notification is not kept, and no mailbox or calendar is read. The sorting is heuristic: Outlook's and Teams' reminder wording varies by version and language, and what is not recognized shows as an ordinary notice.
- Settings › Dynamic notices › Time notices lists which of these applications this PC has (only whether they are installed; nothing is read from them). They remind through Windows notifications, so "Windows app notifications" needs to be on.
- Alarms, ended countdowns and event reminders are high priority and are not held back by the "at most 3 in 10 seconds" limit, but at most 3 ring per minute from one source (each Windows application on its own; brim-notify, and WSL applications, each share one); the rest wait their turn, so a script that sets many timers at once does not hold back another application's alarm. An application you set to muted or low priority stays quiet as usual. While locked, presenting or paused they only count toward the summary. An event entered more than 10 minutes after its start is recorded as an ordinary notice. Windows notifications that arrived while the PC slept are recorded, not shown, on waking, except reminders raised in the last 10 minutes or for an event starting within 10 minutes either way, which still alert. At most 200 timers, events still to remind and running stopwatches are kept; beyond that the oldest created go (and the log says so).
- The live words follow one shared second tick, which runs only while a running timer shows on the island or on the recent notices page. Countdowns and events are worked out from the clock, again at once after waking or a change of the system time. A reminder missed by more than 10 minutes (asleep, BrimDeck not running) does not pop up; the list says "Ended at 15:30".
- Limits: Windows does not share the Clock app's running timers, stopwatch or alarm list, nor the snooze and dismiss buttons of its notifications, so a Clock alarm shows only when it rings and cannot be snoozed from BrimDeck; for a timer on the island use `brim-notify timer`. Calendar sync (reading Outlook's or Windows' calendar directly) is not supported yet; the applications' own reminder notifications are used.

```text
brim-notify timer 25m "Focus"                    count down 25 minutes, then remind (also 1h30m, 90s)
brim-notify timer --at 15:30 "Meeting"           count down to 15:30
brim-notify stopwatch "Build" -r build           a stopwatch showing the time elapsed
brim-notify stop build                           stop the stopwatch, or cancel a countdown or event (same -s source as sent)
brim-notify event "Weekly" --at 15:00 --remind 10m --ends 16:00   an event: reminds 10 minutes before and at its start
brim-notify "New mail" --kind mail               mark a plain notice as mail (or alarm)
```

- Times: `15:30` (the next 15:30) or `2026-10-24 15:30`, read in **Windows' time zone** (also from a WSL distribution set to another zone); or ISO 8601 with an offset, such as `2026-10-24T15:30:00+08:00`. A time daylight saving skips moves to just after the change; one it repeats is the earlier.
- Durations: `90s`, `25m`, `1h30m`, `2d`; countdowns from 1 second to 31 days, reminders at most 7 days ahead of the start, times from 1 day ago to 1 year ahead. A duration counts from when BrimDeck receives it, whatever the WSL clock says.
- Without `-r` a key is made up and printed on standard output, for `brim-notify stop KEY`; sending again with the same `-r` starts it over.
- Countdowns and events still running continue after a restart while "Keep recent notices" is on; an event not reminded yet is not removed by the retention.

## How notices show

- A notice never takes the keyboard focus, and never pops up over the open panel or details you are using; while the panel is open, the bell only gets its red dot.
- It stays 6 seconds, 8 for high priority; hovering, opening the details or working it from the keyboard pauses the time.
- Notices from one application within 0.8 seconds become one notice with a count; at most 3 notices pop up per 10 seconds, the rest go to the recent notices; a normal notice that waited over 30 seconds is not shown late; a merged notice stays at most twice its duration.
- Notices from brim-notify and WSL are judged by when their sender wrote them: a message that had already waited over 30 seconds (5 minutes for high priority) when it was sent only goes to the recent notices, unviewed, and does not pop up. The sender measures the wait by its own clock, so a WSL clock that differs from Windows' does not mislead it; time-kind notices follow their own reminder times. The same message (same event id) delivered again after BrimDeck restarts is neither recorded twice nor shown while the recent notices still hold it.
- A notice updated with `-r` takes the time of the latest message: it pops up as "Just now", not with the time of the notice it replaced.
- A notice that arrives under a resting pointer does not open its details by itself: move away and back, or click.
- Order: what you are working with > high priority notices > other notices > quota alerts > track changes. Alarms, ended countdowns and event reminders are high priority (see "Time notices").
- Nothing is shown while the session is locked or switched, while the panels are hidden or hidden by a full-screen rule. During a Windows focus session, presentation mode and BrimDeck's own pause nothing pops up; afterwards one summary shows how many arrived. Windows' separate "Do not disturb" switch has no public interface and is not detected; use BrimDeck's pause instead.
- Several monitors: a notice pops up on the monitor with the foreground window, if BrimDeck shows there. If that panel is hidden, the notice stays in the recent notices; it does not jump to another monitor.
- The notices shortcut (unset by default): with a notice on screen it opens the details for the keyboard (Tab to move, Enter to choose, Esc to close); otherwise it opens the recent notices.

## Privacy

- Recent notices are kept for 24 hours, at most 500, encrypted with Windows' per-user data protection (DPAPI) in the `notices` folder of BrimDeck's install folder (opened to the current user only; `%LOCALAPPDATA%\BrimDeck\notices` when the install folder cannot be written to). Keeping them can be switched off (memory only: then no application icons are kept on disk either), and the period and folder changed; they can be cleared at any time, or automatically on exit, daily or weekly (daily and weekly clearing keep timers and events still to remind and running stopwatches). A reminder that rang or a stopwatch that stopped is kept for the retention from that moment.
- Changing the folder adds the notices already in the new folder to the list, writes it there, and only then removes the old file. A folder that is a link, belongs to another account, or lets other accounts change its files is neither read nor written, and the settings say why; for the last, "Make it mine only" in the settings opens the folder to the current user and the system only. A folder on a network location (a NAS, a Samba share) is only checked for being a link, since its owner is an account of the server. The folder is checked once when it is chosen or changed, in the background, so a network location that does not answer never holds up the settings; before every read and write BrimDeck checks again that neither the folder nor `notices.dat` is a link, and a `notices.dat` that is a link is never read. When the saved file cannot be reached for now (in use, its drive or network location not available), BrimDeck tries again later and writes nothing until then, so the full list is never overwritten; notices deleted or cleared meanwhile do not come back once the file is read. A file that is actually damaged offers to be cleared; one this account may not read (made by BrimDeck running as administrator, say) offers both trying again and clearing. Every save is on the disk itself before it replaces the old file, so a power cut does not leave a damaged file.
- Deleting or clearing removes BrimDeck's own records only and tells no source. Only closing a notice (its close button or Esc) reports a WSL desktop notification as dismissed by the user, as closing a desktop notification bubble does. Switching notices off or clearing also cancels what shows and everything waiting to show.
- Raw notification content, attachments and pictures are never saved. Icons come only from Windows or local files; nothing is downloaded. Network paths, links and shortcuts as icons are refused.
- The diagnostic log `notices.log` holds only error types, codes and code locations, never titles or bodies.
- The local channel accepts only the current user on this PC (a named pipe that refuses network access). A notice's content is only ever shown as text: it is never run and its links are never opened.
- Each application can be set to Off, Muted (recent notices only), Normal or High, and to show "Title and summary", "Title only" or "App only" on arrival.

## Compatibility (tested)

| Case | Result |
|---|---|
| Windows 11 (26300) without the identity package | Snapshot readable; change events unavailable, so polling; end to end 0.2–2.3 s |
| Icons of Windows notifications | Packaged apps give their icon; otherwise the application's initial |
| Opening the source application | Packaged apps through the app list; desktop apps by the id registered with their Start menu shortcut; otherwise the details say so |
| WSL Ubuntu 26.04 (systemd session bus, no notification service) | The agent serves; gdbus/notify-send notices show; buttons send `ActionInvoked` and `NotificationClosed` back |
| WSL with another notification service | Watched, with correct notification ids and closes |
| Several monitors (negative coordinates, 125% secondary) | Shows on the foreground window's monitor, placed and scaled correctly |
| 8 built-in themes × notch, capsule, indicator | Each checked by eye |

Cases not tested here (actual behavior on the lock screen, event mode with the identity package registered) follow the design and may need confirming on your system.
