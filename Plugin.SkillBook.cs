using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Skills tab (spec §4.3) — Battle-Imagine rows FIRST (distinct treatment: imagine icon, name, stars),
// then the skill loadout rows (skill icon, name, Lv·Tier) from AttrSkillLevelIdList, excluding the
// imagine skill ids already shown above. Snapshotted at 10 Hz into pooled lists; the per-frame icon
// Funcs are cache reads keyed by the snapshot's skill ids (gear-card pattern).
public sealed partial class Plugin
{
    private const int MaxSkillRows = 80;
    private const int MaxImagineRows = 2;

    private readonly List<(int SkillId, string Name, string Meta)> _skillRows = new(MaxSkillRows);
    private IReadOnlyList<SkillLevel>? _lastSkillSnapshot;   // tracker swaps the list wholesale — reference = dirty check
    private readonly UvRect[] _skillUv = new UvRect[MaxSkillRows];
    private readonly UvRect[] _imagineRowUv = new UvRect[MaxImagineRows];

    private HudElement BuildSkillBookBody()
    {
        var rows = new HudElement[1 + MaxImagineRows + MaxSkillRows];
        // Empty state: far players get the "be near them" hint (skills are AOI-broadcast only); near
        // players with genuinely no skill data keep the plain "No skill data." line.
        rows[0] = new ConditionalElement(() => _imagines.Count + _skillRows.Count == 0,
            new TextElement(() => _isRemote
                ? "Battle Imagines & skills appear once you've been near this player."
                : "No skill data.", MutedCol));
        for (var i = 0; i < MaxImagineRows; i++) rows[1 + i] = ImagineRowElement(i);
        for (var i = 0; i < MaxSkillRows; i++) rows[1 + MaxImagineRows + i] = SkillRowElement(i);
        return new ScrollElement(
            new ListElement(() => VisibleSkillTabRows(), rows), 380f);
    }

    private int VisibleSkillTabRows()
    {
        // The pool is [empty-state][imagine slots][skill slots]; ListElement shows the first N, so
        // imagine slots are always counted while any skill row shows. A target with fewer than
        // MaxImagineRows imagines leaves the unused slots' Cond containers DEACTIVATED — the only
        // artifact is one extra 2px list gap, not a blank line (traced in WindowBuilder, ux review).
        return 1 + (_imagines.Count + _skillRows.Count > 0 ? MaxImagineRows + _skillRows.Count : 0);
    }

    // Imagine accent: the teal family the approved mockup tinted imagine rows with. AccentRowElement
    // draws a full-width 0.12-alpha wash + 3px left stripe in this colour (the spec's "distinct
    // tinted background"), so imagine rows read differently from skill rows even with icons pending.
    private static readonly ColorRgba ImagineTint = new(0.30f, 0.75f, 0.82f, 1f);

    private HudElement ImagineRowElement(int slot) => new ConditionalElement(
        () => slot < _imagines.Count,
        new AccentRowElement(
            new RowElement(new HudElement[]
            {
                new GameTextureElement(() => ImagineRowIcon(slot), 24, 24, () => _imagineRowUv[slot]),
                new TextElement(() => slot < _imagines.Count ? _imagines[slot].Name : "", Emphasis: true),
                new TextElement(() => slot < _imagines.Count ? _imagines[slot].Stars : ""),
                new SpacerElement(),
                new TextElement(() => "Battle Imagine", MutedCol),
            }, Gap: 6f),
            Stripe: static () => ImagineTint,
            Share: static () => 1f));

    private object? ImagineRowIcon(int slot)
    {
        if (slot >= _imagines.Count) return null;
        return _services.GameAssets.LoadImagineIcon(_imagines[slot].SkillId, out _imagineRowUv[slot]);
    }

    private HudElement SkillRowElement(int idx) => new RowElement(new HudElement[]
    {
        new GameTextureElement(() => SkillRowIcon(idx), 20, 20, () => _skillUv[idx]),
        new TextElement(() => idx < _skillRows.Count ? _skillRows[idx].Name : ""),
        new SpacerElement(),
        new TextElement(() => idx < _skillRows.Count ? _skillRows[idx].Meta : "", MutedCol),
    }, Gap: 6f);

    private object? SkillRowIcon(int idx)
    {
        if (idx >= _skillRows.Count) return null;
        return _services.GameAssets.LoadSkillIcon(_skillRows[idx].SkillId, out _skillUv[idx]);
    }

    private void RebuildSkillBook()
    {
        // The tracker returns the same stored list until a fresh AttrSkillLevelIdList broadcast
        // replaces it — skip the per-tick string rebuild when nothing changed (perf review).
        var snapshot = TargetSkills();
        if (ReferenceEquals(snapshot, _lastSkillSnapshot)) return;
        _lastSkillSnapshot = snapshot;

        _skillRows.Clear();
        foreach (var sl in snapshot)
        {
            if (_skillRows.Count >= MaxSkillRows) break;
            if (IsImagineSkill(sl.SkillId)) continue;       // shown in the imagine rows above
            // `is { Name.Length: > 0 }`, not `?.Name ??`: the table has rows with EMPTY names (internal/
            // system skills that ride the same loadout attr) — those rendered as blank rows with only a
            // Lv·Tier value (user-flagged 2026-06-13). Show the id so nothing is anonymous.
            var name = _services.GameData.Combat.GetSkill(sl.SkillId) is { Name.Length: > 0 } sk
                ? sk.Name : $"Skill {sl.SkillId}";
            _skillRows.Add((sl.SkillId, name, $"Lv {sl.Level} · Tier {sl.Tier}"));
        }
    }

    private bool IsImagineSkill(int skillId)
    {
        for (var i = 0; i < _imagines.Count; i++)
            if (_imagines[i].SkillId == skillId) return true;
        return false;
    }
}
