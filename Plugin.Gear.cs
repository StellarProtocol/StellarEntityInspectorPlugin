using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Gear tab — data. Cards are grouped by the item's EquipPart code from the equip table (200=Weapon … 210=Charm — table
// truth, confirmed live 2026-06-12; the broadcast slot int is only a fallback). Identity comes from the broadcast
// equipment list for ANY entity; for SELF the cards prefer the decoded GearInstance list (uuid → actual rolls in the
// popover, refine, breakthrough stage). Far players fall back to the cached social reply (slot + id only).
// Breakthrough (approved prototype; KB Gear-Breakthrough-Visibility.md): the stage is NOT visible for other players, so
//   • others: "GS <base>", or "GS 260+" + a small lock tag when the piece HAS breakthrough stages;
//   • self:   the real upgraded GS (EquipBreakThroughTable.EquipGs for the stage) + " · R<refine>" + a stage bar.
// Stage GS values come from the plugin-side Lua read (Plugin.LuaTables.cs), once per equip id. Snapshotted at 10 Hz into a
// fixed card array, re-formatted only when the gear (or a Lua reply) changed; the element Funcs only read it.
public sealed partial class Plugin
{
    private const int WeaponPart = 200;
    private const int GearCardCount = 11;   // [0] = weapon, then parts 201..210

    private struct GearCard
    {
        public int ItemId; public long Uuid; public bool Occupied;
        public string Slot; public string Name; public string Bottom;
        public ColorRgba NameCol;
        public bool Lock;                    // others: the piece has breakthrough stages we can't see
        public int Stage, MaxStage;          // self: stage bar (MaxStage 0 = no bar)
        public int BaseGs, Refine;
        public int[]? StageGs;               // upgraded GS per stage (null = unknown)
    }

    private enum GearSource : byte { None, Self, Live, Profile }

    private readonly GearCard[] _gearCards = new GearCard[GearCardCount];
    private readonly UvRect[] _gearUv = new UvRect[GearCardCount];
    private readonly List<(int Part, int ItemId, long Uuid, int Refine, int Stage)> _gearScratch = new(GearCardCount);
    private GearSource _gearSource;
    private int _gearSig = int.MinValue;     // dirty check: hash of the collected items + Lua version + language
    private object? _gearLang;

    // THIS game's quality tiers: 1=green … 5=red with 4=GOLD (in-world verified 2026-06-12: the gold-bannered earrings
    // carry Quality 4, the purple crown 3 — NOT the classic MMO order). Item art colours, not theme colours.
    private static readonly ColorRgba[] QualityCols =
    {
        new(0.78f, 0.80f, 0.82f, 1f),   // 0 / unknown — neutral
        new(0.45f, 0.80f, 0.45f, 1f),   // 1 green
        new(0.38f, 0.65f, 0.92f, 1f),   // 2 blue
        new(0.72f, 0.50f, 0.92f, 1f),   // 3 purple
        new(0.92f, 0.76f, 0.35f, 1f),   // 4 gold
        new(0.90f, 0.36f, 0.32f, 1f),   // 5 red
    };

    private static ColorRgba QualityColOf(int q)
        => QualityCols[q < 0 ? 0 : q >= QualityCols.Length ? QualityCols.Length - 1 : q];

    private bool _gearHasData;   // any card occupied at last rebuild — gates the far-player note

    private void RebuildGear()
    {
        _gearScratch.Clear();
        _gearSource = CollectGearItems();
        _gearHasData = _gearScratch.Count > 0;

        var lang = _loc.T("ei.tab.gear");
        int sig = (int)_gearSource * 31 + _luaVersion;
        foreach (var g in _gearScratch)
            sig = ((sig * 31 + g.Part) * 31 + g.ItemId) * 31 + g.Refine * 7 + g.Stage;
        if (sig == _gearSig && ReferenceEquals(lang, _gearLang)) return;
        _gearSig = sig; _gearLang = lang;

        for (var i = 0; i < _gearCards.Length; i++) _gearCards[i] = EmptySlotCard(WeaponPart + i);
        foreach (var (part, itemId, uuid, refine, stage) in _gearScratch)
        {
            // Cards sit at FIXED positions (part − 200) so a missing piece renders as a named empty slot instead of
            // shifting later cards (qa review); unknown part codes take the first free cell.
            var idx = part - WeaponPart;
            if (idx < 0 || idx >= _gearCards.Length) idx = FirstFreeGearCell();
            if (idx < 0) continue;
            _gearCards[idx] = MakeGearCard(part, itemId, uuid, refine, stage);
        }
    }

    private GearCard EmptySlotCard(int part) => new()
    {
        Occupied = false,
        Slot = _services.GameData.Equip.GetSlotName(part) ?? "",
        Name = "", Bottom = "",
        NameCol = QualityColOf(0),
    };

    private int FirstFreeGearCell()
    {
        for (var i = 1; i < _gearCards.Length; i++)
            if (!_gearCards[i].Occupied) return i;
        return -1;
    }

    // Self prefers the decoded instance list (uuids, refine, breakthrough stage); broadcast is the fallback (and the
    // primary source for others). Far players with no broadcast fall back to the cached social reply's equip_data
    // (slot+config-id only — populated on ID-card open). GearInstance.Slot is already the EquipPart code.
    private GearSource CollectGearItems()
    {
        if (IsSelf && !IsFrozen)
        {
            foreach (var g in _services.Inventory.GetSelfGear())
                _gearScratch.Add((PartOf(g.ConfigId, g.Slot), g.ConfigId, g.ItemUuid, g.RefineLevel, g.BreakThroughTime));
            if (_gearScratch.Count > 0) return GearSource.Self;
        }
        foreach (var e in TargetGear())
            _gearScratch.Add((PartOf(e.ItemId, int.MaxValue), e.ItemId, 0L, 0, 0));
        if (_gearScratch.Count > 0) return IsSelf ? GearSource.Self : GearSource.Live;
        if (_socialSnap is { Gear.Count: > 0 } s)
        {
            foreach (var g in s.Gear)
                _gearScratch.Add((PartOf(g.EquipId, int.MaxValue), g.EquipId, 0L, 0, 0));
            return GearSource.Profile;
        }
        return GearSource.None;
    }

    private int PartOf(int itemId, int fallbackPart)
        => _services.GameData.Equip.GetEquipRow(itemId) is { } row ? row.EquipPart : fallbackPart;

    private GearCard MakeGearCard(int part, int itemId, long uuid, int refine, int stage)
    {
        var item = _services.GameData.Inventory.GetItem(itemId);
        var row  = _services.GameData.Equip.GetEquipRow(itemId);
        var stageGs = BreakthroughGs(itemId);
        var card = new GearCard
        {
            ItemId = itemId, Uuid = uuid, Occupied = true,
            Slot = _services.GameData.Equip.GetSlotName(part) ?? (part == WeaponPart ? _loc.T("ei.gear.weapon") : "—"),
            Name = item is { Name.Length: > 0 } it ? it.Name : "<" + itemId.ToString(CultureInfo.InvariantCulture) + ">",
            NameCol = QualityColOf(item?.Quality ?? 0),
            BaseGs = row?.Gs ?? 0, Refine = refine, StageGs = stageGs,
        };
        bool self = _gearSource == GearSource.Self;
        if (self && stageGs is { Length: > 0 })
        {
            card.MaxStage = stageGs.Length;
            card.Stage = stage < 0 ? 0 : stage > stageGs.Length ? stageGs.Length : stage;
        }
        card.Lock = !self && stageGs is { Length: > 0 };
        card.Bottom = CardBottomLine(card, self);
        return card;
    }

    // The GS the card shows: self = the upgraded GS for the stage; others = the base GS (+ "+" when it can be upgraded).
    private static int ShownGs(in GearCard c)
        => c.Stage > 0 && c.StageGs is { } s && c.Stage <= s.Length ? s[c.Stage - 1] : c.BaseGs;

    private string CardBottomLine(in GearCard c, bool self)
    {
        var ci = CultureInfo.InvariantCulture;
        int gs = ShownGs(c);
        if (gs <= 0) return c.Refine > 0 ? _loc.TFormat("ei.gear.refineOnly", c.Refine.ToString(ci)) : "";
        var text = _loc.TFormat(c.Lock ? "ei.gear.gsPlus" : "ei.gear.gs", gs.ToString(ci));
        if (self && c.Refine > 0) text += " · R" + c.Refine.ToString(ci);
        return text;
    }

    private object? GearIcon(int idx)
    {
        var c = _gearCards[idx];
        if (!c.Occupied || c.ItemId <= 0) return null;
        return _services.GameAssets.LoadItemIcon(c.ItemId, out _gearUv[idx]);
    }

    // Caption tag for the gear panel: where the list came from. Self needs none (always available).
    private TagKind GearTag() => _gearSource switch
    {
        GearSource.Profile => TagKind.Profile,
        GearSource.Live    => TagKind.Nearby,
        GearSource.None    => IsSelf || IsFrozen ? TagKind.None : TagKind.Nearby,
        _                  => TagKind.None,
    };
}
