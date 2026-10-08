using System;
using System.Runtime.InteropServices;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.EntityInspector;

/// <summary>
/// Tiny CPU rasteriser into a plugin-owned RGBA32 <see cref="Texture2D"/>, shown through the framework's
/// <c>GameTextureElement</c> (which binds any boxed <c>UnityEngine.Texture</c> onto a RawImage — no framework change).
/// Coordinates are canvas-style (x right, y DOWN, pixel centres at +0.5); rows are flipped on write because a
/// Texture2D's row 0 is the bottom. Shapes are anti-aliased by 1-px distance coverage and source-over blended.
/// <para>Upload is allocation-free: the pixel buffer is a managed byte[] PINNED for the raster's lifetime and handed
/// to <c>Texture2D.LoadRawTextureData(IntPtr, int)</c> (dump.cs 1062399) — SetPixels32(Color32[]) would marshal a new
/// IL2CPP array every upload. A second buffer caches the static base layer (arena outline) so a redraw is one
/// BlockCopy plus the dynamic shapes.</para>
/// <para>Copied from StellarRaidManagerPlugin\Mechanics\MinimapRaster.cs (plugins can't share source) — keep fixes
/// in sync by hand.</para>
/// </summary>
internal sealed class MinimapRaster : IDisposable
{
    public readonly int Size;
    private readonly byte[] _px, _base;
    private GCHandle _pin;
    private Texture2D? _tex;

    public MinimapRaster(int size)
    {
        Size = size;
        _px = new byte[size * size * 4];
        _base = new byte[_px.Length];
        _pin = GCHandle.Alloc(_px, GCHandleType.Pinned);
    }

    // Boxed texture for GameTextureElement (created lazily on the main thread at first upload).
    public object? Texture => _tex;

    public void Upload()
    {
        _tex ??= new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        _tex.LoadRawTextureData(_pin.AddrOfPinnedObject(), _px.Length);
        _tex.Apply(false);
    }

    public void SaveBase()    => Buffer.BlockCopy(_px, 0, _base, 0, _px.Length);
    public void RestoreBase() => Buffer.BlockCopy(_base, 0, _px, 0, _px.Length);
    public void Clear()       => Array.Clear(_px, 0, _px.Length);

    public void Dispose()
    {
        if (_tex != null) { UnityEngine.Object.Destroy(_tex); _tex = null; }
        if (_pin.IsAllocated) _pin.Free();
    }

    // ── Pixel blend ──────────────────────────────────────────────────────────────────────────────────────────
    private void Blend(int x, int y, ColorRgba c, float a)
    {
        if (a <= 0f || (uint)x >= (uint)Size || (uint)y >= (uint)Size) return;
        if (a > 1f) a = 1f;
        int i = ((Size - 1 - y) * Size + x) * 4;
        float da = _px[i + 3] / 255f, oa = a + da * (1f - a);
        if (oa <= 0f) return;
        _px[i]     = (byte)((c.R * 255f * a + _px[i]     * da * (1f - a)) / oa);
        _px[i + 1] = (byte)((c.G * 255f * a + _px[i + 1] * da * (1f - a)) / oa);
        _px[i + 2] = (byte)((c.B * 255f * a + _px[i + 2] * da * (1f - a)) / oa);
        _px[i + 3] = (byte)(oa * 255f);
    }

    private static float Cov(float v) => v <= 0f ? 0f : v >= 1f ? 1f : v;

    private void Bounds(float x0, float y0, float x1, float y1, out int ax, out int ay, out int bx, out int by)
    {
        ax = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, x1))); ay = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, y1)));
        bx = Math.Min(Size - 1, (int)MathF.Ceiling(MathF.Max(x0, x1))); by = Math.Min(Size - 1, (int)MathF.Ceiling(MathF.Max(y0, y1)));
    }

    // ── Primitives ───────────────────────────────────────────────────────────────────────────────────────────
    public void FillRect(float x0, float y0, float x1, float y1, ColorRgba c, float a)
    {
        Bounds(x0, y0, x1, y1, out int ax, out int ay, out int bx, out int by);
        float lx = MathF.Min(x0, x1), hx = MathF.Max(x0, x1), ly = MathF.Min(y0, y1), hy = MathF.Max(y0, y1);
        for (int y = ay; y <= by; y++)
        {
            float cy = Cov(MathF.Min(y + 1f, hy) - MathF.Max(y, ly));
            if (cy <= 0f) continue;
            for (int x = ax; x <= bx; x++)
                Blend(x, y, c, a * cy * Cov(MathF.Min(x + 1f, hx) - MathF.Max(x, lx)));
        }
    }

    public void StrokeRect(float x0, float y0, float x1, float y1, float w, ColorRgba c, float a)
    {
        Line(x0, y0, x1, y0, w, c, a); Line(x1, y0, x1, y1, w, c, a);
        Line(x1, y1, x0, y1, w, c, a); Line(x0, y1, x0, y0, w, c, a);
    }

    public void Line(float x0, float y0, float x1, float y1, float w, ColorRgba c, float a)
    {
        float h = w / 2f + 1f;
        Bounds(MathF.Min(x0, x1) - h, MathF.Min(y0, y1) - h, MathF.Max(x0, x1) + h, MathF.Max(y0, y1) + h,
               out int ax, out int ay, out int bx, out int by);
        float dx = x1 - x0, dy = y1 - y0, len2 = dx * dx + dy * dy;
        for (int y = ay; y <= by; y++)
            for (int x = ax; x <= bx; x++)
            {
                float px = x + 0.5f - x0, py = y + 0.5f - y0;
                float t = len2 > 0f ? Math.Clamp((px * dx + py * dy) / len2, 0f, 1f) : 0f;
                float ex = px - t * dx, ey = py - t * dy;
                Blend(x, y, c, a * Cov(w / 2f + 0.5f - MathF.Sqrt(ex * ex + ey * ey)));
            }
    }

    // Filled disc / annulus / circle outline, all by distance-from-centre coverage.
    public void FillCircle(float cx, float cy, float r, ColorRgba c, float a) => FillAnnulus(cx, cy, -1f, r, c, a);

    public void FillAnnulus(float cx, float cy, float rIn, float rOut, ColorRgba c, float a)
    {
        Bounds(cx - rOut - 1, cy - rOut - 1, cx + rOut + 1, cy + rOut + 1, out int ax, out int ay, out int bx, out int by);
        for (int y = ay; y <= by; y++)
            for (int x = ax; x <= bx; x++)
            {
                float ddx = x + 0.5f - cx, ddy = y + 0.5f - cy, d = MathF.Sqrt(ddx * ddx + ddy * ddy);
                float cov = Cov(rOut + 0.5f - d);
                if (rIn >= 0f) cov = MathF.Min(cov, Cov(d - rIn + 0.5f));
                Blend(x, y, c, a * cov);
            }
    }

    public void StrokeCircle(float cx, float cy, float r, float w, ColorRgba c, float a)
        => FillAnnulus(cx, cy, r - w / 2f, r + w / 2f, c, a);

    // Filled triangle (edge-function test on pixel centres, 4× supersampled for soft edges).
    public void FillTriangle(float x0, float y0, float x1, float y1, float x2, float y2, ColorRgba c, float a)
    {
        Bounds(MathF.Min(x0, MathF.Min(x1, x2)), MathF.Min(y0, MathF.Min(y1, y2)),
               MathF.Max(x0, MathF.Max(x1, x2)), MathF.Max(y0, MathF.Max(y1, y2)), out int ax, out int ay, out int bx, out int by);
        float area = (x1 - x0) * (y2 - y0) - (y1 - y0) * (x2 - x0);
        if (MathF.Abs(area) < 1e-4f) return;
        for (int y = ay; y <= by; y++)
            for (int x = ax; x <= bx; x++)
            {
                int hits = 0;
                for (int s = 0; s < 4; s++)
                {
                    float px = x + 0.25f + 0.5f * (s & 1), py = y + 0.25f + 0.5f * (s >> 1);
                    float w0 = (x1 - x0) * (py - y0) - (y1 - y0) * (px - x0);
                    float w1 = (x2 - x1) * (py - y1) - (y2 - y1) * (px - x1);
                    float w2 = (x0 - x2) * (py - y2) - (y0 - y2) * (px - x2);
                    if (area > 0 ? (w0 >= 0 && w1 >= 0 && w2 >= 0) : (w0 <= 0 && w1 <= 0 && w2 <= 0)) hits++;
                }
                Blend(x, y, c, a * hits / 4f);
            }
    }

    // Filled polygon (sectors, half-plane clips) — even-odd scanline fill: per pixel row, the edge crossings at the
    // row centre are sorted and the spans between pairs filled, with fractional coverage on the span ends (the
    // caller strokes the outline with AA lines). `xy` = pixel x,y pairs; `n` = point count. Scratch is reused.
    private float[] _xs = new float[64];

    public void FillPolygon(float[] xy, int n, ColorRgba c, float a)
    {
        if (n < 3) return;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < n; i++) { minY = MathF.Min(minY, xy[2 * i + 1]); maxY = MathF.Max(maxY, xy[2 * i + 1]); }
        int y0 = Math.Max(0, (int)MathF.Floor(minY)), y1 = Math.Min(Size - 1, (int)MathF.Ceiling(maxY));
        if (_xs.Length < n) _xs = new float[n * 2];
        for (int y = y0; y <= y1; y++)
        {
            float yc = y + 0.5f;
            int k = 0;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float ay = xy[2 * i + 1], by = xy[2 * j + 1];
                if ((ay > yc) == (by > yc)) continue;
                float ax = xy[2 * i], bx = xy[2 * j];
                _xs[k++] = ax + (yc - ay) / (by - ay) * (bx - ax);
            }
            Array.Sort(_xs, 0, k);
            for (int s = 0; s + 1 < k; s += 2)
            {
                float l = _xs[s], r = _xs[s + 1];
                int xa = Math.Max(0, (int)MathF.Floor(l)), xb = Math.Min(Size - 1, (int)MathF.Ceiling(r));
                for (int x = xa; x <= xb; x++)
                    Blend(x, y, c, a * Cov(MathF.Min(x + 1f, r) - MathF.Max(x, l)));
            }
        }
    }

    // ── Tiny 3×5 bitmap font: digits plus 'F' and '?' (region labels "1F".."3F", markers 1..6). Scaled (float, so
    // the "Minimap size" factor carries through — glyph cells are AA rects, fractional is fine); with `outline` a dark
    // rim (scale/4, ≥ 1 px — 1 px at the base label scale 4) is drawn first so light text stays legible on any fill. ─
    private static readonly string[] Glyphs =
    {
        "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
        "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111",
    };
    private const string GlyphF = "111100110100100", GlyphQ = "111001011000010", GlyphHash = "101111101111101";  // '#' = raid crystal press order "#N"

    private static string? Glyph(char ch) =>
        ch >= '0' && ch <= '9' ? Glyphs[ch - '0'] : ch == 'F' || ch == 'f' ? GlyphF : ch == '?' ? GlyphQ : ch == '#' ? GlyphHash : null;

    public void Digits(string text, float cx, float cy, float scale, ColorRgba c) => Text(text, cx, cy, scale, c, outline: true);

    public void Text(string text, float cx, float cy, float scale, ColorRgba c, bool outline)
    {
        int n = 0;
        foreach (char ch in text) if (Glyph(ch) != null) n++;
        if (n == 0) return;
        float w = n * 4 * scale - scale, x0 = cx - w / 2f, y0 = cy - 2.5f * scale, rim = MathF.Max(1f, scale / 4f);
        var dark = new ColorRgba(0f, 0f, 0f, 1f);
        for (int pass = outline ? 0 : 1; pass < 2; pass++)
        {
            int k = 0;
            foreach (char ch in text)
            {
                string? g = Glyph(ch);
                if (g == null) continue;
                for (int r = 0; r < 5; r++)
                    for (int col = 0; col < 3; col++)
                    {
                        if (g[r * 3 + col] != '1') continue;
                        float gx = x0 + (k * 4 + col) * scale, gy = y0 + r * scale;
                        if (pass == 0) FillRect(gx - rim, gy - rim, gx + scale + rim, gy + scale + rim, dark, 0.85f);
                        else           FillRect(gx, gy, gx + scale, gy + scale, c, 1f);
                    }
                k++;
            }
        }
    }
}
