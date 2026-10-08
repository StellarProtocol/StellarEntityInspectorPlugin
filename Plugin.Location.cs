using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.EntityInspector;

// Location tab — the GAME'S OWN region map art (read back once from the radar's background texture by
// LocationMapSource) cropped around the inspected player + you, with markers drawn on a plugin-owned CPU raster
// (MinimapRaster) shown through GameTextureElement. Positions come from IEntityTransforms / IPlayerState on the
// 10 Hz snapshot tick (only while the window is shown and this tab is selected); the raster redraws only when a
// marker moved or the base map changed. Every interop step is try/catch'd — failures become a status line.
public sealed partial class Plugin
{
    private const int MapPx = 320;               // on-screen raster size (square)
    private const float MinSpanWorld = 120f;     // never zoom in tighter than this many world units (show surroundings)
    private const float SpanPad = 1.5f;          // crop = separation × this, so both markers sit well inside the edge

    private enum LocState { None, Ok, Frozen, Far, Unavailable }
    private LocState _locState;
    private string _locLine = "";

    private MinimapRaster? _locRaster;
    private LocationMapSource? _locSource;
    private LocationMapSource LocSource => _locSource ??=
        new LocationMapSource(m => _services.Log.Info(m), m => _services.Log.Warning(m));

    // Redraw dirty-check: last drawn marker pixels / yaw / base version (NaN = force).
    private float _lastSu = float.NaN, _lastSv, _lastTu, _lastTv, _lastYaw;
    private int _lastBaseVer = -1;
    private bool _locWarned;

    private static readonly ColorRgba LocSelfCol   = new(1f, 1f, 1f, 1f);
    private static readonly ColorRgba LocTargetCol = new(0.98f, 0.72f, 0.20f, 1f);   // amber
    private static readonly ColorRgba LocRimCol    = new(0.05f, 0.06f, 0.08f, 1f);
    private static readonly string[] Compass8 = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    private HudElement BuildLocationBody() => new ColumnElement(new HudElement[]
    {
        new ConditionalElement(() => _locState != LocState.Ok, new TextElement(LocStateLine, MutedCol)),
        new ConditionalElement(() => _locState == LocState.Ok,
            new GameTextureElement(() => _locRaster?.Texture, MapPx, MapPx)),
        new ConditionalElement(() => _locState == LocState.Ok, new TextElement(() => _locLine)),
    }, Gap: 6f);

    private string LocStateLine() => _locState switch
    {
        LocState.Frozen      => _loc.T("ei.location.frozen"),
        LocState.Far         => _loc.T("ei.location.far"),
        LocState.Unavailable => _loc.T("ei.location.unavailable"),
        _                    => "",
    };

    // Window closed: free the cached map art (up to 16 MB) — re-read on the next Location view.
    private void DropLocationBase()
    {
        _locSource?.DropBase();
        _lastBaseVer = -1;
        _locState = LocState.None;
    }

    private void DisposeLocation()
    {
        _locRaster?.Dispose(); _locRaster = null;
        _locSource?.Dispose(); _locSource = null;
    }

    private void RebuildLocation()
    {
        if (IsFrozen) { _locState = LocState.Frozen; return; }
        try { _locState = UpdateLocation(); }
        catch (Exception ex)
        {
            if (!_locWarned) { _locWarned = true; _services.Log.Warning($"[EntityInspector][Location] update failed: {ex.Message}"); }
            _locState = LocState.Unavailable;
        }
    }

    private LocState UpdateLocation()
    {
        var sp = _services.PlayerState.Position;
        if (sp.X == 0f && sp.Y == 0f && sp.Z == 0f) return LocState.Unavailable;   // not in-world yet
        var self = new Vector3(sp.X, sp.Y, sp.Z);

        bool isSelf = IsSelf;
        Vector3 target; float yaw;
        if (_services.EntityTransforms.TryGetTransform(_target, out var tp, out yaw))
            target = new Vector3(tp.X, tp.Y, tp.Z);
        else if (isSelf) { target = self; yaw = 0f; }   // self always has a position, even if the transform read misses
        else return LocState.Far;
        if (isSelf) self = target;   // one marker: keep both points identical so the crop centres on it

        var src = LocSource;
        if (!src.TryEnsureBase(self, target)) return LocState.Unavailable;
        if (!src.TryImgPos(self, out var su) || !src.TryImgPos(target, out var tu)) return LocState.Unavailable;
        if (!InBase(src, su)) return LocState.Unavailable;   // you're outside the map art the radar shows
        if (!InBase(src, tu)) return LocState.Far;           // target is outside this region's map

        if (_lastBaseVer == src.Version && Near(su.x, _lastSu) && Near(su.y, _lastSv)
            && Near(tu.x, _lastTu) && Near(tu.y, _lastTv) && Math.Abs(yaw - _lastYaw) < 1f)
            return LocState.Ok;   // nothing moved enough to change the picture — keep the last upload
        _lastBaseVer = src.Version; _lastSu = su.x; _lastSv = su.y; _lastTu = tu.x; _lastTv = tu.y; _lastYaw = yaw;

        DrawLocation(src, su, tu, yaw, isSelf);

        float dx = target.x - self.x, dz = target.z - self.z;
        _locLine = isSelf
            ? _loc.T("ei.location.self")
            : _loc.TFormat("ei.location.distance",
                MathF.Sqrt(dx * dx + dz * dz).ToString("F0", CultureInfo.InvariantCulture), CompassOf(dx, dz));
        return LocState.Ok;
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.25f;   // NaN last ⇒ false ⇒ redraw

    private static bool InBase(LocationMapSource s, Vector2 p) => p.x >= 0f && p.y >= 0f && p.x < s.BaseW && p.y < s.BaseH;

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
    private void DrawLocation(LocationMapSource src, Vector2 su, Vector2 tu, float yaw, bool isSelf)
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
        if (!isSelf)
        {
            _locRaster.FillCircle(sx, sy, 6.5f, LocRimCol, 0.85f);
            _locRaster.FillCircle(sx, sy, 4.5f, LocSelfCol, 1f);
        }
        DrawArrow(_locRaster, tx, ty, yaw);
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
