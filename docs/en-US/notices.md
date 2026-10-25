# Dynamic notices

When a notification arrives, the notch, capsule or indicator grows out of its place and shows the application's icon, title and summary; hover for about a quarter of a second or click to see the details, and a few seconds later it returns to its shape. By default Windows' own banners and notification center stay as they are and BrimDeck shows its own copy alongside; to see each notification only once, switch on "Turn off other apps' notification banners" (see below). Everything is processed on this PC, without any cloud service.

## Turning them on

Settings › Notices › Dynamic notices › Turn on dynamic notices (off by default). Then:

- The open panel gains a "Recent notices" page (bell icon, with a red dot while there are notices you have not seen; a notice that popped up but that you did not hover, open or see on the page still counts as unseen). It lists the notices kept, where each can be expanded, opened in its application or deleted; notices can be paused or cleared.
- Notices take the [panel theme](themes.md): they share the surface, gradient, scan lines, rim, shadow or glow and text colors of the notch, capsule, indicator and open panel. A glowing theme flashes once as a notice arrives. An application's color only marks a small dot, the progress ring and its initial, adjusted to read clearly on the theme.

## Sources

| Source | What it is | Default |
|---|---|---|
| Windows app notifications | The current user's notifications in Windows' notification center: source, icon, time and text. Messages inside applications are not read, and the notification center is never cleared or changed. | On |
| brim-notify | `BrimDeck.Notify.exe` in BrimDeck's folder sends notices directly. | On |

### Windows: event log mode and polling

As an ordinary desktop program BrimDeck gets no change events from Windows. Windows' push notification platform writes an entry to its log (Applications and Services Logs › Microsoft › Windows › PushNotification-Platform › Operational) for every notification delivered to the current user, and any user may subscribe to that log, so BrimDeck uses **event log mode** by default: about 0.1 s after each entry it reads the notification list; otherwise it reads it only every 60 seconds, with practically no idle cost. A new notice usually appears within 0.03–1 second (the log service hands entries over in batches about once a second).

When that log is turned off (by group policy, say), cannot be subscribed to or keeps failing, BrimDeck **polls** instead: every 2.5 seconds it checks for a new notification (about 2.5 ms of CPU) and reads the whole list every 15 seconds; a new notice usually appears within 0.2–2.6 seconds, at an idle cost of about 0.36% of one core. The status line in the settings shows which mode is in use.

Early test builds offered an identity package (`BrimDeck.Notices`) that needed Developer Mode, for an event mode; it is no longer needed, and the installer no longer manages it. If you registered it, remove it in PowerShell with `Get-AppxPackage BrimDeck.Notices | Remove-AppxPackage`, and turn Developer Mode off again if you do not need it otherwise.

If Windows has not yet decided whether BrimDeck may read notifications, the settings page shows "Allow access". A refusal can be changed in Windows Settings › Privacy & security › Notifications.

### Windows: turning off other apps' notification banners

Settings › Notices › Sources › Turn off other apps' notification banners (off by default). When it is on, BrimDeck turns off "Show notification banners" in Windows for other apps, so a notification shows once, on the island; it still goes to the notification center, where BrimDeck reads it.

- **What changes**: only the DWORD value `ShowBanner` (set to 0) under `HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\<app id>` of the current user, the value Windows Settings › System › Notifications › an app › "Show notification banners" writes. No administrator rights are needed and nothing is restarted.
- **Which apps**: the apps in the Start menu (`shell:AppsFolder`) and those already in Windows' notification settings; an app installed later is added at BrimDeck's next start or with its first notification.
- **Which not**: BrimDeck itself; apps set to off or muted under "Apps" (their notifications show only in Windows; setting an app to off or muted gives its banner back); apps whose notifications are off in Windows; apps whose banner was already off. Sites in a browser share the browser's setting.
- **How it is undone**: before changing anything, BrimDeck records the previous state (no value, 0 or 1) in `%LOCALAPPDATA%\BrimDeck\notice-banners.json`. Switching this off, switching off dynamic notices or "Windows app notifications", Windows no longer letting BrimDeck read notifications, quitting or uninstalling BrimDeck restores it: a value that was not there is deleted, one that was is written back, and only where BrimDeck's 0 is still there. Anything BrimDeck could not restore after an unexpected exit is restored at its next start.
- **Your own changes**: if you turn an app's banner on again in Windows while this is on, BrimDeck keeps your choice and leaves that app alone; switching this off clears that list.
- **Sound**: only the banner goes, not the sound. Measured on Windows 11 26300: with `ShowBanner` at 0 no banner appears and the notification sound still plays; turn off "Play a sound" for an app in Windows Settings to silence it.
- **Note**: `ShowBanner` is not documented by Windows, and a later version may stop reading it. BrimDeck reads the value back after writing it and says so in the settings when Windows did not keep it; if banners still appear with this on, turn them off per app in Windows Settings › System › Notifications (the settings page has an "Open Windows notification settings" button).

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

## brim-notify

```text
# show a notice
brim-notify "Build finished" --body "Tests passed"
# run a command, then show its result (exit code and time)
brim-notify run -- dotnet test
# the same -r key updates the same notice (task progress)
brim-notify "Deploying" -r deploy --progress 40
# check that BrimDeck receives notices
brim-notify status
```

Options: `-b/--body`, `-s/--source` (the source's name), `-c/--category` (message, task, success, warning, error, info), `-p/--priority` (low, normal, high — a hint; only "High" chosen by you for the application raises it), `-i/--icon` (a local icon file; a relative path is from the current folder), `--id` (shown once however often it is retried). On Windows the program is `BrimDeck.Notify.exe` in BrimDeck's folder.

Exit codes: 0 delivered (or kept quiet by your rules), 1 usage error (a message over 64 KB even with its texts cut to BrimDeck's limits included), 2 refused (a `stop` that finds no timer, stopwatch or event with that key included), 3 BrimDeck is not running, dynamic notices are off, or it did not answer in time (the notice may still show); `run` returns the program's own exit code. Titles and bodies are cut to BrimDeck's limits before they are sent.

## Time notices

Alarms, timers, countdowns, mail and event reminders are recognized and shown by kind:

| Kind | From | Shown |
|---|---|---|
| Alarm | Windows Clock's notifications for alarms, finished timers and finished focus sessions | High priority; "Open app" returns to Clock |
| Timer running (stopwatch) | `brim-notify stopwatch` | "Running 12:03", live; can be stopped |
| Countdown | `brim-notify timer` | "24:59 left" with a ring, live; shows again when it ends (high priority); can be cancelled or reminded again in 5 minutes |
| Alarm | Microsoft To Do reminders | As above |
| Mail | New-mail notifications of Outlook (new and classic), Mail and Calendar, Thunderbird, Foxmail and others; notifications of web mail in a browser (Gmail, Outlook on the web, QQ Mail, NetEase Mail and others) | Ordinary notice |
| Event | Reminders of Microsoft 365 Calendar and Mail and Calendar; Outlook notifications that read as reminders ("In 15 minutes", "Now", or a line starting with "3:00 PM - 3:30 PM"); Teams meeting reminders and "meeting started"; Google Calendar reminders in a browser and web mail notifications that read as reminders; `brim-notify event` | High priority, "Starts in 14 min", "In progress"; for `brim-notify event` BrimDeck itself reminds before the start |

- Notices are sorted by the application's id (AUMID) and the notice's text, in memory on this PC; the original notification is not kept, and no mailbox or calendar is read. The sorting is heuristic: Outlook's and Teams' reminder wording varies by version and language, and what is not recognized shows as an ordinary notice.
- Settings › Dynamic notices › Time notices lists which of these applications this PC has (only whether they are installed; nothing is read from them). They remind through Windows notifications, so "Windows app notifications" needs to be on.
- Alarms, ended countdowns and event reminders are high priority and are not held back by the "at most 3 in 10 seconds" limit, but at most 3 ring per minute from one source (each Windows application on its own; brim-notify shares one); the rest wait their turn, so a script that sets many timers at once does not hold back another application's alarm. An application you set to muted or low priority stays quiet as usual. While locked, presenting or paused they only count toward the summary. An event entered more than 10 minutes after its start is recorded as an ordinary notice. Windows notifications that arrived while the PC slept are recorded, not shown, on waking, except reminders raised in the last 10 minutes or for an event starting within 10 minutes either way, which still alert. At most 200 timers, events still to remind and running stopwatches are kept; beyond that, the one BrimDeck received first is dropped from the source holding the most (and the log says so), so other applications' reminders are not affected.
- The live words follow one shared second tick, which runs only while a running timer shows on the island or on the recent notices page. Countdowns and events are worked out from the clock, again at once after waking or a change of the system time. A reminder missed by more than 10 minutes (asleep, BrimDeck not running) does not pop up; the list says "Ended at 15:30".
- Limits: Windows does not share the Clock app's running timers, stopwatch or alarm list, nor the snooze and dismiss buttons of its notifications, so a Clock alarm shows only when it rings and cannot be snoozed from BrimDeck; for a timer on the island use `brim-notify timer`. Calendar sync (reading Outlook's or Windows' calendar directly) is not supported yet; the applications' own reminder notifications are used.

```text
# count down 25 minutes, then remind (also 1h30m, 90s)
brim-notify timer 25m "Focus"
# count down to 15:30
brim-notify timer --at 15:30 "Meeting"
# a stopwatch showing the time elapsed
brim-notify stopwatch "Build" -r build
# stop the stopwatch, or cancel a countdown or event (same -s source as sent)
brim-notify stop build
# an event: reminds 10 minutes before and at its start
brim-notify event "Weekly" --at 15:00 --remind 10m --ends 16:00
# mark a plain notice as mail (or alarm)
brim-notify "New mail" --kind mail
```

- Times: `15:30` (the next 15:30) or `2026-10-24 15:30`, read in **Windows' time zone**; or ISO 8601 with an offset, such as `2026-10-24T15:30:00+08:00`. A time daylight saving skips moves to just after the change; one it repeats is the earlier.
- Durations: `90s`, `25m`, `1h30m`, `2d`; countdowns from 1 second to 31 days, reminders at most 7 days ahead of the start, times from 1 day ago to 1 year ahead. A duration counts from when BrimDeck receives it.
- Without `-r` a key is made up and printed on standard output, for `brim-notify stop KEY`; sending again with the same `-r` starts it over.
- Countdowns and events still running continue after a restart while "Keep recent notices" is on; an event not reminded yet is not removed by the retention.

## How notices show

- A notice never takes the keyboard focus, and never pops up over the open panel or details you are using; while the panel is open, the bell only gets its red dot.
- It stays 6 seconds, 8 for high priority; hovering, opening the details or working it from the keyboard pauses the time.
- Notices from one application within 0.8 seconds become one notice with a count; at most 3 notices pop up per 10 seconds, the rest go to the recent notices; a normal notice that waited over 30 seconds is not shown late; a merged notice stays at most twice its duration.
- Notices from brim-notify are judged by when their sender wrote them: a message that had already waited over 30 seconds (5 minutes for high priority) when it was sent only goes to the recent notices, unviewed, and does not pop up. The sender measures the wait by its own clock; time-kind notices follow their own reminder times. The same message (same event id) delivered again after BrimDeck restarts is neither recorded twice nor shown while the recent notices still hold it.
- A notice updated with `-r` takes the time of the latest message: it pops up as "Just now", not with the time of the notice it replaced.
- A notice that arrives under a resting pointer does not open its details by itself: move away and back, or click.
- Order: what you are working with > high priority notices > other notices > quota alerts > track changes. Alarms, ended countdowns and event reminders are high priority (see "Time notices").
- Nothing is shown while the session is locked or switched, while the panels are hidden or hidden by a full-screen rule. During a Windows focus session, presentation mode and BrimDeck's own pause nothing pops up; afterwards one summary shows how many arrived. Windows' separate "Do not disturb" switch has no public interface and is not detected; use BrimDeck's pause instead.
- Several monitors: a notice pops up on the monitor with the foreground window, if BrimDeck shows there. If that panel is hidden, the notice stays in the recent notices; it does not jump to another monitor.
- The notices shortcut (unset by default): with a notice on screen it opens the details for the keyboard (Tab to move, Enter to choose, Esc to close); otherwise it opens the recent notices.

## Privacy

- Recent notices are kept for 24 hours, at most 500, encrypted with Windows' per-user data protection (DPAPI) in the `notices` folder of BrimDeck's install folder (opened to the current user only; `%LOCALAPPDATA%\BrimDeck\notices` when the install folder cannot be written to). Keeping them can be switched off (memory only: then no application icons are kept on disk either), and the period and folder changed; they can be cleared at any time, or automatically on exit, daily or weekly (daily and weekly clearing keep timers and events still to remind and running stopwatches). A reminder that rang or a stopwatch that stopped is kept for the retention from that moment.
- Changing the folder adds the notices already in the new folder to the list, writes it there, and only then removes the old file. A folder that is a link, belongs to another account, or lets other accounts change its files is neither read nor written, and the settings say why; for the last, "Make it mine only" in the settings opens the folder to the current user and the system only. A folder on a network location (a NAS, a Samba share) is only checked for being a link, since its owner is an account of the server. The folder is checked once when it is chosen or changed, in the background, so a network location that does not answer never holds up the settings; before every read and write BrimDeck checks again that neither the folder nor `notices.dat` is a link, and a `notices.dat` that is a link is never read. When the saved file cannot be reached for now (in use, its drive or network location not available), BrimDeck tries again later and writes nothing until then, so the full list is never overwritten; notices deleted or cleared meanwhile do not come back once the file is read. A file that is actually damaged offers to be cleared; one this account may not read (made by BrimDeck running as administrator, say) offers both trying again and clearing. Every save is on the disk itself before it replaces the old file, so a power cut does not leave a damaged file.
- Deleting or clearing removes BrimDeck's own records only and tells no source. Switching notices off or clearing also cancels what shows and everything waiting to show.
- Raw notification content, attachments and pictures are never saved. Icons come only from Windows or local files; nothing is downloaded. Network paths, links and shortcuts as icons are refused.
- The diagnostic log `notices.log` holds only error types, codes and code locations, never titles or bodies.
- The local channel accepts only the current user on this PC (a named pipe that refuses network access). A notice's content is only ever shown as text: it is never run and its links are never opened.
- Each application can be set to Off, Muted (recent notices only), Normal or High, and to show "Title and summary", "Title only" or "App only" on arrival.

## Compatibility (tested)

| Case | Result |
|---|---|
| Windows 11 (26300) | Snapshot readable; change events unavailable, so event log mode, end to end 0.03–1 s; polling while the log is off, end to end 0.2–2.3 s |
| Icons of Windows notifications | Packaged apps give their icon; otherwise the application's initial |
| Opening the source application | Packaged apps through the app list; desktop apps by the id registered with their Start menu shortcut; otherwise the details say so |
| Several monitors (negative coordinates, 125% secondary) | Shows on the foreground window's monitor, placed and scaled correctly |
| 8 built-in themes × notch, capsule, indicator | Each checked by eye |

Cases not tested here (actual behavior on the lock screen) follow the design and may need confirming on your system.
