# Dynamic notices

When a notification arrives, the notch, capsule or indicator grows out of its place and shows the application's icon, title and summary; hover for about a quarter of a second or click to see the details, and a few seconds later it returns to its shape. Windows' own banners and notification center stay as they are; BrimDeck shows its own copy alongside. Everything is processed on this PC, without any cloud service.

## Turning them on

Settings › Notices › Dynamic notices › Turn on dynamic notices (off by default). Then:

- The open panel gains a "Recent notices" page (bell icon, with a red dot while there are notices you have not seen). It lists the notices kept, where each can be expanded, opened in its application or deleted; notices can be paused or cleared.
- Notices take the [panel theme](themes.md): they share the surface, gradient, scan lines, rim, shadow or glow and text colors of the notch, capsule, indicator and open panel. A glowing theme flashes once as a notice arrives. An application's color only marks a small dot, the progress ring and its initial, adjusted to read clearly on the theme.

## Sources

| Source | What it is | Default |
|---|---|---|
| Windows app notifications | The current user's notifications in Windows' notification center: source, icon, time and text. Messages inside applications are not read, and the notification center is never cleared or changed. | On |
| brim-notify | `BrimDeck.Notify.exe` on Windows and `brim-notify` in WSL send notices directly. | On |
| WSL desktop notifications | Standard desktop notifications (freedesktop D-Bus, such as `notify-send`) in the chosen distributions. | Off, per distribution |

### Windows: polling and event mode

As an ordinary desktop program BrimDeck gets no change events from Windows, so it **polls**: every 2.5 seconds it checks for a new notification (about 2.5 ms of CPU) and reads the whole list every 15 seconds; a new notice usually appears within 0.2–2.6 seconds. Measured idle cost: about 0.36% of one core.

With the **identity package** registered it switches to **event mode**, with practically no idle cost. `identity\Register-NoticeIdentity.ps1` in BrimDeck's folder registers, for the current user, a package that holds only a manifest and uses the install folder as its external location: nothing is copied or moved and no administrator rights are needed. Windows registers such an unsigned package only in Developer Mode; without it, keep polling — everything works the same. Uninstalling BrimDeck removes the registration; upgrading keeps it.

```powershell
powershell -ExecutionPolicy Bypass -File "<BrimDeck folder>\identity\Register-NoticeIdentity.ps1"
```

If Windows has not yet decided whether BrimDeck may read notifications, the settings page shows "Allow access". A refusal can be changed in Windows Settings › Privacy & security › Notifications.

### WSL

When you switch a distribution on under "WSL (off by default)", BrimDeck:

- copies its notice agent to `~/.local/share/brimdeck/brimdeck-agent` of the distribution's default user, with `bridge.json` and a version stamp;
- adds `~/.local/bin/brim-notify` (a file of that name not written by BrimDeck is left alone);
- starts the agent through `wsl.exe` only while the distribution **already runs**. A distribution is never started for notices, WSL's configuration is not changed and the system's `notify-send` is not replaced.

The agent adapts to the session bus:

| Situation | What happens |
|---|---|
| No notification service | The agent becomes the session's standard notification service; the application's buttons appear in the details and are passed back to it |
| Another service, watching allowed | The agent watches without replacing it; that service's buttons are not offered |
| Another service, watching not allowed | Shown as a limitation; `brim-notify` still works |
| No session bus | Shown as a limitation; `brim-notify` still works |

To remove it: switch the distribution off in settings, then delete `~/.local/share/brimdeck` and `~/.local/bin/brim-notify` in the distribution.

## brim-notify

```text
brim-notify "Build finished" --body "Tests passed"   show a notice
brim-notify run -- dotnet test                      run a command, then show its result (exit code and time)
brim-notify "Deploying" -r deploy --progress 40       the same -r key updates the same notice (task progress)
brim-notify status                                  check that BrimDeck receives notices
```

Options: `-b/--body`, `-s/--source` (the source's name), `-c/--category` (message, task, success, warning, error, info), `-p/--priority` (low, normal, high — a hint; only "High" chosen by you for the application raises it), `-i/--icon` (a local icon file), `--id` (shown once however often it is retried). On Windows the program is `BrimDeck.Notify.exe` in BrimDeck's folder.

Exit codes: 0 delivered (or kept quiet by your rules), 1 usage error, 2 refused, 3 BrimDeck is not running or dynamic notices are off; `run` returns the program's own exit code.

## How notices show

- A notice never takes the keyboard focus, and never pops up over the open panel or details you are using; while the panel is open, the bell only gets its red dot.
- It stays 6 seconds, 8 for high priority; hovering, opening the details or working it from the keyboard pauses the time.
- Notices from one application within 0.8 seconds become one notice with a count; at most 3 notices pop up per 10 seconds, the rest go to the recent notices; a normal notice that waited over 30 seconds is not shown late; a merged notice stays at most twice its duration.
- A notice that arrives under a resting pointer does not open its details by itself: move away and back, or click.
- Order: what you are working with > high priority notices > other notices > quota alerts > track changes.
- Nothing is shown while the session is locked or switched, while the panels are hidden or hidden by a full-screen rule. During a Windows focus session, presentation mode and BrimDeck's own pause nothing pops up; afterwards one summary shows how many arrived. Windows' separate "Do not disturb" switch has no public interface and is not detected; use BrimDeck's pause instead.
- Several monitors: a notice pops up on the monitor with the foreground window, if BrimDeck shows there. If that panel is hidden, the notice stays in the recent notices; it does not jump to another monitor.
- The notices shortcut (unset by default): with a notice on screen it opens the details for the keyboard (Tab to move, Enter to choose, Esc to close); otherwise it opens the recent notices.

## Privacy

- Recent notices are kept for 24 hours, at most 500, encrypted on this PC with Windows' per-user data protection (DPAPI). Keeping them can be switched off (memory only), and the period and folder changed; they can be cleared at any time.
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
