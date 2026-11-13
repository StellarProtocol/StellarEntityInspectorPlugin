using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Party tab — the inspected player's party roster, from the cached social reply's SocialSnapshot.Party
// (SocialData.team_data, field 12). The server sends the FULL roster even for a party we're not in, but only in
// full-mask (mask 0) replies — so the tab asks for one itself (Plugin.Location.Refresh.cs shares the per-target
// request floor). See Social-Location-Probe.md "Party roster of ANOTHER player".
// Rows are pooled (MaxPartyRows, a 20-cap raid) and rebuilt on the 10 Hz snapshot tick into flat per-slot arrays,
// so the element Funcs are pure cache reads. Rebuild is skipped while the Party record + target are unchanged (the
// cache swaps in a new record per reply). Clicking a row re-targets the inspector on that member (tab stays Party).
// No activity name in the summary: TeamTargetTable isn't exposed by the framework's IGameData (no hardcoded names).
public sealed partial class Plugin
{
    private const int MaxPartyRows = 20;   // team_member_type 1 = Twenty

    private enum PartyState { Loading, Solo, Frozen, Ok }
    private PartyState _ptState;
    private int _ptCount;
    private string _ptSummary = "";
    private readonly long[] _ptCharId = new long[MaxPartyRows];
    private readonly string[] _ptName = new string[MaxPartyRows];
    private readonly string[] _ptMeta = new string[MaxPartyRows];   // "Lv 60 · 61,355" / "Offline" / "Viewing"
    private readonly bool[] _ptOnline = new bool[MaxPartyRows];
    private readonly bool[] _ptLeader = new bool[MaxPartyRows];
    private readonly bool[] _ptViewing = new bool[MaxPartyRows];

    // Dirty check + sort scratch (the wire map order is random on every reply → sort client-side).
    private SocialParty? _ptLastParty;
    private EntityId _ptLastTarget = EntityId.None;
    private readonly List<SocialPartyMember> _ptSorted = new(MaxPartyRows);
    private long _ptSortLeader;
    private Comparison<SocialPartyMember>? _ptCompare;

    private const float PartyRowH = 26f, PartyRingPad = 2f;

    // One PARTY panel (approved prototype): hint "click a member to inspect", Profile tag (the roster comes from their
    // profile reply), the rows, then the "n / max" footer. Non-Ok states show one muted centred line instead.
    private HudElement BuildPartyBody()
    {
        var slots = new HudElement[MaxPartyRows];
        for (var i = 0; i < MaxPartyRows; i++) slots[i] = BuildPartyRow(i);
        return CaptionedPanel(() => _loc.T("ei.tab.party"), () => _ptState == PartyState.Ok ? _loc.T("ei.party.clickHint") : "",
            DataTag(() => _ptState == PartyState.Ok && !IsSelf ? TagKind.Profile : TagKind.None, TagCaptionH),
            new HudElement[]
            {
                new ConditionalElement(() => _ptState != PartyState.Ok, new ColumnElement(new HudElement[]
                {
                    new SpacerElement(0f, 24f),
                    new TextElement(PartyStateLine, Color: () => MutedColor(), Align: TextAlign.Center),
                }, Gap: 0.01f)),
                new ConditionalElement(() => _ptState == PartyState.Ok,
                    new ScrollElement(new ListElement(() => _ptCount, slots) { Gap = 0.01f }, 300f), Fill: true),
                StateLine(() => _ptState == PartyState.Ok ? _ptSummary : "", () => MutedColor()),
            });
    }

    // One member row (26 px + 2 gap): an outer bubble (accent on the viewed member, else the tile colour) around the
    // inner tile with 2 px padding = a 2 px accent ring that never resizes the row (BubbleElement's own outline is
    // 1 px). Inside: online dot · bold name · Leader chip · muted "Lv · score" / "Offline" / "Viewing". The hover wash
    // remembers WHICH member it was set on, so a row that rebinds under a still pointer doesn't inherit it.
    // Status dot = PLAIN Text "●" (the bold/TMP path's Noto Sans has no U+25CF → drew nothing).
    private HudElement BuildPartyRow(int idx)
    {
        var hover = new HoverFlag();
        long hoverId = 0;
        bool Hot() => hover.On && hoverId == _ptCharId[idx] && !_ptViewing[idx];
        ColorRgba Inner() => Hot() ? HoverFill(TileColor()) : TileColor();
        return new ColumnElement(new HudElement[]
        {
            new BubbleElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(6f, PartyRowH - 2f * PartyRingPad),
                    new TextElement(() => "●", Color: () => _ptOnline[idx] ? SuccessColor() : MutedColor(), NoWrap: true),
                    new SpacerElement(6f, 0f),
                    new TextElement(() => _ptName[idx] ?? "", Color: () => _ptOnline[idx] ? TextColor() : MutedColor(),
                        NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true },
                    new SpacerElement(6f, 0f),
                    new ConditionalElement(() => _ptLeader[idx], LeaderChip()),
                    new SpacerElement(),
                    new TextElement(() => _ptMeta[idx] ?? "", Color: () => MutedColor(), NoWrap: true),
                    new SpacerElement(8f, 0f),
                }, Gap: 0.01f), () => OnPartyRowClick(idx))
                { Bare = true, OnHover = over => { hover.On = over; hoverId = _ptCharId[idx]; } })
            {
                FillColorFn = () => Fade(Inner()), CornerRadius = TileRadius - PartyRingPad, PaddingX = 0f, PaddingY = 0f,
            })
            {
                FillColorFn = () => Fade(_ptViewing[idx] ? AccentColor() : Inner()),
                CornerRadius = TileRadius, PaddingX = PartyRingPad, PaddingY = PartyRingPad,
            },
            new SpacerElement(0f, 2f),
        }, Gap: 0.01f);
    }

    // Leader chip: bold amber on a 22 % amber wash (house status chip), 18 px. MaxWidth: the chip sits in a Conditional
    // directly in a Row (the 100 px bubble-cap gotcha).
    private HudElement LeaderChip()
    {
        var text = new ContrastCache();
        ColorRgba Fill() => Over(CautionColor() with { A = ChipWashAlpha }, TileColor());
        return new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(7f, 18f),
            DropBold(new TextElement(() => _loc.T("ei.party.leader"), Color: () => text.Get(CautionColor(), Fill(), PillMinContrast),
                NoWrap: true, FontSize: 11) { Bold = true }),
            new SpacerElement(7f, 18f),
        }, Gap: 0.01f))
        { FillColorFn = () => Fade(Fill()), CornerRadius = RoundRadius(18f), PaddingX = 0f, PaddingY = 0f, MaxWidth = 140f };
    }

    private string PartyStateLine() => _ptState switch
    {
        PartyState.Loading => _loc.T("ei.party.loading"),
        PartyState.Solo    => _loc.T("ei.party.solo"),
        PartyState.Frozen  => _loc.T("ei.party.frozen"),
        _                  => "",
    };

    private static int PartyMax(SocialParty p) => p.MemberType == 0 ? 5 : 20;   // ETeamMemberType Five / Twenty

    // A party worth showing: a real team id with at least one decoded member (solo = section present but zeroed).
    private static bool HasParty(SocialParty? p) => p is { TeamId: not 0, Members.Count: > 0 };

    private void RebuildParty()
    {
        if (IsFrozen) { SetPartyState(PartyState.Frozen); return; }
        var party = _socialSnap?.Party;
        if (party is null)
        {
            SetPartyState(PartyState.Loading);
            TickPartyRefresh();   // only a full-mask reply carries team_data — ask for one (floored per target)
            return;
        }
        if (!HasParty(party)) { SetPartyState(PartyState.Solo); return; }
        if (_ptState == PartyState.Ok && ReferenceEquals(party, _ptLastParty) && _target == _ptLastTarget) return;
        _ptLastParty = party; _ptLastTarget = _target;

        _ptSorted.Clear();
        _ptSorted.AddRange(party.Members);
        _ptSortLeader = party.LeaderId;
        _ptSorted.Sort(_ptCompare ??= ComparePartyMembers);

        long viewing = _target.Value >> 16;   // the inspected player's char id (EntityId = charId << 16 | 640)
        int n = Math.Min(_ptSorted.Count, MaxPartyRows);
        for (int i = 0; i < n; i++)
        {
            var m = _ptSorted[i];
            bool online = m.OnlineStatus == 1;
            _ptCharId[i] = m.CharId;
            _ptName[i] = m.Name.Length > 0 ? m.Name : "#" + m.CharId.ToString(CultureInfo.InvariantCulture);
            _ptOnline[i] = online;
            _ptLeader[i] = m.CharId == party.LeaderId;
            _ptViewing[i] = m.CharId == viewing;
            _ptMeta[i] = _ptViewing[i] ? _loc.T("ei.party.viewing")
                       : !online ? _loc.T("ei.party.offline")
                       : PartyMemberMeta(m);
        }
        _ptCount = n;
        _ptSummary = _loc.TFormat("ei.party.summary", party.TeamNum > 0 ? party.TeamNum : party.Members.Count, PartyMax(party));
        _ptState = PartyState.Ok;
    }

    private void SetPartyState(PartyState s)
    {
        _ptState = s;
        _ptCount = 0;
        _ptLastParty = null;   // the next Ok rebuild must refill the slots
    }

    // Leader first, then join order (enter_time ascending), then name — stable across replies.
    private int ComparePartyMembers(SocialPartyMember a, SocialPartyMember b)
    {
        bool la = a.CharId == _ptSortLeader, lb = b.CharId == _ptSortLeader;
        if (la != lb) return la ? -1 : 1;
        int c = a.EnterTime.CompareTo(b.EnterTime);
        return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
    }

    // "Lv 60 · 61,355" (each part only when known).
    private string PartyMemberMeta(SocialPartyMember m)
    {
        var lv = m.Level > 0 ? _loc.TFormat("ei.gd.lv", m.Level) : "";
        if (m.FightPoint <= 0) return lv;
        var fp = m.FightPoint.ToString("N0", CultureInfo.InvariantCulture);
        return lv.Length > 0 ? lv + " · " + fp : fp;
    }

    private string SceneName(int sceneId)
    {
        if (sceneId <= 0) return "—";
        return _services.GameData.World.GetScene(sceneId) is { Name.Length: > 0 } si
            ? si.Name : "#" + sceneId.ToString(CultureInfo.InvariantCulture);
    }

    // Re-target the inspector on a member (same EntityId shape the profile-card path builds: charId << 16 | 640) and
    // ask for their full social reply right away, so a FAR member's roster/location loads without opening their card.
    // Open() keeps _tab, so the Party tab stays selected. The already-viewed row does nothing.
    private void OnPartyRowClick(int idx)
    {
        if (idx >= _ptCount || _ptViewing[idx] || _ptCharId[idx] <= 0) return;
        Open(new EntityId((_ptCharId[idx] << 16) | 640L));
        RequestPartyRefresh();
    }
}
