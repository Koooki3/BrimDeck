# Supported players

The music page and the collapsed view show the song that is playing and offer playback controls. What is available depends on the player:

| Player | Shown | Controls |
| --- | --- | --- |
| NetEase Cloud Music | Track, cover, progress | With SMTC on: play, pause, previous and next<br>With full control: also seeking, playback modes (shuffle, repeat all, repeat one), and liking or unliking the current song |
| QQ Music | Track, progress | Play, pause, previous, next and seeking |
| Apple Music | Track, cover, progress | Play, pause, previous and next<br>With full control: also seeking, shuffle on or off, and the repeat mode |
| Other players | The track, cover and progress the player shares with Windows media controls | The play, pause, previous, next and seeking the player supports |

"Other players" are players that use Windows media controls, that is, players that appear in the media card of the Windows volume flyout while they play.

## NetEase Cloud Music

- **By default**: the track, cover and progress are shown without any setup. The progress is an estimate.
- **With SMTC on**: in NetEase Cloud Music, open 设置 › 系统 and turn on 开启 SMTC (the NetEase client is in Chinese). You can then play, pause and change tracks from BrimDeck.
- **Full control**: select Enable full control on the music page and confirm; NetEase Cloud Music restarts. It then shows the actual progress, and you can seek and switch playback modes (shuffle, repeat all, repeat one). While NetEase Cloud Music is signed in and online, a heart also appears right of the controls: select it to add the current song to your liked songs, and again to remove it; a heart selected in NetEase Cloud Music shows in BrimDeck too. Starting NetEase Cloud Music the normal way returns it to normal mode. SMTC and normal mode offer no liking, because Windows media controls have no command for it.

Full control needs NetEase Cloud Music to open a local debugging port, which carries some risk while it is open. Read [Data and privacy](privacy.md#netease-cloud-music-full-control) before you turn it on.

To keep full control available at any time, turn on Always show "Enable full control" in Settings › Music › Players.

## Apple Music

- **By default**: the track, cover and progress are shown, and you can play, pause and change tracks.
- **Full control**: select Enable full control on the music page and confirm. You can then seek, turn shuffle on or off, and switch the repeat mode (off, repeat all, repeat one). These work by operating the buttons and the progress bar in the Apple Music window through Windows UI Automation, so they need the Apple Music main window; while that window is not minimized, using them brings it to the front. Full control lasts until BrimDeck restarts.
