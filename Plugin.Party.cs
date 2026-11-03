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
    private readonly int[] _ptProf = new int[MaxPartyRows];
    private readonly string[] _ptName = new string[MaxPartyRows];
    private readonly string[] _ptLevel = new string[MaxPartyRows];
    private readonly string[] _ptMap = new string[MaxPartyRows];
    private readonly bool[] _ptOnline = new bool[MaxPartyRows];
    private readonly bool[] _ptLeader = new bool[MaxPartyRows];
    private readonly bool[] _ptViewing = new bool[MaxPartyRows];
    private readonly UvRect[] _ptUv = new UvRect[MaxPartyRows];

    // Dirty check + sort scratch (the wire map order is random on every reply → sort client-side).
    private SocialParty? _ptLastParty;
    private EntityId _ptLastTarget = EntityId.None;
    private readonly List<SocialPartyMember> _ptSorted = new(MaxPartyRows);
    private long _ptSortLeader;
    private Comparison<SocialPartyMember>? _ptCompare;

    private static readonly ColorRgba PartyOnline = new(0x5f / 255f, 0xd3 / 255f, 0x8d / 255f, 1f);   // #5fd38d
    private static readonly ColorRgba PartyTeal   = new(0.40f, 0.82f, 0.80f, 1f);                     // "Viewing" chip

    private HudElement BuildPartyBody()
    {
        var slots = new HudElement[MaxPartyRows];
        for (var i = 0; i < MaxPartyRows; i++)
        {
            _ptUv[i] = new UvRect(0f, 0f, 1f, 1f);   // full rect until the slot's icon resolves (as the header's _profUv)
            slots[i] = BuildPartyRow(i);
        }
        return new ColumnElement(new HudElement[]
        {
            // Same header look as the Overview sections (BuildSectionRow's header variant): bold gold + rule line.
            new SpacerElement(Height: 8f),
            new TextElement(() => _loc.T("ei.tab.party"), AccentCol, Emphasis: true),
            new SeparatorElement(),
            new ConditionalElement(() => _ptState != PartyState.Ok, new ColumnElement(new HudElement[]
            {
                new SpacerElement(Height: 24f),
                new TextElement(PartyStateLine, MutedCol, Align: TextAlign.Center),
            })),
            new ConditionalElement(() => _ptState == PartyState.Ok, new ColumnElement(new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new TextElement(() => _ptSummary, MutedCol, NoWrap: true),
                    new SpacerElement(),   // flexible: pushes the hint to the right edge
                    new TextElement(() => _loc.T("ei.party.clickHint"), MutedCol, NoWrap: true),
                }, Gap: 6f),
                new ScrollElement(new ListElement(() => _ptCount, slots), 360f),
            }, Gap: 6f)),
        }, Gap: 2f);
    }

    // One member row: a raised tile (Bubble OUTSIDE the Selectable — the hub-card pattern, so the hover wash sits on
    // the fill) → [prof icon][name + Leader/Viewing chips][Lv][status dot][map]. Level, dot and map are fixed-width
    // cells so nothing shifts between online/offline or chip states. The 22 px strut + the Selectable's 4+4 = 30 px.
    // The viewed member's tile gets a gold 1 px outline (overlaid, never resizes the tile).
    private HudElement BuildPartyRow(int idx) => new ColumnElement(new HudElement[]
    {
        new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, 22f),
                new GameTextureElement(() => PartyProfIcon(idx), 18, 18, () => _ptUv[idx]),
                new CellElement(new RowElement(new HudElement[]
                {
                    new TextElement(() => _ptName[idx] ?? "", NoWrap: true),
                    new ConditionalElement(() => _ptLeader[idx], PartyChip(() => _loc.T("ei.party.leader"), AccentGold)),
                    new ConditionalElement(() => _ptViewing[idx], PartyChip(() => _loc.T("ei.party.viewing"), PartyTeal)),
                    new SpacerElement(),
                }, Gap: 6f), Weight: 1f),
                new CellElement(new TextElement(() => _ptLevel[idx] ?? "", MutedCol, NoWrap: true), Width: 56f),
                new CellElement(new TextElement(() => "●", () => _ptOnline[idx] ? PartyOnline : MutedCol(),
                    Align: TextAlign.Center, NoWrap: true, FontSize: 10) { Bold = true }, Width: 14f),
                new CellElement(new TextElement(() => _ptMap[idx] ?? "", MutedCol, NoWrap: true), Width: 140f),
            }, Gap: 8f), () => OnPartyRowClick(idx)))
        {
            FillColorFn = PartyTileColor, CornerRadius = 4f, PaddingX = 0f, PaddingY = 0f,
            OutlineColorFn = () => _ptViewing[idx] ? AccentCol() : null,
        },
        new SpacerElement(Height: 3f),
    }, Gap: 0f);

    // Inline status chip: bold coloured text on a 22% pill of the same colour (house StatusChip "on" look). It hugs
    // its label (a Bubble in a Row) because the translations differ a lot in length; the strut pins the height.
    private static HudElement PartyChip(Func<string> label, ColorRgba color) => new BubbleElement(new RowElement(
        new HudElement[]
        {
            new SpacerElement(0f, 18f),
            new TextElement(label, () => color, NoWrap: true, FontSize: 12) { Bold = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    { FillColor = color with { A = 0.22f }, PaddingX = 6f, PaddingY = 1f };

    private static readonly ColorRgba AccentGold = new(0.79f, 0.66f, 0.36f, 1f);   // = AccentCol(), as a static for chips

    // Raised tile = panel (menu background darkened 20%) + 10% menu text — live from the theme (Stellar Design
    // Language "Tile"), so it is lighter on dark themes and darker on Light. Pure struct math, no allocation.
    private ColorRgba? PartyTileColor()
    {
        var m = _services.Theme.Colors;
        var panel = Lerp(m.MenuBackground, new ColorRgba(0f, 0f, 0f, 1f), 0.20f);
        return Lerp(panel, m.MenuText, 0.10f);
    }

    private static ColorRgba Lerp(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    private object? PartyProfIcon(int idx)
    {
        if (idx >= _ptCount || _ptProf[idx] <= 0) return null;
        return _services.GameAssets.LoadProfessionIcon(_ptProf[idx], out _ptUv[idx]);
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
            _ptProf[i] = m.ProfessionId;
            _ptName[i] = m.Name.Length > 0 ? m.Name : "#" + m.CharId.ToString(CultureInfo.InvariantCulture);
            _ptLevel[i] = m.Level > 0 ? _loc.TFormat("ei.gd.lv", m.Level) : "";
            _ptMap[i] = online ? SceneName(m.SceneId) : _loc.T("ei.party.offline");
            _ptOnline[i] = online;
            _ptLeader[i] = m.CharId == party.LeaderId;
            _ptViewing[i] = m.CharId == viewing;
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
