using System;
using System.Reflection;
using HarmonyLib;

namespace Stellar.EntityInspector;

// Removes the vertical-rotation clamp on the portrait model.
//
// PortraitCmdRenderer stores the orbit state in two float fields:
//   _azimuth  : horizontal angle (unclamped by default)
//   _elevation : vertical angle  (clamped to ~±30° inside Orbit)
//
// We postfix PortraitModelHost.Orbit, reach into _cmdRenderer, and overwrite _elevation
// with our own unclamped accumulator — so the renderer uses our value every frame.
// ApplyPitch() re-asserts it at 30 Hz (framework tick) as a safety net.
internal static class VerticalRotationPatch
{
    private static Harmony?        _harmony;
    private static Action<string>? _log;

    private static bool  _active;
    private static float _pitchAccum;
    private const  float PitchScale = 0.3f;

    private static bool       _cmdResolved;
    private static object?    _cmdRendInst;
    private static FieldInfo? _elevationField;

    internal static bool Install(string harmonyId, Action<string> log)
    {
        _log     = log;
        _harmony = new Harmony(harmonyId + ".vertrot");

        var hostType = FindType("Stellar.Infrastructure.Game.PortraitModelHost");
        if (hostType == null) { log("[VertRot] PortraitModelHost not found"); return false; }

        var orbitMethod = hostType.GetMethod("Orbit",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(float), typeof(float) }, null);
        if (orbitMethod == null) { log("[VertRot] PortraitModelHost.Orbit not found"); return false; }

        try
        {
            _harmony.Patch(orbitMethod,
                postfix: new HarmonyMethod(typeof(VerticalRotationPatch), nameof(PostfixOrbit)));
            log("[VertRot] PortraitModelHost.Orbit postfixed");
            return true;
        }
        catch (Exception ex) { log($"[VertRot] patch failed: {ex.Message}"); return false; }
    }

    internal static void Uninstall() { _harmony?.UnpatchSelf(); _harmony = null; }

    internal static void Activate(bool on)
    {
        _active = on;
        if (!on) { _cmdRendInst = null; _cmdResolved = false; }
    }

    internal static void ResetPitch() => _pitchAccum = 0f;

    // Re-assert elevation at 30 Hz so any per-frame resets can't stick.
    internal static void ApplyPitch()
    {
        if (!_active || _cmdRendInst == null || _elevationField == null) return;
        try { _elevationField.SetValue(_cmdRendInst, Math.Clamp(_pitchAccum, -89f, 89f)); }
        catch { _cmdRendInst = null; }
    }

    // __instance = PortraitModelHost
    private static void PostfixOrbit(object __instance, float dx, float dy)
    {
        if (!_active) return;

        // Clamp to ±89° to prevent the gimbal flip at the poles (camera crosses to the
        // model's back side past ±90°, which looks like a jarring flip to the user).
        // Full 360° yaw is already available via horizontal drag (_azimuth, unclamped).
        _pitchAccum = Math.Clamp(_pitchAccum + dy * PitchScale, -89f, 89f);

        if (!_cmdResolved) ResolveCmd(__instance);
        if (_cmdRendInst == null || _elevationField == null) return;

        try { _elevationField.SetValue(_cmdRendInst, _pitchAccum); }
        catch { _cmdRendInst = null; }
    }

    private static void ResolveCmd(object instance)
    {
        _cmdResolved = true;
        var cmdField = FindFieldUp(instance.GetType(), "_cmdRenderer");
        if (cmdField == null) { _log?.Invoke("[VertRot] _cmdRenderer not found on PortraitModelHost"); return; }

        _cmdRendInst = cmdField.GetValue(instance);
        if (_cmdRendInst == null) { _cmdResolved = false; return; } // not yet created — retry

        _elevationField = _cmdRendInst.GetType().GetField("_elevation",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (_elevationField != null)
            _log?.Invoke($"[VertRot] resolved _cmdRenderer._elevation on {_cmdRendInst.GetType().Name}");
        else
            _log?.Invoke($"[VertRot] _elevation not found on {_cmdRendInst.GetType().Name}");
    }

    private static FieldInfo? FindFieldUp(Type? t, string name)
    {
        while (t != null)
        {
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f;
            t = t.BaseType;
        }
        return null;
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t != null) return t;
        }
        return null;
    }
}
