using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Window root + ToolHeader (approved prototype "EntityInspector redesign", layout A):
//   [40 px accent tile: class icon] bold Name  muted "Class · Spec"                                      (?)
//                                   ● Nearby · live · Lv 60 · Ability Score 61,355 · Illusion-Breaking 4,675
//   ┌ PORTRAIT ─ [Weapon: On] ┐ ┌ (Overview)(Gear)(Skills)(Wardrobe)(Location)(Party) ┐
//   │ 3D model · HP · imagines │ │ the selected tab's panels                            │
// The status line has ONE shared builder for its three cases (nearby / far / session view); every string the header shows
// is rebuilt on the 10 Hz snapshot tick and only when its inputs changed, so the element Funcs are pure cache reads.
// Attr ids are local consts (plugins reference Abstractions only) — verified against enum_e_attr_type.proto.
public sealed partial class Plugin
{
    private const int AttrLevel = 10000, AttrSeasonLevel = 10070, AttrProfessionId = 220,
                      AttrSeasonStrength = 11440;
    private const float PortraitPanelW = 340f;   // widened from the prototype's 268 after the first in-game review

    private HudElement BuildRoot() => new ColumnElement(new HudElement[]
    {
        BuildToolHeader(),
        new RowElement(new HudElement[]
        {
            new CellElement(BuildPortraitPanel(), Width: PortraitPanelW),
            new CellElement(new ColumnElement(new HudElement[] { BuildTabTrack(), BuildTabBodies() }, Gap: PanelGap), Weight: 1f),
        }, Gap: PanelGap),
    }, Gap: 10f);

    // ToolHeader: 40 px accent tile · bold name + muted class/spec over the live status line · the round "?".
    private HudElement BuildToolHeader() => new RowElement(new HudElement[]
    {
        HeaderIconTile(),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new TextElement(() => _hdrName, Color: () => TextColor(), Emphasis: true, NoWrap: true),
                new CellElement(new TextElement(() => _hdrClass, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true },
                    Weight: 1f),
            }, Gap: 8f),
            BuildStatusLine(),
        }, Gap: 2f), Weight: 1f),
        HelpButton(),
    }, Gap: 10f);

    // Status line: [● dot] bold coloured main part + muted tail. The dot is PLAIN text "●" (legacy uGUI Text with OS
    // font fallback — the bold/TMP path's Noto Sans has no U+25CF and draws nothing, Party tab finding).
    private HudElement BuildStatusLine() => new RowElement(new HudElement[]
    {
        new ConditionalElement(() => _statusKind != StatusKind.Session, new RowElement(new HudElement[]
        {
            new TextElement(() => "●", Color: StatusMainColor, NoWrap: true),
            new SpacerElement(4f, 0f),
        }, Gap: 0.01f)),
        new TextElement(() => _statusMain, Color: StatusMainColor, NoWrap: true, FontSize: 13) { Bold = true },
        new CellElement(new TextElement(() => _statusTail, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true },
            Weight: 1f),
    }, Gap: 0.01f);

    private enum StatusKind : byte { Nearby, Far, Session }
    private StatusKind _statusKind;
    private string _statusMain = "", _statusTail = "";
    private string _hdrName = "", _hdrClass = "";

    private ColorRgba? StatusMainColor() => _statusKind switch
    {
        StatusKind.Nearby => SuccessColor(),
        StatusKind.Far    => CautionColor(),
        _                 => AccentColor(),
    };

    // Inputs of the last status/identity build (rebuild only when one changes — no per-tick string building).
    private StatusKind _stKind = (StatusKind)255;
    private long _stLvl = -1, _stSeasonLvl = -1, _stFp = -1, _stSeason = -1, _stAgeMin = long.MinValue;
    private string? _stSession, _stName;
    private int _stProf = -1, _stSub = -1;
    private object? _stLang;

    private void RebuildHeader()
    {
        var kind = IsFrozen ? StatusKind.Session : _isRemote ? StatusKind.Far : StatusKind.Nearby;
        long lvl = LevelValue(), seasonLvl = AttrOr(AttrSeasonLevel, 0), fp = FightPointValue(),
             season = AttrOr(AttrSeasonStrength, 0), age = kind == StatusKind.Far ? ProfileAgeMinutes() : -1;
        var lang = _loc.T("ei.status.nearby");   // a cached catalog string: a new reference = the language changed
        var name = NameLine();
        int prof = ProfessionValue(), sub = SpecValue();

        if (!ReferenceEquals(name, _stName) || prof != _stProf || sub != _stSub || !ReferenceEquals(lang, _stLang))
        {
            _stName = name; _stProf = prof; _stSub = sub;
            _hdrName = name;
            _hdrClass = ClassLine(prof, sub);
        }
        if (kind == _stKind && lvl == _stLvl && seasonLvl == _stSeasonLvl && fp == _stFp && season == _stSeason
            && age == _stAgeMin && ReferenceEquals(FrozenLabel, _stSession) && ReferenceEquals(lang, _stLang)) return;
        _stKind = kind; _stLvl = lvl; _stSeasonLvl = seasonLvl; _stFp = fp; _stSeason = season; _stAgeMin = age;
        _stSession = FrozenLabel; _stLang = lang;

        _statusKind = kind;
        var ci = CultureInfo.InvariantCulture;
        var lv = _loc.TFormat("ei.status.lv", seasonLvl > 0
            ? lvl.ToString(ci) + " (+" + seasonLvl.ToString(ci) + ")" : lvl.ToString(ci));
        var score = fp.ToString("N0", ci);
        switch (kind)
        {
            case StatusKind.Session:
                _statusMain = _loc.TFormat("ei.status.session", FrozenLabel);
                _statusTail = " · " + _loc.T("ei.status.sessionTail");
                break;
            case StatusKind.Far:
                _statusMain = age < 0 ? _loc.T("ei.status.far")
                            : age == 0 ? _loc.T("ei.status.farNow")
                            : _loc.TFormat("ei.status.farAge", age);
                _statusTail = " · " + lv + " · " + score;
                break;
            default:
                _statusMain = _loc.T("ei.status.nearby");
                _statusTail = " · " + lv + " · " + _loc.TFormat("ei.status.ability", score)
                            + (season > 0 ? " · " + _loc.TFormat("ei.status.illusion", season.ToString("N0", ci)) : "");
                break;
        }
    }

    // Age of the cached profile reply in whole minutes (its location stamp — the one reply field the framework stamps);
    // -1 when no stamped reply exists or the server clock isn't synced yet.
    private long ProfileAgeMinutes()
    {
        long rx = _socialSnap?.Location?.ReceivedAtMs ?? 0, now = _services.CombatSnapshot.ServerNowMs;
        if (rx <= 0 || now <= 0) return -1;
        return Math.Max(0L, now - rx) / 60_000L;
    }

    // ---- header icon (Funcs run per refresh: cache lookup only, no allocation) ----

    private UvRect _profUv = new(0f, 0f, 1f, 1f);
    private object? ProfIcon()
    {
        var profId = ProfessionValue();
        return profId > 0 ? _services.GameAssets.LoadProfessionIcon(profId, out _profUv) : null;
    }

    // ---- identity ----

    private string NameLine()
    {
        var n = TargetName();
        if (!string.IsNullOrEmpty(n)) return n;
        if (IsSelf && !string.IsNullOrEmpty(_services.PlayerState.Name)) return _services.PlayerState.Name!;
        if (_socialSnap is { Name.Length: > 0 } s) return s.Name;   // far player: cached social identity
        return _loc.T("ei.header.unknown");
    }

    // Precedence: broadcast AttrLevel → self PlayerState → cached social level (far players, last fallback).
    private long LevelValue()
    {
        long socialFallback = IsSelf ? _services.PlayerState.Level : _socialSnap?.Level ?? 0;
        return AttrOr(AttrLevel, socialFallback);
    }

    // Broadcast fight point first; far players fall back to the social reply's user_attr_data (never overrides live).
    private long FightPointValue()
    {
        var fp = TargetFightPoint();
        return fp != 0 ? fp : _socialSnap?.FightPoint ?? 0;
    }

    private int ProfessionValue()
    {
        long socialFallback = IsSelf ? _services.PlayerState.Profession : _socialSnap?.ProfessionId ?? 0;
        return (int)AttrOr(AttrProfessionId, socialFallback);
    }

    // Spec (e.g. "Icicle"): the talent-derived spec when its root buff is seen (ICombatSpec.TryGetTalentSpec,
    // authoritative), else the cast-resolved one (GetSubProfession) — both only exist for players near you. 0 = unknown.
    private int SpecValue()
    {
        if (IsFrozen) return 0;
        if (_services.CombatSpec.TryGetTalentSpec(_target, out var talent) && talent != 0) return talent;
        return _services.CombatSpec.GetSubProfession(_target);
    }

    private string ClassLine(int profId, int sub)
    {
        if (profId <= 0) return "";
        var prof = _services.GameData.Combat.GetProfession(profId);
        var name = prof is { Name: { Length: > 0 } n } ? n : _loc.TFormat("ei.header.classFallback", profId);
        if (sub != 0 && ProfessionSpecs.Name(sub) is { Length: > 0 } spec) return name + " · " + spec;
        return name;
    }

    // Resolve the target's talent-school id (from their cast-resolved spec, ICombatSpec) for the v2 school-lib
    // gear-attr lookup. 0 when the spec is unknown — the gear popover then says so instead of showing wrong ranges.
    private int ResolveTalentSchool()
        => ProfessionSpecs.TalentSchool(_services.CombatSpec.GetSubProfession(_target));

    // Read a scalar attr from the snapshotted target map, falling back to a self value.
    private long AttrOr(int id, long fallback)
        => _targetAttrs.TryGetValue(id, out var v) ? v : fallback;

    // ---- self-stat subscription (IPlayerStats only delivers subscribed attr ids) ----

    // Every id the Overview reads for SELF (IPlayerStats samples only subscribed ids).
    private static System.Collections.Generic.IEnumerable<int> SelfPolledIds()
    {
        foreach (var p in StatPanelDefs)
            foreach (var (id, ratingId) in p.Rows) { yield return id; if (ratingId != 0) yield return ratingId; }
        foreach (var (_, baseId) in ElementalBands)
            for (var e = 0; e < ElementCount; e++) yield return baseId + e * 10;
    }

    // Subscribe ONLY the ids the broadcast snapshot lacks — the probe reflects over every subscribed id each game tick,
    // and for self most Overview ids already arrive on the wire (perf review: the full ~130-id set cost ~7.8k reflection
    // invokes/s). Tracked in a set so unsubscribe releases exactly what was taken even if the snapshot changes meanwhile.
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
