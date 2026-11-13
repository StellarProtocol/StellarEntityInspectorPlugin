using System;
using UnityEngine;

namespace Stellar.EntityInspector;

// Location tab — the interactive view: a square window over the current base art (centre + span, both in base px),
// panned by dragging the map and zoomed by the wheel. Two presets: FULL (span = the whole art, centred, fixed) and
// FOCUS (centred on the target at a close zoom, FOLLOWING a live target as it moves until the user drags). The config
// "location"/"fullMap" only picks which preset a fresh view starts in (open / retarget / new base art).
public sealed partial class Plugin
{
    private const float ZoomStep = 1.25f;          // span × 1/1.25 per wheel notch in, × 1.25 out
    private const float MinViewSpanWorld = 60f;    // closest zoom: this many world units across

    // View window in BASE px (bottom-left origin, like TryImgPos). NaN span = not initialised for the current base.
    private float _viewCx, _viewCy, _viewSpan = float.NaN;
    private bool _viewFollow;                      // Focus: re-centre on the target each update (drag turns it off)
    private ILocationBase? _viewSrc;               // the base the view was initialised for (art change ⇒ re-init)
    private int _viewSrcVer = -1;
    private int _viewVer;                          // bumps on every user view change (redraw dirty-check)
    private int _lastViewVer = -1;
    private bool _locViewDirty;                    // drag/scroll since the last frame ⇒ re-render once next frame

    // Last drawn inputs, so a drag/scroll re-renders without re-reading positions (UpdateLocation runs at 10 Hz).
    private ILocationBase? _drawSrc;
    private Vector2 _drawSu, _drawTu;
    private bool _drawSelf;

    private static float FullSpan(ILocationBase s) => MathF.Max(s.BaseW, s.BaseH);
    private static float MinSpan(ILocationBase s) => MathF.Min(MinViewSpanWorld * s.PxPerWorld, FullSpan(s));

    // The old "zoom" crop size: separation × SpanPad, never tighter than MinSpanWorld, never past the art.
    private static float FocusSpan(ILocationBase s, Vector2 su, Vector2 tu)
    {
        float sep = MathF.Max(MathF.Abs(su.x - tu.x), MathF.Abs(su.y - tu.y));
        return Math.Clamp(MathF.Max(sep * SpanPad, MinSpanWorld * s.PxPerWorld), MinSpan(s), FullSpan(s));
    }

    // Forget the view: the next update re-initialises it from the start preset (open / retarget / window closed).
    private void ResetLocationView() { _viewSpan = float.NaN; _viewSrc = null; _drawSrc = null; _locViewDirty = false; }

    private void InitView(ILocationBase s, Vector2 su, Vector2 tu, bool full)
    {
        _viewSrc = s; _viewSrcVer = s.Version;
        if (full) { _viewSpan = FullSpan(s); _viewFollow = false; }
        else { _viewSpan = FocusSpan(s, su, tu); _viewFollow = true; _viewCx = tu.x; _viewCy = tu.y; }
        _viewVer++;
    }

    // Resolve this update's crop window (left/bottom/span in base px). Follow re-centres on the target; then span is
    // clamped to [MinSpan, FullSpan] and the centre kept so the window never leaves the art. At full span the centre
    // is pinned to the art centre (a non-square art letterboxes evenly, as before); likewise any axis the span
    // already exceeds (span between min(W,H) and max(W,H)).
    private void ResolveView(ILocationBase s, Vector2 su, Vector2 tu, out float left, out float bottom, out float span)
    {
        if (float.IsNaN(_viewSpan) || !ReferenceEquals(s, _viewSrc) || s.Version != _viewSrcVer)
            InitView(s, su, tu, _locFullMap);
        if (_viewFollow) { _viewCx = tu.x; _viewCy = tu.y; }
        int W = s.BaseW, H = s.BaseH;
        _viewSpan = Math.Clamp(_viewSpan, MinSpan(s), FullSpan(s));
        span = _viewSpan;
        float half = span / 2f;
        _viewCx = span >= W ? W / 2f : Math.Clamp(_viewCx, half, W - half);
        _viewCy = span >= H ? H / 2f : Math.Clamp(_viewCy, half, H - half);
        left = _viewCx - half; bottom = _viewCy - half;
    }

    private bool IsFullView => _drawSrc is { } s && !float.IsNaN(_viewSpan) && _viewSpan >= FullSpan(s) - 0.5f;

    // Wheel over the map. Delta convention (framework ticker → Input.mouseScrollDelta.y): wheel UP = +1 per notch,
    // same sign the portrait uses for "closer". Zooms around the current centre (no cursor position is passed).
    private void OnLocationScroll(float delta)
    {
        if (_drawSrc is null || float.IsNaN(_viewSpan) || delta == 0f) return;
        _viewSpan = Math.Clamp(_viewSpan * MathF.Pow(ZoomStep, -delta), MinSpan(_drawSrc), FullSpan(_drawSrc));
        _viewVer++; _locViewDirty = true;
    }

    // Drag over the map. Deltas are Input.mousePosition deltas: SCREEN px, x right, y UP. Base px also run y up, so
    // grab-and-drag (content follows the cursor) is centre −= delta × base-px-per-raster-px on both axes. ⚠ The raster
    // is sized in canvas units; when the canvas scales vs the screen the pan speed is off by that factor.
    private void OnLocationDrag(float dx, float dy)
    {
        if (_drawSrc is null || _locRaster is null || float.IsNaN(_viewSpan)) return;
        _viewFollow = false;   // the user took over — Focus turns following back on
        if (IsFullView) return;   // full view: centre is pinned, nothing to pan
        float k = _viewSpan / Math.Min(_locRaster.W, _locRaster.H);
        _viewCx -= dx * k; _viewCy -= dy * k;
        _viewVer++; _locViewDirty = true;
    }

    // Per frame (window shown): coalesce any drag/scroll since the last frame into ONE re-render of the last inputs.
    private void TickLocationView()
    {
        if (!_locViewDirty) return;
        _locViewDirty = false;
        if (_tab != Tab.Location || _locState != LocState.Ok || _drawSrc is null) return;
        DrawLocation(_drawSrc, _drawSu, _drawTu, _drawSelf);
        _lastViewVer = _viewVer;
    }

    // Buttons: "Full map" resets to the whole art; "Focus" re-centres on the target close-up and resumes following.
    // Each also persists itself as the start preset (config "location"/"fullMap").
    private void ShowFullMap() => SetLocationPreset(full: true);
    private void FocusTarget() => SetLocationPreset(full: false);

    private void SetLocationPreset(bool full)
    {
        _locFullMap = full;
        var s = _services.Config.GetSection("location");
        s.Set("fullMap", full);
        s.Save();
        if (_drawSrc is { } src) InitView(src, _drawSu, _drawTu, full);
        else _viewSpan = float.NaN;
        _lastSu = float.NaN;   // force the redraw even if nothing moved
        RebuildLocation();     // re-render now, not on the next 10 Hz tick
    }
}
