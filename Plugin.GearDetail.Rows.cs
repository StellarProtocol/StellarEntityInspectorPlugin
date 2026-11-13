using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Gear item popover — the attribute rows (basic lib values, advanced roll ranges / actual rolls, mod-effect templates) and
// the pooled row element both popover panels draw with. Split out of Plugin.GearDetail.cs (file-size gate).
public sealed partial class Plugin
{
    // One pooled row list: label (muted) … value (right) | a full-width muted line | a bold
    // muted sub-heading (Recast / Rare). Each kind is its own Conditional so a row never re-measures another kind's parts.
    private HudElement DetailList(DetailRows rows)
    {
        var slots = new HudElement[rows.Capacity];
        for (int i = 0; i < rows.Capacity; i++)
        {
            int idx = i;
            DetailKind Kind() => idx < rows.Count ? rows.Kinds[idx] : DetailKind.Line;
            string Label() => idx < rows.Count ? rows.Labels[idx] : "";
            slots[i] = new ColumnElement(new HudElement[]
            {
                new ConditionalElement(() => Kind() == DetailKind.Sub,
                    new TextElement(Label, Color: () => MutedColor(), NoWrap: true, FontSize: 11) { Bold = true, Height = 20f }),
                new ConditionalElement(() => Kind() == DetailKind.Line,
                    new TextElement(Label, Color: () => MutedColor())),
                new ConditionalElement(() => Kind() == DetailKind.Value,
                    FixedLine(22f, new RowElement(new HudElement[]
                    {
                        new CellElement(new TextElement(Label, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true },
                            Weight: 1f),
                        new SpacerElement(6f, 0f),
                        new TextElement(() => idx < rows.Count ? rows.Values[idx] : "", Color: () => TextColor(),
                            Align: TextAlign.Right, NoWrap: true),
                    }, Gap: 0.01f))),
            }, Gap: 0.01f);
        }
        return new ListElement(() => rows.Count, slots) { Gap = 0.01f };
    }

    // Basic = fixed lib values for anyone; self shows the instance's actual basic rolls when present. For others the lib
    // ids are one per BREAKTHROUGH tier — show tier 0 only (the ZDPS default; every tier repeated the same attrs 3×).
    private void AddBasicSection(EquipRowInfo row, GearInstance? inst)
    {
        if (inst is { Attrs.Basic.Count: > 0 } pi) { AddRollRows(pi.Attrs.Basic, marker: false); return; }
        if (row.BasicAttrLibIds.Length == 0) return;
        foreach (var e in BasicLibEntries(row))
            AddAttrLine(e.AttrId, e.Min, "");
    }

    // v1 libs go through GetAttrLib (part-filtered); v2 spec/school libs through GetSchoolAttrLib (part + the target's
    // talent school, resolved from their spec — _targetTalentSchool). Unknown spec (school 0) → empty, never wrong values.
    private IReadOnlyList<EquipAttrRange> BasicLibEntries(EquipRowInfo row)
        => row.BasicLibVersion == 2
            ? _services.GameData.Equip.GetSchoolAttrLib(row.BasicAttrLibIds[0], row.EquipPart, _targetTalentSchool)
            : _services.GameData.Equip.GetAttrLib(row.BasicAttrLibIds[0], row.EquipPart);

    private IReadOnlyList<EquipAttrRange> AdvancedLibEntries(int libId, EquipRowInfo row)
        => row.AdvancedLibVersion == 2
            ? _services.GameData.Equip.GetSchoolAttrLib(libId, row.EquipPart, _targetTalentSchool)
            : _services.GameData.Equip.GetAttrLib(libId, row.EquipPart);

    // Advanced = roll ranges for others; actual rolls (▲ at ≥90% of the lib max) for self.
    private void AddAdvancedSection(EquipRowInfo row, GearInstance? inst)
    {
        if (inst is { Attrs.Advanced.Count: > 0 } pi)
        {
            AddRollRows(pi.Attrs.Advanced, marker: true);
            if (pi.Attrs.Recast.Count > 0) { AddGdSub(_loc.T("ei.gd.recast")); AddRollRows(pi.Attrs.Recast, marker: false); }
            if (pi.Attrs.Rare.Count > 0) { AddGdSub(_loc.T("ei.gd.rare")); AddRollRows(pi.Attrs.Rare, marker: false); }
            return;
        }
        if (row.AdvancedAttrLibIds.Length == 0) return;
        // v2 (spec/school) rolls need the target's talent school; if we couldn't resolve their spec, say so honestly.
        if (row.AdvancedLibVersion == 2 && _targetTalentSchool == 0) { AddGdLine(_loc.T("ei.gd.specUnknown")); return; }
        // One line per lib slot with THAT lib's filtered range (ZDPS-parity).
        foreach (var libId in row.AdvancedAttrLibIds)
            foreach (var e in AdvancedLibEntries(libId, row))
                AddAttrRange(e);
        // Recast (reforge) is per-instance with no table pool, so others have no "possible range" — no row, no footnote
        // (show only what we get, user decision 2026-11-13).
    }

    // Self rolls arrive as (lib ROW id, percentile 0–100): each row expands to one or more table entries; displayed stat
    // = floor(pct·(Max−Min)/100 + Min) — the game's own formula, verified against the live gear sheet. ▲ marks ≥90th pct.
    private void AddRollRows(IReadOnlyList<GearAttrRoll> rolls, bool marker)
    {
        foreach (var roll in rolls)
        {
            // School-sourced rolls (equip_attr_set) resolve against the v2 school row table; v1 rolls against the v1 table.
            var entries = roll.School
                ? _services.GameData.Equip.GetSchoolAttrLibRow(roll.LibRowId)
                : _services.GameData.Equip.GetAttrLibRow(roll.LibRowId);
            if (entries.Count == 0)
            {
                AddGd(_loc.TFormat("ei.gd.roll", roll.LibRowId), roll.Percentile.ToString(CultureInfo.InvariantCulture) + "%");
                continue;
            }
            // ▲ (not the game's 👍 emoji) — legacy uGUI dynamic fonts have no reliable emoji coverage (ux-ui review).
            var near = marker && roll.Percentile >= 90;
            foreach (var e in entries)
            {
                var value = roll.Percentile * (long)(e.Max - e.Min) / 100 + e.Min;
                AddAttrLine(e.AttrId, value, near ? " ▲" : "");
            }
        }
    }

    // Plain stats → "Name … value". Mod-effect attrs whose NAME is an AttrDescription template ("+{*Decision.…*}") are
    // filled with the magnitude + tag-stripped and emitted as one full-width line (raw templates showed in-world 2026-06-13).
    private void AddAttrLine(int attrId, long value, string suffix)
    {
        var label = AttrLabel(attrId);
        if (IsAttrTemplate(label)) { AddGdLine(FillAttrTemplate(label, value) + suffix); return; }
        AddGd(label, FormatAttr(AttrNumType(attrId), value) + suffix);
    }

    private void AddAttrRange(EquipAttrRange e)
    {
        var label = AttrLabel(e.AttrId);
        var nt = AttrNumType(e.AttrId);
        if (IsAttrTemplate(label)) { AddGdLine(FillAttrTemplate(label, e.Min)); return; }   // fixed magnitude (Min==Max)
        AddGd(label, FormatAttr(nt, e.Min) + " – " + FormatAttr(nt, e.Max));
    }

    private static bool IsAttrTemplate(string label) => label.Contains("{*") || label.Contains('<');

    // Fill the game's AttrDescription placeholders with the entry magnitude and strip rich-text tags. Formatters
    // (AttrDescription.json): unmarkpercent = value/100 %, unmarktime = value/1000 s, marknormal = signed int,
    // unmarknormal/other = plain int. One magnitude per entry, so every placeholder index uses it.
    private static readonly System.Text.RegularExpressions.Regex Placeholder =
        new(@"\{\*Decision\.(\w+)\(\d+\)\*\}", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RichTag =
        new(@"<[^>]*>", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string FillAttrTemplate(string template, long magnitude)
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

    // Lib entries reference ×10 sub-variant ids (11022 = Intellect+2); fall back to the base id (id − id % 10) when the
    // variant itself has no name/format — the catalog carries base ids.
    private string AttrLabel(int attrId)
    {
        if (_services.GameData.Combat.GetAttribute(attrId) is { Name.Length: > 0 } a) return a.Name;
        if (_services.GameData.Combat.GetAttribute(attrId - attrId % 10) is { Name.Length: > 0 } b) return b.Name;
        return _loc.TFormat("ei.field.attrFallback", attrId);
    }

    private int AttrNumType(int attrId)
        => _services.GameData.Combat.GetAttribute(attrId)?.NumType
           ?? _services.GameData.Combat.GetAttribute(attrId - attrId % 10)?.NumType
           ?? -1;
}
