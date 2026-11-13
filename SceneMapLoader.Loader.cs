using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.EntityInspector;

// ZResLoader / UniTask reflection plumbing for SceneMapLoader (split out for the 500-LoC cap). See SceneMapLoader.cs
// for the polling model and Knowledge Base\Minimap-World-Projection.md §"Another scene's map (cross-scene)".
internal sealed partial class SceneMapLoader
{
    // The generic T a load was started with — its ReleaseAsset<T> must match (the loader keys its refcount by type).
    private enum Kind { Object, Texture2D, Sprite }
    private static readonly Type[] KindTypes = { typeof(UnityEngine.Object), typeof(Texture2D), typeof(Sprite) };

    private bool _resolved, _resolveFailed;
    private Type? _regionType;
    private object? _mgr, _loader;
    private MethodInfo? _miRdAddr, _miSceneAddr, _miCurAddr;
    private PropertyInfo? _piCurLayer;
    private readonly MethodInfo?[] _loadByAddr = new MethodInfo?[3], _loadByString = new MethodInfo?[3], _release = new MethodInfo?[3];
    // ONE rented ZCancelSource for the loader's lifetime; CreateToken() mints a fresh uint per load. ZResLoader rejects
    // token=0 AND a reused token ("invaid token") — it releases the token when the load completes, so NeverCancelToken /
    // any cached token faults every load after the first (in-game 2026-10-09: token=27904 faulted every art try, even
    // the known-good cty001 control). Same pattern as the framework's GameAssetsService; Collision-and-Navmesh-Bake.md.
    private object? _cancelSource;
    private MethodInfo? _miCreateToken;

    private object? _task;                                            // pending UniTask<T>
    private Kind _taskKind;
    private readonly List<(object Task, Kind Kind)> _orphans = new();   // abandoned loads, released when they land

    private bool Resolve()
    {
        if (_resolved) return !_resolveFailed;
        try
        {
            var mgrType    = StellarInterop.FindType("Panda.ZGame.MiniMapManager");
            var loaderType = StellarInterop.FindType("ZResource.Loader.ZResLoader");
            _regionType    = StellarInterop.FindType("Panda.ZGame.ZMapRegionData");
            if (mgrType == null || loaderType == null || _regionType == null) return false;   // not loaded yet — retry
            _mgr    = StellarInterop.GetSingleton(mgrType);
            _loader = StellarInterop.GetSingleton(loaderType);
            if (_mgr == null || _loader == null) return false;
            _miRdAddr    = LocationMapSource.FindMethod(mgrType, "GetSceneMapRegionDataAddress", 1);
            _miSceneAddr = LocationMapSource.FindMethod(mgrType, "GetSceneMapAddress", 2);
            _miCurAddr   = LocationMapSource.FindMethod(mgrType, "GetCurRegionMapAddress", 0);   // control-test log only
            _piCurLayer  = StellarInterop.FindPropertyUp(mgrType, "CurLayerId");
            // LoadAssetAsync has three 4-arg generic overloads (string / ZResAddress / uint) — match by first-param TYPE.
            // Each is closed over every Kind (a bad instantiation just leaves that slot null).
            foreach (var m in loaderType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                MethodInfo?[]? slots = null;
                if (m.Name == "LoadAssetAsync" && ps.Length == 4)
                {
                    if (ps[0].ParameterType == typeof(string)) slots = _loadByString;
                    else if (ps[0].ParameterType.Name == "ZResAddress") slots = _loadByAddr;
                }
                else if (m.Name == "ReleaseAsset" && ps.Length == 1) slots = _release;
                if (slots == null) continue;
                for (int k = 0; k < KindTypes.Length; k++)
                    try { slots[k] = m.MakeGenericMethod(KindTypes[k]); } catch { }
            }
            RentCancelSource();

            _resolved = true;
            _resolveFailed = _miRdAddr == null || _loadByAddr[0] == null || _release[0] == null
                             || _cancelSource == null || _miCreateToken == null;
            _info($"[EntityInspector][MapTex] loader resolve rdAddr={_miRdAddr != null} sceneAddr={_miSceneAddr != null} " +
                  $"loadAddr={Slots(_loadByAddr)} loadStr={Slots(_loadByString)} release={Slots(_release)} " +
                  $"curLayer={_piCurLayer != null} token={(_cancelSource != null && _miCreateToken != null ? "rented" : "MISSING")}");
            return !_resolveFailed;
        }
        catch (Exception ex)
        {
            WarnOnce("resolve", $"scene-map loader resolve error: {ex.Message}");
            _resolved = true; _resolveFailed = true; return false;
        }
    }

    // Idempotent: a Resolve that bailed early (types not up yet) retries — never re-rent (would orphan the prior rent).
    private void RentCancelSource()
    {
        if (_cancelSource != null) return;
        var cs = StellarInterop.FindType("ZUtil.ZCancelSource");
        if (cs == null) { WarnOnce("token", "ZUtil.ZCancelSource not found — loads can't get a token"); return; }
        try
        {
            _cancelSource = cs.GetMethod("Rent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                                         null, Type.EmptyTypes, null)?.Invoke(null, null);
        }
        catch (Exception ex) { WarnOnce("token", $"ZCancelSource.Rent() threw: {ex.InnerException?.Message ?? ex.Message}"); return; }
        if (_cancelSource == null) { WarnOnce("token", "ZCancelSource.Rent() returned null"); return; }
        _miCreateToken = cs.GetMethod("CreateToken", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                      null, Type.EmptyTypes, null);
        if (_miCreateToken == null) WarnOnce("token", "ZCancelSource.CreateToken() not found");
    }

    // A fresh single-use token for one load; 0 = mint failed (logged once — the caller treats it as a failed start).
    private uint MintToken()
    {
        try
        {
            if (_miCreateToken?.Invoke(_cancelSource, null) is uint t && t != 0) return t;
            WarnOnce("mint", "ZCancelSource.CreateToken() returned 0");
        }
        catch (Exception ex) { WarnOnce("mint", $"ZCancelSource.CreateToken() threw: {ex.InnerException?.Message ?? ex.Message}"); }
        return 0;
    }

    // "OTS" style flags: which of Object/Texture2D/Sprite closed OK ('-' = failed).
    private static string Slots(MethodInfo?[] s) => $"{(s[0] != null ? 'O' : '-')}{(s[1] != null ? 'T' : '-')}{(s[2] != null ? 'S' : '-')}";

    /// <summary>The radar's <c>MiniMapManager.CurLayerId</c> (our own scene's layer), or -1 when unreadable.</summary>
    public int ReadCurLayer()
    {
        if (!Resolve()) return -1;
        try { return _piCurLayer?.GetValue(_mgr) is int l ? l : -1; } catch { return -1; }
    }

    /// <summary>Control-test diagnostic: the radar's own current art address next to what the cross-scene path would
    /// ask for, for the same (current) scene.</summary>
    public void LogAddressPair(int sceneId, int layer)
    {
        if (!Resolve()) { _info("[EntityInspector][MapTex] control test: loader not resolved"); return; }
        string cur = LocationMapSource.AddressString(Call(_miCurAddr));
        string atLayer = LocationMapSource.AddressString(Call(_miSceneAddr, sceneId, Math.Max(0, layer)));
        string at0 = LocationMapSource.AddressString(Call(_miSceneAddr, sceneId, 0));
        _info($"[EntityInspector][MapTex] control test scene={sceneId} CurLayerId={layer} GetCurRegionMapAddress='{cur}' " +
              $"GetSceneMapAddress({sceneId},{Math.Max(0, layer)})='{atLayer}' GetSceneMapAddress({sceneId},0)='{at0}' " +
              $"match={cur == atLayer}");
    }

    private object? Call(MethodInfo? m, params object[] args)
    {
        try { return m != null && _mgr != null ? m.Invoke(_mgr, args) : null; }
        catch (Exception ex) { WarnOnce("call:" + m?.Name, $"{m?.Name} failed: {ex.InnerException?.Message ?? ex.Message}"); return null; }
    }

    // Kick off one load (ZResAddress object, or a plain string path → the string overload). Returns null when started,
    // else why not. allowFail:true → a missing asset completes with null instead of throwing inside the game's loader;
    // false → the game's loader logs its own error (and may throw synchronously or fault the task — both caught).
    private string? Start(object addr, Kind kind, bool allowFail)
    {
        try
        {
            var m = addr is string ? _loadByString[(int)kind] : _loadByAddr[(int)kind];
            if (m == null) return $"no LoadAssetAsync<{kind}> ({(addr is string ? "string" : "ZResAddress")} overload)";
            uint token = MintToken();   // per load — region data, every art try, and the control test's own loader
            if (token == 0) return "no cancel token (CreateToken failed)";
            var task = m.Invoke(_loader, new object[] { addr, token, 0, allowFail });
            if (task == null) return "LoadAssetAsync returned null";
            _task = task; _taskKind = kind; _ticks = 0;
            return null;
        }
        catch (Exception ex) { return ex.InnerException?.Message ?? ex.Message; }
    }

    // UniTaskStatus: 0 Pending, 1 Succeeded, 2 Faulted, 3 Canceled (dump.cs L1297891). Unreadable → treat as faulted.
    private static int Status(object task)
    {
        try { return Convert.ToInt32(task.GetType().GetProperty("Status")?.GetValue(task)); }
        catch { return 2; }
    }

    // GetResult exactly once per task: it hands the pooled source back. A faulted/cancelled task throws from it —
    // still call it (observes the fault, returns the source), yield null and hand back the exception message.
    private object? Result(object task, int status, out string? err)
    {
        err = null;
        try
        {
            var awaiter = task.GetType().GetMethod("GetAwaiter", Type.EmptyTypes)?.Invoke(task, null);
            if (awaiter == null) { err = "no GetAwaiter"; return null; }
            var getResult = awaiter.GetType().GetMethod("GetResult", Type.EmptyTypes);
            if (getResult == null) { err = "no GetResult"; return null; }
            var r = getResult.Invoke(awaiter, null);
            return status == 1 ? r : null;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            err = $"{inner.GetType().Name}: {inner.Message}";
            if (status == 1) WarnOnce("result", $"GetResult failed: {err}");
            return null;
        }
    }

    private void Release(object? asset, Kind kind)
    {
        var m = _release[(int)kind];
        if (asset == null || m == null) return;
        try { m.Invoke(_loader, new[] { asset }); }
        catch (Exception ex) { WarnOnce("release:" + kind, $"ReleaseAsset<{kind}> failed: {ex.InnerException?.Message ?? ex.Message}"); }
    }

    // A load still in flight when it's given up on: park it, release whatever it yields later.
    private void ParkTask()
    {
        if (_task != null) { _orphans.Add((_task, _taskKind)); _task = null; }
    }

    // Scene change / window close: park the in-flight load and drop the rest of the art ladder.
    private void Abandon()
    {
        ParkTask();
        _artCandidates.Clear();
    }

    private void PollOrphans()
    {
        for (int i = _orphans.Count - 1; i >= 0; i--)
        {
            var (task, kind) = _orphans[i];
            int st = Status(task);
            if (st == 0) continue;
            Release(Result(task, st, out _), kind);
            _orphans.RemoveAt(i);
        }
    }

    private readonly HashSet<string> _warned = new();
    private void WarnOnce(string key, string msg)
    {
        if (_warned.Add(key)) _warn($"[EntityInspector][Location] {msg}");
    }
}
