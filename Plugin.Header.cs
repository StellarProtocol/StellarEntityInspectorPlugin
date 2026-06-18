using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Window root: portrait slot + identity header + stat strip, then the tab bar and the per-tab bodies.
// Identity/score/HP resolve for any in-AOI entity; ATK + secondary stats are self-only (the wire broadcasts
// them as 0 for others) and read from IPlayerStats. Attr ids are local consts (plugins reference Abstractions
// only, not Stellar.Wire's AttrTypeIds) — verified against enum_e_attr_type.proto.
public sealed partial class Plugin
{
    private const int AttrLevel = 10000, AttrSeasonLevel = 10070, AttrProfessionId = 220,
                      AttrSeasonStrength = 11440;

    // Stat strip (label, attr id): percent ids for Crit/Haste/Luck so the strip agrees with the Overview
    // ("41.96%", not the raw rating — ux-ui review: same datum, one format per viewport).
    private static readonly (string Label, int Id)[] SelfStats =
    {
        ("ATK",   11330),
        ("Crit",  11710),
        ("Haste", 11930),
        ("Luck",  11780),
    };

    // Two panes: LEFT = the 3D model portrait spanning the full window height; RIGHT = the info column
    // (identity + stat strip, tab bar, per-tab body). The right body fills leftover height (Resizable window).
    private HudElement BuildRoot() => new RowElement(new HudElement[]
    {
        BuildPortrait(),                       // Plugin.Portrait.cs — tall left pane (self 3D / placeholder)
        new SpacerElement(Width: 10f),
        new ColumnElement(new HudElement[]
        {
            // Frozen-session banner: shown only when rendering a snapshot routed in via IFrozenEntityViewer
            // (the live portrait/stats are disabled in that mode). Closing the window returns to live.
            new ConditionalElement(() => IsFrozen,
                new TextElement(() => $"◷ Viewing session — {FrozenLabel}   ·   close to return to live",
                    () => new ColorRgba(0.96f, 0.80f, 0.36f, 1f))),
            BuildIdentity(),
            new SeparatorElement(),
            BuildTabBar(),
            new ConditionalElement(() => _tab == Tab.Overview,  BuildOverviewBody(),  Fill: true),
            new ConditionalElement(() => _tab == Tab.Gear,      BuildGearBody(),      Fill: true),
            new ConditionalElement(() => _tab == Tab.SkillBook, BuildSkillBookBody(), Fill: true),
            new ConditionalElement(() => _tab == Tab.Wardrobe,  BuildWardrobeBody(),  Fill: true),
        }, Gap: 6f),
    }, Gap: 0f);

    // Identity header: [prof icon] Name  Class / Level + Ability Score line / HP bar / imagine chips / stat strip.
    private HudElement BuildIdentity() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new GameTextureElement(ProfIcon, 18, 18, () => _profUv),
            new TextElement(() => NameLine(), Emphasis: true),
            new TextElement(() => ProfessionLine(), MutedCol),
        }, Gap: 6f),
        new TextElement(() => $"Level: {LevelLine()}   Ability Score: {AbilityScoreLine()}", MutedCol),
        new BarElement(HpFraction, new ColorRgba(0.24f, 0.62f, 0.40f, 1f), Label: HpLine),
        // One imagine per line: real imagine names (~28 chars) can never fit two-abreast in the info
        // column, and name/stars as SEPARATE elements means a squeezed name wraps alone while the star
        // block stays intact (ux-ui review recommendation, measured).
        BuildImagineChip(0),
        BuildImagineChip(1),
        BuildStatStrip(),
    }, Gap: 3f);

    private HudElement BuildImagineChip(int slot) => new RowElement(new HudElement[]
    {
        new GameTextureElement(() => ImagineIcon(slot), 16, 16, () => _imgUv[slot]),
        new TextElement(() => slot < _imagines.Count ? _imagines[slot].Name : "", MutedCol),
        new TextElement(() => slot < _imagines.Count ? _imagines[slot].Stars : ""),
    }, Gap: 6f);

    // ---- header icon + chip state (Funcs run per-frame: cache-lookup only, no allocation) ----

    private UvRect _profUv = new(0f, 0f, 1f, 1f);
    private object? ProfIcon()
    {
        var profId = (int)AttrOr(AttrProfessionId, IsSelf ? _services.PlayerState.Profession : 0);
        return profId > 0 ? _services.GameAssets.LoadProfessionIcon(profId, out _profUv) : null;
    }

    private readonly UvRect[] _imgUv = { new(0f, 0f, 1f, 1f), new(0f, 0f, 1f, 1f) };
    private object? ImagineIcon(int slot)
    {
        if (slot >= _imagines.Count) return null;
        return _services.GameAssets.LoadImagineIcon(_imagines[slot].SkillId, out _imgUv[slot]);
    }

    private float HpFraction()
    {
        var v = TargetVitals();
        if (v.IsKnown && v.MaxHp > 0) return (float)v.Hp / v.MaxHp;
        if (IsSelf && _services.PlayerState.MaxHealth > 0)
            return (float)_services.PlayerState.Health / _services.PlayerState.MaxHealth;
        return 0f;
    }

    private HudElement BuildTabBar() => new RowElement(new HudElement[]
    {
        new ButtonElement(() => "Overview", () => SelectTab(Tab.Overview),  Active: () => _tab == Tab.Overview),
        new ButtonElement(() => "Gear",     () => SelectTab(Tab.Gear),      Active: () => _tab == Tab.Gear),
        new ButtonElement(() => "Skills",   () => SelectTab(Tab.SkillBook), Active: () => _tab == Tab.SkillBook),
        new ButtonElement(() => "Wardrobe", () => SelectTab(Tab.Wardrobe),  Active: () => _tab == Tab.Wardrobe),
    }, Gap: 4f);

    // Self-only secondary stats; "—" for others (not broadcast). Two rows to stay compact.
    private HudElement BuildStatStrip() => new RowElement(new HudElement[]
    {
        new TextElement(() => StatLine(SelfStats[0]), MutedCol),  // ATK
        new TextElement(() => StatLine(SelfStats[1]), MutedCol),  // Crit
        new TextElement(() => StatLine(SelfStats[2]), MutedCol),  // Haste
        new TextElement(() => StatLine(SelfStats[3]), MutedCol),  // Luck
    }, Gap: 10f);

    private string StatLine((string Label, int Id) s)
    {
        var nt = _services.GameData.Combat.GetAttribute(s.Id)?.NumType ?? -1;
        return TryAttr(s.Id, out var v) ? $"{s.Label}: {FormatAttr(nt, v)}"
             : IsSelf ? $"{s.Label}: {FormatAttr(nt, 0)}"
             : $"{s.Label}: —";
    }

    // ---- identity ----

    private string NameLine()
    {
        var n = TargetName();
        if (!string.IsNullOrEmpty(n)) return n!;
        if (IsSelf && !string.IsNullOrEmpty(_services.PlayerState.Name)) return _services.PlayerState.Name!;
        if (_socialSnap is { Name.Length: > 0 } s) return s.Name;   // far player: cached social identity
        return "Unknown";
    }

    private string LevelLine()
    {
        // Precedence: broadcast AttrLevel → self PlayerState → cached social level (far players, last
        // fallback before 0). Social is identity-only, so it never overrides a live broadcast value.
        long socialFallback = IsSelf ? _services.PlayerState.Level : _socialSnap?.Level ?? 0;
        long lvl = AttrOr(AttrLevel, socialFallback);
        long season = AttrOr(AttrSeasonLevel, 0);
        return season > 0 ? $"{lvl} (+{season})" : lvl.ToString(CultureInfo.InvariantCulture);
    }

    private string AbilityScoreLine()
    {
        // Broadcast fight point first; far players fall back to the social reply's user_attr_data
        // (populated when their ID card was fetched with the full mask — never overrides a live value).
        var fp = TargetFightPoint();
        if (fp == 0) fp = _socialSnap?.FightPoint ?? 0;
        var score = fp.ToString("N0", CultureInfo.InvariantCulture);
        var season = AttrOr(AttrSeasonStrength, 0);
        return season > 0 ? $"{score} (+{season})" : score;
    }

    // Resolve the target's talent-school id (from their cast-resolved spec, ICombatSpec) for the v2 school-lib
    // gear-attr lookup. 0 when the spec is unknown (target not yet seen casting a spec-defining skill) — the
    // gear-detail Advanced section then shows an honest "spec unknown" line instead of empty rows. (Was the AOI
    // loadout, which carries both specs' skills and mislabelled players; the framework now resolves from casts.)
    private int ResolveTalentSchool()
        => ProfessionSpecs.TalentSchool(_services.CombatSpec.GetSubProfession(_target));

    private string ProfessionLine()
    {
        long socialFallback = IsSelf ? _services.PlayerState.Profession : _socialSnap?.ProfessionId ?? 0;
        long profId = AttrOr(AttrProfessionId, socialFallback);
        if (profId <= 0) return "—";
        var prof = _services.GameData.Combat.GetProfession((int)profId);
        var name = prof is { Name: { Length: > 0 } n } ? n : $"Class {profId}";
        // Spec (e.g. "Icicle") from the framework's shared cast-resolved cache (ICombatSpec) — correct once the
        // target has been seen casting; omitted otherwise (the AOI loadout can't disambiguate the two specs).
        var sub = _services.CombatSpec.GetSubProfession(_target);
        if (sub != 0 && ProfessionSpecs.Name(sub) is { Length: > 0 } spec)
            return name + " · " + spec;
        return name;
    }

    private string HpLine()
    {
        var v = TargetVitals();
        if (v.IsKnown && v.MaxHp > 0)
            return $"{v.Hp:N0} / {v.MaxHp:N0} ({100f * v.Hp / v.MaxHp:F0}%)";
        if (IsSelf && _services.PlayerState.MaxHealth > 0)
        {
            var ps = _services.PlayerState;
            return $"{ps.Health:N0} / {ps.MaxHealth:N0} ({100f * ps.Health / ps.MaxHealth:F0}%)";
        }
        return "—";
    }

    // Read a scalar attr from the snapshotted target map, falling back to a self value.
    private long AttrOr(int id, long fallback)
        => _targetAttrs.TryGetValue(id, out var v) ? v : fallback;

    // ---- self-stat subscription (IPlayerStats only delivers subscribed attr ids) ----

    // Every id the Overview/stat-strip reads for SELF (IPlayerStats samples only subscribed ids).
    private static System.Collections.Generic.IEnumerable<int> SelfPolledIds()
    {
        foreach (var s in SelfStats) yield return s.Id;
        foreach (var (_, rows) in OverviewSections)
            foreach (var (id, ratingId) in rows) { yield return id; if (ratingId != 0) yield return ratingId; }
        foreach (var (_, baseId) in ElementalBands)
            for (var e = 0; e < ElementCount; e++) yield return baseId + e * 10;
    }

    // Subscribe ONLY the ids the broadcast snapshot lacks — the probe reflects over every subscribed id
    // each game tick, and for self most Overview ids already arrive on the wire (perf review: the full
    // ~130-id set cost ~7.8k reflection invokes/s). Tracked in a set so unsubscribe releases exactly
    // what was taken even if the snapshot changes meanwhile.
    private readonly System.Collections.Generic.HashSet<int> _subscribedIds = new();

    private void SubscribeSelfStats()
    {
        if (_subscribedIds.Count > 0) return;
        foreach (var id in SelfPolledIds())
            if (!_targetAttrs.ContainsKey(id) && _subscribedIds.Add(id))
                _services.PlayerStats.Subscribe(id);
    }

    private void UnsubscribeSelfStats()
    {
        if (_subscribedIds.Count == 0) return;
        foreach (var id in _subscribedIds) _services.PlayerStats.Unsubscribe(id);
        _subscribedIds.Clear();
    }
}
