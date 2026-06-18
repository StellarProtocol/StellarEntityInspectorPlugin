using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Gear tab — game-mirror card grid: featured Weapon card + 4-wide armor row + 6-wide accessory row.
// Cards are grouped by the item's EquipPart code from the equip table (200=Weapon … 210=Charm — table
// truth, confirmed live 2026-06-12; the broadcast slot int is only a fallback). Identity comes from the
// broadcast equipment list for ANY entity; for SELF the cards prefer the decoded GearInstance list and
// carry the instance uuid so the detail popup can show actual rolls. Snapshotted at 10 Hz into a fixed
// card array; element Funcs only read it.
public sealed partial class Plugin
{
    private const int WeaponPart = 200;
    private const int GearGridCards = 10;            // non-weapon cards: armor row 4 + accessory row 6

    private struct GearCard
    {
        public int ItemId; public long Uuid; public bool Occupied;
        public string Slot; public string Name; public string Lv;
        public ColorRgba NameCol;
    }

    private readonly GearCard[] _gearCards = new GearCard[1 + GearGridCards];   // [0] = weapon
    private readonly UvRect[] _gearUv = new UvRect[1 + GearGridCards];
    private readonly List<(int Part, int ItemId, long Uuid, int Refine)> _gearScratch = new(1 + GearGridCards);

    // THIS game's quality tiers: 1=green … 5=red with 4=GOLD (in-world verified 2026-06-12: the
    // gold-bannered earrings carry Quality 4, the purple crown 3 — NOT the classic MMO order).
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

    // Game-mirror cards: slot label (quality-coloured) + icon + Lv — NO item name on the card (the
    // game's own gear screen does the same; names live in the detail popup title). Keeps every cell's
    // content inside its fixed grid bounds — long localized names measurably overflowed (sandbox).
    // Weapon card ABOVE the slot grids (not beside): side-by-side, weapon (~120px) + the 6-wide
    // accessory row (456px) overflowed the pane and clipped the right columns even at full window
    // width — user-flagged in-world 2026-06-13.
    private HudElement BuildGearBody() => new ScrollElement(new ColumnElement(new HudElement[]
    {
        // Far players with NO data at all (no broadcast, no social gear) get the hint instead of a
        // grid of empty slots; once the social fallback fills cards the hint vanishes.
        new ConditionalElement(() => _isRemote && !_gearHasData, new TextElement(
            () => "Equipment appears once you've been near this player.", MutedCol)),
        BuildWeaponCard(),
        BuildGearGrid(first: 1, count: 4, iconPx: 36, cellW: 88f, cellH: 84f),
        BuildGearGrid(first: 5, count: 6, iconPx: 24, cellW: 76f, cellH: 70f),
        new TextElement(() => IsSelf ? "Click a card for full detail (your actual rolls)."
                                     : "Click a card for the item's table detail (their rolls aren't broadcast).",
            MutedCol),
    }, Gap: 8f), 380f);

    // Featured weapon card — horizontal: icon beside slot/name/GS lines (it now spans the pane width,
    // so the old narrow vertical card would float in empty space).
    private HudElement BuildWeaponCard()
        => new SelectableElement(new RowElement(new HudElement[]
        {
            new GameTextureElement(() => GearIcon(0), 64, 48, () => _gearUv[0]),
            new ColumnElement(new HudElement[]
            {
                new TextElement(() => _gearCards[0].Slot, () => _gearCards[0].NameCol),
                new TextElement(() => _gearCards[0].Name, () => _gearCards[0].NameCol),
                new TextElement(() => _gearCards[0].Lv, MutedCol),
            }, Gap: 2f),
            new SpacerElement(),
        }, Gap: 10f), () => OpenGearDetail(0));

    private HudElement BuildGearGrid(int first, int count, int iconPx, float cellW, float cellH)
    {
        var slots = new HudElement[count];
        for (var i = 0; i < count; i++) slots[i] = GearCardElement(first + i, iconPx);
        return new ListElement(() => count, slots, Columns: count, CellWidth: cellW, CellHeight: cellH);
    }

    // The icon box rides between flexible spacers so it centres in the card (column children are
    // left-aligned otherwise — icons sat left of centre, user-flagged in-world 2026-06-13).
    private HudElement GearCardElement(int idx, int iconPx)
        => new SelectableElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => _gearCards[idx].Slot, () => _gearCards[idx].NameCol),
            new RowElement(new HudElement[]
            {
                new SpacerElement(),
                new GameTextureElement(() => GearIcon(idx), iconPx * 4 / 3, iconPx, () => _gearUv[idx]),
                new SpacerElement(),
            }, Gap: 0f),
            new TextElement(() => _gearCards[idx].Lv, MutedCol),
        }, Gap: 2f), () => OpenGearDetail(idx));

    private object? GearIcon(int idx)
    {
        var c = _gearCards[idx];
        if (!c.Occupied || c.ItemId <= 0) return null;
        return _services.GameAssets.LoadItemIcon(c.ItemId, out _gearUv[idx]);
    }

    // Cards sit at FIXED grid positions (part code − 200 → index 0..10) so a missing piece renders
    // as a named empty slot instead of shifting later cards left (qa review). Unknown part codes
    // take the first free cell as a defensive fallback.
    private bool _gearHasData;   // any card occupied at last rebuild — gates the far-player hint

    private void RebuildGear()
    {
        _gearScratch.Clear();
        CollectGearItems();
        _gearHasData = _gearScratch.Count > 0;

        for (var i = 0; i < _gearCards.Length; i++) _gearCards[i] = EmptySlotCard(WeaponPart + i);
        foreach (var (part, itemId, uuid, refine) in _gearScratch)
        {
            var idx = part - WeaponPart;
            if (idx < 0 || idx >= _gearCards.Length) idx = FirstFreeGearCell();
            if (idx < 0) continue;
            _gearCards[idx] = MakeGearCard(part, itemId, uuid, refine);
        }
    }

    private GearCard EmptySlotCard(int part) => new()
    {
        Occupied = false,
        Slot = _services.GameData.Equip.GetSlotName(part) ?? "",
        Name = "", Lv = "—",
        NameCol = QualityColOf(0),
    };

    private int FirstFreeGearCell()
    {
        for (var i = 1; i < _gearCards.Length; i++)
            if (!_gearCards[i].Occupied) return i;
        return -1;
    }

    // Self prefers the decoded instance list (carries uuids for the detail popup); broadcast is the
    // fallback (and the primary source for others). Far players with no broadcast fall back to the
    // cached social reply's equip_data (slot+config-id only, no rolls — populated on ID-card open).
    // GearInstance.Slot is already the EquipPart code.
    private void CollectGearItems()
    {
        if (IsSelf)
        {
            foreach (var g in _services.Inventory.GetSelfGear())
                _gearScratch.Add((PartOf(g.ConfigId, g.Slot), g.ConfigId, g.ItemUuid, g.RefineLevel));
            if (_gearScratch.Count > 0) return;
        }
        foreach (var e in TargetGear())
            _gearScratch.Add((PartOf(e.ItemId, int.MaxValue), e.ItemId, 0L, 0));
        if (_gearScratch.Count == 0 && _socialSnap is { Gear.Count: > 0 } s)
            foreach (var g in s.Gear)
                _gearScratch.Add((PartOf(g.EquipId, int.MaxValue), g.EquipId, 0L, 0));
    }

    private int PartOf(int itemId, int fallbackPart)
        => _services.GameData.Equip.GetEquipRow(itemId) is { } row ? row.EquipPart : fallbackPart;

    private GearCard MakeGearCard(int part, int itemId, long uuid, int refine)
    {
        var item = _services.GameData.Inventory.GetItem(itemId);
        var row  = _services.GameData.Equip.GetEquipRow(itemId);
        return new GearCard
        {
            ItemId = itemId, Uuid = uuid, Occupied = true,
            Slot = _services.GameData.Equip.GetSlotName(part)
                   ?? (part == WeaponPart ? "Weapon" : "—"),
            Name = item is { Name.Length: > 0 } it ? it.Name : $"<{itemId}>",
            Lv   = CardBottomLine(row?.Gs ?? 0, refine),
            NameCol = QualityColOf(item?.Quality ?? 0),
        };
    }

    // Bottom card line: the item's gear level (GS from the equip table — what ZDPS's Gear Inspector
    // shows per item, distinct from refine AND from the wear requirement) + refine when known (self
    // only; others' refine is never public). Wear requirement renders as a labelled row in the detail
    // popup — as a bare "Lv.45" here it read as strengthen level (caps 30), user-flagged 2026-06-13.
    private static string CardBottomLine(int gs, int refine)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (gs > 0 && refine > 0) return "GS " + gs.ToString(ci) + " · R" + refine.ToString(ci);
        if (gs > 0) return "GS " + gs.ToString(ci);
        return refine > 0 ? "Refine " + refine.ToString(ci) : "";
    }
}
