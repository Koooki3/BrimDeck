// The frost's pyramid and the backdrop's statistics (LiquidRenderer), drawn before the glass (Liquid.hlsl) each frame, over the
// padded backdrop only.
//
// Dual filtering (Marius Bjorge, ARM, "Bandwidth-Efficient Rendering", SIGGRAPH 2015): a chain of downsamples, each half the
// size, five bilinear taps (the middle weighted 4, four corners a source texel away along the diagonals: each tap averages
// 2 x 2, so the kernel spans 4 x 4), then upsamples, each twice the size, eight bilinear taps (four along the axes a source
// texel away, weight 1; four on the diagonals half a texel away, weight 2; over 12). Level k of the frost (B_k) is the k-th
// downsample drawn back up one level, into one atlas the glass samples; its Gaussian deviation (LiquidRenderer.LevelSigma,
// measured by FxPreview --frost-calibrate: 1.73, 3.63, 7.34 ... device pixels) about doubles each level. The glass's
// Frost(read, radius) blends the two levels whose deviations bracket the one asked for, by where it lies between them on a
// log scale (Liquid.hlsl): any radius, and as the radius grows the frost changes at an even pace, with no step and no lurch at a
// level (FxPreview --frost-check, the opening sweep).
//
// Stats: per tile of the island (StatsTile device pixels, anchored at the island's top middle so the grid stays put on the
// screen as the island grows; only the tiles over the island and one around it are drawn). First a band image at half size
// (Band): the frosted backdrop's sRGB luma (frosted at SigmaFrost, the frost behind the island's text), the square of its
// band-pass (the frosted backdrop at a deviation of SigmaBand minus at 4 SigmaBand, as level k minus level k + 2, in linear Y:
// detail at the scale of a glyph's strokes, which masks text) and the square of the unfrosted backdrop's band-pass. Then per
// tile (Stats) their means: mean luma, band RMS, unfrosted band RMS; smoothed in time against the last frame's (up fast,
// down slow).
//
// The tables are this file's own constant buffer (FrostPass); the glass has the same in its Frame.

cbuffer FrostPass : register(b0)
{
    float2 DstOrigin;     // the pass's target region's top-left in its texture (the atlas), pixels
    float2 SrcInverse;    // 1 / the source texture's size
    float2 Extent;        // the pyramid's full size (the backdrop rounded up to a whole number of the coarsest texels)
    float2 AtlasInverse;  // 1 / the atlas's size
    float LevelCount;     // levels in the atlas (B_1 .. B_n)
    float StatsTile;      // a tile's side, device pixels
    float SigmaFrost;     // the frost behind the island's text, as a Gaussian deviation (device pixels)
    float SigmaBand;      // the band-pass's finer deviation (device pixels)
    float2 StatsOrigin;   // the island pixel at tile (0, 0)'s top-left corner
    float AlphaUp;        // the smoothing's weight for this frame: a value rising (and the mean either way); falling
    float AlphaDown;
    float BackdropPad;    // the backdrop's padding (island pixel + pad = backdrop pixel)
    float StampLast;      // the stats pass's frame stamps (1 .. 1000, in the stats' alpha): a tile whose alpha is not last
    float StampNow;       // frame's (never drawn, or not since) has no history
    float Spare;
    float4 Levels[8];     // per level: its atlas region's top-left (pixels), its texel (device pixels), its deviation
};

Texture2D Source : register(t0);     // the chain passes' source; the band pass: the backdrop (8-bit); the stats pass: the band image
Texture2D Atlas : register(t1);      // the band pass: the frost's levels
Texture2D Previous : register(t2);   // the stats pass: last frame's stats
SamplerState Smooth : register(s0);

struct Vertex { float4 Position : SV_Position; };

// Downsample: the output texel's centre lies on a corner between four source texels.
float4 Down(Vertex input) : SV_Target
{
    float2 c = (input.Position.xy - DstOrigin) * 2;
    float4 sum = Source.SampleLevel(Smooth, c * SrcInverse, 0) * 4;
    sum += Source.SampleLevel(Smooth, (c + float2(-1, -1)) * SrcInverse, 0);
    sum += Source.SampleLevel(Smooth, (c + float2(1, -1)) * SrcInverse, 0);
    sum += Source.SampleLevel(Smooth, (c + float2(-1, 1)) * SrcInverse, 0);
    sum += Source.SampleLevel(Smooth, (c + float2(1, 1)) * SrcInverse, 0);
    return float4(sum.rgb / 8, 1);
}

// Upsample: the output texel's centre a quarter of a source texel off a source texel's centre.
float4 Up(Vertex input) : SV_Target
{
    float2 c = (input.Position.xy - DstOrigin) * .5;
    float4 sum = 0;
    sum += Source.SampleLevel(Smooth, (c + float2(-1, 0)) * SrcInverse, 0);
    sum += Source.SampleLevel(Smooth, (c + float2(1, 0)) * SrcInverse, 0);
    sum += Source.SampleLevel(Smooth, (c + float2(0, -1)) * SrcInverse, 0);
    sum += Source.SampleLevel(Smooth, (c + float2(0, 1)) * SrcInverse, 0);
    sum += Source.SampleLevel(Smooth, (c + float2(-.5, -.5)) * SrcInverse, 0) * 2;
    sum += Source.SampleLevel(Smooth, (c + float2(.5, -.5)) * SrcInverse, 0) * 2;
    sum += Source.SampleLevel(Smooth, (c + float2(-.5, .5)) * SrcInverse, 0) * 2;
    sum += Source.SampleLevel(Smooth, (c + float2(.5, .5)) * SrcInverse, 0) * 2;
    return float4(sum.rgb / 12, 1);
}

// Level k (0-based: B_(k+1)) at a backdrop pixel, filtered, kept inside its own region of the atlas.
float3 Level(int k, float2 read)
{
    float4 l = Levels[k];
    float2 size = Extent / l.z;
    float2 q = clamp((read + .5) / l.z, .5, size - .5);
    return Atlas.SampleLevel(Smooth, (l.xy + q) * AtlasInverse, 0).rgb;
}

// The backdrop at a pixel under a Gaussian of deviation s (device pixels): the two levels around it, blended by where s lies
// between theirs on a log scale (below the first level, the backdrop itself and the first, linearly). read: backdrop pixels, continuous (+.5: the centre).
float3 FrostSigma(float2 read, float s)
{
    int count = (int)LevelCount;
    float first = Levels[0].w;
    [branch] if (s <= first)
    {
        // The backdrop itself, filtered: the band image's texels lie on the corners between pixels, each the mean of 2 x 2
        // (point samples a texel apart would alias fine text).
        float3 sharp = Source.SampleLevel(Smooth, (read + .5) * SrcInverse, 0).rgb;
        return lerp(sharp, Level(0, read), s / first);
    }
    [loop] for (int k = 0; k + 1 < count; k++)
    {
        float a = Levels[k].w, b = Levels[k + 1].w;
        if (s < b) return lerp(Level(k, read), Level(k + 1, read), log2(s / a) / log2(b / a));
    }
    return Level(count - 1, read);
}

float Luma(float3 c) { return dot(c, float3(.2126, .7152, .0722)); }
float LinearY(float3 c)
{
    float3 l = c <= .04045 ? c / 12.92 : pow((max(c, 0) + .055) / 1.055, 2.4);
    return dot(l, float3(.2126, .7152, .0722));
}

// The band image, at half the backdrop's size (each texel a 2 x 2 block, the island and a tile around it only): r the frosted
// backdrop's sRGB luma, g the square of its band-pass, b the square of the unfrosted backdrop's band-pass (linear Y).
float4 Band(Vertex input) : SV_Target
{
    float2 size;
    Source.GetDimensions(size.x, size.y);
    float2 read = clamp((input.Position.xy - DstOrigin) * 2 - .5, 0, size - 1);
    float wide = sqrt(SigmaFrost * SigmaFrost + SigmaBand * SigmaBand), wider = sqrt(SigmaFrost * SigmaFrost + 16 * SigmaBand * SigmaBand);
    float d = LinearY(FrostSigma(read, wide)) - LinearY(FrostSigma(read, wider));
    float e = LinearY(FrostSigma(read, SigmaBand)) - LinearY(FrostSigma(read, 4 * SigmaBand));
    return float4(Luma(FrostSigma(read, SigmaFrost)), d * d, e * e, 1);
}

// One tile: its mean of the band image (n x n bilinear taps on the corners between its texels, each the mean of 2 x 2), as
// mean luma, frosted band RMS, unfrosted band RMS; smoothed with last frame's (r, g, b); a the frame's stamp.
float4 Stats(Vertex input) : SV_Target
{
    int2 tile = int2(input.Position.xy);
    int n = max(1, (int)round(StatsTile / 4));
    float3 sum = 0;
    [loop] for (int j = 0; j < n * n; j++)
    {
        float2 at = StatsOrigin + (float2(tile) + (float2(j % n, j / n) + .5) / n) * StatsTile + BackdropPad;
        sum += Source.SampleLevel(Smooth, at * .5 * SrcInverse, 0).rgb;
    }
    sum /= n * n;
    float3 now = float3(sum.r, sqrt(sum.g), sqrt(sum.b));
    float4 last = Previous.Load(int3(tile, 0));
    if (abs(last.a - StampLast) > .5) return float4(now, StampNow);
    float3 alpha = float3(AlphaUp, now.gb > last.gb ? AlphaUp : AlphaDown);
    return float4(lerp(last.rgb, now, alpha), StampNow);
}
