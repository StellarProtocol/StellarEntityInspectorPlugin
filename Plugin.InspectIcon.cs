using UnityEngine;

namespace Stellar.EntityInspector;

/// <summary>
/// Builds the procedural magnifier icon for the native profile-card "Inspect" action button, encoded as
/// raw PNG bytes for <c>ProfileCardActionSpec.IconPng</c> (the framework rasterises it back to a texture
/// via its own icon cache). The drawing previously lived in Infrastructure's hardcoded inspect button; it
/// moves here now that the injector is generic — the plugin owns the look of its own action.
///
/// <para>The glyph: a white ring (circle outline) in the upper-left + a short diagonal handle to the
/// lower-right, drawn to an RGBA32 texture with a soft 1px distance-band edge so it scales cleanly. Baked
/// at 4× the on-screen footprint (supersampled) so the framework's mip downsample stays crisp.</para>
/// </summary>
public sealed partial class Plugin
{
    private const int IconTexPx = 176;   // 4 × the ~44px on-screen footprint — supersampled

    // Bake the magnifier glyph and return its PNG bytes. The transient texture is destroyed after encoding
    // (only the bytes are kept — the framework owns the live render texture). Runs at plugin construction
    // (main thread), so Texture2D ops are safe.
    private static byte[]? BuildMagnifierPng()
    {
        var tex = BakeMagnifier();
        try
        {
            return ImageConversion.EncodeToPNG(tex);
        }
        finally
        {
            Object.Destroy(tex);
        }
    }

    // Draw a white magnifying-glass glyph (ring + diagonal handle) procedurally to an RGBA32 texture with a
    // soft distance-band edge (antialias) so it scales cleanly. White; alpha is coverage — the framework's
    // RawImage tints it muted-white. Texture origin is bottom-left (x right, y up).
    private static Texture2D BakeMagnifier()
    {
        const int n = IconTexPx;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: false);
        var px = new Color[n * n];
        var ringC = new Vector2(n * 0.40f, n * 0.60f);   // ring centre, upper-left
        float ringR = n * 0.26f;                          // ring centre radius
        float ringW = n * 0.075f;                         // ring stroke half-width
        float handW = n * 0.06f;                          // handle half-width
        var handA = new Vector2(ringC.x + ringR * 0.707f, ringC.y - ringR * 0.707f);   // touches ring at 45° down-right
        var handB = new Vector2(n * 0.86f, n * 0.14f);                                   // toward bottom-right corner

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            float ringD = Mathf.Abs((p - ringC).magnitude - ringR) - ringW;   // signed dist to ring stroke
            float handD = DistToSegment(p, handA, handB) - handW;             // signed dist to handle stroke
            float d = Mathf.Min(ringD, handD);
            float aa = 1.5f;                                                  // edge softness (px)
            float alpha = Mathf.Clamp01(0.5f - d / aa);                       // 1 inside, 0 outside, soft band
            px[y * n + x] = new Color(1f, 1f, 1f, alpha);
        }
        tex.SetPixels(px);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        return tex;
    }

    // Shortest distance from point p to the segment [a,b] (handle stroke skeleton).
    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.sqrMagnitude;
        float t = len2 <= 1e-4f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return (p - (a + ab * t)).magnitude;
    }
}
