using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.EntityInspector;

/// <summary>
/// Map art for ANY scene (the Location tab's far-player case — the target is in another zone), loaded from the scene's
/// own assets WITHOUT touching the live radar: region params from the <c>ZMapRegionData</c> asset at
/// <c>MiniMapManager.GetSceneMapRegionDataAddress(sceneId)</c>, art from <c>GetSceneMapAddress(sceneId, layer)</c> with a
/// <c>ui/textures/scenemaps/&lt;base&gt;/&lt;base&gt;_&lt;RegionName&gt;</c> fallback (base = the region-data address's
/// folder). ⚠ <c>MiniMapManager.LoadMapRegionData</c> is deliberately NOT used — it likely writes the radar's live
/// region dict. See Knowledge Base\Minimap-World-Projection.md §"Another scene's map (cross-scene)".
/// <para>Loads go through <c>ZResLoader.LoadAssetAsync&lt;UnityEngine.Object&gt;(…, allowFail: true)</c> (the interop
/// wrapper constrains TObject to UnityEngine.Object; a ref-type instantiation shares the compiled <c>&lt;object&gt;</c>
/// code) and the returned <c>UniTask&lt;T&gt;</c> is POLLED from the 10 Hz tick (<c>Status</c>, then
/// <c>GetAwaiter().GetResult()</c> once) — never awaited, never blocking, and no managed callback handed to native code
/// (cf. the Lua→C# callback crash). Every loaded asset is handed back with <c>ReleaseAsset</c> right after use; a load
/// abandoned mid-flight (scene switch / window close) is parked and released when it lands.</para>
/// <para>One base at a time (the newest scene asked for); region params are tiny and cached per scene. Reflection only
/// (the game types aren't compile-time references). Unity main thread only.</para>
/// </summary>
internal sealed partial class SceneMapLoader : ILocationBase, IDisposable
{
    private const int TimeoutTicks = 300;        // ~30 s at the 10 Hz tick — a load that never lands counts as "no map"
    private const string ArtRoot = "ui/textures/scenemaps/";

    private enum Phase { Idle, Region, Art, Ready, Failed }
    private readonly record struct RegionParams(string Name, Vector3 BL, Vector2 Size, Vector2 Off, string RdAddr);

    private readonly Action<string> _info, _warn;
    public SceneMapLoader(Action<string> info, Action<string> warn) { _info = info; _warn = warn; }

    // ---- ILocationBase ----
    public byte[]? Base { get; private set; }
    public int BaseW { get; private set; }
    public int BaseH { get; private set; }
    public float PxPerWorld { get; private set; } = 1f;
    public int Version { get; private set; }

    public int SceneId { get; private set; }
    public bool IsLoading => _phase is Phase.Region or Phase.Art;
    public bool IsReady => _phase == Phase.Ready && Base != null;

    private Phase _phase;
    private int _layer, _ticks;
    private bool _isCurrent;
    private object? _task;                                   // pending UniTask<UnityEngine.Object>
    private readonly List<object> _orphans = new();          // abandoned loads, released when they land
    private readonly Dictionary<int, RegionParams?> _regions = new();   // null = scene has no region data
    private RegionParams _rp;
    private readonly Queue<(object Addr, string Text)> _artCandidates = new();
    private string _sceneAddr = "?";

    /// <summary>Make <paramref name="sceneId"/> the scene whose art is loaded. No-op when it already is (loading, ready
    /// or failed — a failed scene is retried only after <see cref="DropBase"/>).</summary>
    public void Request(int sceneId, int layer, bool isCurrentScene)
    {
        if (sceneId == SceneId && _phase != Phase.Idle) return;
        Abandon();
        DropBaseOnly();
        SceneId = sceneId; _layer = layer; _isCurrent = isCurrentScene; _ticks = 0; _sceneAddr = "?";
        if (sceneId == 0) { _phase = Phase.Failed; return; }
        // Types/singletons not up yet → stay Idle so the next tick retries; a broken resolve is final.
        if (!Resolve()) { _phase = _resolved ? Phase.Failed : Phase.Idle; return; }
        if (_regions.TryGetValue(sceneId, out var cached))
        {
            if (cached is { } rp) StartArt(rp); else _phase = Phase.Failed;
            return;
        }
        var rdAddr = Call(_miRdAddr, sceneId);
        if (rdAddr == null || !Start(rdAddr, null)) { Fail($"no region-data address for scene {sceneId}"); return; }
        _rp = new RegionParams("", default, default, default, LocationMapSource.AddressString(rdAddr));
        _phase = Phase.Region;
    }

    /// <summary>Poll the in-flight load (call every tick while the tab shows this scene). <paramref name="target"/>
    /// only feeds the one-line calibration log.</summary>
    public void Tick(Vector3 target)
    {
        PollOrphans();
        if (_task == null) return;
        int st = Status(_task);
        if (st == 0)
        {
            if (++_ticks > TimeoutTicks) { Abandon(); Fail($"load timed out (scene {SceneId})"); }
            return;
        }
        var task = _task; _task = null; _ticks = 0;
        var result = Result(task, st);
        if (_phase == Phase.Region) OnRegion(result);
        else if (_phase == Phase.Art) OnArt(result, target);
        else Release(result);
    }

    /// <summary>Free the loaded art (window close). Region params stay cached; a failed scene may retry.</summary>
    public void DropBase()
    {
        Abandon();
        DropBaseOnly();
        _phase = Phase.Idle; SceneId = 0;
    }

    public void Dispose() { DropBase(); _orphans.Clear(); }

    public bool TryImgPos(Vector3 world, out Vector2 px)
    {
        px = default;
        if (Base == null || _rp.Size.x <= 0f || _rp.Size.y <= 0f) return false;
        // Same arithmetic as the validated MiniMapManager.GetImgPos (Minimap-World-Projection.md), sized to our buffer.
        // ImgOffSet is added raw (⚠ unit unconfirmed — logged on every build; 0 on the regions seen so far).
        px = new Vector2((world.x - _rp.BL.x) / _rp.Size.x * BaseW + _rp.Off.x,
                         (world.z - _rp.BL.z) / _rp.Size.y * BaseH + _rp.Off.y);
        return !float.IsNaN(px.x) && !float.IsNaN(px.y);
    }

    // ---- phases ----

    private void OnRegion(object? result)
    {
        var rd = result != null ? LocationMapSource.TryCast(result, _regionType!) : null;
        try
        {
            if (rd == null) { _regions[SceneId] = null; Fail($"region data missing for scene {SceneId} ('{_rp.RdAddr}')"); return; }
            var t = rd.GetType();
            var rp = new RegionParams(
                StellarInterop.FindPropertyUp(t, "RegionName")?.GetValue(rd) as string ?? "",
                StellarInterop.FindPropertyUp(t, "BottomLeftPos")?.GetValue(rd) is Vector3 bl ? bl : default,
                StellarInterop.FindPropertyUp(t, "RegionSize")?.GetValue(rd) is Vector2 sz ? sz : default,
                StellarInterop.FindPropertyUp(t, "ImgOffSet")?.GetValue(rd) is Vector2 off ? off : default,
                _rp.RdAddr);
            bool ok = rp.Size.x > 0f && rp.Size.y > 0f;
            _regions[SceneId] = ok ? rp : null;
            if (ok) StartArt(rp); else Fail($"region data for scene {SceneId} has no size");
        }
        catch (Exception ex) { Fail($"region data read failed: {ex.Message}"); }
        finally { Release(result); }
    }

    private void StartArt(RegionParams rp)
    {
        _rp = rp;
        _artCandidates.Clear();
        var sceneAddr = Call(_miSceneAddr, SceneId, _layer);
        _sceneAddr = LocationMapSource.AddressString(sceneAddr);
        bool haveSceneAddr = sceneAddr != null && _sceneAddr.Length > 1;

        // Built path: ui/textures/scenemaps/<base>/<base>_<RegionName>, <base> = the region-data address's folder
        // (the radar's own art is 'ui/textures/scenemaps/cty001/cty001_cty001').
        string? built = null, folder = null;
        if (rp.RdAddr.StartsWith(ArtRoot, StringComparison.Ordinal) && rp.Name.Length > 0)
        {
            var rest = rp.RdAddr.Substring(ArtRoot.Length);
            int slash = rest.IndexOf('/');
            if (slash > 0) { var b = rest.Substring(0, slash); folder = $"/{b}/"; built = $"{ArtRoot}{b}/{b}_{rp.Name}"; }
        }

        // GetSceneMapAddress's body is stubbed in the dump — it may lean on the radar's CURRENT region. Trust it first
        // only when it points into this scene's own folder; otherwise the built path goes first (both are logged).
        bool sceneAddrFirst = haveSceneAddr && (folder == null || _sceneAddr.Contains(folder, StringComparison.Ordinal));
        if (sceneAddrFirst) _artCandidates.Enqueue((sceneAddr!, _sceneAddr));
        if (built != null && built != _sceneAddr) _artCandidates.Enqueue((built, built));
        if (haveSceneAddr && !sceneAddrFirst) _artCandidates.Enqueue((sceneAddr!, _sceneAddr));
        _phase = Phase.Art;
        NextArt();
    }

    private void NextArt()
    {
        while (_artCandidates.Count > 0)
        {
            var (addr, text) = _artCandidates.Dequeue();
            if (Start(addr, text)) return;
        }
        Fail($"no map art for scene {SceneId} (sceneAddr='{_sceneAddr}' region={_rp.Name})");
    }

    private void OnArt(object? result, Vector3 target)
    {
        Texture? tex = null;
        try { tex = (result as Il2CppObjectBase)?.TryCast<Texture>(); } catch { }
        try
        {
            if (tex == null || tex.width <= 1 || tex.height <= 1) { NextArt(); return; }
            if (!LocationMapSource.Readback(tex, LocationMapSource.MaxReadback, m => WarnOnce("readback", m),
                    out var buf, out int w, out int h)) { NextArt(); return; }
            Base = buf; BaseW = w; BaseH = h; Version++;
            PxPerWorld = BaseW / _rp.Size.x;
            _phase = Phase.Ready;
            string tgt = TryImgPos(target, out var tp) ? tp.ToString() : "?";
            _info($"[EntityInspector][MapTex] scene={SceneId} cur={_isCurrent} rdAddr='{_rp.RdAddr}' sceneAddr='{_sceneAddr}' " +
                  $"region={_rp.Name} BL=({_rp.BL.x:F1},{_rp.BL.z:F1}) size={_rp.Size} off={_rp.Off} tex={tex.name} " +
                  $"{tex.width}x{tex.height} read={w}x{h} layer={_layer} targetPx={tgt} via=loaded");
        }
        finally { Release(result); }   // the readback is a managed copy — the asset can go back right away
    }

    private void Fail(string why)
    {
        _phase = Phase.Failed;
        _info($"[EntityInspector][MapTex] scene={SceneId} cur={_isCurrent} no map: {why}");
    }

    private void DropBaseOnly() { Base = null; BaseW = BaseH = 0; }
}
