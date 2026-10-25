using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrimDeck;
using BrimDeck.Core;
using BrimDeck.LiquidGlass;
using Path = System.IO.Path;

// The keyboard focus pass over the real panel offscreen (--liquid-pages <backgrounds> <output folder> --focus [--scale N]):
// every page of the page sheets in both liquid looks, its focusable controls visited with Tab as WPF's own keyboard navigation
// moves (MoveFocus Next from the island's first stop, the root cycling as a Window does), each one focused for real
// (Keyboard.Focus) and drawn with its focus mark over the glass of several backgrounds.
// The mark is what WPF shows for keyboard focus: the control's FocusVisualStyle, laid over the control as WPF's focus adorner
// lays it (a Control of that style, the control's size, its transform to the root), and whatever the control's own template
// changes on IsKeyboardFocused. It is found as the difference between the stage drawn focused and drawn with the focus parked
// elsewhere (the same scroll, the same layout), so a mark of either kind is measured the same way.
// Checked for every stop and background:
//   (a) the mark shows: across the middle of each side of the control, from outside in, the first band that changed is at
//       least 2 DIP wide (its pixels changed by at least 40 levels and a third of the most changed one; floor(2 x scale)
//       device pixels), and the part of it that stands out most against the pixel just outside it, as composed over the
//       glass, does so at least 3:1 (the 10th percentile of the scanlines); also logged: the band's core (its most changed
//       pixel) against that pixel, and the core focused against the same pixel unfocused (WCAG 2.4.13's change of contrast);
//   (b) Tab visits every focusable control of the island once (none twice, none left out, none outside the island), in reading
//       order: never back up within the same column, never right to left within the same row.
// Writes focus.log (focus-at<percent>.log at another scale), a sheet per look and sheet background (focus-<background>-<look>.png:
// per page the island with its stops numbered in Tab order, then each stop focused, zoomed 2x pixel for pixel).
internal static partial class LiquidPages
{
    // Light to dark: the light glass's worst ground is white, the dark glass's black.
    private static readonly string[] FocusGrounds = ["white-150", "chrome-light-150", "photo-light-150", "grayui-150", "bloom-150", "video-150", "photo-dark-150", "chrome-dark-150", "black-150"];
    private static readonly string[] SheetGrounds = ["white-150", "chrome-dark-150"];
    private const double WantedRing = 2, WantedContrast = 3;

    private sealed record Ground(string Name, byte[] Pixels, int Width, int Height);
    // The glass of one page over one ground: the desktop with the glass laid on (under), its size, and where the layer's origin falls.
    private sealed record Under(Ground Ground, byte[] Pixels, int Width, int Height, int OffsetX);
    private sealed record Shot(byte[] Focused, byte[] Plain, int Width, int Height);

    public static int RunFocus(string backgrounds, string folder, double scale)
    {
        Scale = scale;
        Directory.CreateDirectory(folder);
        var log = new StringWriter(CultureInfo.InvariantCulture);
        var console = Console.Out;
        Console.SetOut(new Tee(console, log));
        try { return Focus(backgrounds, folder); }
        finally
        {
            Console.SetOut(console);
            File.WriteAllText(Path.Combine(folder, Math.Abs(Scale - 1.5) < .001 ? "focus.log" : $"focus-at{Scale * 100:0}.log"), log.ToString());
        }
    }

    private static int Focus(string backgrounds, string folder)
    {
        Console.WriteLine($"Keyboard focus pass. Display scale: {Scale * 100:0} %; ring wanted at least {WantedRing} DIP ({Math.Floor(WantedRing * Scale)} device pixels) and {WantedContrast}:1 against the pixels just outside it");
        var files = FocusGrounds.Select(name => Path.Combine(backgrounds, name + ".png")).Where(File.Exists).ToList();
        if (files.Count == 0) files = Directory.GetFiles(backgrounds, "*-150.png").Order().ToList();
        if (files.Count == 0) { Console.WriteLine("FAIL: no *-150.png backgrounds in " + backgrounds); return 1; }
        var grounds = files.Select(LoadGround).ToList();
        Console.WriteLine("Backgrounds: " + string.Join(", ", grounds.Select(g => g.Name)));

        var (app, deck, root, source) = Offscreen(animations: false);
        using var _ = source;
        // The root cycles as the Window it stands in for does (Window's own defaults for these three).
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(root, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(root, KeyboardNavigationMode.Cycle);
        // Above the stage: the focus mark, as WPF's adorner layer holds it, and a place to park the focus (no tab stop, no mark).
        var overlay = new Canvas { IsHitTestVisible = false, Focusable = false };
        var park = new Border { Width = 1, Height = 1, Focusable = true, FocusVisualStyle = null };
        KeyboardNavigation.SetIsTabStop(park, false);
        overlay.Children.Add(park);
        root.Children.Add(overlay);

        var cover = Cover();
        var track = Track();
        var snapshot = Snapshot();
        using var device = LiquidPreview.CreateDevice();
        using var renderer = new LiquidRenderer(device);

        var tiles = new Dictionary<(string Ground, string Look), List<(string Label, BitmapSource Tile, int Column, int Row)>>();
        var lowest = new Dictionary<string, (double Ratio, string Where)>();
        var thinnest = new Dictionary<string, (double Width, string Where)>();
        int stops = 0;
        foreach (var look in Looks)
        {
            UseLook(app, deck, root, look);
            string lookName = look == PanelThemes.GlassDark ? "liquid dark" : "liquid light";
            for (int row = 0; row < Pages.Length; row++)
            {
                var (page, label) = Pages[row];
                string name = $"{look} {page}";
                Keyboard.Focus(park);
                Show(deck, root, page, track, cover, snapshot);
                var island = deck.Island;
                var at = island.TranslatePoint(new Point(), root);
                int x = (int)Math.Round(at.X * Scale), y = (int)Math.Round(at.Y * Scale);
                int w = (int)Math.Round(island.ActualWidth * Scale), h = (int)Math.Round(island.ActualHeight * Scale);
                int width = (int)Math.Ceiling(root.ActualWidth * Scale), height = (int)Math.Ceiling(root.ActualHeight * Scale);
                var c = island.CornerRadius;
                var frame = (Top: c.TopLeft * Scale, Bottom: c.BottomLeft * Scale, Shoulder: island.ShoulderRadius * Scale, Open: deck.GlassOpenness());
                var unders = grounds.Select(g => GlassUnder(device, renderer, g, x, y, w, h, frame, look == PanelThemes.GlassDark)).ToList();

                var order = TabOrder(deck, root, island, park, name);
                var plainIsland = Shoot(deck, overlay, width, height);
                // The order map: the island unfocused over the sheet grounds, each stop numbered.
                var boxes = order.Select(e => Box(e, root)).ToList();
                foreach (var under in unders.Where(u => SheetGrounds.Contains(u.Ground.Name)))
                {
                    var islandBox = new Rect(at.X, at.Y, island.ActualWidth, island.ActualHeight);
                    var map = Numbered(Crop(plainIsland, width, height, under, Inflate(islandBox, 10)), Inflate(islandBox, 10), boxes);
                    Add(tiles, (under.Ground.Name, look), ($"{label} · {lookName} · Tab order ({order.Count} stops)", map, 0, row));
                }

                for (int i = 0; i < order.Count; i++)
                {
                    var element = order[i];
                    string stop = $"{name} #{i + 1} {Describe(element)}";
                    var shot = Focused(deck, root, overlay, park, element, width, height);
                    var box = Box(element, root);
                    stops++;
                    foreach (var under in unders)
                    {
                        var (ok, ring, ratio) = Measure(shot, box, under, $"{under.Ground.Name} {stop}");
                        if (!lowest.TryGetValue(look, out var low) || ratio < low.Ratio) lowest[look] = (ratio, $"{under.Ground.Name} {stop}");
                        if (!thinnest.TryGetValue(look, out var thin) || ring < thin.Width) thinnest[look] = (ring, $"{under.Ground.Name} {stop}");
                        if (SheetGrounds.Contains(under.Ground.Name))
                        {
                            var zone = Inflate(box, 10);
                            Add(tiles, (under.Ground.Name, look), ($"#{i + 1} {Describe(element)}{(ok ? "" : " — FAIL")}", Crop(shot.Focused, shot.Width, shot.Height, under, zone), i + 1, row));
                        }
                    }
                }
                Keyboard.Focus(park);
            }
        }

        foreach (var ((ground, look), list) in tiles)
        {
            string file = $"focus-{ground}-{(look == PanelThemes.GlassDark ? "dark" : "light")}" + (Math.Abs(Scale - 1.5) < .001 ? "" : $"-at{Scale * 100:0}") + ".png";
            LiquidPreview.Save(FocusSheet($"{ground} · {(look == PanelThemes.GlassDark ? "liquid dark" : "liquid light")} — keyboard focus on every Tab stop at {Scale * 100:0} % (zoomed 2x)", list), Path.Combine(folder, file));
            Console.WriteLine($"Wrote {file}");
        }
        Console.WriteLine($"Stops measured: {stops} (each over {grounds.Count} backgrounds)");
        foreach (var look in Looks)
        {
            if (lowest.TryGetValue(look, out var low)) Console.WriteLine($"SUMMARY {look}: lowest ring contrast (10th percentile of a stop) {low.Ratio:0.00}:1 at {low.Where}");
            if (thinnest.TryGetValue(look, out var thin)) Console.WriteLine($"SUMMARY {look}: thinnest ring {thin.Width:0.00} DIP at {thin.Where}");
        }
        Console.WriteLine(_failures == 0 ? "PASS: all focus checks" : $"{_failures} focus checks failed");
        return _failures == 0 ? 0 : 1;
    }

    // ----- Tab order -----

    // Tab from the island's first stop until it comes round again, as WPF moves the keyboard focus; checked against the
    // focusable controls the island shows.
    private static List<FrameworkElement> TabOrder(MainWindow deck, Grid root, FrameworkElement island, UIElement park, string name)
    {
        var order = new List<FrameworkElement>();
        // Where each stop stands while it has the focus (a list scrolls its focused row into view).
        var seenAt = new Dictionary<FrameworkElement, Rect>(ReferenceEqualityComparer.Instance);
        var outside = new List<string>();
        Keyboard.Focus(park);
        island.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        Settle(root, deck);
        var first = Keyboard.FocusedElement as FrameworkElement;
        var current = first;
        for (int step = 0; current is not null && step < 200; step++)
        {
            if (!current.IsKeyboardFocused) { Report(false, $"{name}: Keyboard.Focus did not take on {Describe(current)} (focus offscreen not possible?)"); break; }
            if (island.IsAncestorOf(current)) { order.Add(current); seenAt[current] = Box(current, root); } else outside.Add(Describe(current));
            current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Settle(root, deck);
            var next = Keyboard.FocusedElement as FrameworkElement;
            if (next is null || ReferenceEquals(next, current) || ReferenceEquals(next, first)) break;
            current = next;
        }
        Keyboard.Focus(park);
        Settle(root, deck);

        var expected = Focusables(island);
        var seen = new HashSet<FrameworkElement>(ReferenceEqualityComparer.Instance);
        var twice = order.Where(e => !seen.Add(e)).Select(Describe).Distinct().ToList();
        var missed = expected.Where(group => !group.Any(seen.Contains)).Select(group => Describe(group[0]) + (group.Count > 1 ? $" (one of {group.Count} in a group)" : "")).ToList();
        var lines = order.Select((e, i) => { var b = seenAt[e]; return $"    {i + 1,2}. {Describe(e)} at ({b.X:0.##},{b.Y:0.##}) {b.Width:0.##}x{b.Height:0.##} DIP, mark {StyleName(e)}"; });
        Console.WriteLine($"{name}: Tab order, {order.Count} stops ({expected.Count} focusable controls or groups shown):\n" + string.Join("\n", lines));
        // Controls the mouse can click (the hand cursor) that Tab never reaches: neither they nor anything around them takes focus.
        var mouseOnly = new List<string>();
        void Clickable(DependencyObject node, bool focusable)
        {
            if (node is UIElement { IsVisible: false }) return;
            focusable |= node is UIElement { Focusable: true };
            if (!focusable && node is FrameworkElement { Cursor: var cursor } element && cursor == Cursors.Hand && !HasFocusable(element)) { mouseOnly.Add(Describe(element)); return; }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Clickable(VisualTreeHelper.GetChild(node, i), focusable);
        }
        static bool HasFocusable(DependencyObject node)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (VisualTreeHelper.GetChild(node, i) is var child && (child is UIElement { Focusable: true, IsVisible: true } || HasFocusable(child))) return true;
            return false;
        }
        Clickable(island, false);
        if (mouseOnly.Count > 0) Console.WriteLine($"NOTE: {name}: clickable with the mouse but not reachable with Tab: " + string.Join(", ", mouseOnly.Distinct()));
        if (order.Count == 0 && expected.Count == 0) { Console.WriteLine($"NOTE: {name}: nothing on it takes keyboard focus"); return order; }
        Report(twice.Count == 0, $"{name}: Tab visits each stop once" + (twice.Count > 0 ? " — twice: " + string.Join(", ", twice) : ""));
        Report(missed.Count == 0, $"{name}: Tab reaches every focusable control shown" + (missed.Count > 0 ? " — never reached: " + string.Join(", ", missed) : ""));
        Report(outside.Count == 0, $"{name}: Tab stays within the island" + (outside.Count > 0 ? " — also visits: " + string.Join(", ", outside) : ""));
        // Tab cycles, so the order is read from the stop nearest the top left (the toolbar's first) round to the one before it.
        var backwards = new List<string>();
        int start = order.Count == 0 ? 0 : order.IndexOf(order.MinBy(e => { var b = seenAt[e]; return (Math.Round(b.Top / 8), b.Left); })!);
        var cycle = order.Skip(start).Concat(order.Take(start)).ToList();
        Console.WriteLine($"{name}: read from #{start + 1} round: " + string.Join(" → ", cycle.Select(e => order.IndexOf(e) + 1)));
        for (int i = 1; i < cycle.Count; i++)
        {
            Rect a = seenAt[cycle[i - 1]], b = seenAt[cycle[i]];
            double across = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left), down = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
            bool up = b.Bottom <= a.Top + 1 && across > 0;
            bool leftward = down > .5 * Math.Min(a.Height, b.Height) && b.Right <= a.Left + 1;
            if (up || leftward) backwards.Add($"#{order.IndexOf(cycle[i - 1]) + 1} {Describe(cycle[i - 1])} -> #{order.IndexOf(cycle[i]) + 1} {Describe(cycle[i])} ({(up ? "up the same column" : "right to left in a row")})");
        }
        Report(backwards.Count == 0, $"{name}: Tab follows reading order" + (backwards.Count > 0 ? " — backwards: " + string.Join("; ", backwards) : ""));
        return order;
    }

    // The focusable controls the island shows, in tree order: each a group of one, or every stop of a container that Tab enters
    // once (TabNavigation Once); none under a container Tab skips (None).
    private static List<List<FrameworkElement>> Focusables(FrameworkElement island)
    {
        var groups = new List<List<FrameworkElement>>();
        var once = new Dictionary<DependencyObject, List<FrameworkElement>>(ReferenceEqualityComparer.Instance);
        void Walk(DependencyObject node, DependencyObject? group)
        {
            if (node is UIElement { IsVisible: false } or UIElement { IsEnabled: false }) return;
            if (!ReferenceEquals(node, island))
            {
                var mode = KeyboardNavigation.GetTabNavigation(node);
                if (mode == KeyboardNavigationMode.None) return;
                if (mode == KeyboardNavigationMode.Once && group is null) group = node;
            }
            if (node is FrameworkElement { Focusable: true, ActualWidth: > 0, ActualHeight: > 0 } element && KeyboardNavigation.GetIsTabStop(element) && !ReferenceEquals(node, island))
            {
                if (group is null) groups.Add([element]);
                else if (once.TryGetValue(group, out var list)) list.Add(element);
                else { once[group] = list = [element]; groups.Add(list); }
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i), group);
        }
        Walk(island, null);
        return groups;
    }

    // ----- One stop -----

    // The stage focused on the control, with its mark laid over it as WPF's focus adorner lays it, and with the focus parked.
    private static Shot Focused(MainWindow deck, Grid root, Canvas overlay, UIElement park, FrameworkElement element, int width, int height)
    {
        Keyboard.Focus(element);
        Settle(root, deck);
        Control? mark = null;
        if (element.FocusVisualStyle is { } style)
        {
            mark = new Control { Style = style, Width = element.ActualWidth, Height = element.ActualHeight, Focusable = false, IsHitTestVisible = false, FlowDirection = element.FlowDirection };
            mark.RenderTransform = new MatrixTransform(element.TransformToAncestor(root) is Transform t ? t.Value : Matrix.Identity);
            // At the window's scale, as WPF's adorner layer is: given before its template's visuals are made, and again after.
            VisualTreeHelper.SetRootDpi(mark, new DpiScale(Scale, Scale));
            mark.ApplyTemplate();
            VisualTreeHelper.SetRootDpi(mark, new DpiScale(Scale == 1 ? 2 : 1, Scale == 1 ? 2 : 1));
            VisualTreeHelper.SetRootDpi(mark, new DpiScale(Scale, Scale));
            overlay.Children.Add(mark);
            Settle(root, deck);
        }
        var focused = Shoot(deck, overlay, width, height);
        if (mark is not null) overlay.Children.Remove(mark);
        Keyboard.Focus(park);
        Settle(root, deck);
        var plain = Shoot(deck, overlay, width, height);
        return new Shot(focused, plain, width, height);
    }

    // The stage and, over it, the overlay: one layer, premultiplied BGRA.
    private static byte[] Shoot(MainWindow deck, Canvas overlay, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(deck.Stage);
        bitmap.Render(overlay);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    // (a): across the middle of each side, from outside in, the first band the focus changed: its width, and its core against
    // the pixel just outside it over the glass.
    private static (bool Ok, double Ring, double Ratio) Measure(Shot shot, Rect box, Under under, string name)
    {
        int left = (int)Math.Floor(box.Left * Scale), top = (int)Math.Floor(box.Top * Scale), right = (int)Math.Ceiling(box.Right * Scale) - 1, bottom = (int)Math.Ceiling(box.Bottom * Scale) - 1;
        int pad = (int)Math.Ceiling(10 * Scale);
        int cx = (left + right) / 2, cy = (top + bottom) / 2;
        var widths = new List<int>[4];
        var ratios = new List<double>();
        var changes = new List<double>();
        var cores = new List<double>();
        int sides = 0;
        for (int side = 0; side < 4; side++)
        {
            widths[side] = [];
            bool vertical = side < 2; // top and bottom sides: scan columns
            int from = vertical ? left : top, to = vertical ? right : bottom, span = to - from;
            for (int along = from + span * 3 / 10; along <= from + span * 7 / 10; along++)
            {
                int start = side switch { 0 => top - pad, 1 => bottom + pad, 2 => left - pad, _ => right + pad };
                int end = side switch { 0 => cy, 1 => cy, 2 => cx, _ => cx };
                int stepSign = Math.Sign(end - start);
                if (stepSign == 0) continue;
                (int X, int Y) At(int across) => vertical ? (along, across) : (across, along);
                int first = int.MinValue;
                for (int p = start; p != end; p += stepSign)
                {
                    var (px, py) = At(p);
                    if (Changed(shot, px, py)) { first = p; break; }
                }
                if (first == int.MinValue) continue;
                // The band, and how much each of its pixels changed as composed over the glass.
                var band = new List<(int P, double Change)>();
                for (int p = first; p != end && Changed(shot, At(p).X, At(p).Y); p += stepSign)
                    band.Add((p, Distance(Composed(shot.Focused, shot, At(p), under), Composed(shot.Plain, shot, At(p), under))));
                double most = band.Max(b => b.Change);
                if (most <= 0) continue;
                int core = band.First(b => b.Change == most).P;
                // The ring's width: its pixels clearly changed (by at least 40 levels and a third of the most changed one).
                var solid = band.Where(b => b.Change >= Math.Max(40, most / 3)).ToList();
                widths[side].Add(solid.Count);
                // Against the pixel just outside it: the part of the ring that stands out most (the accent, or the surface line
                // inside it where the accent sits close to the glass's own tone), and the ring's core (its most changed pixel).
                var outside = Composed(shot.Focused, shot, At(first - stepSign), under);
                var ring = Composed(shot.Focused, shot, At(core), under);
                cores.Add(Ratio(ring, outside));
                ratios.Add(solid.Max(b => Ratio(Composed(shot.Focused, shot, At(b.P), under), outside)));
                changes.Add(Ratio(ring, Composed(shot.Plain, shot, At(core), under)));
            }
            if (widths[side].Count > 0) sides++;
        }
        if (sides == 0) { Report(false, $"{name}: no focus mark found around it"); return (false, 0, 0); }
        var medians = widths.Where(list => list.Count > 0).Select(list => { list.Sort(); return list[list.Count / 2]; }).ToList();
        double thinnest = medians.Min() / Scale;
        ratios.Sort(); changes.Sort(); cores.Sort();
        double low = ratios[ratios.Count / 10], change = changes[changes.Count / 10], coreLow = cores[cores.Count / 10];
        bool ok = sides >= 2 && medians.Min() >= Math.Floor(WantedRing * Scale) && low >= WantedContrast;
        Report(ok, $"{name}: ring {thinnest:0.00} DIP ({medians.Min()} px, narrowest side; {sides} sides found), against the pixels just outside it {low:0.00}:1 (10th percentile; lowest {ratios[0]:0.00}:1, median {ratios[ratios.Count / 2]:0.00}:1 over {ratios.Count} scanlines; its core alone {coreLow:0.00}:1), focused against unfocused {change:0.00}:1");
        return (ok, thinnest, low);
    }

    private static bool Changed(Shot shot, int x, int y)
    {
        if (x < 0 || y < 0 || x >= shot.Width || y >= shot.Height) return false;
        int i = (y * shot.Width + x) * 4;
        int most = 0;
        for (int k = 0; k < 4; k++) most = Math.Max(most, Math.Abs(shot.Focused[i + k] - shot.Plain[i + k]));
        return most >= 24;
    }

    // A layer pixel laid over the glass under it (the desktop where the layer reaches past the island).
    private static (int R, int G, int B) Composed(byte[] layer, Shot shot, (int X, int Y) at, Under under)
    {
        int dx = Math.Clamp(at.X - under.OffsetX, 0, under.Width - 1), dy = Math.Clamp(at.Y, 0, under.Height - 1);
        int d = (dy * under.Width + dx) * 4;
        double r = under.Pixels[d + 2], g = under.Pixels[d + 1], b = under.Pixels[d];
        if (at.X >= 0 && at.Y >= 0 && at.X < shot.Width && at.Y < shot.Height)
        {
            int l = (at.Y * shot.Width + at.X) * 4;
            double a = layer[l + 3] / 255d;
            r = layer[l + 2] + (1 - a) * r; g = layer[l + 1] + (1 - a) * g; b = layer[l] + (1 - a) * b;
        }
        return ((int)Math.Clamp(Math.Round(r), 0, 255), (int)Math.Clamp(Math.Round(g), 0, 255), (int)Math.Clamp(Math.Round(b), 0, 255));
    }

    private static double Distance((int R, int G, int B) a, (int R, int G, int B) b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
    private static double Ratio((int R, int G, int B) a, (int R, int G, int B) b)
    {
        double la = Luminance(a.R, a.G, a.B), lb = Luminance(b.R, b.G, b.B);
        return (Math.Max(la, lb) + .05) / (Math.Min(la, lb) + .05);
    }

    // ----- The glass -----

    private static Ground LoadGround(string file)
    {
        var decoded = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(Path.GetFullPath(file)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
        var pixels = new byte[decoded.PixelWidth * decoded.PixelHeight * 4];
        decoded.CopyPixels(pixels, decoded.PixelWidth * 4, 0);
        return new Ground(Path.GetFileNameWithoutExtension(file), pixels, decoded.PixelWidth, decoded.PixelHeight);
    }

    // As Compose: the desktop, the glass for the island's shape laid on it at the top centre.
    private static Under GlassUnder(Vortice.Direct3D11.ID3D11Device device, LiquidRenderer renderer, Ground ground, int x, int y, int w, int h,
        (double Top, double Bottom, double Shoulder, double Open) shape, bool dark)
    {
        int dw = ground.Width, dh = Math.Max(ground.Height, y + h + (int)Math.Ceiling(40 * Scale));
        var desktop = new byte[dw * dh * 4];
        for (int row = 0; row < dh; row++) Buffer.BlockCopy(ground.Pixels, Math.Min(row, ground.Height - 1) * dw * 4, desktop, row * dw * 4, dw * 4);
        int gx = (int)Math.Round((dw - w) / 2.0);
        var frame = LiquidFrame.Preset(dark, w, h, (float)shape.Top, (float)shape.Bottom, (float)shape.Shoulder, (float)Scale) with { Open = (float)shape.Open };
        var glass = LiquidPreview.Render(device, renderer, desktop, dw, dh, (gx, y, w, h), frame, (int)Math.Ceiling(48 * Scale));
        for (int row = 0; row < h; row++)
            for (int column = 0; column < w; column++)
                Over(glass, (row * w + column) * 4, desktop, ((y + row) * dw + gx + column) * 4);
        return new Under(ground, desktop, dw, dh, x - gx);
    }

    // ----- The sheet -----

    private static Rect Box(FrameworkElement element, Visual root) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
    private static Rect Inflate(Rect rect, double by) { rect.Inflate(by, by); return rect; }

    private static BitmapSource Crop(byte[] layer, int width, int height, Under under, Rect zone)
    {
        int x0 = (int)Math.Floor(zone.Left * Scale), y0 = Math.Max(0, (int)Math.Floor(zone.Top * Scale));
        int cw = (int)Math.Ceiling(zone.Width * Scale), ch = (int)Math.Ceiling(zone.Bottom * Scale) - y0;
        var shot = new Shot(layer, layer, width, height);
        var pixels = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                var (r, g, b) = Composed(layer, shot, (x0 + x, y0 + y), under);
                int i = (y * cw + x) * 4;
                pixels[i] = (byte)b; pixels[i + 1] = (byte)g; pixels[i + 2] = (byte)r; pixels[i + 3] = 255;
            }
        var bitmap = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Bgra32, null, pixels, cw * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // The island with each stop's box outlined thinly and numbered in Tab order.
    private static BitmapSource Numbered(BitmapSource island, Rect zone, List<Rect> boxes)
    {
        var visual = new DrawingVisual();
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x30, 0x80)), 1);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(island, new Rect(0, 0, island.PixelWidth, island.PixelHeight));
            for (int i = 0; i < boxes.Count; i++)
            {
                var b = new Rect((boxes[i].X - zone.X) * Scale, (boxes[i].Y - zone.Y) * Scale, boxes[i].Width * Scale, boxes[i].Height * Scale);
                dc.DrawRectangle(null, pen, b);
                var text = new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11, Brushes.White, 1);
                var badge = new Rect(b.X, b.Y, text.Width + 6, text.Height + 2);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xE0, 0xD0, 0x10, 0x60)), null, badge);
                dc.DrawText(text, new Point(b.X + 3, b.Y + 1));
            }
        }
        var bitmap = new RenderTargetBitmap(island.PixelWidth, island.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // Rows are pages; the first tile of a row is the order map (1x), the rest each stop focused (2x, nearest pixel), wrapping.
    private static BitmapSource FocusSheet(string title, List<(string Label, BitmapSource Tile, int Column, int Row)> tiles)
    {
        const int gap = 14, label = 22, top = 40, wrap = 3600;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        var face = new Typeface("Segoe UI");
        var placed = new List<(string Label, BitmapSource Tile, double X, double Y, double W, double H)>();
        double y = top, widest = 0;
        foreach (var row in tiles.GroupBy(t => t.Row).OrderBy(g => g.Key))
        {
            double x = gap, rowHeight = 0;
            foreach (var (text, tile, column, _) in row.OrderBy(t => t.Column))
            {
                double zoom = column == 0 ? 1 : 2, w = tile.PixelWidth * zoom, h = tile.PixelHeight * zoom;
                if (x > gap && x + w > wrap) { y += rowHeight + label + gap; x = gap; rowHeight = 0; }
                placed.Add((text, tile, x, y, w, h));
                x += Math.Max(w, 160) + gap; rowHeight = Math.Max(rowHeight, h); widest = Math.Max(widest, x);
            }
            y += rowHeight + label + gap * 2;
        }
        int width = (int)Math.Ceiling(Math.Max(widest, 900)), height = (int)Math.Ceiling(y);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20)), null, new Rect(0, 0, width, height));
            dc.DrawText(new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 18, Brushes.White, 1), new Point(gap, 10));
            var ink = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE6));
            foreach (var (text, tile, x, ty, w, h) in placed)
            {
                dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 12, ink, 1) { MaxTextWidth = Math.Max(w, 160), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis }, new Point(x, ty + 3));
                dc.DrawImage(tile, new Rect(x, ty + label, w, h));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> lists, TKey key, TValue value) where TKey : notnull
    {
        if (!lists.TryGetValue(key, out var list)) lists[key] = list = [];
        list.Add(value);
    }

    // ----- Names -----

    private static string Describe(FrameworkElement element)
    {
        string? what = AutomationProperties.GetName(element) is { Length: > 0 } automation ? automation
            : element.ToolTip is string tip && tip.Length > 0 ? tip
            : element is ContentControl { Content: string content } && content.Length > 0 ? content
            : element is ContentControl { Content: TextBlock { Text: { Length: > 0 } text } } ? text
            : element.Name is { Length: > 0 } named ? named : null;
        if (what is null)
            foreach (var shown in Texts(element)) { what = shown; break; }
        what = what?.Replace('\n', ' ');
        if (what is { Length: > 28 }) what = what[..27] + "…";
        return element.GetType().Name + (what is null ? "" : $" \"{what}\"");
    }

    private static IEnumerable<string> Texts(DependencyObject node)
    {
        if (node is TextBlock { Text: { Length: > 0 } text }) yield return text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var inner in Texts(VisualTreeHelper.GetChild(node, i))) yield return inner;
    }

    private static string StyleName(FrameworkElement element)
    {
        if (element.FocusVisualStyle is not { } style) return "none (own template)";
        foreach (var key in new[] { "PanelKeyboardFocus", "PanelWideKeyboardFocus", "PanelOutsetKeyboardFocus", "MusicKeyboardFocus", "MusicSeekKeyboardFocus", "ControlDialogKeyboardFocus" })
            if (ReferenceEquals(element.TryFindResource(key), style)) return key;
        return ReferenceEquals(style, element.TryFindResource(SystemParameters.FocusVisualStyleKey)) ? "the system's dotted rectangle" : "a style of its own";
    }

    private sealed class Tee(TextWriter first, TextWriter second) : TextWriter
    {
        public override Encoding Encoding => first.Encoding;
        public override void Write(char value) { first.Write(value); second.Write(value); }
        public override void Write(string? value) { first.Write(value); second.Write(value); }
        public override void WriteLine(string? value) { first.WriteLine(value); second.WriteLine(value); }
    }
}
