using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Skills tab (approved prototype) — one SKILLS panel of 26 px tile rows (+2 gap): icon · bold name · muted "Lv N · Tier N".
// The equipped Battle Imagines come first (imagine icon, stars, muted "Battle Imagine"), then the skill loadout from
// AttrSkillLevelIdList minus those imagine ids. Skills are only broadcast for players near you: a far player shows the
// Nearby tag + a note. A VirtualList draws only the visible rows (pool of 18) — the list can reach 80 entries.
// Snapshotted at 10 Hz into pooled lists; row Funcs are cache reads keyed by the snapshot's skill ids.
public sealed partial class Plugin
{
    private const int MaxSkillRows = 80;
    private const int SkillPool = 18;
    private const float SkillRowPitch = TileRowH + 2f;

    private readonly List<(int SkillId, string Name, string Meta)> _skillRows = new(MaxSkillRows);
    private IReadOnlyList<SkillLevel>? _lastSkillSnapshot;   // tracker swaps the list wholesale — reference = dirty check
    private object? _skLang;
    private int _skFirst;                                    // VirtualList window start
    private readonly UvRect[] _skillUv = new UvRect[SkillPool];

    private int SkillItemCount => _imagines.Count + _skillRows.Count;

    private HudElement BuildSkillBookBody()
    {
        var pool = new HudElement[SkillPool];
        for (int k = 0; k < SkillPool; k++) pool[k] = SkillRowElement(k);
        return CaptionedPanel(() => _loc.T("ei.tab.skills"), NoHint,
            DataTag(() => SkillItemCount == 0 && _isRemote && !IsFrozen ? TagKind.Nearby : TagKind.None, TagCaptionH),
            new HudElement[]
            {
                StateLine(() => SkillItemCount > 0 ? "" : _loc.T(_isRemote ? "ei.skill.farNote" : "ei.skill.none"),
                    () => MutedColor()),
                new ConditionalElement(() => SkillItemCount > 0,
                    new VirtualListElement(() => SkillItemCount, SkillRowPitch, pool, first => _skFirst = first, 300f),
                    Fill: true),
            });
    }

    // Pool slot k shows item _skFirst + k: imagine rows first, then skills.
    private HudElement SkillRowElement(int k)
    {
        int Item() => _skFirst + k;
        bool IsImagine() => Item() < _imagines.Count;
        string Name()
        {
            int i = Item();
            if (i < _imagines.Count) return _imagines[i].Name;
            i -= _imagines.Count;
            return i < _skillRows.Count ? _skillRows[i].Name : "";
        }
        string Meta()
        {
            int i = Item();
            if (i < _imagines.Count) return _imagines[i].Stars;
            i -= _imagines.Count;
            return i < _skillRows.Count ? _skillRows[i].Meta : "";
        }
        return new ColumnElement(new HudElement[]
        {
            new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(6f, TileRowH),
                new GameTextureElement(() => SkillRowIcon(k), 20, 20, () => _skillUv[k]),
                new SpacerElement(8f, 0f),
                new CellElement(new TextElement(Name, Color: () => TextColor(), NoWrap: true, FontSize: 13)
                    { Bold = true, Ellipsize = true }, Weight: 1f),
                new ConditionalElement(IsImagine, new TextElement(() => _loc.T("ei.skill.battleImagine"),
                    Color: () => MutedColor(), NoWrap: true)),
                new SpacerElement(8f, 0f),
                new TextElement(Meta, Color: () => IsImagine() ? CautionColor() : MutedColor(), NoWrap: true),
                new SpacerElement(8f, 0f),
            }, Gap: 0.01f))
            {
                FillColorFn = () => Fade(IsImagine() ? Over(AccentColor() with { A = 0.10f }, TileColor()) : TileColor()),
                CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
            },
            new SpacerElement(0f, 2f),
        }, Gap: 0.01f);
    }

    private object? SkillRowIcon(int k)
    {
        int i = _skFirst + k;
        if (i < _imagines.Count) return _services.GameAssets.LoadImagineIcon(_imagines[i].SkillId, out _skillUv[k]);
        i -= _imagines.Count;
        if (i >= _skillRows.Count) return null;
        return _services.GameAssets.LoadSkillIcon(_skillRows[i].SkillId, out _skillUv[k]);
    }

    private void RebuildSkillBook()
    {
        // The tracker returns the same stored list until a fresh AttrSkillLevelIdList broadcast replaces it — skip the
        // per-tick string rebuild when nothing changed (perf review).
        var snapshot = TargetSkills();
        var lang = _loc.T("ei.tab.skills");
        if (ReferenceEquals(snapshot, _lastSkillSnapshot) && ReferenceEquals(lang, _skLang)) return;
        _lastSkillSnapshot = snapshot; _skLang = lang;

        _skillRows.Clear();
        foreach (var sl in snapshot)
        {
            if (_skillRows.Count >= MaxSkillRows) break;
            if (IsImagineSkill(sl.SkillId)) continue;       // shown in the imagine rows above
            // `is { Name.Length: > 0 }`: the table has rows with EMPTY names (internal/system skills on the same loadout
            // attr) — show the id so nothing is anonymous (user-flagged 2026-06-13).
            var name = _services.GameData.Combat.GetSkill(sl.SkillId) is { Name.Length: > 0 } sk
                ? sk.Name : _loc.TFormat("ei.skill.fallback", sl.SkillId);
            _skillRows.Add((sl.SkillId, name, _loc.TFormat("ei.skill.levelTier", sl.Level, sl.Tier)));
        }
    }

    private bool IsImagineSkill(int skillId)
    {
        for (var i = 0; i < _imagines.Count; i++)
            if (_imagines[i].SkillId == skillId) return true;
        return false;
    }
}
