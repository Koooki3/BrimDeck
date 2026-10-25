// Liquid glass (LiquidRenderer): one pass that draws the island as a pane of liquid glass over the desktop captured under it.
//
// Ported from xiaofangstudio/liquid-glass-web (showcase.html, commit 3094add; its README states the code is MIT; its refraction engine is
// shuding/liquid-glass, MIT, (c) 2025 Shu Ding, commit a2d2e84), as the browser actually draws it:
//   The displacement map: for every texel of the element (CSS pixels), uv across its box, ix = x / W - .5, iy = y / H - .5;
//   d = roundedRectSDF(ix, iy, .3, .2, rad = .6); s = smoothStep(0, 1, smoothStep(.8, 0, d - .15 / edge / strength)); the shift
//   is (ix * s + .5) * W - x, so the middle is flat and a ring at the edge pulls what lies inward out to the rim. The shifts go
//   into an 8-bit canvas (Uint8ClampedArray, rounded), normalised by half the largest (MaxHalf), and feDisplacementMap's scale
//   is MaxHalf * ref: every shift is clamped to a quarter of the largest and stepped in 1/255ths.
//   The page sets the filter's interpolation space as "colorInterpolationFilters", which the browser ignores (SVG attributes
//   are hyphenated), so the filter runs in linearRGB: the backdrop's colours are filtered in linear light (LinearFilters), and
//   that is reproduced. The browser also reads the map's bytes as linear light - a byte of 128, no shift, reads as .216, and
//   every shift gains a constant -.284 * MaxHalf * ref - which on a symmetric island makes the ring lopsided (strong at the
//   top left, almost none at the right and bottom; round-8 d1-offset-island.png). That one thing is not reproduced: the map
//   is read as the page meant it, as sRGB bytes with no constant shift (LinearMap 0; 1 is the browser's, for the proof sheets).
//   feDisplacementMap reads the backdrop at whole pixels: the shift is rounded to whole device pixels and the pixel there taken
//   as it is. At a scale above 100 % the map is drawn up to device pixels first, by a cubic (Mitchell) between its bytes, from
//   the element's corner snapped to whole device pixels, its texels its whole CSS size (Displacement). The backdrop the
//   filter sees is the element's box only, mirrored past its edges (a shift longer than the box, as in a low bar, folds back
//   again). An element between device pixels: its box (background, clip) runs from round(x) to round(x + w), half way rounding
//   up; the filter's region from round(x) for its exact size, cut at x + w, to the nearest pixel - so where the corner
//   snapped back, the box's last column or row has no filter (FilterEnd). All of this was read off the browser itself (a
//   backdrop whose pixels encode their own place, harness fid/; round 9: fid/probe.py, rects.py).
//   backdrop-filter then: blur(0.25px) (under a device pixel: left out) contrast(1.2) brightness(1.05) saturate(1.1).
//   background: rgba(255, 255, 255, .08).
//   box-shadow: 0 4px 8px rgba(0,0,0,.22) (outside the glass: drawn by the host), 0 -8px 20px inset rgba(0,0,0,.14).
//   Pressed (a button): the inset glow 0 0 16px 3px rgba(255,255,255,.55) and inset 0 -8px 16px rgba(0,0,0,.18), at once.
//   Shadows are Gaussian (deviation = blur / 2) over the rounded box, worked out as the browser does: the box's edge function
//   along x (erf) integrated over y with the Gaussian.
// Lengths in the source are CSS pixels: here device pixels over Scale.
//
// Adapted to the island (Faithful 0; 1 is the source exactly, for the proof sheets):
//   The outline is IslandBorder.BuildOutline's, a signed distance in device pixels: a capsule or rounded rectangle, or a shape
//   square at the top (the open panel, the notch) whose top edge is the screen's. A square top's box reaches TopExtra above the
//   screen, so the ring and the inset shadow never show along it; there the inset shadow's sides are narrowed (Narrow).
//   The source's uv field stretches with the element (a wide pill gets a wide, shallow ring, its ends pulled from the far
//   middle): here it is the source's field for a square of the shape's shorter side, its middle stretched along the longer one.
//   Low shapes take a smaller inset shadow (InsetY, InsetBlur, InsetAlpha from the renderer).
//   Legibility (Low, High; Legible): the glass as the port draws it (filtered, tinted) is mapped on its luma alone, its chroma
//   following in proportion (no channel lifted on its own), monotonically (no two pixels change places), into the band the
//   theme's text needs: light glass at least Low, dark glass at most High (both as they are after the tint). The backdrop's 2nd
//   and 98th percentiles (BandA, BandB, measured by the renderer before the filters) are passed through the filters and the
//   tint here, so the map is fitted to the glass the port draws. It only compresses, about an anchor: the dark glass about its
//   darkest end (color = a + (color - a) k), which is never left darker than the backdrop's own darkest (the port's linear-light
//   contrast crushes a dark page to black: that much is given back), the light glass alike about its brightest. Only where the
//   anchor itself is past the band does the whole range shift, and only as far as the band asks (its slope growing from 0 as
//   the anchor goes past, so the map changes continuously with the backdrop). The few pixels
//   past the percentiles (a word on a page, finer than the measure's grain) are held to the band with a soft knee.
//   Open panel (Open, the island's own opening, eased as it opens): the backdrop is frosted (FrostRadius, from the mips) and
//   what lies behind the panel's text is held within Cap:1 of contrast about the backdrop's mean (BandMean), so a page behind
//   cannot compete with the page on it.
//   The capsule, the notch and the bar (Open 0) stay clear, the port's own glass.
// The output is premultiplied; outside the outline it is transparent. Edge pixels take their coverage from four subsamples.

cbuffer Frame : register(b0)
{
    float2 Size;        // island, device pixels
    float2 Inverse;     // 1 / the background's size
    float Pad;          // background padding on each side
    float HalfWidth;    // Size.x / 2
    float Side;         // x of the body's side from the middle (HalfWidth - Shoulder)
    float Shoulder;     // clamped as IslandBorder does
    float Depth;        // shoulder depth (Shoulder * 7/16)
    float CornerTop;    // 0 for a square top
    float CornerBottom;
    float SideTop;      // y where the side begins: below the shoulder, the top corner, or far above for a square top
    float4 Tint;        // straight rgb, alpha (the source's background, or the dark look's)
    float Scale;        // device pixels per CSS pixel
    float Ref;          // the source's refraction (feDisplacementMap scale = MaxHalf * Ref)
    float Edge;         // the source's ring thickness
    float Rad;          // the source's inner rounded rectangle's corner
    float MaxHalf;      // half the largest shift of the map, CSS pixels (computed by the renderer)
    float Faithful;     // 1: the source's uv field exactly; 0: the island's
    float TopExtra;     // device pixels the box reaches above the top, for a square top
    float Strength;     // the source's strength (1; a dragged lens 1.2)
    float Contrast;
    float Brightness;
    float Saturate;
    float Low;          // the band the theme's text needs: at least Low (light), at most High (dark)
    float High;
    float InsetY;       // the inset shadow: offset (CSS pixels, negative: up), blur, opacity
    float InsetBlur;
    float InsetAlpha;
    float BandA;        // the backdrop's brightness under the island: 2nd and 98th percentiles (sRGB luma, unfiltered)
    float BandB;
    float Narrow;       // how far the inset shadow's sides are narrowed (0 none, 1 to nothing), for a square top
    float Press;        // 0 or 1, the pressed shadows
    float Dark;
    float Band;         // how far from the shoulder its curves are worked out (device pixels)
    float LinearMap;    // 1: the map's bytes read as linear light (the browser's shift); 0: as the page meant it
    float LinearFilters;// the backdrop filtered in linear light
    float2 BoxCss;      // the source's element in whole CSS pixels, its map's texels (Faithful; 0: the box's own size / Scale)
    float2 FilterEnd;   // where the source's backdrop-filter ends, device pixels from the box's corner (Faithful; 0: the box's end)
    float Open;         // 0 closed .. 1 the open panel (eased with the island's opening)
    float FrostRadius;  // the open panel's frost, device pixels
    float BandMean;     // the backdrop's mean brightness under the island (sRGB luma, unfiltered)
    float Spare;
};

Texture2D Background : register(t0);
SamplerState Smooth : register(s0);

// The most contrast left behind the open panel's text, and how far the band may move the glass's mean brightness from the
// backdrop's (dark glass on a dark page, light glass on a light one).
static const float Cap = 1.5, Keep = 0;

struct Vertex { float4 Position : SV_Position; };

Vertex VS(uint id : SV_VertexID)
{
    float2 uv = float2((id << 1) & 2, id & 2);
    Vertex v;
    v.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}

float Cross2(float2 a, float2 b) { return a.x * b.y - a.y * b.x; }

float2 Cubic(float2 a, float2 b, float2 c, float2 d, float t)
{
    float u = 1 - t;
    return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
}

float2 CubicTangent(float2 a, float2 b, float2 c, float2 d, float t)
{
    float u = 1 - t;
    return 3 * u * u * (b - a) + 6 * u * t * (c - b) + 3 * t * t * (d - c);
}

// The point of a cubic nearest q: a coarse search, then Newton steps on dot(B - q, B') = 0.
float NearestOnCubic(float2 q, float2 a, float2 b, float2 c, float2 d, out float2 at, out float2 tangent)
{
    float best = 1e20, t = 0;
    [loop] for (int i = 0; i <= 6; i++)
    {
        float s = i / 6.0;
        float2 e = Cubic(a, b, c, d, s) - q;
        float l = dot(e, e);
        if (l < best) { best = l; t = s; }
    }
    [loop] for (int k = 0; k < 3; k++)
    {
        float u = 1 - t;
        float2 e = Cubic(a, b, c, d, t) - q;
        float2 d1 = CubicTangent(a, b, c, d, t);
        float2 d2 = 6 * u * (c - 2 * b + a) + 6 * t * (d - 2 * c + b);
        float f = dot(e, d1), g = dot(d1, d1) + dot(e, d2);
        t = saturate(t - f / max(g, 1e-4));
    }
    at = Cubic(a, b, c, d, t);
    tangent = CubicTangent(a, b, c, d, t);
    float2 e = at - q;
    return dot(e, e);
}

// Signed distance to the outline (negative inside) and its outward normal, for a point in island pixels. The outline is
// symmetric, so the right half is worked out on the point folded across the middle.
float Outline(float2 p, out float2 normal)
{
    float2 q = float2(abs(p.x - HalfWidth), p.y);
    float h = Size.y, side = Side;
    float2 nearest = float2(side, clamp(q.y, SideTop, h - CornerBottom));
    float2 pieceNormal = float2(1, 0);
    float best = dot(q - nearest, q - nearest);
    float2 c = float2(clamp(q.x, 0, side - CornerBottom), h);
    float l = dot(q - c, q - c);
    if (l < best) { best = l; nearest = c; pieceNormal = float2(0, 1); }
    float2 centre = float2(side - CornerBottom, h - CornerBottom);
    if (CornerBottom > 0)
    {
        float2 v = max(q - centre, 1e-5);
        c = centre + normalize(v) * CornerBottom;
        l = dot(q - c, q - c);
        if (l < best) { best = l; nearest = c; pieceNormal = normalize(v); }
    }
    bool inside = q.y <= h && q.x <= side;
    if (q.x > side - CornerBottom && q.y > h - CornerBottom) inside = length(q - centre) < CornerBottom;
    if (CornerTop > 0)
    {
        c = float2(clamp(q.x, 0, side - CornerTop), 0);
        l = dot(q - c, q - c);
        if (l < best) { best = l; nearest = c; pieceNormal = float2(0, -1); }
        float2 top = float2(side - CornerTop, CornerTop);
        float2 v = float2(max(q.x - top.x, 1e-5), min(q.y - top.y, -1e-5));
        c = top + normalize(v) * CornerTop;
        l = dot(q - c, q - c);
        if (l < best) { best = l; nearest = c; pieceNormal = normalize(v); }
        if (q.y < 0) inside = false;
        else if (q.x > top.x && q.y < top.y) inside = length(q - top) < CornerTop;
    }
    // The shoulder: two cubics from the screen edge down to the body's side (IslandBorder.BuildOutline, folded).
    if (Shoulder > 0 && q.y < Depth + Band + 2 && q.x > side - Band - 2)
    {
        float s = Shoulder, w = HalfWidth, d = Depth;
        float2 p0 = float2(w, 0), p1 = float2(w - s * .28, 0), p2 = float2(w - s * .64, 0), p3 = float2(w - s * .82, d * .18);
        float2 p4 = float2(side, d * .36), p5 = float2(side, d * .72), p6 = float2(side, d);
        float2 a, ta, b, tb;
        float la = NearestOnCubic(q, p0, p1, p2, p3, a, ta);
        float lb = NearestOnCubic(q, p3, p4, p5, p6, b, tb);
        if (lb < la) { la = lb; a = b; ta = tb; }
        if (la < best) { best = la; nearest = a; pieceNormal = normalize(float2(-ta.y, ta.x)) * -1; }
        if (q.y < d && q.x > side) inside = Cross2(ta, q - a) > 0 && q.y >= 0;
    }
    float dist = sqrt(best);
    float2 n = dist > 1e-3 ? (q - nearest) / dist * (inside ? -1 : 1) : pieceNormal;
    normal = float2(p.x < HalfWidth ? -n.x : n.x, n.y);
    return inside ? -dist : dist;
}

float Luma(float3 c) { return dot(c, float3(.2126, .7152, .0722)); }
float3 ToLinear(float3 c) { return c <= .04045 ? c / 12.92 : pow((c + .055) / 1.055, 2.4); }
float3 ToSrgb(float3 c) { return c <= .0031308 ? c * 12.92 : 1.055 * pow(max(c, 0), 1 / 2.4) - .055; }
float SoftMax(float a, float b, float k) { float h = max(k - abs(a - b), 0) / k; return max(a, b) + h * h * k * .25; }
float SoftMin(float a, float b, float k) { float h = max(k - abs(a - b), 0) / k; return min(a, b) - h * h * k * .25; }
float ToLinear1(float c) { return c <= .04045 ? c / 12.92 : pow((c + .055) / 1.055, 2.4); }
float ToSrgb1(float c) { return c <= .0031308 ? c * 12.92 : 1.055 * pow(max(c, 0), 1 / 2.4) - .055; }

// The source's roundedRectSDF.
float RoundedRect(float x, float y, float w, float h, float r)
{
    float qx = abs(x) - w + r, qy = abs(y) - h + r;
    return min(max(qx, qy), 0) + length(float2(max(qx, 0), max(qy, 0))) - r;
}

// The shift (CSS pixels) the map holds at a texel (x, y) of the box (CSS pixels, W x H): the source's field (Faithful) or the
// island's (see the head of the file).
float2 Shift(float2 texel, float2 box)
{
    float edge = .15 / Edge / Strength;
    if (Faithful > .5)
    {
        float ix = texel.x / box.x - .5, iy = texel.y / box.y - .5;
        float s = smoothstep(0, 1, smoothstep(.8, 0, RoundedRect(ix, iy, .3, .2, Rad) - edge));
        return float2(ix, iy) * (s - 1) * box;
    }
    float m = min(box.x, box.y);
    float2 extent = (box - m) * .5;
    float2 q = texel + .5 - box * .5;
    float2 spine = clamp(q, -extent, extent);
    float2 i = q / m;
    float s = smoothstep(0, 1, smoothstep(.8, 0, RoundedRect(i.x, i.y, .3 + extent.x / m, .2 + extent.y / m, Rad) - edge));
    return (q - spine) * (s - 1);
}

// The map's two bytes at a texel: round((shift / MaxHalf + .5) * 255), clamped (a Uint8ClampedArray).
float2 Bytes(float2 texel, float2 box)
{
    texel = clamp(texel, 0, box - 1);
    return round(saturate(Shift(texel, box) / max(MaxHalf, 1e-4) + .5) * 255);
}

// The Mitchell-Netravali cubic (B = C = 1/3), the browser's high-quality filter for drawing an image up.
float Mitchell(float t)
{
    t = abs(t);
    if (t < 1) return ((7 * t - 12) * t * t + 16.0 / 3) / 6;
    if (t < 2) return (((-7.0 / 3 * t + 12) * t - 20) * t + 32.0 / 3) / 6;
    return 0;
}

// The shift feDisplacementMap applies at a device pixel (island pixels from the box's corner), device pixels: the map drawn up
// to device pixels, read in linear light where the filter runs there, and scaled. The browser draws the map up (feImage) with
// its high-quality filter, the Mitchell cubic over 4 x 4 texels, clamped to the bytes' range, and not rounded back to bytes; the
// map's texels are CSS pixels from the box's corner (the element's, snapped to the device pixel half way rounds up to). Read
// off the browser with the coordinate backdrop (round-9 fid/probe.py): bilinear is up to a pixel off wherever a shift is near
// a half (at 150 %, 0.7 % of the pixels: the "rounding of .5"), the cubic matches all but 0.02 %.
float2 Displacement(float2 pixel, float2 box)
{
    float2 at = (floor(pixel) + .5) / Scale - .5;
    float2 byte = 0;
    if (Scale == 1) byte = Bytes(at, box);
    else if (Faithful < .5)
    {
        // The island: bilinear between the four bytes around (a fifth of the cubic's cost; the cubic's difference, a shift near a
        // half rounded the other way, is a pixel here and there and only shows against the browser, on the proof sheets).
        float2 i = floor(at), f = at - i;
        byte = lerp(lerp(Bytes(i, box), Bytes(i + float2(1, 0), box), f.x), lerp(Bytes(i + float2(0, 1), box), Bytes(i + 1, box), f.x), f.y);
    }
    else
    {
        float2 i = floor(at), f = at - i;
        // Looped, not unrolled: sixteen copies of the map's field make a shader the driver takes over a second to build.
        [loop] for (int y = -1; y <= 2; y++)
        {
            float2 row = 0;
            [loop] for (int x = -1; x <= 2; x++) row += Mitchell(f.x - x) * Bytes(i + float2(x, y), box);
            byte += Mitchell(f.y - y) * row;
        }
        byte = clamp(byte, 0, 255);
    }
    float2 v = byte / 255;
    if (LinearMap > .5) v = float2(ToLinear1(v.x), ToLinear1(v.y));
    return MaxHalf * Ref * (v - .5) * Scale;
}

// CSS contrast, brightness and saturate, in that order, each clamped as the filter functions are.
float3 Filters(float3 c)
{
    c = saturate((c - .5) * Contrast + .5);
    c = saturate(c * Brightness);
    float s = Saturate;
    float3x3 m = float3x3(
        .213 + .787 * s, .715 - .715 * s, .072 - .072 * s,
        .213 - .213 * s, .715 + .285 * s, .072 - .072 * s,
        .213 - .213 * s, .715 - .715 * s, .072 + .928 * s);
    return saturate(mul(m, c));
}

float Gaussian(float x, float sigma) { return exp(-(x * x) / (2 * sigma * sigma)) / (2.5066283 * sigma); }
float2 Erf(float2 x)
{
    float2 s = sign(x), a = abs(x);
    x = 1 + (.278393 + (.230389 + .078108 * (a * a)) * a) * a;
    x *= x;
    return s - s / (x * x);
}

// How much of a rounded box (half size halfSize, corner r, centred at the origin), blurred by a Gaussian of deviation sigma,
// covers the point p: along x the box's row at each height is a span whose blur is a difference of erfs, and those rows are
// summed over the Gaussian in y (six steps across 3 deviations each side). Narrow widens the box along x by that many
// deviations (the narrowed sides of a square-topped shape).
float BoxCoverage(float2 p, float2 halfSize, float r, float sigma, float narrow)
{
    halfSize.x += narrow * sigma;
    float low = p.y - halfSize.y, high = p.y + halfSize.y;
    float start = clamp(-3 * sigma, low, high), end = clamp(3 * sigma, low, high);
    float step = (end - start) / 6, y = start + step * .5, value = 0;
    [loop] for (int i = 0; i < 6; i++)
    {
        float row = p.y - y;
        float delta = min(halfSize.y - r - abs(row), 0);
        float span = halfSize.x - r + sqrt(max(0, r * r - delta * delta));
        float2 integral = .5 + .5 * Erf((p.x + float2(-span, span)) * (.70710678 / sigma));
        value += (integral.y - integral.x) * Gaussian(y, sigma) * step;
        y += step;
    }
    return value;
}

// The open panel's frost: the backdrop around a point (background pixels) under a Gaussian of about FrostRadius * .75, from the
// mips (5 x 5 taps, half the radius apart, each a filtered read of the mip a quarter of the radius wide).
float3 Frost(float2 read)
{
    float r = max(FrostRadius, 1);
    float level = max(log2(r) - 1, 0);
    float3 sum = 0;
    float weight = 0;
    [loop] for (int y = -2; y <= 2; y++)
        [loop] for (int x = -2; x <= 2; x++)
        {
            float w = exp(-(x * x + y * y) / 4.5);
            sum += w * Background.SampleLevel(Smooth, (read + .5 + float2(x, y) * r * .5) * Inverse, level).rgb;
            weight += w;
        }
    return sum / weight;
}

// The luma of a gray (sRGB) through the filters.
float FilteredLuma(float v)
{
    float3 c = v;
    return LinearFilters > .5 ? ToSrgb(Filters(ToLinear(c))).g : Filters(c).g;
}

// The band's soft knees: what the measure, at its grain, could not see (a word on a page) held to the band.
float Knees(float l, float least, float most)
{
    if (least >= 0) l = SoftMax(l, least, .1);
    if (most <= 1) l = SoftMin(l, most, .1);
    return saturate(l);
}

// The port's glass (filtered and tinted; raw: the same backdrop unfiltered, tinted) mapped into the band the theme's text needs
// (see the head of the file).
float3 Legible(float3 port, float3 raw)
{
    if (Low <= 0 && High >= 1) return port;
    float t = Tint.a, tl = Luma(Tint.rgb);
    // What is mapped: the port's glass, but for the dark look never darker than the backdrop itself (the port's contrast, in
    // linear light, crushes a dark page to black), and for the light look never brighter (it lifts a light page). Its 2nd and
    // 98th percentiles and its mean, from the backdrop's.
    float dark = High < 1 ? 1 : 0;
    float p = Luma(port), r = Luma(raw);
    float3 c = (dark > 0 ? r > p : r < p) ? raw : port;
    float x = Luma(c);
    float au = BandA, bu = max(BandB, BandA + 1e-3);
    float xa = lerp(FilteredLuma(au), tl, t), xb = lerp(FilteredLuma(bu), tl, t), xm = lerp(FilteredLuma(BandMean), tl, t);
    float ra = lerp(au, tl, t), rb = lerp(bu, tl, t), rm = lerp(BandMean, tl, t);
    if (dark > 0) { xa = max(xa, ra); xb = max(xb, rb); xm = max(xm, rm); }
    else { xa = min(xa, ra); xb = min(xb, rb); xm = min(xm, rm); }
    xb = max(xb, xa + 1e-3);
    float range = xb - xa;
    float least = Low > 0 ? lerp(Low, 1, t) : -1, most = High < 1 ? lerp(High, tl, t) : 2;
    float slope = 1, offset = 0;
    if (Low > 0 && xa < least)
    {
        // Compressed only, its bottom to the floor; its top never above the backdrop's own (anchor), and its mean kept to
        // within Keep (the anchor moves down only as far as that asks). Past the band (the top itself under the floor), the
        // whole range shifts up just to the floor, its slope growing from 0 as the top goes past.
        float anchored = (xb - least) / range;
        float kept = xm > least ? (xm + Keep - least) / max(xm - xa, 1e-3) : 1e5;
        slope = anchored >= 0 ? saturate(min(anchored, kept)) : min(1, (1 - least) / range) * saturate(-anchored * 4);
        offset = least - xa * slope;
    }
    if (High < 1 && xb > most)
    {
        // Compressed only, its top to the ceiling; its bottom never below the backdrop's own (color = a + (color - a) k, the
        // anchor a), and its mean kept to within Keep. Past the band, the whole range shifts down just to the ceiling.
        float anchored = (most - xa) / range;
        float kept = xm < most ? (most - xm + Keep) / max(xb - xm, 1e-3) : 1e5;
        slope = anchored >= 0 ? saturate(min(anchored, kept)) : min(1, most / range) * saturate(-anchored * 4);
        offset = most - xb * slope;
    }
    float l = Knees(x * slope + offset, least, most), k = slope;
    [branch] if (Open > 0)
    {
        // The open panel: what lies behind its text kept within Cap:1, about the backdrop's mean (so a page behind keeps its
        // brightness), inside the band.
        float m = Knees(xm * slope + offset, least, most);
        float y = ToLinear1(m) + .05, spread = sqrt(Cap);
        float lo = ToSrgb1(y / spread - .05), hi = ToSrgb1(y * spread - .05);
        if (lo < least) { lo = least; hi = ToSrgb1((ToLinear1(least) + .05) * Cap - .05); }
        if (hi > most) { hi = most; lo = ToSrgb1((ToLinear1(most) + .05) / Cap - .05); }
        // Compressed about the mean, so it stays (when the window holds it: else from the window's near side), as far as the
        // range must to fit the window.
        float qa = xa * slope + offset, qb = max(xb * slope + offset, qa + 1e-3), k2, capped;
        if (m >= lo && m <= hi)
        {
            k2 = min(1, min((hi - m) / max(qb - m, 1e-3), (m - lo) / max(m - qa, 1e-3)));
            capped = m + (l - m) * k2;
        }
        else
        {
            k2 = min(1, (hi - lo) / (qb - qa));
            capped = m < lo ? lo + (l - qa) * k2 : hi - (qb - l) * k2;
        }
        l = lerp(l, clamp(capped, lo, hi), Open);
        k *= lerp(1, k2, Open);
    }
    // The chroma follows the luma's compression, as far as it stays in gamut (the luma kept exactly).
    float3 d = c - x;
    float3 room = d > 0 ? (1 - l) / max(d, 1e-5) : d < 0 ? l / max(-d, 1e-5) : 1e5;
    return l + d * min(k, min(room.x, min(room.y, room.z)));
}

float3 Glass(float2 p, float sd)
{
    // The element's box (device pixels): the island, reaching TopExtra above a square top.
    // The map's texels: the source's element's whole CSS pixels (offsetWidth) when given, whatever whole device pixels its edges
    // snap to (at 125 % a 261 px element from x = 281 spans 351.25 .. 677.5 and is drawn 327 wide; its map is still 261).
    float2 boxCss = BoxCss.x > 0 ? BoxCss : float2(Size.x, Size.y + TopExtra) / Scale;
    float2 pixel = float2(p.x, p.y + TopExtra);

    // The backdrop through the map, at whole pixels, then the filters.
    float2 raw = Displacement(pixel, boxCss);
    float2 shift = floor(raw + .5);
    // The backdrop the filter sees is the element's box, mirrored past its edges (the browser's backdrop-filter edge mode).
    float2 at = floor(pixel) + shift, box = FilterEnd.x > 0 ? FilterEnd : float2(Size.x, Size.y + TopExtra);
    // Mirrored as a tiling: a shift longer than the box (a low bar's) folds back again.
    float2 period = 2 * box, t = at - period * floor(at / period);
    at = t < box ? t : period - t - 1;
    int2 read = int2(at - float2(0, TopExtra) + Pad);
    float3 color = Background.Load(int3(read, 0)).rgb;
    // The open panel: frosted, from the mips, as it opens.
    [branch] if (Open > 0) color = lerp(color, Frost(float2(read)), Open);
    float3 unfiltered = color;
    if (LinearFilters > .5) color = ToSrgb(Filters(ToLinear(color)));
    else color = Filters(color);
    // Past the filter's region (an element between device pixels whose corner snapped back: its last column or row), the
    // backdrop as it is.
    if (FilterEnd.x > 0 && any(floor(pixel) >= FilterEnd)) color = Background.Load(int3(int2(floor(pixel) - float2(0, TopExtra) + Pad), 0)).rgb;

    // The element's background, then legibility (see the head of the file).
    color = Legible(lerp(color, Tint.rgb, Tint.a), lerp(unfiltered, Tint.rgb, Tint.a));

    // The inset shadows: dark where the box moved by the offset, blurred, does not cover. Pressed, the source's inner glow
    // (white where the box shrunk by its spread, blurred, does not cover) and its deeper bottom shadow, at once.
    float2 halfSize = float2(Size.x, Size.y + TopExtra) * .5;
    float2 centred = pixel - halfSize;
    float r = (CornerTop > 0 ? CornerTop : CornerBottom);
    float sigma = max(InsetBlur * .5 * Scale, .5);
    float shadow = InsetAlpha * (1 - BoxCoverage(centred - float2(0, InsetY * Scale), halfSize, r, sigma, Narrow));
    if (Press > 0)
    {
        float k = InsetBlur / 20;
        float deep = .18 * (1 - BoxCoverage(centred - float2(0, -8 * k * Scale), halfSize, r, max(8 * k * Scale, .5), Narrow));
        shadow = 1 - (1 - shadow) * (1 - Press * deep);
        float spread = 3 * k * Scale;
        float glow = .55 * (1 - BoxCoverage(centred, halfSize - spread, max(r - spread, 0), max(8 * k * Scale, .5), Narrow));
        color = lerp(color, 1, Press * glow);
    }
    return saturate(color * (1 - shadow));
}

float4 PS(Vertex input) : SV_Target
{
    float2 p = input.Position.xy;
    float2 n;
    float sd = Outline(p, n);
    if (sd > 1.5) return 0;
    // The edge: how much of the pixel the outline covers, from four subsamples (the outline's curves are far wider than a
    // pixel, so each takes the centre's distance carried along the normal). The glass itself is the same for the whole pixel:
    // the browser reads the backdrop at whole pixels.
    static const float2 Offsets[4] = { float2(-.25, -.25), float2(.25, -.25), float2(-.25, .25), float2(.25, .25) };
    float cover = 1;
    if (sd > -1.5)
    {
        cover = 0;
        [unroll] for (int i = 0; i < 4; i++) cover += saturate(.5 - (sd + dot(n, Offsets[i])) * 2);
        cover *= .25;
    }
    if (cover <= 0) return 0;
    return float4(Glass(p, sd) * cover, cover);
}
