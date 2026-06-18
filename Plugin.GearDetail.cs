using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.EntityInspector;

// Gear detail popup — a separate cursor-positioned Borderless window (same mechanism as the CombatMeter
// row menu, approved mockup gear-detail-v2). One card's truth: identity line, GS/perfection, Basic
// Attributes (fixed lib values), Advanced Attributes (roll RANGES for others; ACTUAL rolls with a 👍
// near-max marker for self via the card's GearInstance uuid), Effect (self enchant; "not broadcast"
// for others), and the per-instance footnote. Rows are snapshotted on open — no per-poll table walks.
public sealed partial class Plugin
{
    private const int MaxDetailRows = 34;   // fully rolled piece: multi-entry basics + adv + recast + rare + effect
    private const float DetailW = 320f, DetailRowH = 19f;

    private IWindowControl _gearDetailWindow = null!;
    private readonly List<string> _gdLabels = new(MaxDetailRows);
    private readonly List<string> _gdValues = new(MaxDetailRows);
    private string _gdTitle = "";
    private ColorRgba _gdTitleCol = new(1f, 1f, 1f, 1f);

    // GlassMenu chrome to match the inspector (user request 2026-06-13) — a real glass title bar with a
    // ✕ and drag handle. The item NAME is dynamic per-card so it stays the first body row (the static
    // title bar can't carry it); the title bar reads a generic "Item Detail".
    private IWindowControl RegisterGearDetailWindow()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "entityinspector.geardetail",
                Title:       "Item Detail",
                DefaultRect: new WindowRect(120f, 120f, DetailW, 260f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, HideUntilInWorld = true, Closable = true, Draggable = true },
            BuildGearDetailRoot(),
            OnClose: () => _gearDetailWindow.SetVisible(false)));

    private HudElement BuildGearDetailRoot()
    {
        var rows = new HudElement[MaxDetailRows];
        for (var i = 0; i < MaxDetailRows; i++)
        {
            var idx = i;
            // Same header/normal section styling as the Overview tab (BuildSectionRow): header rows are
            // marked by an empty value (AddGd(title, "")).
            rows[i] = BuildSectionRow(
                () => idx < _gdLabels.Count ? _gdLabels[idx] : "",
                () => idx < _gdValues.Count ? _gdValues[idx] : "",
                () => IsGdHeaderRow(idx), labelWidth: 168f);
        }
        // No in-body Close button — the GlassMenu title-bar ✕ closes it (matches the inspector).
        return new ColumnElement(new HudElement[]
        {
            new TextElement(() => _gdTitle, () => _gdTitleCol, Emphasis: true),
            new ListElement(() => _gdLabels.Count, rows),
        }, Gap: 3f);
    }

    // Header rows carry an empty value (AddGd(title, "")); label/value + footnote rows carry a non-empty
    // value (a real value, or " " for footnotes), so they don't get the header treatment.
    private bool IsGdHeaderRow(int idx) => idx < _gdValues.Count && _gdValues[idx].Length == 0;

    private void OpenGearDetail(int cardIdx)
    {
        var card = _gearCards[cardIdx];
        if (!card.Occupied) return;
        BuildGearDetailRows(card);
        // Cursor-anchored (Input is bottom-left origin; window rects top-left — RowMenu precedent).
        // The window content-sizes itself; the height here is only an ESTIMATE used to clamp the
        // anchor so the popup (incl. its Close button) can't open off the right/bottom screen edge.
        var mp = Input.mousePosition;
        var estH = (_gdLabels.Count + 2) * DetailRowH + 48f;
        var x = Mathf.Min(mp.x, Screen.width - DetailW - 8f);
        var y = Mathf.Min(Screen.height - mp.y, Screen.height - estH - 8f);
        _gearDetailWindow.SetRect(new WindowRect(Mathf.Max(0f, x), Mathf.Max(0f, y), DetailW, estH));
        _gearDetailWindow.SetVisible(true);
    }

    private void BuildGearDetailRows(GearCard card)
    {
        _gdLabels.Clear(); _gdValues.Clear();
        _gdTitle = card.Name; _gdTitleCol = card.NameCol;
        var row  = _services.GameData.Equip.GetEquipRow(card.ItemId);
        var inst = SelfInstanceOf(card);

        AddGd(card.Slot, " ");   // slot header; GS/refine/req-level get their own labelled rows below
        if (row is { } r)
        {
            AddGd("GS", r.Gs.ToString("N0", CultureInfo.InvariantCulture));
            // Wear REQUIREMENT (table data) — labelled explicitly; rendered as a bare "Lv.45" on the
            // card it read as strengthen level (caps at 30), user-flagged in-world 2026-06-13.
            if (r.WearLevel > 1) AddGd("Req. level", r.WearLevel.ToString(CultureInfo.InvariantCulture));
            if (inst is { } pi && pi.Perfection.Max > 0)
                AddGd("Perfection", $"{pi.Perfection.Value:N0}/{pi.Perfection.Max:N0}");
            else if (r.PerfectCap > 0)
                AddGd("Max perfection", r.PerfectCap.ToString("N0", CultureInfo.InvariantCulture));
            if (inst is { RefineLevel: > 0 } ri) AddGd("Refine", ri.RefineLevel.ToString(CultureInfo.InvariantCulture));
            AddBasicSection(r, inst);
            AddAdvancedSection(r, inst);
        }
        AddEffectSection(inst);
        if (!IsSelf) AddGd("Their rolls / refine / gem are per-instance — not public.", " ");
    }

    private void AddGd(string label, string value)
    {
        if (_gdLabels.Count >= MaxDetailRows)
        {
            // Make truncation visible instead of silently dropping trailing sections.
            if (_gdLabels[^1] != "…") { _gdLabels[^1] = "…"; _gdValues[^1] = ""; }
            return;
        }
        _gdLabels.Add(label); _gdValues.Add(value);
    }

    // Basic = fixed lib values for anyone; self shows the instance's actual basic rolls when present.
    // For others the lib ids are one per BREAKTHROUGH tier — show tier 0 only (the ZDPS default);
    // dumping every tier repeated the same attrs 3× (user-flagged in-world 2026-06-13).
    private void AddBasicSection(Abstractions.Domain.GameData.EquipRowInfo row, GearInstance? inst)
    {
        AddGd("Basic Attributes", "");
        if (inst is { Attrs.Basic.Count: > 0 } pi) { AddRollRows(pi.Attrs.Basic, marker: false); return; }
        if (row.BasicAttrLibIds.Length == 0) return;
        foreach (var e in BasicLibEntries(row))
            AddGd(AttrLabel(e.AttrId), FormatAttr(AttrNumType(e.AttrId), e.Min));
    }

    // v1 libs go through GetAttrLib (part-filtered); v2 spec/school libs through GetSchoolAttrLib
    // (part + the target's talent school, resolved from their spec — _targetTalentSchool). When the
    // spec is unknown (talent school 0) the school lookup returns empty rather than wrong values.
    private IReadOnlyList<EquipAttrRange> BasicLibEntries(Abstractions.Domain.GameData.EquipRowInfo row)
        => row.BasicLibVersion == 2
            ? _services.GameData.Equip.GetSchoolAttrLib(row.BasicAttrLibIds[0], row.EquipPart, _targetTalentSchool)
            : _services.GameData.Equip.GetAttrLib(row.BasicAttrLibIds[0], row.EquipPart);

    private IReadOnlyList<EquipAttrRange> AdvancedLibEntries(int libId, Abstractions.Domain.GameData.EquipRowInfo row)
        => row.AdvancedLibVersion == 2
            ? _services.GameData.Equip.GetSchoolAttrLib(libId, row.EquipPart, _targetTalentSchool)
            : _services.GameData.Equip.GetAttrLib(libId, row.EquipPart);

    // Advanced = roll ranges for others; actual rolls (👍 at ≥90% of the lib max) for self.
    private void AddAdvancedSection(Abstractions.Domain.GameData.EquipRowInfo row, GearInstance? inst)
    {
        if (row.AdvancedAttrLibIds.Length == 0 && inst is not { Attrs.Advanced.Count: > 0 }) return;

        if (inst is { Attrs.Advanced.Count: > 0 } pi)
        {
            AddGd("Advanced Attributes — rolled", "");
            AddRollRows(pi.Attrs.Advanced, marker: true);
            if (pi.Attrs.Recast.Count > 0) { AddGd("Recast", ""); AddRollRows(pi.Attrs.Recast, marker: false); }
            if (pi.Attrs.Rare.Count > 0) { AddGd("Rare", ""); AddRollRows(pi.Attrs.Rare, marker: false); }
            return;
        }
        AddGd("Advanced Attributes — possible rolls", "");
        // v2 (spec/school) rolls need the target's talent school; if we couldn't resolve their spec,
        // say so honestly rather than show wrong ranges.
        if (row.AdvancedLibVersion == 2 && _targetTalentSchool == 0)
        { AddGd("Spec-dependent rolls (spec unknown — get nearer / open their card).", " "); return; }
        // One line per lib slot with THAT lib's filtered range (ZDPS-parity). The old widened cross-lib
        // union rendered every attr with one giant identical range (user-flagged 2026-06-13).
        foreach (var libId in row.AdvancedAttrLibIds)
            foreach (var e in AdvancedLibEntries(libId, row))
                AddAttrRange(e);
        // Recast (reforge) is a runtime player choice stored per-instance with no item-table pool, so —
        // unlike advanced rolls — it has no "possible range" to show for others. State that explicitly so
        // its absence reads as a data limit, not a missing section (user-flagged 2026-06-13).
        AddGd("Recast", "");
        AddGd("Reforge is per-instance — not broadcast.", " ");
    }

    // Self rolls arrive as (lib ROW id, percentile 0–100): each row expands to one or more table
    // entries; displayed stat = floor(pct·(Max−Min)/100 + Min) — the game's own formula, verified
    // against the live gear sheet (earrings: pct 100 → Intellect 268 / Luck 756 / Crit 378 ✓).
    // 👍 marks rolls at the ≥90th percentile.
    private void AddRollRows(IReadOnlyList<GearAttrRoll> rolls, bool marker)
    {
        foreach (var roll in rolls)
        {
            // School-sourced rolls (equip_attr_set) resolve against the v2 school row table; v1 rolls
            // against the v1 table — the row-id spaces collide, so the provenance flag picks the table.
            var entries = roll.School
                ? _services.GameData.Equip.GetSchoolAttrLibRow(roll.LibRowId)
                : _services.GameData.Equip.GetAttrLibRow(roll.LibRowId);
            if (entries.Count == 0)
            {
                // Table not loaded yet / unknown row — show the raw pair rather than nothing.
                AddGd($"Roll {roll.LibRowId}", roll.Percentile.ToString(CultureInfo.InvariantCulture) + "%");
                continue;
            }
            // ▲ (not the game's 👍 emoji) — legacy uGUI dynamic fonts have no reliable emoji
            // coverage; the marker must be a font-safe glyph (ux-ui review).
            var near = marker && roll.Percentile >= 90;
            foreach (var e in entries)
            {
                var value = roll.Percentile * (long)(e.Max - e.Min) / 100 + e.Min;
                AddAttrLine(e.AttrId, value, near ? " ▲" : "");
            }
        }
    }

    // Emit one advanced/basic attr line. Plain stats → "Name … value". Mod-effect attrs whose NAME is an
    // AttrDescription template ("+{*Decision.unmarkpercent(1)*}", "{*…*} PHY Boost while in Galeform")
    // are filled with the magnitude + tag-stripped and emitted as a single readable line (the magnitude is
    // inlined, so the value column is blank) — raw templates showed in-world 2026-06-13.
    private void AddAttrLine(int attrId, long value, string suffix)
    {
        var label = AttrLabel(attrId);
        if (IsAttrTemplate(label)) { AddGd(FillAttrTemplate(label, value, AttrNumType(attrId)) + suffix, " "); return; }
        AddGd(label, FormatAttr(AttrNumType(attrId), value) + suffix);
    }

    private void AddAttrRange(EquipAttrRange e)
    {
        var label = AttrLabel(e.AttrId);
        var nt = AttrNumType(e.AttrId);
        // Mod-effect templates carry a fixed magnitude (Min==Max) — fill + inline like the rolled case.
        if (IsAttrTemplate(label)) { AddGd(FillAttrTemplate(label, e.Min, nt), " "); return; }
        AddGd(label, FormatAttr(nt, e.Min) + " – " + FormatAttr(nt, e.Max));
    }

    private static bool IsAttrTemplate(string label) => label.Contains("{*") || label.Contains('<');

    // Fill the game's AttrDescription placeholders with the entry magnitude and strip rich-text tags.
    // Formatters (from AttrDescription.json): unmarkpercent = value/100 %, unmarktime = value/1000 s,
    // marknormal = signed int, unmarknormal/other = plain int. We only have ONE magnitude per entry, so
    // every placeholder index uses it (multi-magnitude mod effects are rare; refine if one shows wrong).
    private static readonly System.Text.RegularExpressions.Regex Placeholder =
        new(@"\{\*Decision\.(\w+)\(\d+\)\*\}", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RichTag =
        new(@"<[^>]*>", System.Text.RegularExpressions.RegexOptions.Compiled);

    private string FillAttrTemplate(string template, long magnitude, int numType)
    {
        var ci = CultureInfo.InvariantCulture;
        var filled = Placeholder.Replace(template, m => m.Groups[1].Value switch
        {
            "unmarkpercent" => (magnitude / 100.0).ToString("0.##", ci) + "%",
            "unmarktime"    => (magnitude / 1000.0).ToString("0.##", ci) + "s",
            "marknormal"    => (magnitude >= 0 ? "+" : "") + magnitude.ToString(ci),
            _               => magnitude.ToString("N0", ci),   // unmarknormal + any future formatter
        });
        filled = RichTag.Replace(filled.Replace("<br>", " "), "");
        return System.Text.RegularExpressions.Regex.Replace(filled, @"\s+", " ").Trim();
    }

    private void AddEffectSection(GearInstance? inst)
    {
        AddGd("Effect", "");
        if (!IsSelf) { AddGd("Socketed gem isn't broadcast for others.", " "); return; }
        if (inst?.Enchant is not { } en) { AddGd("No gem socketed.", " "); return; }
        // Resolve the wire (typeId, enchant_level) → gem item id whose NAME carries the display level
        // ("Crimson Foxen Sigil Lv.2") — the wire enchant_level is an internal index, not the shown level
        // (it read "Lv 8" for an in-game "Lv.2", user-flagged 2026-06-13). Fall back to the old display
        // if the enchant table hasn't loaded yet.
        if (_services.GameData.Equip.GetEnchantItem(en.ItemTypeId, en.Level) is { } gem)
        {
            var gemName = _services.GameData.Inventory.GetItem(gem.GemItemId)?.Name ?? $"Gem {gem.GemItemId}";
            AddGd(gemName, " ");
            foreach (var eff in gem.Effects)
                AddGd(AttrLabel(eff.AttrId), FormatAttr(AttrNumType(eff.AttrId), eff.Value));
            return;
        }
        var name = _services.GameData.Inventory.GetItem(en.ItemTypeId)?.Name ?? $"Enchant {en.ItemTypeId}";
        AddGd(name, $"Lv {en.Level}");
    }

    private GearInstance? SelfInstanceOf(GearCard card)
    {
        if (!IsSelf || card.Uuid == 0) return null;
        foreach (var g in _services.Inventory.GetSelfGear())
            if (g.ItemUuid == card.Uuid) return g;
        return null;
    }

    // Lib entries reference ×10 sub-variant ids (11022 = Intellect+2); fall back to the base id
    // (id - id % 10) when the variant itself has no name/format — the catalog carries base ids.
    private string AttrLabel(int attrId)
    {
        if (_services.GameData.Combat.GetAttribute(attrId) is { Name.Length: > 0 } a) return a.Name;
        if (_services.GameData.Combat.GetAttribute(attrId - attrId % 10) is { Name.Length: > 0 } b) return b.Name;
        return $"Attr{attrId}";
    }

    private int AttrNumType(int attrId)
        => _services.GameData.Combat.GetAttribute(attrId)?.NumType
           ?? _services.GameData.Combat.GetAttribute(attrId - attrId % 10)?.NumType
           ?? -1;
}
