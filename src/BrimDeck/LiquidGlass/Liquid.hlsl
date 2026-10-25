// Liquid glass (LiquidRenderer): one pass that draws the island as a pane of liquid glass over the desktop captured under it.
//
// Ported from xiaofangstudio/liquid-glass-web (showcase.html, MIT; its refraction engine is shuding/liquid-glass, MIT, (c) Shu
// Ding): the CSS and SVG it builds the glass from, done here in one shader with the same math and constants.
//   The displacement map: for every pixel of the element, uv in 0..1 across its box, ix = u - .5, iy = v - .5; the distance to
//   an inner rounded rectangle d = roundedRectSDF(ix, iy, .3, .2, rad = .6); disp = smoothStep(.8, 0, d - .15 / edge / strength);
//   s = smoothStep(0, 1, disp); the background is read from (ix * s + .5) * size, so the middle is flat and a ring at the edge
//   pulls what lies inward out to the rim (a lens's magnifying edge). The map is written to an 8-bit image normalised by half
//   the largest shift (MaxHalf here) and feDisplacementMap's scale is MaxHalf * ref: every shift is clamped to a quarter of the
//   largest and stepped in 1/255ths, as the image holds it; both are reproduced.
//   backdrop-filter: url(#map) blur(0.25px) contrast(1.2) brightness(1.05) saturate(1.1), in that order (the blur is under a
//   device pixel and is left out).
//   background: rgba(255, 255, 255, tint = .08).
//   box-shadow: 0 4px 8px rgba(0,0,0,.22) (outside the glass, drawn by the host), 0 -8px 20px inset rgba(0,0,0,.14).
//   Pressed: the inset glow 0 0 16px 3px rgba(255,255,255,.55) and inset 0 -8px 16px rgba(0,0,0,.18); while pressed the lens's
//   strength is 1.2 (a thicker ring). The light other glass throws on it (.lit-glow): a radial gradient from the light's point,
//   white .9 at 0, .25 at 40 %, nothing at 70 % of the farthest corner, soft-light blended; here it comes from the pointer.
// Lengths in the source are CSS pixels: here device pixels over Scale.
//
// Adapted to the island (Faithful 0; 1 is the source exactly, for the proof sheets):
//   The outline is IslandBorder.BuildOutline's, a signed distance in device pixels: a capsule or rounded rectangle, or a shape
//   square at the top (the open panel, the notch) whose top edge is the screen's. A square top's box reaches TopExtra above the
//   screen, so the ring and the inset shadow never show along it.
//   The source's uv field stretches with the element (a wide pill gets a wide, shallow ring, its ends pulled from the far
//   middle): here it is the source's field for a square of the shape's shorter side, its middle stretched along the longer one,
//   and the shift points at the nearest point of that middle, so every edge bends as the lens's does.
//   The inset shadows' offset and blur shrink with shapes lower than the source's buttons (InsetY, InsetBlur; the renderer).
//   Legibility: the theme's band (Low, High): only what lies outside it is moved, each pixel just far enough (a soft maximum and
//   minimum), toward white or the glass's dark tint. Over what already lies in the band the glass is the source's.
// The output is premultiplied; outside the outline it is transparent. Edge pixels take four subsamples.

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
    float4 Tint;        // straight rgb, alpha (the source's background)
    float Scale;        // device pixels per CSS pixel
    float Ref;          // the source's refraction (feDisplacementMap scale = MaxHalf * Ref)
    float Edge;         // the source's ring thickness
    float Rad;          // the source's inner rounded rectangle's corner
    float MaxHalf;      // half the largest shift of the map, CSS pixels (computed by the renderer)
    float Faithful;     // 1: the source's uv field exactly; 0: the island's
    float TopExtra;     // device pixels the box reaches above the top, for a square top
    float Strength;     // the source's strength (1, 1.2 pressed)
    float Contrast;
    float Brightness;
    float Saturate;
    float Low;          // the band of brightness the glass is held to (legibility)
    float High;
    float InsetY;       // the inset shadow: offset (CSS pixels, negative: up), blur, opacity
    float InsetBlur;
    float InsetAlpha;
    float2 Pointer;     // island pixels
    float Lit;          // the pointer's light (.lit-glow's opacity)
    float Press;        // 0..1, the pressed shadows
    float Dark;
    float Band;         // how far from the shoulder its curves are worked out (device pixels)
    float Spare0;
    float Spare1;
};

Texture2D Background : register(t0);
SamplerState Linear : register(s0);

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
    [unroll] for (int i = 0; i <= 6; i++)
    {
        float s = i / 6.0;
        float2 e = Cubic(a, b, c, d, s) - q;
        float l = dot(e, e);
        if (l < best) { best = l; t = s; }
    }
    [unroll] for (int k = 0; k < 3; k++)
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

float3 Read(float2 at)
{
    return Background.SampleLevel(Linear, (at + Pad) * Inverse, 0).rgb;
}

float Luma(float3 c) { return dot(c, float3(.2126, .7152, .0722)); }
float SoftMax(float a, float b, float k) { float h = max(k - abs(a - b), 0) / k; return max(a, b) + h * h * k * .25; }
float SoftMin(float a, float b, float k) { float h = max(k - abs(a - b), 0) / k; return min(a, b) - h * h * k * .25; }

// The source's roundedRectSDF.
float RoundedRect(float x, float y, float w, float h, float r)
{
    float qx = abs(x) - w + r, qy = abs(y) - h + r;
    return min(max(qx, qy), 0) + length(float2(max(qx, 0), max(qy, 0))) - r;
}

// The shift of the background at p (device pixels, island coordinates), in CSS pixels, before the map's clamp: the source's
// field (Faithful) or the island's (see the head of the file). The box is the island's, reaching TopExtra above a square top.
float2 Shift(float2 p)
{
    float2 box = float2(Size.x, Size.y + TopExtra) / Scale;
    // The map's texel this point falls in: the source's x and y are texel indices, their centres at +.5.
    float2 at = float2(p.x, p.y + TopExtra) / Scale - .5;
    float edge = .15 / Edge / Strength;
    if (Faithful > .5)
    {
        float ix = at.x / box.x - .5, iy = at.y / box.y - .5;
        float d = RoundedRect(ix, iy, .3, .2, Rad);
        float s = smoothstep(0, 1, smoothstep(.8, 0, d - edge));
        return float2(ix, iy) * (s - 1) * box;
    }
    float m = min(box.x, box.y);
    float2 extent = (box - m) * .5;
    float2 q = at + .5 - box * .5;
    float2 spine = clamp(q, -extent, extent);
    float2 i = q / m;
    float d = RoundedRect(i.x, i.y, .3 + extent.x / m, .2 + extent.y / m, Rad);
    float s = smoothstep(0, 1, smoothstep(.8, 0, d - edge));
    return (q - spine) * (s - 1);
}

// The 8-bit map: a shift held as round((shift / MaxHalf + .5) * 255), clamped to a byte, and read back by feDisplacementMap as
// scale * (byte / 255 - .5) with scale = MaxHalf * Ref.
float2 Mapped(float2 shift)
{
    float2 byte = round(saturate(shift / max(MaxHalf, 1e-4) + .5) * 255);
    return MaxHalf * Ref * (byte / 255 - .5);
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

// A blurred shape's coverage at a signed distance, for a Gaussian of deviation sigma (a box-shadow's blur / 2).
float Covered(float sd, float sigma) { return 1 / (1 + exp(1.702 * sd / max(sigma, 1e-3))); }

// CSS soft-light of white over c, by a.
float3 SoftLightWhite(float3 c, float a)
{
    float3 d = c <= .25 ? ((16 * c - 12) * c + 4) * c : sqrt(c);
    return lerp(c, d, a);
}

float3 Glass(float2 p, float sd)
{
    // The backdrop through the map, then the filters.
    float2 shift = Mapped(Shift(p)) * Scale;
    float3 color = Filters(Read(p + shift));

    // Legibility: only what lies outside the theme's band is moved, just far enough.
    float luma = Luma(color);
    float held = luma;
    if (Low > 0) held = SoftMax(held, Low, .14);
    if (High < 1) held = SoftMin(held, High, .14);
    if (held > luma) color += (1 - color) * saturate((held - luma) / max(1 - luma, 1e-3));
    else if (held < luma)
    {
        float deep = Luma(Tint.rgb);
        if (luma > deep + .02 && held >= deep) color = lerp(Tint.rgb, color, saturate((held - deep) / (luma - deep)));
        else color *= held / max(luma, 1e-3);
    }

    // The element's background.
    color = lerp(color, Tint.rgb, Tint.a);

    // The inset shadow (0 InsetY InsetBlur black InsetAlpha): dark where the shape moved by the offset, blurred, does not cover;
    // pressed, the source's inner glow and its deeper bottom shadow (0 -8px 16px .18, scaled with InsetBlur as the rest).
    float2 unused;
    float k = InsetBlur / 20;
    float shadow = InsetAlpha * (1 - Covered(Outline(p - float2(0, InsetY * Scale), unused), InsetBlur * .5 * Scale));
    if (Press > 0)
    {
        float deep = Press * .18 * (1 - Covered(Outline(p + float2(0, 8 * k * Scale), unused), 8 * k * Scale));
        shadow = 1 - (1 - shadow) * (1 - deep);
        color = lerp(color, 1, Press * .55 * (1 - Covered(sd + 3 * k * Scale, 8 * k * Scale)));
    }
    color *= 1 - shadow;

    // The pointer's light (.lit-glow): a radial gradient from it to the box's farthest corner, soft-light.
    if (Lit > 0)
    {
        float2 corner = max(Pointer, Size - Pointer);
        float r = length(p - Pointer) / max(length(corner), 1);
        float a = r < .4 ? lerp(.9, .25, r / .4) : r < .7 ? lerp(.25, 0, (r - .4) / .3) : 0;
        color = SoftLightWhite(color, a * Lit);
    }
    return saturate(color);
}

float4 PS(Vertex input) : SV_Target
{
    float2 p = input.Position.xy;
    float2 n;
    float sd = Outline(p, n);
    if (sd > 1.5) return 0;
    if (sd < -1.5) return float4(Glass(p, sd), 1);
    static const float2 Offsets[4] = { float2(-.25, -.25), float2(.25, -.25), float2(-.25, .25), float2(.25, .25) };
    float4 sum = 0;
    [unroll] for (int i = 0; i < 4; i++)
    {
        float2 at = p + Offsets[i];
        float d = sd + dot(n, Offsets[i]);
        float cover = saturate(.5 - d * 2);
        if (cover > 0) sum += float4(Glass(at, d) * cover, cover);
    }
    return sum * .25;
}
