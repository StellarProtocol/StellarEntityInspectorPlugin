using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.EntityInspector;

// Location tab — the GAME'S OWN region map art cropped around the inspected player (+ you), with markers drawn on a
// plugin-owned CPU raster (MinimapRaster) shown through RenderTextureHostElement. Refreshed on the 10 Hz snapshot tick (only
// while the window is shown and this tab is selected); the raster redraws only when a marker moved or the base changed.
// Where the TARGET is comes from, in order:
//   (a) IEntityTransforms — in AOI ⇒ in our scene: live pos + yaw, art = the live radar's (LocationMapSource);
//   (b) the cached social snapshot's SocialLocation (filled by the target's profile/ID card fetch, any distance, any
//       zone): CurrentSceneId/CurrentPos, no facing (dir is always 0) ⇒ plain dot; art = the radar's when it's our
//       scene, else that scene's art loaded from its assets (SceneMapLoader — never touches the live radar);
//   (c) neither ⇒ a hint to open their profile card.
// Drawing (crop/resample/markers) lives in Plugin.Location.Draw.cs. Every interop step is try/catch'd — failures
// become a status line.
public sealed partial class Plugin
{
    private enum LocState { None, Ok, Frozen, Far, Unavailable, Loading, NoMap }
    private LocState _locState;
    private string _locLine = "", _locZone = "", _locAge = "";

    private MinimapRaster? _locRaster;
    private LocationMapSource? _locSource;
    private LocationMapSource LocSource => _locSource ??=
        new LocationMapSource(m => _services.Log.Info(m), m => _services.Log.Warning(m));
    private SceneMapLoader? _sceneMap;   // one other-scene base at a time (plus the radar's current-scene base above)
    private SceneMapLoader SceneMap => _sceneMap ??=
        new SceneMapLoader(m => _services.Log.Info(m), m => _services.Log.Warning(m));
    // One-shot per session CONTROL TEST (log only, never drawn): the first time the radar path serves the tab, the
    // cross-scene loader is ALSO run for our own scene — does the loader path work at all for known-good art (cty001),
    // or do only field maps fail? See Minimap-World-Projection.md §"Another scene's map (cross-scene)".
    private SceneMapLoader? _locProbe;
    private bool _locProbeStarted;
    private int _locProbeScene, _locProbeLayer, _locProbeIdle;

    // Redraw dirty-check: last drawn base / marker pixels / yaw / marker style (NaN = force).
    private float _lastSu = float.NaN, _lastSv, _lastTu, _lastTv, _lastYaw;
    private int _lastBaseVer = -1;
    private ILocationBase? _lastBaseSrc;
    private bool _lastDrawSelf, _lastArrow, _lastFull;
    private bool _locWarned;

    // Full map (whole region art) vs zoom (crop around the markers). Persisted: config "location" / "fullMap".
    private bool _locFullMap = true;

    // The map is a RenderTextureHostElement inside a Fill conditional: the host stretches to the column width and
    // takes the leftover body height (flexibleHeight), so it scales with the window instead of overflowing at a fixed
    // size. Its OnViewportResize drives the raster size (Plugin.Location.Draw.cs). No drag/scroll/pan handlers.
    // Toggle row + zone lines are fixed-height around it; at MinHeight 360 the host shrinks to its LocPaneMin.
    private HudElement BuildLocationBody() => new ColumnElement(new HudElement[]
    {
        new ConditionalElement(() => _locState != LocState.Ok, new TextElement(LocStateLine, MutedCol)),
        // The label names the ACTION (what a click switches to), not the current view.
        new ConditionalElement(() => _locState == LocState.Ok,
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => _loc.T(_locFullMap ? "ei.location.zoomIn" : "ei.location.fullMap"),
                    ToggleLocationFullMap),
                new SpacerElement(),
            })),
        new ConditionalElement(() => _locState == LocState.Ok,
            new RenderTextureHostElement(() => _locRaster?.Texture, LocPaneMin, LocPaneMin,
                OnViewportResize: OnLocationPaneResize),
            Fill: true),
        // Zone + age stay visible without art ("No map for this area" still says WHERE they are).
        new ConditionalElement(() => _locZone.Length > 0 && ShowsZone(), new TextElement(() => _locZone)),
        new ConditionalElement(() => _locAge.Length > 0 && ShowsZone(), new TextElement(() => _locAge, MutedCol)),
        new ConditionalElement(() => _locState == LocState.Ok && _locLine.Length > 0, new TextElement(() => _locLine)),
    }, Gap: 6f);

    private void ToggleLocationFullMap()
    {
        _locFullMap = !_locFullMap;
        var s = _services.Config.GetSection("location");
        s.Set("fullMap", _locFullMap);
        s.Save();
        _lastSu = float.NaN;   // force the redraw even if nothing moved
        RebuildLocation();     // re-render now, not on the next 10 Hz tick
    }

    private bool ShowsZone() => _locState is LocState.Ok or LocState.NoMap or LocState.Loading;

    private string LocStateLine() => _locState switch
    {
        LocState.Frozen      => _loc.T("ei.location.frozen"),
        LocState.Far         => _loc.T("ei.location.far"),
        LocState.Unavailable => _loc.T("ei.location.unavailable"),
        LocState.Loading     => _loc.T("ei.location.loading"),
        LocState.NoMap       => _loc.T("ei.location.noMap"),
        _                    => "",
    };

    // Window closed: free the cached map art (up to 16 MB each) — re-read on the next Location view.
    private void DropLocationBase()
    {
        _locSource?.DropBase();
        _sceneMap?.DropBase();
        _lastBaseVer = -1; _lastBaseSrc = null;
        _locState = LocState.None;
    }

    private void DisposeLocation()
    {
        _locRaster?.Dispose(); _locRaster = null;
        _locSource?.Dispose(); _locSource = null;
        _sceneMap?.Dispose(); _sceneMap = null;
        _locProbe?.Dispose(); _locProbe = null;
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
        _locZone = _locAge = _locLine = "";
        var sp = _services.PlayerState.Position;
        if (sp.X == 0f && sp.Y == 0f && sp.Z == 0f) return LocState.Unavailable;   // not in-world yet
        var self = new Vector3(sp.X, sp.Y, sp.Z);
        int curScene = CurrentSceneId();
        TickLocationProbe(self);

        // ---- where is the target? (a) live / (b) social / (c) unknown ----
        bool isSelf = IsSelf, live = true;
        Vector3 target; int scene = curScene;
        SocialLocation? social = null;
        if (_services.EntityTransforms.TryGetTransform(_target, out var tp, out var yaw))
            target = new Vector3(tp.X, tp.Y, tp.Z);
        else if (isSelf) { target = self; yaw = 0f; }   // self always has a position, even if the transform read misses
        else if (_socialSnap?.Location is { } loc && loc.CurrentSceneId != 0)
        {
            social = loc; live = false; yaw = 0f;
            scene = loc.CurrentSceneId;
            var p = loc.CurrentPos;
            target = new Vector3(p.X, p.Y, p.Z);
        }
        else return LocState.Far;
        if (isSelf) self = target;   // one marker: keep both points identical so the crop centres on it

        // In AOI ⇒ our scene by definition; a social location is ours only when the scene ids match.
        bool sameScene = live || (curScene != 0 && scene == curScene);
        _locZone = ZoneLabel(scene, social);
        _locAge = social is { } s ? AgeLabel(s) : "";

        // ---- base art: the live radar for our own scene, else that scene's loaded art ----
        ILocationBase src;
        Vector2 su = default, tu;
        bool drawSelf;
        var radar = LocSource;
        if (sameScene && radar.TryEnsureBase(self, target, curScene))
        {
            src = radar;
            if (!src.TryImgPos(self, out su) || !src.TryImgPos(target, out tu)) return LocState.Unavailable;
            if (!InBase(src, su)) return LocState.Unavailable;                     // you're outside the radar's map art
            if (!InBase(src, tu)) return live ? LocState.Far : LocState.NoMap;   // target outside this region's map
            drawSelf = !isSelf;
            StartLocationProbe(curScene);
        }
        else
        {
            var sm = SceneMap;
            // CurLayerId is OUR scene's layer; for another scene the loader also falls back to layer 0.
            int layer = sm.ReadCurLayer();
            sm.Request(scene, layer < 0 ? 0 : layer, sameScene);
            sm.Tick(target);
            if (sm.IsLoading) return LocState.Loading;
            if (!sm.IsReady) return LocState.NoMap;
            src = sm;
            if (!src.TryImgPos(target, out tu) || !InBase(src, tu)) return LocState.NoMap;
            drawSelf = sameScene && !isSelf && src.TryImgPos(self, out su) && InBase(src, su);
        }
        if (!drawSelf) su = tu;   // crop centres on the target alone
        bool arrow = live;        // social locations carry no facing ⇒ plain dot

        SetDistanceLine(isSelf, sameScene, self, target);
        if (ReferenceEquals(src, _lastBaseSrc) && _lastBaseVer == src.Version && drawSelf == _lastDrawSelf
            && arrow == _lastArrow && _locFullMap == _lastFull && Near(su.x, _lastSu) && Near(su.y, _lastSv)
            && Near(tu.x, _lastTu) && Near(tu.y, _lastTv) && Math.Abs(yaw - _lastYaw) < 1f)
            return LocState.Ok;   // nothing moved enough to change the picture — keep the last upload
        _lastBaseSrc = src; _lastBaseVer = src.Version; _lastDrawSelf = drawSelf; _lastArrow = arrow; _lastFull = _locFullMap;
        _lastSu = su.x; _lastSv = su.y; _lastTu = tu.x; _lastTv = tu.y; _lastYaw = yaw;

        DrawLocation(src, su, tu, yaw, drawSelf, arrow, _locFullMap);
        return LocState.Ok;
    }

    // Distance/compass only when both players are in the same scene (a cross-scene distance is meaningless).
    private void SetDistanceLine(bool isSelf, bool sameScene, Vector3 self, Vector3 target)
    {
        if (isSelf) { _locLine = _loc.T("ei.location.self"); return; }
        if (!sameScene) { _locLine = ""; return; }
        float dx = target.x - self.x, dz = target.z - self.z;
        _locLine = _loc.TFormat("ei.location.distance",
            MathF.Sqrt(dx * dx + dz * dz).ToString("F0", CultureInfo.InvariantCulture), CompassOf(dx, dz));
    }

    // IClientState.CurrentSceneName carries the numeric scene-table id (SceneInfo.Id); 0 when unknown.
    private int CurrentSceneId()
        => int.TryParse(_services.ClientState.CurrentSceneName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id : 0;

    // "<Scene name> · Line N". The area name (SceneAreaTable) isn't exposed by the framework's GameData, so it's
    // omitted; the line only comes with a social location (a live AOI target shares ours, which we don't know).
    private readonly List<string> _zoneParts = new(2);
    private string ZoneLabel(int scene, SocialLocation? social)
    {
        if (scene == 0) return "";
        _zoneParts.Clear();
        _zoneParts.Add(_services.GameData.World.GetScene(scene) is { Name.Length: > 0 } si
            ? si.Name : "#" + scene.ToString(CultureInfo.InvariantCulture));
        if (social is { LineId: > 0 } s) _zoneParts.Add(_loc.TFormat("ei.location.line", s.LineId));
        return string.Join("  ·  ", _zoneParts);
    }

    // "as of N min ago" — ReceivedAtMs and ServerNowMs share the framework's server clock; either 0 = pre-sync ⇒ omit.
    private string AgeLabel(SocialLocation s)
    {
        long now = _services.CombatSnapshot.ServerNowMs;
        if (s.ReceivedAtMs <= 0 || now <= 0) return "";
        long mins = Math.Max(0L, now - s.ReceivedAtMs) / 60_000L;
        return mins < 1 ? _loc.T("ei.location.asOfNow") : _loc.TFormat("ei.location.asOf", mins);
    }

    // Control test start (see _locProbe): once per session, on the first radar-served view.
    private void StartLocationProbe(int curScene)
    {
        if (_locProbeStarted || curScene == 0) return;
        _locProbeStarted = true;
        _locProbe = new SceneMapLoader(m => _services.Log.Info(m), m => _services.Log.Warning(m));
        _locProbeScene = curScene;
        _locProbeLayer = _locProbe.ReadCurLayer();
        _locProbe.LogAddressPair(curScene, _locProbeLayer);
        _locProbe.Request(curScene, Math.Max(0, _locProbeLayer), isCurrentScene: true);
    }

    // Polled from the tab tick like the real loader (never blocking); logs its result once, then frees everything.
    private void TickLocationProbe(Vector3 self)
    {
        var p = _locProbe;
        if (p == null) return;
        if (p.IsIdle)   // resolve wasn't ready — retry a while, then give up
        {
            if (++_locProbeIdle > 50) { _services.Log.Info("[EntityInspector][MapTex] control test gave up (loader never resolved)"); p.Dispose(); _locProbe = null; return; }
            p.Request(_locProbeScene, Math.Max(0, _locProbeLayer), isCurrentScene: true);
            return;
        }
        p.Tick(self);
        if (p.IsLoading) return;
        _services.Log.Info($"[EntityInspector][MapTex] control test done scene={_locProbeScene} layer={_locProbeLayer} " +
                           $"ready={p.IsReady} read={p.BaseW}x{p.BaseH}");
        p.Dispose(); _locProbe = null;
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.25f;   // NaN last ⇒ false ⇒ redraw

    private static bool InBase(ILocationBase s, Vector2 p) => p.x >= 0f && p.y >= 0f && p.x < s.BaseW && p.y < s.BaseH;
}
