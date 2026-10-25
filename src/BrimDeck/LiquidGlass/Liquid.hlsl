// Liquid glass (LiquidRenderer): one pass that draws the island as a slab of thick glass over the desktop captured under it.
//
// The slab's outline is IslandBorder.BuildOutline's, as a signed distance in device pixels (negative inside): a capsule or
// rounded rectangle, or a shape square at the top (the open panel, the notch) whose top edge is the screen's and has no
// bevel, with the notch's shoulders flaring out to the screen edge. Inside the outline the slab's surface rises over a bevel
// Thickness wide, a superellipse profile, and is flat beyond it. A ray from the eye straight down is refracted by the surface
// (Snell, IOR 1.5); on the bevel it leaves the vertical, and the background is read that far off along the outline's normal,
// outward, so the desktop just outside the island is drawn compressed into its rim, as a thick lens's edge does. Red bends a
// little less than blue (Dispersion). The flat middle shows the background unbent under a frost read from the background's
// mips, so text over it reads as over the old blur. The theme's band of brightness (PanelGlass.Low/High/Saturation) holds the
// middle as the held blur did, widened toward the rim, where no text sits; then the tint.
//
// Light: a specular highlight of the bevel's surface toward a light that comes from top-left, or from the pointer while it is
// over the island, with a fainter one from the opposite side (light reflected inside the slab), a thin rim line that is
// brightest where it faces the light, and a soft glow near the pointer. A press makes the glass bend more and squashes the
// highlight into a thinner, brighter arc. Edge pixels take four subsamples, each with its own coverage, refraction and light.
// The output is premultiplied; outside the outline it is transparent.

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
    float EdgeBand;     // how far the band opens toward 0..1 at the rim
    float Scale;        // display scale (device pixels per DIP)
    float Gain;         // overall strength of the light
    float Glow;         // pointer glow radius, device pixels
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

// The surface's slope over the bevel at x (0 at the rim, 1 where it is flat): a superellipse z = (1 - (1 - x)^3)^(1/3).
float Slope(float x)
{
    float u = 1 - clamp(x, .004, 1);
    float u3 = u * u * u;
    return u * u * pow(max(1 - u3, 1e-4), -2.0 / 3.0);
}

// The glass at one subsample: the background as refracted and frosted there, held to the band, tinted and lit.
float3 Glass(float2 p, float sd, float2 n)
{
    float depth = max(-sd, 0);
    float thickness = max(Thickness, 1);
    float x = saturate(depth / thickness);
    float slope = Slope(x);
    float3 surface = normalize(float3(n * slope, 1));
    // The ray from the eye refracted into the slab: how far it leaves the vertical, as a tangent.
    float3 ray = refract(float3(0, 0, -1), surface, 1 / 1.5);
    float bend = length(ray.xy) / max(-ray.z, 1e-3) / 1.118;
    float strength = Refraction * (1 + .7 * Press + .6 * Morph);
    float shift = strength * thickness * bend;

    // The rim: the background shifted outward along the normal, red a little less than blue, nearly sharp at the edge and
    // softer inward, so the frost of the middle comes on without a seam.
    float spread = Dispersion * (1 + Morph) * .35;
    float frost = smoothstep(.12, .95, x);
    float3 color = 0;
    if (frost < 1)
    {
        float level = lerp(.25, Frost - 1, smoothstep(0, .7, x));
        float3 rim;
        rim.r = Read(p + n * shift * (1 - spread), level).r;
        rim.g = Read(p + n * shift, level).g;
        rim.b = Read(p + n * shift * (1 + spread), level).b;
        color = rim * (1 - frost);
    }
    if (frost > 0)
    {
        // The middle: a frost of twelve reads from the mips on a golden-angle spiral, so neither the mips' blocks nor fine
        // lines behind show through as bands.
        float level = Frost - 1.5;
        float radius = exp2(Frost) * 1.3;
        float2 at = p + n * shift;
        float3 f = 0;
        [unroll] for (int i = 0; i < 12; i++)
        {
            float r = radius * sqrt((i + .5) / 12);
            float a = i * 2.39996323;
            f += Read(at + r * float2(cos(a), sin(a)), level);
        }
        color += f / 12 * frost;
    }

    // The band: saturated about the luma and mapped into Low..High, opened toward 0..1 at the rim.
    float luma = dot(color, float3(.2126, .7152, .0722));
    color = max(lerp(luma.xxx, color, Saturation), 0);
    float open = EdgeBand * (1 - x) * (1 - x) * (3 - 2 * (1 - x)) ;
    float low = lerp(Low, 0, open), high = lerp(High, 1, open);
    color = low + (high - low) * color;
    color = lerp(color, Tint.rgb, Tint.a);

    // Light. The light's direction rises 40 degrees over the plane; its highlight on the bevel narrows with a press.
    float hover = Hover;
    float3 light = normalize(float3(Light * .77, .64));
    float3 half1 = normalize(light + float3(0, 0, 1));
    float3 half2 = normalize(float3(-light.xy, light.z) + float3(0, 0, 1));
    float shine = 24 * (1 + 2.5 * Press);
    float facing = dot(n, Light);
    float bevel = 1 - x;
    float spec = pow(saturate(dot(surface, half1)), shine) * bevel;
    float back = pow(saturate(dot(surface, half2)), shine) * bevel * .38;
    // The rim line: a device pixel and a half wide, brightest facing the light.
    float line1 = exp(-depth / (.9 * Scale));
    float rimLight = line1 * (.22 + .78 * pow(saturate(facing * .5 + .5), 3));
    // The pointer's glow: the light gathers on the stretch of rim nearest it, and faintly over the glass under it.
    float2 toPointer = p - Pointer;
    float glow = Near * exp(-dot(toPointer, toPointer) / (Glow * Glow));
    float lift = (1 + .6 * hover) * (1 + .5 * Press);
    float amount = (spec * (.55 + .45 * Press) + back + rimLight * .55) * lift * (1 + 1.4 * glow) + glow * (.05 + .25 * bevel * bevel);
    // Fresnel: the steep rim reflects a little of the sky.
    float fresnel = pow(1 - surface.z, 3) * .35;
    float3 sky = Dark > .5 ? float3(.75, .8, .88) : float3(1, 1, 1);
    color = lerp(color, sky, fresnel * Gain * (Dark > .5 ? .6 : 1));
    color += amount * Gain * (Dark > .5 ? float3(.82, .88, 1) : float3(1, 1, 1));
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
