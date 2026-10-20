# Panel theme guide

The Notch, Capsule and Indicator, the quota notice, the expanded panel and its pop-ups are colored by a "panel theme". Choose it in **Settings › Panel theme**. There are three kinds:

| Kind | Themes |
| --- | --- |
| Basics | System, Dark (the default: the original pure black look), Light |
| Theme extensions | One Dark Pro, GitHub Dark, GitHub Light, Dracula Official, Night Owl |
| Custom | Cyberpunk: Edgerunners (the demo), and the theme files in the themes folder |

Choosing a theme in settings sets the theme, warning and critical colors of every app column back to "Default" (the theme's colors) and switches the clock to the style and color the theme suggests. Both can still be changed in "Quotas and usage" and "Collapsed view" until the next theme is chosen.

The theme extensions are ported from open-source VS Code themes and use their colors under each theme's MIT License: [One Dark Pro](https://github.com/Binaryify/OneDark-Pro), [GitHub Theme](https://github.com/primer/github-vscode-theme), [Dracula Official](https://github.com/dracula/visual-studio-code) and [Night Owl](https://github.com/sdras/night-owl-vscode-theme). The copyright and license notices of these projects ship with BrimDeck in `ThirdPartyNotices.txt`. Monokai Pro is a commercially licensed theme and is not included.

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
| `accent` / `onAccent` | Keyboard focus and chosen dates / text on them (3:1) |
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

The default colors of the quota columns, bars and rings: `claude`, `codex`, `antigravity`, `cursor`, `zcode`, `dsh`, `grokBot`, `manual` (every source entered by hand), `other`, and `warning` and `critical`. A color chosen for a column in "Quotas and usage" takes precedence.

### Clock

The clock style and color that suit the theme. Choosing the theme in settings switches the collapsed clock to them; it can still be changed on the Collapsed view page. Its fields are inherited one by one like the colors: a file that names only a `style` keeps its base theme's color.

```json
"clock": { "style": "segment", "color": "default", "custom": "#B9A8FF", "gradient": ["#FF79C6", "#BD93F9", "#8BE9FD"] }
```

`style` is one of `minimal`, `stacked`, `digits`, `dots`, `segment`, `neon`, `serif`, `wide`, `condensed`; `color` is `default` (the `clock` color above), `amber`, `cyan`, `pink`, `lime`, `custom`, `gradient` or `cover`. `palette` sets this theme's colors for the amber, cyan, pink and lime swatches, and `gradients` the preset gradients it offers (one to eight, each exactly three colors); both need 4.5:1 on the surface. When the theme changes, a preset gradient in use becomes the new theme's preset in the same place. `custom` is used when `color` is `custom`; `gradient`, exactly three colors, when `color` is `gradient`. On light themes the glow of the neon and seven-segment styles is softened so the digits stay sharp.

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
