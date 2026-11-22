using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Wardrobe tab — "Copy outfit code" (approved 2026-11-22, option A; spec _scratch/prototypes/
// entityinspector-outfit-code-APPROVED.md, research KB Fashion-Share-Code.md). The pill sits right of the WEARING summary
// line, fixed 150 px in every state. A click turns the outfit the tab shows (same snapshot RebuildWardrobe used — nearby
// broadcast or the profile fallback) into the game's own share text "<label>Fashion/<base64>", which the game's Wardrobe
// share box pastes (its parser only matches "Fashion/…", the label is ignored).
//
// Bytes: the GAME's encoder builds them — we only splice numbers into a Lua chunk that lays out valueList/valueTypeList in
// encodeShareCode's exact order (fashion_share_helper.lua:129-150) and calls Z.ShareCodeUtils.EncodeContnent, so the
// pack → GZip → base64 layout never needs re-implementing. Header gender = OUR gender, read in Lua exactly like the
// game's getPlayerGender (the target's isn't exposed; the paste side decodes it but never checks it).
//
// Colours: FashionEntry.Dyes is RGB the framework derived from the wire HSV (h 0-360, s/v 0-100 ints; SDK 2.32.0 does not
// expose the raw triple), so each dye is turned back into HSV here and rounded like convertHSVToCodeInt. The RGB is
// unquantized floats, so the round trip lands on the original ints; only a grey (s = 0) loses its hue, which then has no
// visible effect. Weapon skins are skipped like the game's own encoder (:174) → the amber "N of M" footer.
//
// Perf: click-only (no per-frame or per-poll cost); one Lua chunk + one string readback per click.
public sealed partial class Plugin
{
    private const float CodePillW = 150f;                    // spec: same width in every state / locale
    private const int WeaponSkinRegion = 731;                // E.FashionRegion.WeapoonSkin (enum_define.lua:220)
    private const int MaxCodeItems = 255;                    // encodeShareCode rejects > 255 wears or colours
    private const string LuaCodeOut = "__stellar_ei_code";

    private long _codeCopiedAtMs = long.MinValue / 2;
    private bool _codeWarned;
    private readonly StringBuilder _codeLua = new(1024);     // reused: built only on click
    private readonly List<int> _codeWears = new(32);         // region, fashionId pairs
    private readonly List<int> _codeColors = new(128);       // fashionId, area, h, s, v quintuples

    private bool CodeFlashing() => Environment.TickCount64 - _codeCopiedAtMs < ChipFlashMs;

    // Primary pill → success wash + green "✓ Copied" for ~1 s; recessed (not clickable) while the tab has no outfit rows.
    private HudElement CopyOutfitCodePill()
    {
        var solid = new SolidLabelCache();
        var green = new ContrastCache();
        ColorRgba Fill() => CodeFlashing() ? Over(SuccessColor() with { A = ChipWashAlpha }, PanelColor()) : PrimaryFill();
        return PillButton(
            () => CodeFlashing() ? _loc.T("ei.wardrobe.codeCopied") : _loc.T("ei.wardrobe.codeButton"),
            Fill,
            () => CodeFlashing() ? green.Get(SuccessColor(), Fill(), PillMinContrast) : solid.Get(PrimaryFill()),
            CopyOutfitCode, CodePillW - 2f * PillPadX, PanelColor, () => _wardrobeRows.Count > 0);
    }

    private void CopyOutfitCode()
    {
        var snapshot = _lastFashionSnapshot;
        if (snapshot is null || snapshot.Count == 0) return;

        _codeWears.Clear(); _codeColors.Clear();
        int total = 0;
        foreach (var f in snapshot)
        {
            if (f.FashionId <= 0) continue;
            total++;
            if (f.Slot == WeaponSkinRegion || _codeWears.Count / 2 >= MaxCodeItems) continue;
            _codeWears.Add(f.Slot); _codeWears.Add(f.FashionId);
            // Only areas the player changed are on the wire ({-1,-1,-1} resets already dropped by the reader); an entry
            // without area keys (old reader shape) can't be placed on an area, so its colours are left out.
            for (int d = 0; d < f.Dyes.Length && d < f.DyeAreas.Length; d++)
            {
                int area = f.DyeAreas[d];
                if (area is < 1 or > 16 || _codeColors.Count / 5 >= MaxCodeItems) continue;
                RgbToCodeHsv(f.Dyes[d], out int h, out int s, out int v);
                _codeColors.Add(f.FashionId); _codeColors.Add(area); _codeColors.Add(h); _codeColors.Add(s); _codeColors.Add(v);
            }
        }
        int wears = _codeWears.Count / 2;

        string? code = wears > 0 ? EncodeOutfit() : null;
        if (code is { Length: > 0 })
        {
            try
            {
                UnityEngine.GUIUtility.systemCopyBuffer = _loc.TFormat("ei.wardrobe.codeLabel", NameLine()) + "Fashion/" + code;
            }
            catch (Exception ex)
            {
                if (!_copyWarned) { _copyWarned = true; _services.Log.Warning($"[EntityInspector] clipboard copy failed: {ex.Message}"); }
                code = null;
            }
        }

        _copiedKey = -1;                              // no dye chip flashes for this copy
        _copiedAtMs = Environment.TickCount64;        // shows the footer
        if (code is not { Length: > 0 })
        {
            _codeCopiedAtMs = long.MinValue / 2;      // the pill stays normal on failure
            _copyFooterTone = FooterTone.Danger;
            _copyFooter = _loc.T("ei.wardrobe.codeFailed");
            return;
        }
        _codeCopiedAtMs = _copiedAtMs;
        if (wears < total)
        {
            _copyFooterTone = FooterTone.Caution;
            _copyFooter = _loc.TFormat("ei.wardrobe.codePartial", wears, total);
        }
        else
        {
            _copyFooterTone = FooterTone.Success;
            _copyFooter = _loc.T("ei.wardrobe.codeDone");
        }
    }

    // Runs the game's encoder over _codeWears/_codeColors; the base64 payload, or null on any failure (one-time warning).
    // Only ints are spliced into the chunk — no player text ever reaches Lua.
    private string? EncodeOutfit()
    {
        var lua = _services.Lua;
        if (!lua.Ready) { WarnCodeOnce("Lua not ready"); return null; }
        int wears = _codeWears.Count / 2, colors = _codeColors.Count / 5;
        var sb = _codeLua.Clear();
        sb.Append("rawset(_G,'").Append(LuaCodeOut).Append("',nil) pcall(function()\n");
        sb.Append("local cs = Z.ContainerMgr and Z.ContainerMgr.CharSerialize\n");
        sb.Append("local g = cs and cs.charBase and tonumber(cs.charBase.gender) or 0\n");
        // header: type=2 EFashionShareCode (Byte), version=2 (Byte), gender (Byte), region=0 whole outfit (Int32),
        // wear / colour / setting counts (Byte). ValueType: Int32=1, Byte=3.
        sb.Append("local v = {2,2,g,0,").Append(I(wears)).Append(',').Append(I(colors)).Append(",0");
        for (int i = 0; i < _codeWears.Count; i++) sb.Append(',').Append(I(_codeWears[i]));
        for (int i = 0; i < _codeColors.Count; i++) sb.Append(',').Append(I(_codeColors[i]));
        sb.Append("}\nlocal t = {3,3,3,1,3,3,3");
        for (int i = 0; i < wears; i++) sb.Append(",1,1");                 // region Int32, wearFashionId Int32
        for (int i = 0; i < colors; i++) sb.Append(",1,3,1,3,3");          // fashionId Int32, area Byte, h Int32, s/v Byte
        sb.Append("}\nlocal c = Z.ShareCodeUtils.EncodeContnent(v, t)\n");
        sb.Append("if c and c ~= '' then rawset(_G,'").Append(LuaCodeOut).Append("',tostring(c)) end\nend)");
        string? code = null;
        try
        {
            lua.DoString(sb.ToString());
            code = lua.ReadGlobalString(LuaCodeOut);
            lua.DoString("rawset(_G,'" + LuaCodeOut + "',nil)");
        }
        catch (Exception ex) { WarnCodeOnce(ex.Message); return null; }
        if (code is not { Length: > 0 }) { WarnCodeOnce("encoder returned nothing"); return null; }
        return code;
    }

    private static string I(int n) => n.ToString(CultureInfo.InvariantCulture);

    private void WarnCodeOnce(string why)
    {
        if (_codeWarned) return;
        _codeWarned = true;
        _services.Log.Warning($"[EntityInspector] outfit code failed: {why}");
    }

    // RGB (0-1) → the share code's HSV ints: h 0-360, s/v 0-100, rounded like convertHSVToCodeInt (floor(x + 0.5)).
    private static void RgbToCodeHsv(ColorRgba c, out int h, out int s, out int v)
    {
        float r = Math.Clamp(c.R, 0f, 1f), g = Math.Clamp(c.G, 0f, 1f), b = Math.Clamp(c.B, 0f, 1f);
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        float hue = 0f;
        if (delta > 1e-6f)
        {
            if (max == r) hue = 60f * ((g - b) / delta);
            else if (max == g) hue = 60f * ((b - r) / delta + 2f);
            else hue = 60f * ((r - g) / delta + 4f);
            if (hue < 0f) hue += 360f;
        }
        h = (int)Math.Floor(hue + 0.5f);
        if (h >= 360) h -= 360;
        s = max > 1e-6f ? (int)Math.Floor(delta / max * 100f + 0.5f) : 0;
        v = (int)Math.Floor(max * 100f + 0.5f);
    }
}
