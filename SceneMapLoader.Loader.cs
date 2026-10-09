using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// ZResLoader / UniTask reflection plumbing for SceneMapLoader (split out for the 500-LoC cap). See SceneMapLoader.cs
// for the polling model and Knowledge Base\Minimap-World-Projection.md §"Another scene's map (cross-scene)".
internal sealed partial class SceneMapLoader
{

    private bool _resolved, _resolveFailed;
    private Type? _regionType;
    private object? _mgr, _loader;
    private MethodInfo? _miRdAddr, _miSceneAddr, _miLoadByAddr, _miLoadByString, _miRelease;
    private uint _token;

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
            // LoadAssetAsync has three 4-arg generic overloads (string / ZResAddress / uint) — match by first-param TYPE.
            foreach (var m in loaderType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (m.Name == "LoadAssetAsync" && ps.Length == 4)
                {
                    if (ps[0].ParameterType == typeof(string)) _miLoadByString = m.MakeGenericMethod(typeof(UnityEngine.Object));
                    else if (ps[0].ParameterType.Name == "ZResAddress") _miLoadByAddr = m.MakeGenericMethod(typeof(UnityEngine.Object));
                }
                else if (m.Name == "ReleaseAsset" && ps.Length == 1) _miRelease = m.MakeGenericMethod(typeof(UnityEngine.Object));
            }
            // ZCancelSource.NeverCancelToken — what the game's own fire-and-forget loads pass; 0 if unresolvable.
            var cancel = StellarInterop.FindType("ZUtil.ZCancelSource");
            try { _token = cancel?.GetProperty("NeverCancelToken", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is uint t ? t : 0u; } catch { }

            _resolved = true;
            _resolveFailed = _miRdAddr == null || _miLoadByAddr == null || _miRelease == null;
            _info($"[EntityInspector][MapTex] loader resolve rdAddr={_miRdAddr != null} sceneAddr={_miSceneAddr != null} " +
                  $"loadAddr={_miLoadByAddr != null} loadStr={_miLoadByString != null} release={_miRelease != null} token={_token}");
            return !_resolveFailed;
        }
        catch (Exception ex)
        {
            WarnOnce("resolve", $"scene-map loader resolve error: {ex.Message}");
            _resolved = true; _resolveFailed = true; return false;
        }
    }

    private object? Call(MethodInfo? m, params object[] args)
    {
        try { return m != null && _mgr != null ? m.Invoke(_mgr, args) : null; }
        catch (Exception ex) { WarnOnce("call:" + m?.Name, $"{m?.Name} failed: {ex.InnerException?.Message ?? ex.Message}"); return null; }
    }

    // Kick off one load (ZResAddress object, or a plain string path → the string overload). allowFail: a missing
    // asset completes with null instead of throwing inside the game's loader.
    private bool Start(object addr, string? text)
    {
        try
        {
            object? task = addr is string s
                ? _miLoadByString?.Invoke(_loader, new object[] { s, _token, 0, true })
                : _miLoadByAddr!.Invoke(_loader, new object[] { addr, _token, 0, true });
            if (task == null) return false;
            _task = task; _ticks = 0;
            return true;
        }
        catch (Exception ex) { WarnOnce("load:" + text, $"LoadAssetAsync('{text ?? "?"}') failed: {ex.InnerException?.Message ?? ex.Message}"); return false; }
    }

    // UniTaskStatus: 0 Pending, 1 Succeeded, 2 Faulted, 3 Canceled (dump.cs L1297891). Unreadable → treat as faulted.
    private static int Status(object task)
    {
        try { return Convert.ToInt32(task.GetType().GetProperty("Status")?.GetValue(task)); }
        catch { return 2; }
    }

    // GetResult exactly once per task: it hands the pooled source back. A faulted/cancelled task throws from it —
    // still call it (observes the fault, returns the source) and yield null.
    private object? Result(object task, int status)
    {
        try
        {
            var awaiter = task.GetType().GetMethod("GetAwaiter", Type.EmptyTypes)?.Invoke(task, null);
            var r = awaiter?.GetType().GetMethod("GetResult", Type.EmptyTypes)?.Invoke(awaiter, null);
            return status == 1 ? r : null;
        }
        catch (Exception ex)
        {
            if (status == 1) WarnOnce("result", $"GetResult failed: {ex.InnerException?.Message ?? ex.Message}");
            return null;
        }
    }

    private void Release(object? asset)
    {
        if (asset == null || _miRelease == null) return;
        try { _miRelease.Invoke(_loader, new[] { asset }); }
        catch (Exception ex) { WarnOnce("release", $"ReleaseAsset failed: {ex.InnerException?.Message ?? ex.Message}"); }
    }

    // A load still in flight when the scene changes / window closes: park it, release whatever it yields later.
    private void Abandon()
    {
        if (_task != null) { _orphans.Add(_task); _task = null; }
        _artCandidates.Clear();
    }

    private void PollOrphans()
    {
        for (int i = _orphans.Count - 1; i >= 0; i--)
        {
            int st = Status(_orphans[i]);
            if (st == 0) continue;
            Release(Result(_orphans[i], st));
            _orphans.RemoveAt(i);
        }
    }

    private readonly HashSet<string> _warned = new();
    private void WarnOnce(string key, string msg)
    {
        if (_warned.Add(key)) _warn($"[EntityInspector][Location] {msg}");
    }
}
