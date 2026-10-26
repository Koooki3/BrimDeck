# Panel theme guide

The Notch, Capsule and Indicator, the quota notice, the expanded panel and its pop-ups are colored by a "panel theme". Choose it in **Settings › Panel theme**. There are three kinds:

| Kind | Themes |
| --- | --- |
| Basics | System, Dark (the default: the original pure black look), Light, Liquid Glass (light), Liquid Glass (dark) |
| Theme extensions | One Dark Pro, GitHub Dark, GitHub Light, Dracula Official, Night Owl, Island Holiday, Pixel Blocks, Cyberpunk: Edgerunners (the demo) |
| Custom | The theme files in the themes folder, which can be created, edited and deleted in settings |

Each theme remembers the theme, warning and critical colors of the app columns and the clock's style and color you change while using it: they are saved when you switch to another theme and come back when you return. A theme chosen for the first time uses its own colors and suggested clock. Restoring defaults in "Quotas and usage" or "Collapsed view" restores the current theme's defaults.

One Dark Pro, GitHub Dark, GitHub Light, Dracula Official and Night Owl are ported from open-source VS Code themes and use their colors under each theme's MIT License: [One Dark Pro](https://github.com/Binaryify/OneDark-Pro), [GitHub Theme](https://github.com/primer/github-vscode-theme), [Dracula Official](https://github.com/dracula/visual-studio-code) and [Night Owl](https://github.com/sdras/night-owl-vscode-theme). The copyright and license notices of these projects ship with BrimDeck in `ThirdPartyNotices.txt`.

## Changing colors in settings

A theme can be adjusted without writing a file. Under "Colors" in **Settings › Panel theme**:

- **Background, foreground and accent**: changing one of them makes the colors that follow from it again. The background sets the surface and the pop-ups; the foreground sets the text, dividers and fills; the accent sets keyboard focus, chosen dates and the date range. When the background turns from dark to light (or the reverse) and the foreground would no longer read on it, the foreground changes too.
- **Advanced**: lists every color, the default app colors and the effects (rim width, shadow, scan lines). A translucent color keeps its opacity when a new color is picked.

A changed item is marked "Unsaved" and can be undone on its own, or all at once with "Undo all changes". **Changes take effect only once saved**: until then the panel and the theme cards stay as they are. While there are changes, cards under "Custom" turn into save cards:

- **Save current theme** (blue, in place of "New from current theme"): saves the current theme with the changes as a new custom theme and switches to it. A built-in theme cannot be changed and is saved this way only.
- **Overwrite current theme** (amber, in place of the chosen custom theme's card): writes the changes into that theme's file. Only the changed values are rewritten; comments and layout stay as they were.

Unsaved changes are kept while BrimDeck runs and are lost when it exits. The surface and rim gradients, the clock's preset colors and gradients, and the color picker's presets are still set in the theme file.

## Theme files

Like a VS Code theme extension, a theme is a JSON file:

- The themes folder is `%LOCALAPPDATA%\BrimDeck\themes` ("Open themes folder" on the settings page).
- Every `*.json` in the folder, and every `theme.json` in a subfolder, is a theme.
- Comments (`//`) and trailing commas are allowed; unknown properties are ignored.
- Every theme has a **base theme** (`base`, `dark` if left out); the file only needs what differs from it.

The smallest theme is a few lines:

```json
{
  "id": "midnight-teal",
  "name": "Midnight teal",
  "base": "dark",
  "colors": { "surface": "#04161A", "accent": "#2DD4BF", "clock": "#5EEAD4" }
}
```

The full template is [`theme-template.json`](../../src/BrimDeck.Core/Templates/theme-template.json). It lists every color, app color and effect with its use and contrast minimum. **New from current theme** on the settings page fills the template with every value of the current theme, saves it as a new custom theme and switches to it; open it afterwards from "Open themes folder" or the file link under "Current theme". While the settings page is shown, saving a theme file reloads it and the panel updates.

### Colors

Colors are `#RRGGBB` or `#AARRGGBB` (AA is the opacity). The minimum in brackets is a WCAG 2 contrast. The three text levels, `menuDetail`, `accent` and `danger` must reach it on the surface (each stop of its gradient included) and on the pop-up surface; `liked`, `clock` and `equalizer` on the surface only; `onAccent` on the accent and `indicatorProgress` on the indicator. Text also stands on fills: `textPrimary` on `fillSelected`, `pressed`, `input`, `rangeFill`, `tooltip` and `bubble`, and `textSecondary` on `fill` and `hover`, each needing 4.5:1.

| Key | Use |
| --- | --- |
| `surface` | The notch, capsule and expanded panel; always opaque |
| `textPrimary` | Titles and figures (7:1) |
| `textSecondary` | Labels (4.5:1) |
| `textTertiary` | Reset times and notes (3:1) |
| `accent` / `onAccent` | Keyboard focus and chosen dates (3:1) / text on them, such as a notice's main button (4.5:1) |
| `danger` | Errors and paused sync (4.5:1) |
| `liked` | A liked song (3:1) |
| `clock` | The clock when its color is "Theme default" (4.5:1) |
| `equalizer` | The equalizer when it does not follow the cover (3:1) |
| `hairline` `track` `ringTrack` `fill` `fillSelected` `hover` `pressed` `outline` | Dividers, tracks, joined switches, hover and pressed states, outlines |
| `popup` `popupBorder` `input` `rangeFill` | Menus and pop-ups, date fields and date ranges |
| `coverPlaceholder` `coverOutline` `scrollThumb` | A missing cover, the cover outline, scroll bars |
| `rim` `shadow` | The line along the surface's inner edge; the collapsed shadow (a bright color with no depth is a glow) |
| `indicator` `indicatorProgress` | The Indicator and the playback progress on it (2:1) |
| `switchFill` `seekRail` `rowDivider` `rowHover` | The toolbar page switch, the seek bar track, model detail row dividers and hover |
| `menuDetail` | Song titles in the playback source menu (3:1) |
| `tooltip` `tooltipBorder` `bubble` `bubbleBorder` | Tooltips on the panel and the time bubble over the seek bar |

### App colors (apps)

The default colors of the quota columns, bars and rings: `claude`, `codex`, `antigravity`, `cursor`, `zcode`, `dsh`, `grokBot`, `kimi`, `manual` (every source entered by hand), `other`, and `warning` and `critical`. A color chosen for a column in "Quotas and usage" takes precedence.

### Clock

The clock style and color that suit the theme. Choosing the theme in settings switches the collapsed clock to them; it can still be changed on the Collapsed view page. Its fields are inherited one by one like the colors: a file that names only a `style` keeps its base theme's color.

```json
"clock": { "style": "segment", "color": "default", "custom": "#B9A8FF", "gradient": ["#FF79C6", "#BD93F9", "#8BE9FD"] }
```

`style` is one of `minimal`, `stacked`, `digits`, `dots`, `segment`, `neon`, `serif`, `wide`, `condensed`, `pixel`, `tektur`, `rounded`; `color` is `default` (the `clock` color above), `amber`, `cyan`, `pink`, `lime`, `custom`, `gradient` or `cover`. `palette` sets this theme's colors for the amber, cyan, pink and lime swatches, and `gradients` the preset gradients it offers (one to eight, each exactly three colors); both need 4.5:1 on the surface. When the theme changes, a preset gradient in use becomes the new theme's preset in the same place. `custom` is used when `color` is `custom`; `gradient`, exactly three colors, when `color` is `gradient`. On light themes the glow of the neon and seven-segment styles is softened so the digits stay sharp.

### Picker swatches (swatches)

The twelve colors under the color picker in settings. Without this list the picker offers the theme's app, warning, critical and clock colors (without repeats, filled with in-between colors when there are fewer than twelve); the dark theme keeps the original twelve. This list is not inherited.

### Effects

| Key | Effect | Range |
| --- | --- | --- |
| `surfaceGradient` | A gradient over the surface, top to bottom, 2 to 8 colors | |
| `rimGradient` | A gradient for the rim, left to right; replaces `rim` | |
| `rimWidth` | Rim width | 0–3 |
| `shadowBlur` `shadowDepth` `shadowOpacity` | Blur, offset and opacity of the shadow | 0–40, 0–12, 0–1 |
| `scanlines` | Horizontal scan lines in the primary text color at this opacity | 0–0.12 |
| `glass` | Glass: `tint` is a translucent color laid over the desktop (its opacity strictly between 0 and 1), `edge` the light gathered along the inner edge (0–1), `highlight` its color. See "Glass" below | |
| `pixel` | Pixel art: the outline's curves become steps of this many DIP (rounded to whole device pixels) and the island's text is drawn unsmoothed; with a `rimGradient` the rim becomes a bevel, its first color lighting the edges that face up and left and its last shading those that face down and right. Not with `glass`, also from the base theme: such a file is refused | 0, or 1–4 |
| `texture` | With `pixel`: squares of the pixel size a little lighter or darker than the surface, a block's grain | 0–0.12 |
| `decor` | Quiet pictures drawn into the surface under the content, each value inherited on its own (a night look that names only `sea` keeps the day's `at` and `line`): `horizon` a sea (`sea`) below `at` of the height with a line of light (`line`) along it; `caustics` a net of soft light lines below the horizon (over the whole surface without one); `palm` a palm frond's shadow in the open panel's top-right corner, above the horizon; `hud` 45-degree cuts across the bottom-left and the top-right corner (the bottom-right on an island hanging from the screen's edge, `chamfer`) with a hexagonal tick beside each. A fully transparent color turns a part off, e.g. in a night look. Text is held to its minimums over the sea (at the horizon and at its deepest), the line and the motifs | `at` 0.3–0.9, caustics alpha ≤ 0.2, palm alpha ≤ 0.12, `chamfer` 0–10 |

#### Glass

A glass theme's surface is a translucent tint over the desktop rather than a solid color, so the desktop shows through the island. Readability is still guaranteed: text is checked over the tint on a **black** and on a **white** desktop, so a light glass needs a fairly dense tint (about 75%). The glass carries two lights along its edge: `rimGradient`, a specular rim, and `edge`, light that fades inward from the edge as in thick glass.

The glass is drawn as the opaque `surface` color instead while Windows' transparency effects are off (Settings › Personalization › Colors) or in high contrast; the "Current theme" section says so. The shadow under a glass island is drawn only outside it, so it never shows through.

Where Windows can, the desktop under the glass is blurred: BrimDeck puts a window that shows only the blurred backdrop right under the island (the composition engine's host backdrop brush, which DWM blurs), cut to the island's shape. The two windows cannot change shape on the same frame, so while the island opens, closes, changes page or boots, the blur steps aside and fades back in the new shape once the island has held still for about a tenth of a second; it never shows outside the island. With transparency effects off, in high contrast, on battery saver, in a remote session or on a Windows that cannot draw it, there is no blur and the glass is clear: what lies behind shows faintly, and the readability rules hold all the same.

### Motion (motion)

How the theme moves on BrimDeck's own windows: slow ambient light on the island, and how the pointer and the keyboard are answered. Like the effects, the section is merged key by key along the base chain, so a theme only writes what it changes. Every value is held to the range below (a value outside it is moved to its nearest end), so no theme file can make anything flash: brightness never changes more than three times a second. Nothing moves while animations are off in BrimDeck's settings or in Windows, or in high contrast; the ambient light also stops while the island is hidden or powering off, the session is locked, battery saver is on, or a maximized or full-screen window is in front on the island's monitor (as a live wallpaper pauses); the particles a press left go at once when the panel closes. The settings window keeps its own hover and focus styles.

`ambient`: periods are seconds, and 0 turns an effect off.

| Key | Effect | Range |
| --- | --- | --- |
| `breathe` `breatheDepth` | The rim dims a little and comes back once per period; how far it dims | 3–30, 0–0.35 |
| `drift` | The rim gradient's colors travel one width in this time | 4–60 |
| `sweep` `sweepOpacity` | A faint scan line passes down the surface once per period (taking 2.4 s); its strength | 6–120, 0–0.12 |
| `flicker` `flickerDepth` | The neon sags softly once per period (taking 1.2 s); how deep | 20–300, 0–0.15 |

The answers to the pointer are drawn on the control's own visible shape, never around its layout box and never following the pointer. A press answers as the button goes down, holds while it is held and completes on release (or backs out when released elsewhere); Space and Enter on a focused control press it too, without particles.

| Key | Effect | Values |
| --- | --- | --- |
| `hover` | The mark on a hovered control (also `lens`: light pooled at the top of a glass bead and a bright top rim; `wave`: a faint tint and a small wave that rolls in along the bottom and settles; `slot`: a square highlight with a solid inner frame, as an inventory slot): `none`, `reveal` (faint fill, edge lit from the top), `lift` (soft shadow outside, light top edge), `underline` (subtle fill, 2 px bar along the bottom), `caret` (line highlight, caret bar at the left), `brackets` (HUD corner brackets), `sheen` (two-color tint and edge), `glow` (warm inner glow and a glint) | earlier names: `spot` = reveal, `edge` = underline, `neon` = brackets |
| `hoverStrength` | How strongly the mark shows | 0–0.3 |
| `press` | The press (also `bevel`: the bevel turns over, shaded at the top left and lit at the bottom right, as a pixel-art button pushed in; `jelly`: the light on the glass squashes wider and lower, shaded along the bottom only, and springs back once past its shape on release; `splash`: a ring of water spreads out from the control's own edge as it goes down and a second, fainter one as a completed press lets go, 320–400 ms each, easing out): `none`, `shade` (the shape shades, its edge firms), `sink` (the shadow collapses, pushed in), `indicator` (the bottom bar takes the highlight), `select` (a selection sweeps from the caret), `lock` (brackets clamp on with a chromatic ghost and a glitch slice; a scan line on release), `sparkle` (the tint deepens; stars on release), `ember` (a warm glow swells; fireflies on release) | earlier names: `pulse` and `ripple` = shade, `ring` = lock |
| `pressStrength` | How strongly the press shows | 0–0.35 |
| `particles` | What a completed press leaves for a moment, from the control's edges: `none`, `dust`, `pixel`, `spark`, `star`, `firefly`, `neon`, `rain` (three or four fine 1 × 6 DIP slanted streaks falling from the bottom edge, at most 0.25 opaque, about 1 s), `bubble` (three to five bubbles 2–4 DIP in radius rising 22–32 DIP from the bottom edge over 0.8–1.2 s, their outline at most 0.5 opaque) | |
| `trail` | How strongly those particles show; 0 shows none | 0–0.6 |
| `focus` | A short accent where keyboard focus lands (Tab after Tab plays it only on the first move after a pause; the others show the static focus frame): `none`, `ring`, `glow`, `brackets`, `caret` | earlier name: `neon` = brackets |
| `pace` | Scales the pace of the theme's answers to the pointer and the keyboard (hover, press, focus, particles); independent of the expand and collapse duration in settings | 0.6–1.6 |
| `steps` | 0 moves smoothly; 2 to 8 moves in that many hard steps, for a mechanical feel. Glitch slices and scan lines are rationed to two a second however fast the clicks come | 0, 2–8 |
| `color` | The main mark (hover and focus) | a color key, `#RRGGBB`, or `system` (the Windows accent) |
| `secondary` | Its second tone: a gradient's end, a shadow, a ghost or a neutral bar | as `color` |
| `highlight` | The color of the moment of a press: an indicator, a selection, a scan line, sparkles, fireflies, rain, bubbles | as `color` |
| `spring` | How much the springs give (a resize drag, `jelly` springing back), always at the monitor page's 16 rad/s: `firm` (damping 0.85, barely passes its shape), `default` (0.70), `jelly` (0.50, springs back twice) | |
| `particleMs` | How long the particles a press leaves live, in milliseconds, each keeping its own small spread; 0 keeps the style's own (rain about 1000) | 0, 300–2400 |
| `sliceMs` | How long `lock`'s glitch slice holds as the press goes down, in milliseconds; 0 is about 70 | 0, 50–160 |
| `scanMs` | How long `lock`'s scan line takes to pass on release, in milliseconds; 0 is about 140 | 0, 100–400 |
| `splashMs` | How long each of `splash`'s rings takes to spread, in milliseconds; the second starts on release but never before the first is half way out; 0 is about 360 | 0, 300–800 |

Motion colors are kept readable on the surface like any other color chosen outside the theme. The default dark and light themes (and any theme with no `motion` section anywhere in its base chain) draw nothing over the controls, which keep their own hover and press looks. The "system" theme uses Windows' accent color; over a base with answers it gives the quiet Fluent ones (`reveal` and `shade`, keeping a base's `lift` and `sink`), and over the default dark and light it draws nothing either; no ambient light, particles or steps, and its base theme's strengths and pace stay. `Templates/theme-template.json` lists every key with its default.

### Day, dusk and night looks (night, dusk, daylight)

A theme can carry a night look: `night` holds the `colors`, `apps`, `effects`, `motion` and `clock` that change at night, laid over the day look. A night look belongs to the file that writes it and is not inherited through `base`: a theme based on Island Holiday that should change at night writes its own `night` ("New from current theme" copies it, with its `dusk` and `daylight`).

```jsonc
"daylight": { "mode": "clock", "day": "06:30", "night": "18:30", "duskMinutes": 60 },
"night": { "colors": { "surface": "#0B1622" } },
"dusk": { "colors": { "surface": "#2A1A2E" } }
```

| Key | What it does | Values |
|---|---|---|
| `daylight.mode` | What switches the looks. `clock`: the day look from `day` to `night` and the night look the rest of the time. `system`: the night look while Windows apps use dark mode and the day look in light mode, with no dusk | `clock` (default), `system` |
| `daylight.day`, `daylight.night` | When the day and the night begin, by the clock; a day may run past midnight | `HH:mm`, 06:30 and 18:30 by default |
| `daylight.duskMinutes` | How long the dusk look shows before the night begins (clock mode only) | 15–180, 60 by default |
| `dusk` | An optional dusk look, written like `night` and laid over the day look; it needs a `night` look beside it | |

An unknown `mode`, a `duskMinutes` outside 15–180, a dusk look without a night look, or a bad color in either look makes the file unusable. The user can choose "Follow Windows" or move the hours in Settings › Current theme; the hours cannot be changed while the theme follows Windows. At each change (day to dusk, dusk to night, night to day, or Windows switching between light and dark) the island's old look fades out over 1.6 seconds; with animations off it switches at once. Every look is held to the readability rules below.

## Readability

A theme decides how the panel looks, never whether it can be read:

1. Every color with a minimum is checked against the surface, each gradient stop and the pop-up surface before it is used.
2. A color below its minimum is lightened or darkened along its own hue, in whichever direction needs the smaller change, just far enough to pass. A color that passes is used as written. A translucent color that has to move comes back opaque, as it looked over the surface. When the backgrounds are both light and dark and no lightness passes them all, the lightness with the best worst case is used (never worse than the color as written), and the settings page says the minimum could not be met.
3. Colors chosen elsewhere, such as app colors, clock colors and the cover color, get the same treatment against the current surface, so a pale warning color or a lime clock stays clear on a light theme.
4. "Current theme › Readability" on the settings page lists every adjustment. A theme file that cannot be used is listed with the reason (invalid JSON, an unknown color, a duplicate id, …) and cannot be chosen.

Every built-in theme passes these rules as written; `PanelThemeTests` checks each one.

## Design steps

The demo theme was made in these five steps.

1. **Collect references**: frames of the work or brand; note the light and dark, the main and accent colors, and the shapes that recur.
2. **Assign roles**: the surface (the largest area), three text levels, the accent, the semantic colors (warning, critical, error) and the decoration (rim, shadow, gradient, texture).
3. **Surface and text first**: the surface sets every contrast; space the text levels at 7 : 4.5 : 3.
4. **Then accents and app colors**: app colors must be told apart and reach 3:1 on the surface; warning and critical should read as yellow and red.
5. **Check**: look at the collapsed and expanded panel, the notice and the pop-ups, and make sure "Readability" is empty or acceptable; otherwise go back to step 3.

## The newer themes

**Liquid Glass (light / dark)**: after Apple's Liquid Glass, liquid-glass-react and the "transparent island" concept, and the clear mode leaked for Xiaomi HyperOS 4. The desktop under the island is blurred (see "Glass" above). The light glass is a milky tint (`#C2F7F9FC`), the dark one graphite (`#C8121419`), just dense enough for body text to keep 7:1 over a black or a white desktop. The specular rim is brightest at the top left and catches again at the bottom right, and travels around the edge in about half a minute (`drift`); the inner edge light makes the edge read as thick glass. Hover is `lens`, press `jelly` (after the jelly press of xiaofangstudio/liquid-glass-web: squashed 1.05 x 0.93, shaded only at the bottom, springing back); no particles.

**Island Holiday**: a postcard of an island. By day, a clear sky (`#A8E1F8`) fading into the warm haze of the horizon, and below a horizon about two thirds down a turquoise lagoon (`decor.horizon`) with a line of sunlight along it, the soft net of light a sunlit sea throws (`decor.caustics`) and a palm frond's shade in the open panel's corner (`decor.palm`); deep-sea text, a lagoon accent, a sunburnt coral clock in the rounded Fredoka face (the `rounded` style), a mango Indicator, hibiscus for liked songs and a shore rim (lagoon, sky, sand, coral). At dusk, the hour before night, the sky turns peach over a sea warmed by the low sun, with a gold horizon and a sunset rim. By night, a deep ocean navy (`#001E3C`) over a dark teal sea under a horizon lit in moon gold, moonlight playing faintly on the water, a lantern-gold clock and a soft glow under the island. The small collapsed island keeps the plain sky, so the horizon never crosses the clock. The rim breathes like a swell and its colors flow like water; a glint of sun crosses now and then by day, a rarer moonlight by night. A small wave rolls in along a hovered control; a press spreads a ring of water from the control (`splash`), and a completed one lets bubbles rise by day (`bubble`) and glowing plankton at night (`firefly`).

**Pixel Blocks**: an unofficial pixel-art look inspired by block sandbox games, using no artwork, fonts, textures or logos from any game. A deep slate surface with a 5% block grain, 2 DIP stepped corners, a bevel rim lit at the top left and shaded at the bottom right, and a hard shadow with no blur. Chat-style colors: white text, a yellow block clock (the `pixel` clock style), a green accent and a green experience-bar indicator; violet menus with a violet frame. Everything moves in four hard steps: `slot` on hover, `bevel` on press, square particles after; no ambient light, so it costs nothing while idle.

## Demo: Cyberpunk: Edgerunners

An unofficial fan palette. It uses colors only, no artwork, logos or fonts from the series.

**Style.** *Cyberpunk: Edgerunners* by Studio TRIGGER carries the retro-future look of 1980s anime: flat cel color, saturated neon, a violet-black sky over Night City with streets lit by magenta and cyan signs, and the signature yellow of *Cyberpunk 2077* in the opening and the interfaces. Lucy's hair runs from cyan to pink, Rebecca wears mint green and pink; scan lines, chromatic aberration and cut-corner HUD frames recur, and so does the moon.

| Source | Color | Panel role |
| --- | --- | --- |
| Night City's indigo sky over the street's teal haze | `#0C0C22` → `#101030` → `#0A1F33` | Surface and its top-to-bottom gradient; menus and tooltips `#10102A` |
| The haze lit by the city | `#EEF4FF` / `#BCCEEC` / `#8698C2` | Three text levels (cool white, pale blue); dividers, fills and hovers in that pale blue at low opacity |
| A lit window | `#CFF4F6` | Clock, in Tektur (the squared HUD face of the taskbar clock) |
| Cyan neon, Lucy's hair | `#00F0FF` | Accent, focus, tracks: neon only where it is thin |
| Magenta neon | `#FF2A6D` | Collapsed glow, liked song, Indicator progress |
| Cyberpunk yellow | `#FCEE0A` | Indicator, warning, the clock's amber preset |
| Rebecca's mint | `#3DFFB0` | The Codex column |
| Neon gradient | cyan → violet → the signs' pale pink halo → magenta | Rim |
| The signs' pale pink halo | `#F090F0` | A faint tint on chosen date ranges |
| HUD frames | half-lit cyan 45° cuts with a hexagonal tick | `decor.hud`, two corners |
| CRT screens | 3.5% scan lines | Surface texture |

**Check.** Primary text is above 15:1 on every gradient stop and tertiary text above 5.5:1; the pale clock is about 14:1 and the yellow warning about 13.8:1; the magenta progress is about 3:1 on the yellow Indicator. Every rule passes the first time, with no adjustment.

The full file is [`cyberpunk-edgerunners.json`](../../src/BrimDeck.Core/Themes/cyberpunk-edgerunners.json). To start your own theme from it, choose it in settings and press "New from current theme".
