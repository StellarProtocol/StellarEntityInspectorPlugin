using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Overview tab — curated grouped stats (spec §4.1) replacing the raw attr dump. Sections are FIXED id
// lists; names + value format resolve via IGameData.Combat.GetAttribute (live table merged with the
// generated AttrCatalog). Percent stats pair with their flat rating id ("25.5% (4,870)", ZDPS-style).
// Values come from the broadcast snapshot (_targetAttrs); self-only ids fall back to IPlayerStats when
// inspecting yourself; otherwise "—". Rebuilt on the ~10 Hz snapshot tick into flat label/value lists so
// the element Funcs never scan or allocate. Header rows are marked by an empty value (accent colour).
public sealed partial class Plugin
{
    // (stat id to display, paired flat rating id or 0 — rating renders as a trailing "(N)").
    private static readonly (string Title, (int Id, int RatingId)[] Rows)[] OverviewSections =
    {
        ("Core",      new[] { (11330, 0), (11340, 0), (11320, 0), (11350, 0), (11360, 0) }),
        ("Primary",   new[] { (11010, 0), (11020, 0), (11030, 0), (11040, 0) }),
        ("Secondary", new[] { (11710, 11110), (11930, 11120), (11780, 11130),
                              (11940, 11140), (11950, 11150), (11970, 11170) }),
        ("Offense",   new[] { (12510, 0), (12530, 0), (12550, 0), (12570, 0), (12590, 0), (12610, 0),
                              (12630, 0), (11370, 0), (11380, 0), (11390, 0), (11400, 0), (11720, 0),
                              (11730, 0), (11740, 0), (11760, 0), (11750, 0), (11910, 0), (11830, 0) }),
        ("Defense",   new[] { (12520, 0), (12540, 0), (12560, 0), (12580, 0), (12600, 0), (12620, 0),
                              (12640, 0), (12660, 0), (12680, 0), (13400, 0) }),
        ("Healing & Shields", new[] { (11790, 0), (11800, 0), (11810, 0), (11820, 0), (12720, 0), (12740, 0) }),
        ("Utility",   new[] { (10200, 0), (10210, 0), (10220, 0), (10240, 0), (10250, 0), (10260, 0),
                              (10230, 0), (10270, 0), (20010, 0), (20020, 0) }),
    };

    // Elemental bands (zero-suppressed; the whole section is hidden when nothing is non-zero).
    private static readonly (string Suffix, int BaseId)[] ElementalBands =
    {
        ("Attack", 11500), ("Power", 13000), ("Bonus", 13100), ("Resist", 13200), ("Reduction", 13310),
    };
    private const int ElementCount = 9;   // All/Fire/Ice/Forest/Thunder/Wind/Rock/Light/Dark, ×10 apart

    private const int MaxOverviewRows = 116;   // 66 fixed + up to 46 elemental + 4 identity (review: avoid silent truncation)
    private readonly List<string> _ovLabels = new(MaxOverviewRows);
    private readonly List<string> _ovValues = new(MaxOverviewRows);

    private HudElement BuildOverviewBody()
    {
        var slots = new HudElement[MaxOverviewRows];
        for (var i = 0; i < MaxOverviewRows; i++)
        {
            var idx = i;
            // Shared header/normal section styling (BuildSectionRow). The old 14px right-margin spacer is
            // gone — the scroll VIEWPORT is inset clear of the scrollbar globally in BuildScroll.
            slots[i] = BuildSectionRow(
                () => idx < _ovLabels.Count ? _ovLabels[idx] : "",
                () => idx < _ovValues.Count ? _ovValues[idx] : "",
                () => IsHeaderRow(idx), labelWidth: 170f);
        }
        // Far players have no broadcast attrs (the whole sheet is "—"); a one-line banner explains why
        // and auto-vanishes once they enter AOI (broadcast arrives → _isRemote false).
        return new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => _isRemote, new TextElement(
                () => "Detailed stats sync only while this player is near you.", MutedCol)),
            new ScrollElement(new ListElement(() => _ovLabels.Count, slots), 360f),
        }, Gap: 6f);
    }

    private bool IsHeaderRow(int idx) => idx < _ovValues.Count && _ovValues[idx].Length == 0;

    private ColorRgba? AccentCol() => new ColorRgba(0.79f, 0.66f, 0.36f, 1f);   // section header gold

    private void RebuildOverview()
    {
        _ovLabels.Clear(); _ovValues.Clear();
        AddIdentitySection();
        foreach (var (title, rows) in OverviewSections)
        {
            AddSectionHeader(title);
            foreach (var (id, ratingId) in rows)
            {
                if (id == 11320) AddMaxHpRow();        // effective max from vitals, not the base attr
                else AddStatRow(id, ratingId);
            }
        }
        AddElementalSection();
    }

    // Guild / Party / Master Score from the cached social reply. The game's own ID card fetches
    // mask 0 = ALL sections, and our wire tap caches that reply — so these populate for any carded
    // player regardless of distance (the one Overview block far players DO get). Value-gated: thin-mask
    // replies (nameplate queries) and guildless/solo/hidden players render no row at all.
    private void AddIdentitySection()
    {
        if (_socialSnap is not { Identity: var id }) return;
        if (id.Guild.Length == 0 && id.PartySize == 0 && id.MasterScore == 0) return;
        AddSectionHeader("Identity");
        if (id.Guild.Length > 0) { _ovLabels.Add("Guild"); _ovValues.Add(id.Guild); }
        if (id.PartySize > 0)
        { _ovLabels.Add("Party"); _ovValues.Add(id.PartySize.ToString(CultureInfo.InvariantCulture) + " members"); }
        if (id.MasterScore > 0)
        { _ovLabels.Add("Master Score"); _ovValues.Add(id.MasterScore.ToString("N0", CultureInfo.InvariantCulture)); }
    }

    private void AddSectionHeader(string title)
    {
        if (_ovLabels.Count >= MaxOverviewRows) return;
        _ovLabels.Add(title); _ovValues.Add("");        // empty value = header row (accent colour)
    }

    private void AddStatRow(int id, int ratingId)
    {
        if (_ovLabels.Count >= MaxOverviewRows) return;
        var info = _services.GameData.Combat.GetAttribute(id);
        var label = info is { Name.Length: > 0 } a ? a.Name : $"Attr{id}";
        // Self: an absent id means ZERO (everything self-relevant is broadcast or polled), so render it
        // like the game sheet does ("0%"/"0"). Others: absent = genuinely not broadcast → "—".
        var value = TryAttr(id, out var raw)
            ? FormatAttr(info?.NumType ?? -1, raw) + RatingSuffix(ratingId)
            : IsSelf ? FormatAttr(info?.NumType ?? -1, 0) : "—";
        _ovLabels.Add(label); _ovValues.Add(value);
    }

    // The broadcast 11320 carries the BASE max HP (157,347 on the test char) while the game's character
    // sheet and our HP bar show the EFFECTIVE max from the vitals stream (181,411 — verified in-world
    // 2026-06-12). Prefer vitals for the Core Max HP row so the panel agrees with the game sheet.
    private void AddMaxHpRow()
    {
        if (_ovLabels.Count >= MaxOverviewRows) return;
        var v = TargetVitals();
        if (v.IsKnown && v.MaxHp > 0)
        {
            _ovLabels.Add(_services.GameData.Combat.GetAttribute(11320)?.Name ?? "Max HP");
            _ovValues.Add(v.MaxHp.ToString("N0", CultureInfo.InvariantCulture));
            return;
        }
        AddStatRow(11320, 0);
    }

    private string RatingSuffix(int ratingId)
        => ratingId != 0 && TryAttr(ratingId, out var r)
            ? " (" + r.ToString("N0", CultureInfo.InvariantCulture) + ")" : "";

    // Broadcast snapshot first; self-only ids fall back to IPlayerStats when inspecting yourself.
    private bool TryAttr(int id, out long value)
    {
        if (_targetAttrs.TryGetValue(id, out value)) return true;
        // Live self-stat fallback only when NOT viewing a frozen session — frozen attrs are the source of truth.
        if (!IsFrozen && IsSelf && _services.PlayerStats.TryGetAttribute(id) is { } self) { value = self; return true; }
        value = 0; return false;
    }

    // Percent = 2 decimals to match the game's own character sheet exactly (41.96%, verified in-world).
    private static string FormatAttr(int numType, long raw) => numType switch
    {
        1 => (raw / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%",
        2 => (raw / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s",
        _ => raw.ToString("N0", CultureInfo.InvariantCulture),
    };

    private void AddElementalSection()
    {
        var headerAdded = false;
        foreach (var (suffix, baseId) in ElementalBands)
            for (var e = 0; e < ElementCount; e++)
            {
                var id = baseId + e * 10;
                if (!TryAttr(id, out var raw) || raw == 0) continue;
                if (!headerAdded) { AddSectionHeader("Elemental"); headerAdded = true; }
                if (_ovLabels.Count >= MaxOverviewRows) return;
                var info = _services.GameData.Combat.GetAttribute(id);
                _ovLabels.Add(info is { Name.Length: > 0 } a ? a.Name : $"Attr{id} {suffix}");
                _ovValues.Add(FormatAttr(info?.NumType ?? -1, raw));
            }
    }
}
