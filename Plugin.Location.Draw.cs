using System;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.EntityInspector;

// Location tab — raster painting: crop the cached map art around the marker(s) (zoom) or take all of it (full map),
// resample it into the MinimapRaster, draw you (white dot) + the target (amber arrow when facing is known, amber dot
// otherwise). Also owns the raster's pane-driven sizing. Split out of Plugin.Location.cs, which decides WHERE the
// target is and WHICH base art to use.
public sealed partial class Plugin
{
    // The map is shown through a RenderTextureHostElement, which FILLS its pane (column width × leftover height) and
    // STRETCHES the bound texture to it (RawImage anchored 0..1, no AspectRatioFitter) — so the raster is sized to the
    // pane (W×H, reported each frame through OnViewportResize) and the square map is drawn centred/aspect-fit inside
    // it, the margins letterboxed with LocBackdrop.
    private const int LocPaneMin = 120;          // host LayoutElement min (both axes) — fits the body at MinHeight 360
    private const int LocRasterMin = 160, LocRasterMax = 1024;   // raster px clamp per axis (1024² = 4 MB × 2 buffers)
    private const int LocResizeStep = 8;         // rebuild at once when the pane changed by ≥ this many px …
    private const long LocResizeSettleMs = 200;  // … or once a smaller change has held this long (drags fire per frame)
    private const float MinSpanWorld = 120f;     // never zoom in tighter than this many world units (show surroundings)
    private const float SpanPad = 1.5f;          // crop = separation × this, so both markers sit well inside the edge

    private static readonly ColorRgba LocSelfCol   = new(1f, 1f, 1f, 1f);
    private static readonly ColorRgba LocTargetCol = new(0.98f, 0.72f, 0.20f, 1f);   // amber
    private static readonly ColorRgba LocRimCol    = new(0.05f, 0.06f, 0.08f, 1f);
    private static readonly string[] Compass8 = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    // Accepted raster size (what DrawLocation builds at) + the debounce's pending candidate.
    private int _locPxW = 400, _locPxH = 400;
    private int _locWantW, _locWantH;
    private long _locWantSinceMs;

    // OnViewportResize: called EVERY frame by the framework ticker (before it binds the texture), in canvas px.
    private void OnLocationPaneResize(int w, int h)
    {
        w = Math.Clamp(w, LocRasterMin, LocRasterMax);
        h = Math.Clamp(h, LocRasterMin, LocRasterMax);
        if (w == _locPxW && h == _locPxH) { _locWantW = 0; return; }
        long now = Environment.TickCount64;
        if (w != _locWantW || h != _locWantH) { _locWantW = w; _locWantH = h; _locWantSinceMs = now; }
        bool big = Math.Abs(w - _locPxW) >= LocResizeStep || Math.Abs(h - _locPxH) >= LocResizeStep;
        if (!big && now - _locWantSinceMs < LocResizeSettleMs) return;
        _locPxW = w; _locPxH = h; _locWantW = 0;
        _lastSu = float.NaN;   // force the redraw at the new size
        // Re-render now: the ticker binds the texture right after this returns, so the new raster shows this frame
        // and the old (Destroy-deferred) texture is never bound again. Off the Location tab it waits for the next view.
        if (_tab == Tab.Location && _locState == LocState.Ok) RebuildLocation();
    }

    // Direction self → target, N = +Z, E = +X (⚠ needs visual confirmation against the game's map orientation).
    private static string CompassOf(float dx, float dz)
    {
        if (dx * dx + dz * dz < 1f) return "";
        float deg = MathF.Atan2(dx, dz) * (180f / MathF.PI);   // 0 = N, 90 = E
        int i = (int)MathF.Floor(((deg + 360f + 22.5f) % 360f) / 45f) & 7;
        return Compass8[i];
    }

    // ZOOM: crop a square window of the cached map art centred on the midpoint of the two markers, sized to fit both
    // (≥ MinSpanWorld, ≤ the art), clamped inside the art. FULL: the whole art — a square window of side max(W, H)
    // centred on it, so a non-square art is letterboxed (Resample paints outside-the-art as backdrop) rather than
    // stretched. Either way the window is resampled into a side = min(rasterW, rasterH) square centred in the raster.
    // With no self marker the caller passes su == tu, so the crop centres on the target alone.
    private void DrawLocation(ILocationBase src, Vector2 su, Vector2 tu, float yaw, bool drawSelf, bool arrow, bool full)
    {
        if (_locRaster != null && (_locRaster.W != _locPxW || _locRaster.H != _locPxH))
        { _locRaster.Dispose(); _locRaster = null; }
        _locRaster ??= new MinimapRaster(_locPxW, _locPxH);
        int W = src.BaseW, H = src.BaseH;
        float span, left, bottom;
        if (full)
        {
            span = MathF.Max(W, H);
            left = (W - span) / 2f; bottom = (H - span) / 2f;   // ≤ 0: the letterbox margins
        }
        else
        {
            float sep = MathF.Max(MathF.Abs(su.x - tu.x), MathF.Abs(su.y - tu.y));
            span = MathF.Max(sep * SpanPad, MinSpanWorld * src.PxPerWorld);
            span = MathF.Min(span, MathF.Min(W, H));
            left   = Math.Clamp((su.x + tu.x) / 2f - span / 2f, 0f, W - span);
            bottom = Math.Clamp((su.y + tu.y) / 2f - span / 2f, 0f, H - span);
        }

        int side = Math.Min(_locRaster.W, _locRaster.H);
        int ox = (_locRaster.W - side) / 2, oy = (_locRaster.H - side) / 2;   // the square's top-left, canvas coords
        Resample(src.Base!, W, H, left, bottom, span, _locRaster, ox, oy, side);

        // Base px → raster canvas coords (x right, y DOWN — the raster's primitive convention).
        float k = side / span;
        float sx = ox + (su.x - left) * k, sy = oy + side - (su.y - bottom) * k;
        float tx = ox + (tu.x - left) * k, ty = oy + side - (tu.y - bottom) * k;
        // Markers are a CONSTANT on-screen size — the raster maps ~1:1 to screen px (the host stretches it to a pane
        // it was sized to), so fixed raster px = fixed screen px whatever the pane size or zoom/full view. (They used
        // to scale with side/320 plus a 1.35× full-view boost: on a ~700 px pane that made the target halo a ~44 px
        // blob.) Full view adds only a thin, faint ring around the target — a small location ping, not a disc.
        if (drawSelf)
        {
            _locRaster.FillCircle(sx, sy, 5f, LocRimCol, 0.85f);
            _locRaster.FillCircle(sx, sy, 3.5f, LocSelfCol, 1f);
        }
        if (full) Ping(_locRaster, tx, ty, 9f, LocTargetCol);
        if (arrow) DrawArrow(_locRaster, tx, ty, yaw);
        else
        {
            // Social (far) location: no facing on the wire (dir is always 0) — a plain amber dot, larger than yours.
            _locRaster.FillCircle(tx, ty, 6.75f, LocRimCol, 0.85f);
            _locRaster.FillCircle(tx, ty, 5f, LocTargetCol, 1f);
        }
        _locRaster.Upload();
    }

    // Full-view "find me" ping: a thin ring only (no filled disc — that's what read as a blob). A faint dark
    // under-stroke keeps it visible on bright art, the low-alpha colour stroke on dark art. Drawn under the marker.
    private static void Ping(MinimapRaster r, float cx, float cy, float rad, ColorRgba c)
    {
        r.StrokeCircle(cx, cy, rad, 2.2f, LocRimCol, 0.35f);
        r.StrokeCircle(cx, cy, rad, 1.2f, c, 0.55f);
    }

    // Facing arrow. Yaw = Unity-style degrees (0 = +Z/north, clockwise toward +X/east seen from above) on a north-up,
    // east-right map with canvas y down ⇒ direction (sin, −cos). ⚠ Needs visual confirmation in-game.
    // Fixed size in raster px (= screen px), same in zoom and full view.
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

    // Bilinear sample of the base window [left, left+span)² into the side×side square at canvas (ox, oy) of the
    // raster; everything outside that square — and any sample outside the art (full-view letterbox) — is backdrop.
    // Both buffers are bottom-origin RGBA32, so square byte row r maps straight to v = bottom + (r + 0.5)·step.
    private static void Resample(byte[] b, int W, int H, float left, float bottom, float span, MinimapRaster r,
        int ox, int oy, int side)
    {
        var dst = r.Pixels;
        int RW = r.W, RH = r.H;
        int row0 = RH - oy - side;   // the square's first byte row (canvas y runs down, byte rows run up)
        float step = span / side;
        for (int row = 0; row < RH; row++)
        {
            int sr = row - row0, o = row * RW * 4;
            float v = bottom + (sr + 0.5f) * step - 0.5f;
            bool rowIn = sr >= 0 && sr < side && v >= -0.5f && v <= H - 0.5f;
            int v0 = Math.Clamp((int)MathF.Floor(v), 0, H - 1), v1 = Math.Min(v0 + 1, H - 1);
            float fv = Math.Clamp(v - v0, 0f, 1f);
            for (int col = 0; col < RW; col++, o += 4)
            {
                int sc = col - ox;
                float u = left + (sc + 0.5f) * step - 0.5f;
                if (!rowIn || sc < 0 || sc >= side || u < -0.5f || u > W - 0.5f)
                {
                    dst[o] = LocBackdropB[0]; dst[o + 1] = LocBackdropB[1]; dst[o + 2] = LocBackdropB[2]; dst[o + 3] = 255;
                    continue;
                }
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
    private static readonly byte[] LocBackdropB = { 20, 24, 30 };

    private static float Lerp2(byte[] b, int i00, int i10, int i01, int i11, float fu, float fv)
    {
        float top = b[i00] + (b[i10] - b[i00]) * fu;
        float bot = b[i01] + (b[i11] - b[i01]) * fu;
        return top + (bot - top) * fv;
    }
}
