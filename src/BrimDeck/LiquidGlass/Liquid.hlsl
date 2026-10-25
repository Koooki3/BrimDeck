// Liquid glass (LiquidRenderer): one pass that draws the island as a slab of clear glass over the desktop captured under it.
//
// The slab's outline is IslandBorder.BuildOutline's, as a signed distance in device pixels (negative inside): a capsule or
// rounded rectangle, or a shape square at the top (the open panel, the notch) whose top edge is the screen's and has no
// bevel, with the notch's shoulders flaring out to the screen edge. Inside the outline the slab's surface rises over a bevel
// Thickness wide, a convex squircle (z = (1 - (1 - x)^4)^(1/4), as the kube.io article on liquid glass describes), and is
// flat beyond it. A ray from the eye straight down is refracted by the surface (Snell, IOR 1.5); on the bevel it bends
// inward, so the background is read from further in and what lies under the rim is drawn magnified out to the edge, as a
// lens's rim does. Red bends a little less than blue (Dispersion). The flat middle shows the background almost as it is:
// a light frost from the background's mips, made stronger where the background is busy, and its colors a little more
// saturated.
//
// Legibility: the theme's text is fitted to a band of brightness (PanelGlass.Low/High). The middle is held to it adaptively:
// a background already inside the band passes unchanged (light glass over a light page is clear glass, not a white slab),
// and only what lies outside it is lifted toward the glass's own white (Low) or smoked toward black (High), each pixel
// exactly as far as it must go, with a soft knee. Toward the rim, where no text sits, the band relaxes a little (EdgeBand),
// so the refraction there shows the background's own colors. Nothing is remapped wholesale, so over flat content the rim
// and the middle are the same color: no ring.
//
// Light: a crisp line along the very rim, brightest where it faces the light (from the pointer while it is over the island,
// else from the top left) and faint on the far side; a soft highlight inside the bevel on the lit side (Sheen); a narrow
// specular glint on the bevel; and a soft glow near the pointer. Light is laid on by screen, so it never clips to a white
// patch. Opposite the light the rim darkens a little in the background's own hue (Shade: light passing through more glass),
// which is what shows the glass over flat white. A press bends more and squashes the glint into a thinner, brighter arc.
// Edge pixels take four subsamples, each with its own coverage, refraction and light. The output is premultiplied;
// outside the outline it is transparent.

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
    float4 Tint;        // straight rgb, alpha
    float Thickness;    // bevel width, device pixels
    float Refraction;   // displacement at the rim, in bevel widths
    float Dispersion;   // relative spread of red and blue
    float Frost;        // mip level of the middle's frost
    float2 Pointer;     // island pixels
    float Near;         // how much the pointer's glow shows (0 when it is away)
    float Hover;
    float2 Light;       // unit direction toward the light, in the island's plane (y down)
    float Press;
    float Morph;
    float Dark;
    float Low;          // the band of brightness the middle is held to
    float High;
    float Saturation;
    float EdgeBand;     // how far the band relaxes at the rim
    float Scale;        // display scale (device pixels per DIP)
    float Gain;         // overall strength of the light
    float Glow;         // pointer glow radius, device pixels
    float Open;         // 0 collapsed .. 1 the open panel: a little more frost
    float Shade;        // the rim's darkening opposite the light
    float Sheen;        // the soft highlight inside the bevel on the lit side
    float Spare;
};

Texture2D Background : register(t0);
SamplerState Linear : register(s0);

struct Vertex { float4 Position : SV_Position; };

// One triangle that covers the target.
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

// The point of a cubic nearest q: a coarse search, then Newton steps on dot(B - q, B') = 0. Returns the distance squared, the
// point and the tangent there.
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
    // The bottom edge and its corner.
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
    // A rounded top: its edge and corner.
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
    // The shoulder: two cubics from the screen edge down to the body's side (IslandBorder.BuildOutline, folded). Only near it
    // is it worked out.
    if (Shoulder > 0 && q.y < Depth + Thickness + 2 && q.x > side - Thickness - 2)
    {
        float s = Shoulder, w = HalfWidth, d = Depth;
        float2 p0 = float2(w, 0), p1 = float2(w - s * .28, 0), p2 = float2(w - s * .64, 0), p3 = float2(w - s * .82, d * .18);
        float2 p4 = float2(side, d * .36), p5 = float2(side, d * .72), p6 = float2(side, d);
        float2 a, ta, b, tb;
        float la = NearestOnCubic(q, p0, p1, p2, p3, a, ta);
        float lb = NearestOnCubic(q, p3, p4, p5, p6, b, tb);
        if (lb < la) { la = lb; a = b; ta = tb; }
        if (la < best) { best = la; nearest = a; pieceNormal = normalize(float2(-ta.y, ta.x)) * -1; }
        // The outline runs from the screen edge down the shoulder with the inside on its left (y down).
        if (q.y < d && q.x > side) inside = Cross2(ta, q - a) > 0 && q.y >= 0;
    }
    float dist = sqrt(best);
    float2 n = dist > 1e-3 ? (q - nearest) / dist * (inside ? -1 : 1) : pieceNormal;
    normal = float2(p.x < HalfWidth ? -n.x : n.x, n.y);
    return inside ? -dist : dist;
}

float3 Read(float2 at, float level)
{
    return Background.SampleLevel(Linear, (at + Pad) * Inverse, level).rgb;
}

float Luma(float3 c) { return dot(c, float3(.2126, .7152, .0722)); }

// Polynomial smooth maximum and minimum with a knee k wide: never below max(a, b) (above min(a, b)).
float SoftMax(float a, float b, float k) { float h = max(k - abs(a - b), 0) / k; return max(a, b) + h * h * k * .25; }
float SoftMin(float a, float b, float k) { float h = max(k - abs(a - b), 0) / k; return min(a, b) - h * h * k * .25; }

// The surface's slope over the bevel at x (0 at the rim, 1 where it is flat): the convex squircle z = (1 - (1 - x)^4)^(1/4).
float Profile(float x)
{
    float u = 1 - saturate(x);
    return pow(max(1 - u * u * u * u, 0), .25);
}

float Slope(float x)
{
    float u = 1 - clamp(x, .004, 1);
    float u4 = u * u * u * u;
    return u * u * u * pow(max(1 - u4, 1e-4), -.75);
}

// The glass at one subsample: the background as refracted and frosted there, held to the band, shaded, tinted and lit.
float3 Glass(float2 p, float sd, float2 n)
{
    float depth = max(-sd, 0);
    float thickness = max(Thickness, 1);
    float x = saturate(depth / thickness);
    float rim = 1 - x;
    float slope = Slope(x);
    float3 surface = normalize(float3(n * slope * .8, 1));
    // The ray from the eye refracted into the slab (IOR 1.5) travels through the glass down to the background: how far it
    // lands from where it entered is its tangent off the vertical times the glass's height there, which rises with the bevel
    // (the kube.io method). Steep at the rim, the glass is thin there, so the very edge shifts little and nothing smears; the
    // shift peaks inside the bevel, where the magnification shows, and is nothing over the flat middle.
    float3 ray = refract(float3(0, 0, -1), surface, 1 / 1.5);
    float tilt = length(ray.xy) / max(-ray.z, 1e-3);
    float height = thickness * 1.6 * Profile(x) + 4 * Scale;
    float strength = Refraction * (1 + .7 * Press + .6 * Morph);
    float shift = strength * tilt * height;

    // The convex rim bends the ray inward: the background is read from further inside, magnified out to the edge. Red bends
    // a little less than blue, only near the outer edge.
    float2 at = p - n * shift;
    float spread = Dispersion * (1 + Morph) * .3 * rim * rim;
    // How busy the background is here: its brightness against its wide average. A busy background takes more frost behind
    // text; a calm one stays clear.
    // Only inside the rim's outer part, where the frost begins (the edge's subsamples never need it).
    float busy = 0;
    [branch] if (x > .6) busy = saturate(abs(Luma(Read(at, Frost)) - Luma(Read(at, Frost + 3))) * 5);
    float frostLevel = Frost + .8 * Open + 1.2 * busy;
    float level = frostLevel * smoothstep(.6, 1, x);
    // The middle's frost: a few reads on a small golden-angle spiral, so the mips' blocks do not show. Where it covers
    // wholly (the flat middle, most of the open panel) the sharp reads are not made.
    float frost = smoothstep(.7, 1, x);
    float3 color = 0;
    [branch] if (frost < 1)
    {
        color.r = Read(at + n * shift * spread, level).r;
        color.g = Read(at, level).g;
        color.b = Read(at - n * shift * spread, level).b;
    }
    [branch] if (frost > 0)
    {
        float radius = exp2(frostLevel) * .9;
        float3 f = 0;
        [unroll] for (int i = 0; i < 6; i++)
        {
            float r = radius * sqrt((i + .5) / 6);
            float a = i * 2.39996323;
            f += Read(at + r * float2(cos(a), sin(a)), frostLevel);
        }
        color = lerp(color, f / 6, frost);
    }
    // Vibrancy: the colors behind a little more saturated, kept in range.
    float luma = Luma(color);
    color = saturate(luma + (color - luma) * Saturation);

    // The band, adaptively: only what lies outside it is moved, each pixel just far enough, with a soft knee. Light glass
    // lifts toward white (a veil), dark glass smokes toward black, both keeping the hue. Relaxed toward the rim.
    // The relaxing fades out as the background lies far outside the band (light glass over a dark page, dark glass over a
    // light one, as while automatic glass is about to turn): a dark rim around a milky middle would read as a metal tube.
    luma = Luma(color);
    float outside = max(Low - luma, luma - High);
    float relax = EdgeBand * rim * sqrt(rim) * saturate(1 - outside / .35);
    float low = Low * (1 - relax), high = lerp(High, 1, relax);
    float held = luma;
    if (low > 0) held = SoftMax(held, low, .14);
    if (high < 1) held = SoftMin(held, high, .14);
    // How far the glass is lifting or smoking the background here: its light is held back by as much, so a highlight on
    // a veil never blows out into a white chrome patch.
    float veil = saturate(abs(held - luma) / .5);
    if (held > luma) color += (1 - color) * saturate((held - luma) / max(1 - luma, 1e-3));
    else
    {
        // Smoked toward the glass's own deep tint rather than scaled toward black, so a light gray behind dark glass turns to
        // deep glass, not to dull charcoal; the brightness lands where the band says all the same.
        float deep = Luma(Tint.rgb);
        if (luma > deep + .02 && held >= deep) color = lerp(Tint.rgb, color, saturate((held - deep) / (luma - deep)));
        else color *= held / max(luma, 1e-3);
    }
    color = lerp(color, Tint.rgb, Tint.a);

    // Light from the pointer's side (or the top left). Opposite it the rim darkens a little in the background's own hue.
    float facing = dot(n, Light);
    float towards = saturate(facing), away = saturate(-facing);
    float shade = Shade * pow(rim, 2.5) * (.3 + .7 * away);
    color = lerp(color, color * lerp(1, color, .5) * .86, shade);

    // A crisp line along the very rim, a soft highlight inside the lit bevel, a narrow glint; a press narrows and brightens
    // them, hover lifts them a little, and the pointer's light gathers on the stretch of rim nearest it.
    // The line lies just inside the edge (a device pixel at 100%), so the outermost pixel keeps the glass's own color and the
    // outline stays crisp against any background; it falls off sharply outward and softly inward.
    float ring = depth - .8 * Scale;
    float line1 = ring < 0 ? exp(-ring * ring / (.3 * Scale * Scale)) : exp(-ring / Scale);
    float rimLight = line1 * (.15 + .85 * towards * towards + .3 * away * away);
    float inner = Sheen * exp(-depth / (thickness * .35)) * (.8 * pow(towards, 1.5) + .2 * away);
    float3 light = normalize(float3(Light * .77, .64));
    float3 half1 = normalize(light + float3(0, 0, 1));
    float spec = pow(saturate(dot(surface, half1)), 70 * (1 + 1.5 * Press)) * rim * rim * pow(towards, 1.5);
    float2 toPointer = p - Pointer;
    float glow = Near * exp(-dot(toPointer, toPointer) / (Glow * Glow));
    float lift = (1 + .35 * Hover) * (1 + .4 * Press) * (1 + .8 * glow);
    float amount = saturate((rimLight * .85 + (inner + spec * .15) * (1 - .7 * veil) + glow * .06) * lift * Gain * (1 - .35 * veil));
    float3 tone = lerp(float3(1, 1, 1), float3(.88, .93, 1), saturate(Dark));
    color = 1 - (1 - color) * (1 - amount * tone);
    return saturate(color);
}

float4 PS(Vertex input) : SV_Target
{
    float2 p = input.Position.xy;
    float2 n;
    float sd = Outline(p, n);
    if (sd > 1.5) return 0;
    // Away from the edge a pixel is wholly inside: one sample. At the edge four, one in the middle of each quarter of the
    // pixel and covering that quarter, its distance carried from the centre along the normal (the outline's curves are far
    // wider than a pixel).
    if (sd < -1.5) return float4(Glass(p, sd, n), 1);
    static const float2 Offsets[4] = { float2(-.25, -.25), float2(.25, -.25), float2(-.25, .25), float2(.25, .25) };
    float4 sum = 0;
    [unroll] for (int i = 0; i < 4; i++)
    {
        float2 at = p + Offsets[i];
        float d = sd + dot(n, Offsets[i]);
        float cover = saturate(.5 - d * 2);
        if (cover > 0) sum += float4(Glass(at, d, n) * cover, cover);
    }
    return sum * .25;
}
