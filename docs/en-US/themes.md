# Panel theme guide

The Notch, Capsule and Indicator, the quota notice, the expanded panel and its pop-ups are colored by a "panel theme". Choose it in **Settings › Panel theme**. There are three kinds:

| Kind | Themes |
| --- | --- |
| Basics | System, Dark (the default: the original pure black look), Light, Liquid Glass (light), Liquid Glass (dark) |
| Theme extensions | One Dark Pro, GitHub Dark, GitHub Light, Dracula Official, Night Owl, Island Holiday, Pixel Blocks, Cyberpunk: Edgerunners (the demo) |
| Custom | The theme files in the themes folder, which can be created, edited and deleted in settings |

Each theme remembers the theme, warning and critical colors of the app columns and the clock's style and color you change while using it: they are saved when you switch to another theme and come back when you return. A theme chosen for the first time uses its own colors and suggested clock. Restoring defaults in "Quotas and usage" or "Collapsed view" restores the current theme's defaults.

The theme extensions are ported from open-source VS Code themes and use their colors under each theme's MIT License: [One Dark Pro](https://github.com/Binaryify/OneDark-Pro), [GitHub Theme](https://github.com/primer/github-vscode-theme), [Dracula Official](https://github.com/dracula/visual-studio-code) and [Night Owl](https://github.com/sdras/night-owl-vscode-theme). The copyright and license notices of these projects ship with BrimDeck in `ThirdPartyNotices.txt`.

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

The full template is [`theme-template.json`](../../src/BrimDeck.Core/Templates/theme-template.json). It lists every color, app color and effect with its use and contrast minimum. **New from current theme** on the settings page fills the template with every value of the current theme and opens it; when you save, the page reloads it and the panel updates.

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

`style` is one of `minimal`, `stacked`, `digits`, `dots`, `segment`, `neon`, `serif`, `wide`, `condensed`, `pixel`; `color` is `default` (the `clock` color above), `amber`, `cyan`, `pink`, `lime`, `custom`, `gradient` or `cover`. `palette` sets this theme's colors for the amber, cyan, pink and lime swatches, and `gradients` the preset gradients it offers (one to eight, each exactly three colors); both need 4.5:1 on the surface. When the theme changes, a preset gradient in use becomes the new theme's preset in the same place. `custom` is used when `color` is `custom`; `gradient`, exactly three colors, when `color` is `gradient`. On light themes the glow of the neon and seven-segment styles is softened so the digits stay sharp.

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
| `pixel` | Pixel art: the outline's curves become steps of this many DIP (rounded to whole device pixels) and the island's text is drawn unsmoothed; with a `rimGradient` the rim becomes a bevel, its first color lighting the edges that face up and left and its last shading those that face down and right | 0, or 1–4 |
| `texture` | With `pixel`: squares of the pixel size a little lighter or darker than the surface, a block's grain | 0–0.12 |

#### Glass

A glass theme's surface is a translucent tint over the desktop rather than a solid color, so the desktop shows through the island. Readability is still guaranteed: text is checked over the tint on a **black** and on a **white** desktop, so a light glass needs a fairly dense tint (about 75%). The glass carries two lights along its edge: `rimGradient`, a specular rim, and `edge`, light that fades inward from the edge as in thick glass.

The glass is drawn as the opaque `surface` color instead while Windows' transparency effects are off (Settings › Personalization › Colors) or in high contrast; the "Current theme" section says so. The shadow under a glass island is drawn only outside it, so it never shows through.

Where Windows can, the desktop under the glass is blurred: BrimDeck puts a window that shows only the blurred backdrop right under the island (the composition engine's host backdrop brush, which DWM blurs), cut to the island's shape. The two windows cannot change shape on the same frame, so while the island opens, closes, changes page or boots, the blur steps aside and fades back in the new shape once the island has held still for about a tenth of a second; it never shows outside the island. With transparency effects off, in high contrast, on battery saver, in a remote session or on a Windows that cannot draw it, there is no blur and the glass is clear: what lies behind shows faintly, and the readability rules hold all the same.

### Motion (motion)

How the theme moves on BrimDeck's own windows: slow ambient light on the island, and how the pointer and the keyboard are answered. Like the effects, the section is merged key by key along the base chain, so a theme only writes what it changes. Every value is held to the range below (a value outside it is moved to its nearest end), so no theme file can make anything flash: brightness never changes more than three times a second. Nothing moves while animations are off in BrimDeck's settings or in Windows, or in high contrast; the ambient light also stops while the island is hidden or powering off, the session is locked, or battery saver is on. The settings window keeps its own hover and focus styles.

`ambient`: periods are seconds, and 0 turns an effect off.

| Key | Effect | Range |
| --- | --- | --- |
| `breathe` `breatheDepth` | The rim dims a little and comes back once per period; how far it dims | 3–30, 0–0.35 |
| `drift` | The rim gradient's colors travel one width in this time | 4–60 |
| `sweep` `sweepOpacity` | A faint scan line passes down the surface once per period (taking 2.4 s); its strength | 6–120, 0–0.12 |
| `flicker` `flickerDepth` | The neon sags softly once per period (taking 1.2 s); how deep | 20–300, 0–0.15 |

The answers to the pointer are drawn on the control's own visible shape, never around its layout box and never following the pointer. A press answers as the button goes down, holds while it is held and completes on release (or backs out when released elsewhere); Space and Enter on a focused control press it too.

| Key | Effect | Values |
| --- | --- | --- |
| `hover` | The mark on a hovered control (also `lens`: light pooled at the top of a glass bead and a bright top rim; `wave`: a faint tint and a small wave that rolls in along the bottom and settles; `slot`: a square highlight with a solid inner frame, as an inventory slot): `none`, `reveal` (faint fill, edge lit from the top), `lift` (soft shadow outside, light top edge), `underline` (subtle fill, 2 px bar along the bottom), `caret` (line highlight, caret bar at the left), `brackets` (HUD corner brackets), `sheen` (two-color tint and edge), `glow` (warm inner glow and a glint) | earlier names: `spot` = reveal, `edge` = underline, `neon` = brackets |
| `hoverStrength` | How strongly the mark shows | 0–0.3 |
| `press` | The press (also `bevel`: the bevel turns over, shaded at the top left and lit at the bottom right, as a pixel-art button pushed in; `jelly`: the light on the glass squashes wider and lower, shaded along the bottom only, and springs back once past its shape on release): `none`, `shade` (the shape shades, its edge firms), `sink` (the shadow collapses, pushed in), `indicator` (the bottom bar takes the highlight), `select` (a selection sweeps from the caret), `lock` (brackets clamp on with a chromatic ghost and a glitch slice; a scan line on release), `sparkle` (the tint deepens; stars on release), `ember` (a warm glow swells; fireflies on release) | earlier names: `pulse` and `ripple` = shade, `ring` = lock |
| `pressStrength` | How strongly the press shows | 0–0.35 |
| `particles` | What a completed press leaves for a moment, from the control's edges: `none`, `dust`, `pixel`, `spark`, `star`, `firefly`, `neon` | |
| `trail` | How strongly those particles show; 0 shows none | 0–0.6 |
| `focus` | A short accent where keyboard focus lands: `none`, `ring`, `glow`, `brackets`, `caret` | earlier name: `neon` = brackets |
| `pace` | Scales the animation length chosen in settings | 0.6–1.6 |
| `steps` | 0 moves smoothly; 2 to 8 moves in that many hard steps, for a mechanical feel. Glitch slices and scan lines are rationed to two a second however fast the clicks come | 0, 2–8 |
| `color` | The main mark (hover and focus) | a color key, `#RRGGBB`, or `system` (the Windows accent) |
| `secondary` | Its second tone: a gradient's end, a shadow, a ghost or a neutral bar | as `color` |
| `highlight` | The color of the moment of a press: an indicator, a selection, a scan line, sparkles, fireflies | as `color` |

Motion colors are kept readable on the surface like any other color chosen outside the theme. The "system" theme uses Windows' accent color and the quiet Fluent answers (`reveal` and `shade`, or `lift` and `sink` on a light base), with no ambient light, particles or steps; its base theme's strengths and pace stay. `Templates/theme-template.json` lists every key with its default.

### Day and night looks (night, daylight)

A theme can carry a night look: `night` holds the `colors`, `apps`, `effects`, `motion` and `clock` that change at night, laid over the day look by the local clock. `daylight`'s `day` and `night` are when the day and the night begin (`HH:mm`, 06:30 and 18:30 by default); the user can move them in Settings › Current theme › Daytime. At the hour the island's old look fades out over 1.6 seconds, like dusk; with animations off it switches at once. Both looks are held to the readability rules below.

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

**Island Holiday**: by day, warm sand (`#FFF6E8`) fading into sea foam, deep-sea text, a lagoon accent, a coral serif clock and a shoreline rim (turquoise, sky, sand). By night, a moonlit sea from navy into teal, a lantern-gold clock and a soft glow under the island. The rim breathes like a swell and its colors flow like water; a glint of sun crosses now and then by day, a rarer moonlight by night. A small wave rolls in along a hovered control; a press leaves sun glints by day (`sparkle` + `star`) and lets glowing plankton rise at night (`ember` + `firefly`).

**Pixel Blocks**: an unofficial pixel-art look inspired by block sandbox games such as Minecraft, using no artwork, fonts, textures or logos from any game. A deep slate surface with a 5% block grain, 2 DIP stepped corners, a bevel rim lit at the top left and shaded at the bottom right, and a hard shadow with no blur. Chat-style colors: white text, a yellow block clock (the `pixel` clock style), a green accent and a green experience-bar indicator; violet menus with a violet frame. Everything moves in four hard steps: `slot` on hover, `bevel` on press, square particles after; no ambient light, so it costs nothing while idle.

## Demo: Cyberpunk: Edgerunners

An unofficial fan palette. It uses colors only, no artwork, logos or fonts from the series.

**Style.** *Cyberpunk: Edgerunners* by Studio TRIGGER carries the retro-future look of 1980s anime: flat cel color, saturated neon, a violet-black sky over Night City with streets lit by magenta and cyan signs, and the signature yellow of *Cyberpunk 2077* in the opening and the interfaces. Lucy's hair runs from cyan to pink, Rebecca wears mint green and pink; scan lines, chromatic aberration and cut-corner HUD frames recur, and so does the moon.

| Source | Color | Panel role |
| --- | --- | --- |
| Night City's violet sky | `#0A0613` → `#1C0B30` | Surface and its top-to-bottom gradient |
| Moonlight | `#F4F0FF` / `#C9BEEB` / `#8F84BD` | Three text levels (pale lavender) |
| Cyan neon, Lucy's hair | `#00F0FF` | Accent, focus, the tint of switches and tracks |
| Magenta neon | `#FF2A6D` | Collapsed glow, liked song, Indicator progress |
| Cyberpunk yellow | `#FCEE0A` | Clock (seven-segment, like a HUD readout), Indicator, warning |
| Rebecca's mint | `#3DFFB0` | The Codex column |
| Neon gradient | cyan → violet → magenta | Rim |
| CRT screens | 4% scan lines | Surface texture |

**Check.** Primary text is above 15:1 on both gradient stops and tertiary text above 5:1; the yellow clock and warning are about 15:1 on the violet-black; the magenta progress is about 3:1 on the yellow Indicator. Every rule passes the first time, with no adjustment.

The full file is [`cyberpunk-edgerunners.json`](../../src/BrimDeck.Core/Themes/cyberpunk-edgerunners.json). To start your own theme from it, choose it in settings and press "New from current theme".
