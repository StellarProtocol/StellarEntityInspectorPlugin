using System;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.EntityInspector;

// Location tab — raster painting: crop the cached map art around the marker(s), resample it into the MinimapRaster,
// draw you (white dot) + the target (amber arrow when facing is known, amber dot otherwise). Split out of
// Plugin.Location.cs, which decides WHERE the target is and WHICH base art to use.
public sealed partial class Plugin
{
    private const int MapPx = 320;               // on-screen raster size (square)
    private const float MinSpanWorld = 120f;     // never zoom in tighter than this many world units (show surroundings)
    private const float SpanPad = 1.5f;          // crop = separation × this, so both markers sit well inside the edge

    private static readonly ColorRgba LocSelfCol   = new(1f, 1f, 1f, 1f);
    private static readonly ColorRgba LocTargetCol = new(0.98f, 0.72f, 0.20f, 1f);   // amber
    private static readonly ColorRgba LocRimCol    = new(0.05f, 0.06f, 0.08f, 1f);
    private static readonly string[] Compass8 = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    // Direction self → target, N = +Z, E = +X (⚠ needs visual confirmation against the game's map orientation).
    private static string CompassOf(float dx, float dz)
    {
        if (dx * dx + dz * dz < 1f) return "";
        float deg = MathF.Atan2(dx, dz) * (180f / MathF.PI);   // 0 = N, 90 = E
        int i = (int)MathF.Floor(((deg + 360f + 22.5f) % 360f) / 45f) & 7;
        return Compass8[i];
    }

    // Crop a square window of the cached map art centred on the midpoint of the two markers, sized to fit both
    // (≥ MinSpanWorld, ≤ the art), clamped inside the art; bilinear-resample it into the raster; draw the markers.
    // With no self marker the caller passes su == tu, so the crop centres on the target alone.
    private void DrawLocation(ILocationBase src, Vector2 su, Vector2 tu, float yaw, bool drawSelf, bool arrow)
    {
        _locRaster ??= new MinimapRaster(MapPx);
        int W = src.BaseW, H = src.BaseH;
        float sep = MathF.Max(MathF.Abs(su.x - tu.x), MathF.Abs(su.y - tu.y));
        float span = MathF.Max(sep * SpanPad, MinSpanWorld * src.PxPerWorld);
        span = MathF.Min(span, MathF.Min(W, H));
        float left   = Math.Clamp((su.x + tu.x) / 2f - span / 2f, 0f, W - span);
        float bottom = Math.Clamp((su.y + tu.y) / 2f - span / 2f, 0f, H - span);

        Resample(src.Base!, W, H, left, bottom, span, _locRaster);

        // Base px → raster canvas coords (x right, y DOWN — the raster's primitive convention).
        float k = MapPx / span;
        float sx = (su.x - left) * k, sy = MapPx - (su.y - bottom) * k;
        float tx = (tu.x - left) * k, ty = MapPx - (tu.y - bottom) * k;
        if (drawSelf)
        {
            _locRaster.FillCircle(sx, sy, 6.5f, LocRimCol, 0.85f);
            _locRaster.FillCircle(sx, sy, 4.5f, LocSelfCol, 1f);
        }
        if (arrow) DrawArrow(_locRaster, tx, ty, yaw);
        else
        {
            // Social (far) location: no facing on the wire (dir is always 0) — a plain amber dot, larger than yours.
            _locRaster.FillCircle(tx, ty, 8f, LocRimCol, 0.85f);
            _locRaster.FillCircle(tx, ty, 6f, LocTargetCol, 1f);
        }
        _locRaster.Upload();
    }

    // Facing arrow. Yaw = Unity-style degrees (0 = +Z/north, clockwise toward +X/east seen from above) on a north-up,
    // east-right map with canvas y down ⇒ direction (sin, −cos). ⚠ Needs visual confirmation in-game.
    private static void DrawArrow(MinimapRaster r, float cx, float cy, float yawDeg)
    {
        float a = yawDeg * (MathF.PI / 180f);
        float dx = MathF.Sin(a), dy = -MathF.Cos(a), px = -dy, py = dx;
        for (int pass = 0; pass < 2; pass++)
        {
            float s = pass == 0 ? 1.35f : 1f;   // dark rim pass first, then the amber body
            float tipX = cx + dx * 11f * s, tipY = cy + dy * 11f * s;
            float bx = cx - dx * 6f * s, by = cy - dy * 6f * s;
            r.FillTriangle(tipX, tipY, bx + px * 7f * s, by + py * 7f * s, bx - px * 7f * s, by - py * 7f * s,
                pass == 0 ? LocRimCol : LocTargetCol, pass == 0 ? 0.85f : 1f);
        }
    }

    // Bilinear sample of the base crop [left, left+span) × [bottom, bottom+span) into the raster. Both buffers are
    // bottom-origin RGBA32, so raster byte row r maps straight to v = bottom + (r + 0.5)·step — no flip.
    private static void Resample(byte[] b, int W, int H, float left, float bottom, float span, MinimapRaster r)
    {
        var dst = r.Pixels;
        int S = r.Size;
        float step = span / S;
        for (int row = 0; row < S; row++)
        {
            float v = bottom + (row + 0.5f) * step - 0.5f;
            int v0 = Math.Clamp((int)MathF.Floor(v), 0, H - 1), v1 = Math.Min(v0 + 1, H - 1);
            float fv = Math.Clamp(v - v0, 0f, 1f);
            int o = row * S * 4;
            for (int col = 0; col < S; col++, o += 4)
            {
                float u = left + (col + 0.5f) * step - 0.5f;
                int u0 = Math.Clamp((int)MathF.Floor(u), 0, W - 1), u1 = Math.Min(u0 + 1, W - 1);
                float fu = Math.Clamp(u - u0, 0f, 1f);
                int i00 = (v0 * W + u0) * 4, i10 = (v0 * W + u1) * 4, i01 = (v1 * W + u0) * 4, i11 = (v1 * W + u1) * 4;
                float al = Lerp2(b, i00 + 3, i10 + 3, i01 + 3, i11 + 3, fu, fv) / 255f;
                // Composite over a dark backdrop by the art's alpha (off-map areas are transparent in the art) so
                // the raster is opaque and those areas read as "no map" rather than undefined colour.
                for (int ch = 0; ch < 3; ch++)
                    dst[o + ch] = (byte)(Lerp2(b, i00 + ch, i10 + ch, i01 + ch, i11 + ch, fu, fv) * al
                                         + LocBackdrop[ch] * (1f - al) + 0.5f);
                dst[o + 3] = 255;
            }
        }
    }

    private static readonly float[] LocBackdrop = { 20f, 24f, 30f };

    private static float Lerp2(byte[] b, int i00, int i10, int i01, int i11, float fu, float fv)
    {
        float top = b[i00] + (b[i10] - b[i00]) * fu;
        float bot = b[i01] + (b[i11] - b[i01]) * fu;
        return top + (bot - top) * fv;
    }
}
