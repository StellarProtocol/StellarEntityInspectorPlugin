using System;
using System.Collections.Generic;
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
/// <para>Loads go through <c>ZResLoader.LoadAssetAsync&lt;T&gt;(…)</c> (the interop wrapper constrains T to
/// UnityEngine.Object; a ref-type instantiation shares the compiled <c>&lt;object&gt;</c> code) and the returned
/// <c>UniTask&lt;T&gt;</c> is POLLED from the 10 Hz tick (<c>Status</c>, then <c>GetAwaiter().GetResult()</c> once) —
/// never awaited, never blocking, and no managed callback handed to native code (cf. the Lua→C# callback crash). Every
/// loaded asset is handed back with the MATCHING <c>ReleaseAsset&lt;T&gt;</c> right after use; a load abandoned
/// mid-flight (scene switch / window close) is parked and released when it lands.</para>
/// <para>Art is tried as a ladder (in-game 2026-10-09: <c>&lt;Object&gt;</c> + allowFail on scene 7's
/// <c>fld001_fld001</c> completed with nothing usable and no error): every address × T ∈ {Object, Texture2D, Sprite}
/// with allowFail:true, then the same again with allowFail:false so the game's loader logs its own error. One
/// <c>[MapTex] art try</c> info line per attempt — that line is the diagnosis.</para>
/// <para>One base at a time (the newest scene asked for); region params are tiny and cached per scene. Reflection only
/// (the game types aren't compile-time references). Unity main thread only.</para>
/// </summary>
internal sealed partial class SceneMapLoader : ILocationBase, IDisposable
{
    private const int TimeoutTicks = 300;        // ~30 s at the 10 Hz tick — a load that never lands counts as failed
    private const string ArtRoot = "ui/textures/scenemaps/";

    private enum Phase { Idle, Region, Art, Ready, Failed }
    private readonly record struct RegionParams(string Name, Vector3 BL, Vector2 Size, Vector2 Off, string RdAddr);
    private readonly record struct ArtTry(object Addr, string Text, Kind Kind, bool AllowFail);

    private readonly Action<string> _info, _warn;
    public SceneMapLoader(Action<string> info, Action<string> warn) { _info = info; _warn = warn; }

    // ---- ILocationBase ----
    public byte[]? Base { get; private set; }
    public int BaseW { get; private set; }
    public int BaseH { get; private set; }
    public float PxPerWorld { get; private set; } = 1f;
    public int Version { get; private set; }

    public int SceneId { get; private set; }
    public bool IsIdle => _phase == Phase.Idle;
    public bool IsLoading => _phase is Phase.Region or Phase.Art;
    public bool IsReady => _phase == Phase.Ready && Base != null;

    private Phase _phase;
    private int _layer, _ticks;
    private bool _isCurrent;
    private readonly Dictionary<int, RegionParams?> _regions = new();   // null = scene has no region data
    private RegionParams _rp;
    private readonly Queue<ArtTry> _artCandidates = new();
    private ArtTry _curTry;
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
        if (rdAddr == null) { Fail($"no region-data address for scene {sceneId}"); return; }
        _rp = new RegionParams("", default, default, default, LocationMapSource.AddressString(rdAddr));
        if (Start(rdAddr, Kind.Object, true) is { } err) { Fail($"region-data load failed to start ('{_rp.RdAddr}'): {err}"); return; }
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
            if (++_ticks <= TimeoutTicks) return;
            ParkTask();   // released if it ever lands; the art ladder (if any) moves on
            if (_phase == Phase.Art) { LogTry(0, null, null, "timed out"); NextArt(); }
            else Fail($"load timed out (scene {SceneId})");
            return;
        }
        var task = _task; var kind = _taskKind; _task = null; _ticks = 0;
        var result = Result(task, st, out var err);
        if (_phase == Phase.Region) OnRegion(result, err);
        else if (_phase == Phase.Art) OnArt(result, st, err, target);
        else Release(result, kind);
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

    private void OnRegion(object? result, string? err)
    {
        var rd = result != null ? LocationMapSource.TryCast(result, _regionType!) : null;
        try
        {
            if (rd == null)
            {
                _regions[SceneId] = null;
                Fail($"region data missing for scene {SceneId} ('{_rp.RdAddr}'){(err != null ? ": " + err : "")}");
                return;
            }
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
        finally { Release(result, Kind.Object); }
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
        var addrs = new List<(object Addr, string Text)>(3);
        void Add(object a, string text) { if (!addrs.Exists(x => x.Text == text)) addrs.Add((a, text)); }
        bool sceneAddrFirst = haveSceneAddr && (folder == null || _sceneAddr.Contains(folder, StringComparison.Ordinal));
        if (sceneAddrFirst) Add(sceneAddr!, _sceneAddr);
        if (built != null) Add(built, built);
        if (haveSceneAddr && !sceneAddrFirst) Add(sceneAddr!, _sceneAddr);
        // _layer is OUR CurLayerId — for another scene it may not exist there, so layer 0 is the last resort.
        if (_layer != 0 && Call(_miSceneAddr, SceneId, 0) is { } a0 && LocationMapSource.AddressString(a0) is { Length: > 1 } t0)
            Add(a0, t0);

        // Ladder: per address T = Object → Texture2D → Sprite (the loader passes typeof(T) into its internal load, so
        // the type may matter), all quiet (allowFail); then the same loud (allowFail:false → the game logs why).
        foreach (bool quiet in new[] { true, false })
            foreach (var (a, text) in addrs)
                foreach (var k in new[] { Kind.Object, Kind.Texture2D, Kind.Sprite })
                    _artCandidates.Enqueue(new ArtTry(a, text, k, quiet));
        _phase = Phase.Art;
        NextArt();
    }

    private void NextArt()
    {
        while (_artCandidates.Count > 0)
        {
            _curTry = _artCandidates.Dequeue();
            if (Start(_curTry.Addr, _curTry.Kind, _curTry.AllowFail) is not { } err) return;
            LogTry(-1, null, null, "start failed: " + err);
        }
        Fail($"no map art for scene {SceneId} (sceneAddr='{_sceneAddr}' region={_rp.Name} layer={_layer})");
    }

    private void OnArt(object? result, int status, string? err, Vector3 target)
    {
        var kind = _curTry.Kind;   // captured: NextArt() below swaps _curTry before the finally runs
        try
        {
            var found = ExtractTexture(result, out var note);
            var tex = found;
            string why;
            if (tex == null) why = err != null ? $"{StatusWord(status)}: {err}" : note;
            else if (tex.width <= 1 || tex.height <= 1) { why = "texture too small"; tex = null; }
            else if (!LocationMapSource.Readback(tex, LocationMapSource.MaxReadback, m => WarnOnce("readback", m),
                         out var buf, out int w, out int h)) { why = "readback failed"; tex = null; }
            else
            {
                Base = buf; BaseW = w; BaseH = h; Version++;
                PxPerWorld = BaseW / _rp.Size.x;
                why = note.Length > 0 ? "ok; " + note : "ok";
            }
            LogTry(status, result, found, why);
            if (tex == null) { NextArt(); return; }

            _phase = Phase.Ready;
            _artCandidates.Clear();
            string tgt = TryImgPos(target, out var tp) ? tp.ToString() : "?";
            _info($"[EntityInspector][MapTex] scene={SceneId} cur={_isCurrent} rdAddr='{_rp.RdAddr}' sceneAddr='{_sceneAddr}' " +
                  $"region={_rp.Name} BL=({_rp.BL.x:F1},{_rp.BL.z:F1}) size={_rp.Size} off={_rp.Off} tex={tex.name} " +
                  $"{tex.width}x{tex.height} read={BaseW}x{BaseH} layer={_layer} generic={_curTry.Kind} " +
                  $"allowFail={_curTry.AllowFail} targetPx={tgt} via=loaded");
        }
        catch (Exception ex) { LogTry(status, result, null, "handler threw: " + ex.Message); NextArt(); }
        finally { Release(result, kind); }   // the readback is a managed copy — the asset can go back right away
    }

    // Texture2D / RenderTexture / any Texture, or a Sprite's backing texture (⚠ a Sprite may be an atlas slice — its
    // textureRect is logged; the projection assumes the full texture = the region). note = why nothing usable came back.
    private static Texture? ExtractTexture(object? result, out string note)
    {
        note = "";
        if (result is not Il2CppObjectBase ob) { note = "null result"; return null; }
        try
        {
            if (ob.TryCast<Texture2D>() is { } t2 && t2 != null) return t2;
            if (ob.TryCast<RenderTexture>() is { } rt && rt != null) return rt;
            if (ob.TryCast<Texture>() is { } tx && tx != null) return tx;
            if (ob.TryCast<Sprite>() is { } sp && sp != null)
            {
                var st = sp.texture;
                note = $"sprite→texture '{(st != null ? st.name : "null")}' textureRect={sp.textureRect} (may need a crop)";
                return st != null ? st : null;
            }
            if (ob.TryCast<UnityEngine.Object>() is { } uo && uo == null) { note = "destroyed object"; return null; }
            note = "not a Texture/Sprite";
        }
        catch (Exception ex) { note = "cast failed: " + ex.Message; }
        return null;
    }

    private static string StatusWord(int st) => st switch { 1 => "succeeded", 2 => "faulted", 3 => "canceled", _ => "pending" };

    // One info line per art attempt — the in-game diagnosis of the cross-scene art load.
    private void LogTry(int status, object? result, Texture? tex, string why)
    {
        string type = "null", name = "-", size = "-";
        try
        {
            if (result is Il2CppObjectBase ob)
            {
                type = ob.TryCast<Il2CppSystem.Object>()?.GetIl2CppType()?.FullName ?? result.GetType().FullName ?? "?";
                if (ob.TryCast<UnityEngine.Object>() is { } uo) name = uo != null ? uo.name : "(destroyed)";
            }
            else if (result != null) type = "managed:" + result.GetType().FullName;
            if (tex != null) size = $"{tex.width}x{tex.height}";
        }
        catch (Exception ex) { type += $" (describe failed: {ex.Message})"; }
        _info($"[EntityInspector][MapTex] art try scene={SceneId} addr='{_curTry.Text}' generic={_curTry.Kind} " +
              $"allowFail={_curTry.AllowFail} status={status} result={type} name={name} size={size} why={why}");
    }

    private void Fail(string why)
    {
        _phase = Phase.Failed;
        _info($"[EntityInspector][MapTex] scene={SceneId} cur={_isCurrent} no map: {why}");
    }

    private void DropBaseOnly() { Base = null; BaseW = BaseH = 0; }
}
