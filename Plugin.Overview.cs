using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Overview tab — data. Curated grouped stats as SectionPanels (approved prototype): IDENTITY, CORE, PRIMARY, SECONDARY,
// OFFENSE, DEFENSE, HEALING & SHIELDS, UTILITY, ELEMENTAL (hidden when all zero). Fixed panels are FIXED id lists; names
// + value format resolve via IGameData.Combat.GetAttribute. Percent stats pair with their flat rating id ("31.20% (5,880)",
// rating muted and smaller). The old header strip's ATK/Crit/Haste/Luck now lives in CORE (Crit/Haste/Luck moved there
// from SECONDARY so they show once).
// Every row's availability is decided LIVE (no hard-coded list): value present → value; missing + far → faint "—" and a
// Nearby tag on the panel caption; missing + nearby (not self) → an inline "Not shared" tag + "N not shared" caption hint.
// Self: an absent id means zero (everything self-relevant is broadcast or polled).
// Rebuilt at 10 Hz into per-row cached strings that are re-formatted ONLY when the row's raw value / state changed — the
// element Funcs are pure array reads and a steady sheet allocates nothing.
public sealed partial class Plugin
{
    private static readonly (string Title, (int Id, int RatingId)[] Rows)[] StatPanelDefs =
    {
        ("ei.section.core",      new[] { (11330, 0), (11340, 0), (11320, 0), (11350, 0), (11360, 0),
                                         (11710, 11110), (11930, 11120), (11780, 11130) }),
        ("ei.section.primary",   new[] { (11010, 0), (11020, 0), (11030, 0), (11040, 0) }),
        ("ei.section.secondary", new[] { (11940, 11140), (11950, 11150), (11970, 11170) }),
        ("ei.section.offense",   new[] { (12510, 0), (12530, 0), (12550, 0), (12570, 0), (12590, 0), (12610, 0),
                                         (12630, 0), (11370, 0), (11380, 0), (11390, 0), (11400, 0), (11720, 0),
                                         (11730, 0), (11740, 0), (11760, 0), (11750, 0), (11910, 0), (11830, 0) }),
        ("ei.section.defense",   new[] { (12520, 0), (12540, 0), (12560, 0), (12580, 0), (12600, 0), (12620, 0),
                                         (12640, 0), (12660, 0), (12680, 0), (13400, 0) }),
        ("ei.section.healingShields", new[] { (11790, 0), (11800, 0), (11810, 0), (11820, 0), (12720, 0), (12740, 0) }),
        ("ei.section.utility",   new[] { (10200, 0), (10210, 0), (10220, 0), (10240, 0), (10250, 0), (10260, 0),
                                         (10230, 0), (10270, 0), (20010, 0), (20020, 0) }),
    };

    // Elemental bands (zero-suppressed; the whole panel is hidden when nothing is non-zero).
    private static readonly (string Suffix, int BaseId)[] ElementalBands =
    {
        ("Attack", 11500), ("Power", 13000), ("Bonus", 13100), ("Resist", 13200), ("Reduction", 13310),
    };
    private const int ElementCount = 9;   // All/Fire/Ice/Forest/Thunder/Wind/Rock/Light/Dark, ×10 apart
    private const int MaxIdentityRows = 5, MaxElementalRows = 45;
    private const int AttrMaxHp = 11320;

    // Overview stats the game DOES send for other players (FightAttr rows flagged IsSyncAoi, plus Attack SPD / Cast Speed,
    // confirmed live) — see Knowledge Base\Other-Player-Stat-Visibility.md. Far: these show a faint "—" + the panel's
    // Nearby tag (they'd arrive in range); every other id shows "Not shared" right away (it never arrives).
    private static readonly System.Collections.Generic.HashSet<int> SharedForOthers = new() { 11320, 13400, 11720, 11730 };

    private enum RowState : byte { Value, Far, NotShared }

    // One SectionPanel's rows. Strings are kept per row and replaced only when an input changes.
    private sealed class OvPanel
    {
        public readonly string TitleKey;
        public readonly (int Id, int RatingId)[] Defs;   // empty for the dynamic panels (identity / elemental)
        public readonly string[] Labels, Values;
        public readonly RowState[] States;
        private readonly long[] _raw, _rating;
        private readonly int[] _ids;
        public int Count;
        public TagKind CaptionTag;
        public string Hint = "";
        private int _hintCount = -1;

        public OvPanel(string titleKey, (int, int)[] defs, int capacity)
        {
            TitleKey = titleKey; Defs = defs;
            Labels = new string[capacity]; Values = new string[capacity]; States = new RowState[capacity];
            _raw = new long[capacity]; _rating = new long[capacity]; _ids = new int[capacity];
            for (int i = 0; i < capacity; i++) { Labels[i] = ""; Values[i] = ""; _raw[i] = long.MinValue; }
        }

        public int Capacity => Labels.Length;

        // True when row i must be (re)formatted for these inputs; records them.
        public bool Changed(int i, int id, long raw, long rating, RowState st, bool force)
        {
            if (!force && _ids[i] == id && _raw[i] == raw && _rating[i] == rating && States[i] == st) return false;
            _ids[i] = id; _raw[i] = raw; _rating[i] = rating; States[i] = st;
            return true;
        }

        public void SetHint(int notShared, ILocalization loc, bool force)
        {
            if (notShared == _hintCount && !force) return;
            _hintCount = notShared;
            Hint = notShared > 0 ? loc.TFormat("ei.tag.notSharedCount", notShared) : "";
        }

        public void Clear() { Count = 0; CaptionTag = TagKind.None; }
    }

    private readonly OvPanel _identityPanel = new("ei.section.identity", System.Array.Empty<(int, int)>(), MaxIdentityRows);
    private readonly OvPanel _elementalPanel = new("ei.section.elemental", System.Array.Empty<(int, int)>(), MaxElementalRows);
    private OvPanel[] _statPanels = null!;   // one per StatPanelDefs entry (built in InitOverview)

    private string _ratingHex = "";          // muted colour as #RRGGBB for the rating's rich-text tag (theme-live)
    private ColorRgba _ratingHexOf;
    private object? _ovLang;                 // language marker: a switch re-formats every row once
    private SocialSnapshot? _idLastSnap;
    private int _idLastLuaVer = -1;

    private void InitOverview()
    {
        _statPanels = new OvPanel[StatPanelDefs.Length];
        for (int i = 0; i < StatPanelDefs.Length; i++)
            _statPanels[i] = new OvPanel(StatPanelDefs[i].Title, StatPanelDefs[i].Rows, StatPanelDefs[i].Rows.Length);
    }

    private void ClearOverview()
    {
        _identityPanel.Clear(); _elementalPanel.Clear(); _idLastSnap = null;
        foreach (var p in _statPanels) p.Clear();
    }

    private void RebuildOverview()
    {
        var lang = _loc.T("ei.section.core");
        bool force = !ReferenceEquals(lang, _ovLang);
        _ovLang = lang;
        var muted = MutedColor();
        if (!muted.Equals(_ratingHexOf) || _ratingHex.Length == 0) { _ratingHexOf = muted; _ratingHex = HexOf(muted); force = true; }

        RebuildIdentity(force);
        foreach (var p in _statPanels) RebuildStatPanel(p, force);
        RebuildElemental(force);
    }

    private void RebuildStatPanel(OvPanel p, bool force)
    {
        int notShared = 0;
        bool far = false;
        for (int i = 0; i < p.Defs.Length; i++)
        {
            var (id, ratingId) = p.Defs[i];
            RowState st; long raw = 0, rating = long.MinValue;
            bool shared = SharedForOthers.Contains(id);
            if (id == AttrMaxHp && TargetVitals() is { IsKnown: true, MaxHp: > 0 } v) { st = RowState.Value; raw = v.MaxHp; }
            else if (IsSelf) { TryAttr(id, out raw); st = RowState.Value; }
            // Other player: a live value wins (even for an id outside the set); an unshared id the wire sends as 0 is
            // "not shared", not a real zero; missing → Nearby-pending only for ids that would arrive in range.
            else if (TryAttr(id, out raw) && (raw != 0 || shared)) st = RowState.Value;
            else st = _isRemote && shared ? RowState.Far : RowState.NotShared;
            if (st == RowState.Value && ratingId != 0 && TryAttr(ratingId, out var r)) rating = r;
            if (st == RowState.Far) far = true; else if (st == RowState.NotShared) notShared++;

            if (p.Labels[i].Length == 0 || force) p.Labels[i] = AttrLabel(id);
            if (!p.Changed(i, id, raw, rating, st, force)) continue;
            p.Values[i] = st switch
            {
                RowState.Far       => "—",
                RowState.NotShared => "",
                _ => FormatAttr(AttrNumType(id), raw) + (rating == long.MinValue ? "" : RatingSuffix(rating)),
            };
        }
        p.Count = p.Defs.Length;
        p.CaptionTag = far ? TagKind.Nearby : TagKind.None;
        p.SetHint(notShared, _loc, force);
    }

    // " (5,880)" muted + one size smaller, via rich text on the plain value label.
    private string RatingSuffix(long rating)
        => " <color=#" + _ratingHex + "><size=12>(" + rating.ToString("N0", CultureInfo.InvariantCulture) + ")</size></color>";

    // Guild / Party / Title / Master Score / Collections from the cached social reply (the game's ID card fetches mask 0
    // = ALL sections and our wire tap caches that reply — so these fill for any carded player at any distance). Value-
    // gated: thin-mask replies and guildless/solo/hidden players render no row. Rebuilt only when the reply object or a
    // Lua title read changed.
    private void RebuildIdentity(bool force)
    {
        var snap = _socialSnap;
        if (!force && ReferenceEquals(snap, _idLastSnap) && _idLastLuaVer == _luaVersion) return;
        _idLastSnap = snap; _idLastLuaVer = _luaVersion;
        var p = _identityPanel;
        p.Count = 0;
        p.CaptionTag = TagKind.None;
        if (snap is not { Identity: var id }) return;
        var ci = CultureInfo.InvariantCulture;
        if (id.Guild.Length > 0) AddIdentityRow("ei.field.guild", id.Guild);
        if (HasParty(snap.Party) && snap.Party is { } party)
        {
            int num = party.TeamNum > 0 ? party.TeamNum : party.Members.Count;
            var value = _loc.TFormat("ei.field.partyCount", num, PartyMax(party));
            foreach (var m in party.Members)
                if (m.CharId == party.LeaderId && m.Name.Length > 0)
                { value = _loc.TFormat("ei.field.partyWithLeader", num, PartyMax(party), m.Name); break; }
            AddIdentityRow("ei.field.party", value);
        }
        else if (id.PartySize > 0) AddIdentityRow("ei.field.party", _loc.TFormat("ei.field.members", id.PartySize));
        if (TitleName(id.TitleId) is { Length: > 0 } title) AddIdentityRow("ei.field.title", title);
        if (id.MasterScore > 0) AddIdentityRow("ei.field.masterScore", id.MasterScore.ToString("N0", ci));
        if (id.FashionCollect > 0 || id.RideCollect > 0 || id.WeaponSkinCollect > 0)
            AddIdentityRow("ei.field.collections", _loc.TFormat("ei.field.collectionsValue",
                id.FashionCollect.ToString("N0", ci), id.RideCollect.ToString("N0", ci), id.WeaponSkinCollect.ToString("N0", ci)));
        if (p.Count > 0) p.CaptionTag = TagKind.Profile;
    }

    private void AddIdentityRow(string labelKey, string value)
    {
        var p = _identityPanel;
        if (p.Count >= p.Capacity) return;
        p.Labels[p.Count] = _loc.T(labelKey); p.Values[p.Count] = value; p.States[p.Count] = RowState.Value;
        p.Count++;
    }

    private void RebuildElemental(bool force)
    {
        var p = _elementalPanel;
        int n = 0;
        foreach (var (suffix, baseId) in ElementalBands)
            for (var e = 0; e < ElementCount && n < p.Capacity; e++)
            {
                var id = baseId + e * 10;
                if (!TryAttr(id, out var raw) || raw == 0) continue;
                if (p.Changed(n, id, raw, 0, RowState.Value, force))
                {
                    p.Labels[n] = AttrLabel(id);
                    p.Values[n] = FormatAttr(AttrNumType(id), raw);
                }
                n++;
            }
        p.Count = n;
    }

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
}
