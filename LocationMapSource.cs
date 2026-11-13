using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.EntityInspector;

/// <summary>
/// The game's OWN region map art + projection for the Location tab. Finds the live radar widget
/// (<c>Panda.ZUi.MiniMap</c>, GameObject <c>node_map_comp</c>), reads its background <c>mapBgImg_</c> (ZRawImage)
/// texture, and copies it to a managed RGBA32 buffer ONCE per texture/region change via a GPU readback
/// (Blit → ReadPixels — the map art is not CPU-readable and possibly compressed). Projection is the game's own
/// <c>MiniMapManager.GetImgPos(world, sizeDelta)</c> (validated: bottom-left-origin region pixels, world X→u,
/// Z→v, Y ignored) called with sizeDelta = the readback size, so it returns base-buffer pixels directly.
/// See Knowledge Base\Minimap-World-Projection.md (radar acquisition recipe + Il2CppInterop gotchas). Direct
/// reflection only — no Harmony. Unity main thread only.
/// </summary>
internal sealed class LocationMapSource : ILocationBase, IDisposable
{
    // Readback cap: the radar art is 2048² in town (16 MB as RGBA32). Larger art is Blit-downscaled to this, and
    // GetImgPos is called with the readback size so projection stays consistent.
    internal const int MaxReadback = 2048;
    // A failed widget scan (no radar in this scene / UI not built yet) retries only every N calls — the scan is a
    // full Resources.FindObjectsOfTypeAll, far too heavy for the 10 Hz snapshot tick (~2 s at 10 Hz).
    private const int RescanEvery = 20;

    private readonly Action<string> _info, _warn;
    public LocationMapSource(Action<string> info, Action<string> warn) { _info = info; _warn = warn; }

    private bool _resolved, _resolveFailed;
    private Type? _widgetType, _widgetBaseType, _mgrType;
    private PropertyInfo? _piMapBgSize, _piFlagParent, _piMapBgImg, _piCurLayerId;
    private MethodInfo? _miGetImgPos, _miGetAddr, _miRegionSize, _miRegionCenter, _miRdAddr, _miSceneAddr;
    private UnityEngine.Object? _widget;   // Unity-overloaded null: goes "null" when the scene rebuilds the radar
    private object? _mgr;
    private int _scanCooldown;

    // ---- cached base layer (the map art) ----
    public byte[]? Base { get; private set; }
    public int BaseW { get; private set; }
    public int BaseH { get; private set; }
    public float PxPerWorld { get; private set; } = 1f;   // base-buffer px per world unit (diag + crop sizing)
    public int Version { get; private set; }               // bumps on every (re)build so the painter redraws
    private string? _baseKey, _failedKey;

    /// <summary>Ensure <see cref="Base"/> holds the radar's current map art. Rebuilds (one readback) only when the
    /// radar's texture instance / size / layer changed; a key whose readback failed is not retried until it changes.
    /// <paramref name="self"/>/<paramref name="target"/>/<paramref name="sceneId"/> feed the one-line calibration log on a rebuild.</summary>
    public bool TryEnsureBase(Vector3 self, Vector3 target, int sceneId)
    {
        if (!Resolve()) return false;
        var tex = CurrentTexture(out var img);
        if (tex == null) return false;
        int layer = ReadLayer();
        string key = $"{tex.GetInstanceID()}|{tex.width}x{tex.height}|L{layer}";
        if (key == _baseKey && Base != null) return true;
        if (key == _failedKey) return false;

        DropBase();
        if (!Readback(tex, MaxReadback, m => WarnOnce("readback", m), out var buf, out int w, out int h)) { _failedKey = key; return false; }
        Base = buf; BaseW = w; BaseH = h; _baseKey = key; _failedKey = null; Version++;
        PxPerWorld = MeasurePxPerWorld(self);
        LogCalibration(tex, img, layer, sceneId, self, target);
        return true;
    }

    /// <summary>World → base-buffer pixel (bottom-left origin) via the game's GetImgPos.</summary>
    public bool TryImgPos(Vector3 world, out Vector2 px)
    {
        px = default;
        var mgr = Manager;
        if (mgr == null || _miGetImgPos == null || Base == null) return false;
        try
        {
            if (_miGetImgPos.Invoke(mgr, new object[] { world, new Vector2(BaseW, BaseH) }) is not Vector2 p) return false;
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.x) || float.IsInfinity(p.y)) return false;
            px = p; return true;
        }
        catch (Exception ex) { WarnOnce("imgpos", $"GetImgPos failed: {ex.Message}"); return false; }
    }

    /// <summary>Free the cached map art (window close / dispose). The next <see cref="TryEnsureBase"/> re-reads.</summary>
    public void DropBase() { Base = null; BaseW = BaseH = 0; _baseKey = null; }

    public void Dispose() { DropBase(); _widget = null; _mgr = null; }

    // ---- resolve ----

    private bool Resolve()
    {
        if (_resolved) return !_resolveFailed;
        try
        {
            _widgetType     = StellarInterop.FindType("Panda.ZUi.MiniMap");
            _widgetBaseType = StellarInterop.FindType("Panda.ZUi.MiniMapBase");
            _mgrType        = StellarInterop.FindType("Panda.ZGame.MiniMapManager");
            if (_widgetType == null || _widgetBaseType == null || _mgrType == null) return false;   // not loaded yet — retry

            // FindPropertyUp walks the base chain: mapBgImg_/mapBgSize_/flagParent_ are declared on MiniMapBase.
            _piMapBgImg   = StellarInterop.FindPropertyUp(_widgetType, "mapBgImg_");
            _piMapBgSize  = StellarInterop.FindPropertyUp(_widgetType, "mapBgSize_");
            _piFlagParent = StellarInterop.FindPropertyUp(_widgetType, "flagParent_");
            _piCurLayerId = StellarInterop.FindPropertyUp(_mgrType, "CurLayerId");
            // Name+arity hand-scans (GetImgPos has an overload-free (Vector3,Vector2) shape on the manager; the region
            // getters are 0-arg) — see Framework-Interop-Foundation.md on FindMethod's count-only matching.
            _miGetImgPos    = FindMethod(_mgrType, "GetImgPos", 2);
            _miGetAddr      = FindMethod(_mgrType, "GetCurRegionMapAddress", 0);
            _miRegionSize   = FindMethod(_mgrType, "GetCurRegionSize", 0);
            _miRegionCenter = FindMethod(_mgrType, "GetCurRegionCenter", 0);
            _miRdAddr       = FindMethod(_mgrType, "GetSceneMapRegionDataAddress", 1);   // diag only (shared log format)
            _miSceneAddr    = FindMethod(_mgrType, "GetSceneMapAddress", 2);

            _resolved = true;
            _resolveFailed = _piMapBgImg == null || _miGetImgPos == null;   // the two essentials
            if (_resolveFailed)
                WarnOnce("resolve", $"resolve incomplete mapBgImg={_piMapBgImg != null} getImgPos={_miGetImgPos != null}");
            return !_resolveFailed;
        }
        catch (Exception ex)
        {
            WarnOnce("resolve", $"resolve error: {ex.Message}");
            _resolved = true; _resolveFailed = true; return false;
        }
    }

    private object? Manager
    {
        get
        {
            if (_mgr != null) return _mgr;
            try { _mgr = StellarInterop.GetSingleton(_mgrType!); } catch { _mgr = null; }
            return _mgr;
        }
    }

    // The radar's map-art texture, or null (no radar here / not yet bound / no art loaded).
    private Texture? CurrentTexture(out RawImage? img)
    {
        img = null;
        var w = EnsureWidget();
        if (w == null) return null;
        try
        {
            img = _piMapBgImg!.GetValue(w) as RawImage;   // ZRawImage : RawImage — the interop wrapper is typed
            var tex = img != null ? img.texture : null;
            return tex != null && tex.width > 1 && tex.height > 1 ? tex : null;
        }
        catch (Exception ex) { WarnOnce("tex", $"mapBgImg_ read failed: {ex.Message}"); return null; }
    }

    private object? EnsureWidget()
    {
        if (_widget != null) return _widget;
        if (_scanCooldown > 0) { _scanCooldown--; return null; }
        var w = FindWidget();
        if (w == null) { _scanCooldown = RescanEvery; return null; }
        _widget = (UnityEngine.Object)w;
        return w;
    }

    // Scans the BASE type (a scan of the concrete MiniMap returns 0 in-game) and TryCasts each hit to MiniMap —
    // FindObjectsOfTypeAll hands back base UnityEngine.Object wrappers. Prefer active + initialised (mapBgSize_ > 1,
    // flagParent_ set): two MiniMap instances exist, the live radar and an inactive template.
    private object? FindWidget()
    {
        try
        {
            var arr = Resources.FindObjectsOfTypeAll(Il2CppType.From(_widgetBaseType!));
            if (arr == null) return null;
            foreach (var o in arr)
            {
                if (o == null) continue;
                var w = TryCast(o, _widgetType!);
                if (w == null) continue;
                bool inited = false, active = false;
                try { inited = _piMapBgSize?.GetValue(w) is Vector2 v && v.x > 1f && v.y > 1f
                               && _piFlagParent?.GetValue(w) != null; } catch { }
                try { active = ((Component)w).gameObject.activeInHierarchy; } catch { }
                if (active && inited) return w;
            }
            return null;   // no live radar — the template alone has no map art worth reading
        }
        catch (Exception ex) { WarnOnce("scan", $"radar scan error: {ex.Message}"); return null; }
    }

    private static MethodInfo? _tryCast;
    internal static object? TryCast(object o, Type t)
    {
        try
        {
            if (_tryCast == null)
            {
                var b = o.GetType();
                while (b != null && b.Name != "Il2CppObjectBase") b = b.BaseType;
                _tryCast = b?.GetMethod("TryCast", BindingFlags.Public | BindingFlags.Instance);
            }
            return _tryCast?.MakeGenericMethod(t).Invoke(o, null);
        }
        catch { return null; }
    }

    internal static MethodInfo? FindMethod(Type t, string name, int argc)
    {
        for (Type? cur = t; cur != null; cur = cur.BaseType)
            foreach (var m in cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (m.Name == name && m.GetParameters().Length == argc) return m;
        return null;
    }

    // ---- readback (once per base build — never per frame) ----

    // Blit the (non-readable, possibly compressed) art into a temp sRGB RT, ReadPixels into a throwaway RGBA32
    // Texture2D, copy its pixels out, and release both immediately. sRGB read/write so a linear-space project
    // re-encodes on write and the bytes round-trip unchanged (no-op in gamma space) — confirm colours in-game.
    // ReadPixels row 0 = bottom, same as GetImgPos's bottom-left origin, so no flip.
    internal static bool Readback(Texture src, int max, Action<string> warn, out byte[] buf, out int w, out int h)
    {
        buf = Array.Empty<byte>();
        w = Math.Min(src.width, max); h = Math.Min(src.height, max);
        RenderTexture? rt = null; Texture2D? rb = null;
        var prev = RenderTexture.active;
        try
        {
            rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            rb = new Texture2D(w, h, TextureFormat.RGBA32, false);
            rb.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);   // CPU copy only — no Apply() (no GPU re-upload needed)
            var px = rb.GetPixels32();
            var bytes = MemoryMarshal.AsBytes(px.AsSpan());       // Color32 = 4 blittable bytes, RGBA order
            if (bytes.Length != w * h * 4) { warn($"readback size mismatch {bytes.Length} != {w * h * 4}"); return false; }
            buf = bytes.ToArray();
            return true;
        }
        catch (Exception ex) { warn($"map readback failed: {ex.Message}"); return false; }
        finally
        {
            RenderTexture.active = prev;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (rb != null) UnityEngine.Object.Destroy(rb);
        }
    }

    // Base px per world unit, by differencing GetImgPos over a 10 m X step at the player (robust to whatever
    // region scale the game uses). Falls back to 1 when unresolvable.
    private float MeasurePxPerWorld(Vector3 at)
    {
        if (TryImgPos(at, out var a) && TryImgPos(at + new Vector3(10f, 0f, 0f), out var b))
        {
            float d = Math.Abs(b.x - a.x) / 10f;
            if (d > 1e-4f && !float.IsInfinity(d)) return d;
        }
        return 1f;
    }

    // ---- diagnostics ----

    private int ReadLayer()
    {
        try { return _piCurLayerId?.GetValue(Manager) is int l ? l : -1; } catch { return -1; }
    }

    // ONE info line per base (re)build — this is how the projection/readback gets calibrated in-game. Same leading
    // fields as SceneMapLoader's line (scene/cur/rdAddr/sceneAddr/…/via) so radar and loaded builds compare directly;
    // region= here is the radar's current region address (the radar has no RegionName accessor).
    private void LogCalibration(Texture tex, RawImage? img, int layer, int sceneId, Vector3 self, Vector3 target)
    {
        try
        {
            string typeName = "?";
            try { typeName = tex.GetIl2CppType().FullName; } catch { }
            string uv = "?";
            try { if (img != null) uv = img.uvRect.ToString(); } catch { }
            var size = Invoke0(_miRegionSize);
            var center = Invoke0(_miRegionCenter);
            string bl = size is Vector2 s && center is Vector3 c
                ? $"({c.x - s.x / 2f:F1},{c.z - s.y / 2f:F1})" : "?";   // BL = centre − size/2 on x/z (KB live check)
            string selfPx = TryImgPos(self, out var sp) ? sp.ToString() : "?";
            string tgtPx = TryImgPos(target, out var tp) ? tp.ToString() : "?";
            string rdAddr = "?", sceneAddr = "?";
            try { if (_miRdAddr != null && Manager is { } m1) rdAddr = AddressString(_miRdAddr.Invoke(m1, new object[] { sceneId })); } catch { }
            try { if (_miSceneAddr != null && Manager is { } m2) sceneAddr = AddressString(_miSceneAddr.Invoke(m2, new object[] { sceneId, layer })); } catch { }
            _info($"[EntityInspector][MapTex] scene={sceneId} cur=True rdAddr='{rdAddr}' sceneAddr='{sceneAddr}' " +
                  $"region='{ReadAddress()}' BL={bl} size={size ?? "?"} off=? tex={tex.name} {tex.width}x{tex.height} " +
                  $"read={BaseW}x{BaseH} layer={layer} type={typeName} readable={tex.isReadable} uvRect={uv} " +
                  $"center={center ?? "?"} pxPerWorld={PxPerWorld:F3} selfPx={selfPx} targetPx={tgtPx} via=radar");
        }
        catch (Exception ex) { WarnOnce("diag", $"calibration log failed: {ex.Message}"); }
    }

    private object? Invoke0(MethodInfo? m)
    {
        try { return m != null && Manager is { } mgr ? m.Invoke(mgr, null) : null; } catch { return null; }
    }

    // ZResAddress (struct: _nativeString + _addressString) → its path. Il2CppInterop wraps it as a class, whose
    // ToString may be just the type name, so prefer the game's own explicit string conversion, then the field.
    private string ReadAddress() => AddressString(Invoke0(_miGetAddr));

    // Shared with SceneMapLoader (log-confirmed on the radar path: 'ui/textures/scenemaps/cty001/cty001_cty001').
    internal static string AddressString(object? addr)
    {
        try
        {
            if (addr == null) return "?";
            var t = addr.GetType();
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "op_Explicit" && m.ReturnType == typeof(string) && m.GetParameters().Length == 1)
                    if (m.Invoke(null, new[] { addr }) is string s1) return s1;
            if (StellarInterop.FindPropertyUp(t, "_addressString")?.GetValue(addr) is string s2) return s2;
            return addr.ToString() ?? "?";
        }
        catch { return "?"; }
    }

    private readonly HashSet<string> _warned = new();
    private void WarnOnce(string key, string msg)
    {
        if (_warned.Add(key)) _warn($"[EntityInspector][Location] {msg}");
    }
}
