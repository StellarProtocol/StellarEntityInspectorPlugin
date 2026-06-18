using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Wardrobe tab (spec §4.4) — what the inspected player is wearing right now, for ANY AOI player:
// fashion icon, item name (quality-coloured), slot label, and the player's actual dye colours as
// swatch dots. Source: IEntityDetail.GetFashion (broadcast attr 201), falling back to the cached
// social reply's fashion_data for far players (populated on ID-card open). Cosmetics are ItemTable
// rows, so names/quality/icons resolve through the existing item path. Snapshotted at 10 Hz.
public sealed partial class Plugin
{
    private const int MaxWardrobeRows = 16;
    private const int MaxDyeDots = 4;

    private struct WardrobeRow
    {
        public int FashionId; public string Name; public ColorRgba NameCol;
        public int DyeCount; public ColorRgba Dye0, Dye1, Dye2, Dye3;
        public string Hex0, Hex1, Hex2, Hex3;   // precomputed at rebuild — element Funcs never format

        public ColorRgba Dye(int i) => i switch { 0 => Dye0, 1 => Dye1, 2 => Dye2, _ => Dye3 };
        public string Hex(int i) => i switch { 0 => Hex0, 1 => Hex1, 2 => Hex2, _ => Hex3 };
    }

    private readonly List<WardrobeRow> _wardrobeRows = new(MaxWardrobeRows);
    private readonly UvRect[] _wardrobeUv = new UvRect[MaxWardrobeRows];
    private IReadOnlyList<FashionEntry>? _lastFashionSnapshot;   // tracker swaps wholesale — reference = dirty check

    private HudElement BuildWardrobeBody()
    {
        var rows = new HudElement[1 + MaxWardrobeRows];
        // Empty state: far players get the "be near them" hint (fashion is AOI-broadcast only); near
        // players with no fashion equipped keep the plain "No wardrobe data." line.
        rows[0] = new ConditionalElement(() => _wardrobeRows.Count == 0,
            new TextElement(() => _isRemote
                ? "Wardrobe appears once you've been near this player."
                : "No wardrobe data.", MutedCol));
        for (var i = 0; i < MaxWardrobeRows; i++) rows[1 + i] = WardrobeRowElement(i);
        return new ScrollElement(
            new ListElement(() => 1 + _wardrobeRows.Count, rows), 380f);
    }

    private HudElement WardrobeRowElement(int idx)
    {
        // Per user feedback 2026-06-13: no slot number (meaningless code), and each swatch carries its
        // hex code inline (the game's own dye picker shows codes, e.g. D9D9D9; we have no hover-tooltip
        // infra, so inline beats hidden).
        var cells = new List<HudElement>(2 + 2 * MaxDyeDots)
        {
            new GameTextureElement(() => WardrobeIcon(idx), 24, 24, () => _wardrobeUv[idx]),
            new TextElement(() => idx < _wardrobeRows.Count ? _wardrobeRows[idx].Name : "",
                () => idx < _wardrobeRows.Count ? _wardrobeRows[idx].NameCol : default(ColorRgba?)),
            new SpacerElement(),
        };
        for (var d = 0; d < MaxDyeDots; d++)
        {
            var dye = d;
            cells.Add(new ConditionalElement(
                () => idx < _wardrobeRows.Count && dye < _wardrobeRows[idx].DyeCount,
                // 12px: BuildSwatch insets the fill 1px/side; at 10px dark dyes vanished on the
                // dark panel (ux review). 12px keeps a legible 10px colour core.
                new SwatchElement(() => idx < _wardrobeRows.Count
                    ? _wardrobeRows[idx].Dye(dye) : default, Size: 12f)));
            cells.Add(new ConditionalElement(
                () => idx < _wardrobeRows.Count && dye < _wardrobeRows[idx].DyeCount,
                new TextElement(() => idx < _wardrobeRows.Count ? _wardrobeRows[idx].Hex(dye) : "", MutedCol)));
        }
        return new RowElement(cells, Gap: 6f);
    }

    // Game-style colour code (the dye picker shows e.g. D9D9D9) — computed once per rebuild.
    private static string HexOf(ColorRgba c) => string.Create(6, c, static (span, col) =>
    {
        WriteHexByte(span, 0, col.R); WriteHexByte(span, 2, col.G); WriteHexByte(span, 4, col.B);
    });

    private static void WriteHexByte(System.Span<char> span, int at, float channel)
    {
        var b = (int)(channel * 255f + 0.5f);
        b = b < 0 ? 0 : b > 255 ? 255 : b;
        const string digits = "0123456789ABCDEF";
        span[at] = digits[b >> 4]; span[at + 1] = digits[b & 0xF];
    }

    private object? WardrobeIcon(int idx)
    {
        if (idx >= _wardrobeRows.Count) return null;
        return _services.GameAssets.LoadItemIcon(_wardrobeRows[idx].FashionId, out _wardrobeUv[idx]);
    }

    private void RebuildWardrobe()
    {
        // Same dirty-check as the skill rebuild: the fashion list reference only changes on a new
        // attr-201 broadcast, so steady-state ticks skip the string building entirely (perf review).
        var snapshot = TargetFashion();
        // Far players have no broadcast — fall back to the social reply's fashion_data (populated when
        // their ID card was fetched with the full mask; same FashionEntry shape via AttrFashionDataReader).
        if (snapshot.Count == 0 && _socialSnap is { Fashion.Count: > 0 } s) snapshot = s.Fashion;
        if (ReferenceEquals(snapshot, _lastFashionSnapshot)) return;
        _lastFashionSnapshot = snapshot;

        _wardrobeRows.Clear();
        foreach (var f in snapshot)
        {
            if (_wardrobeRows.Count >= MaxWardrobeRows) break;
            var item = _services.GameData.Inventory.GetItem(f.FashionId);
            var row = new WardrobeRow
            {
                FashionId = f.FashionId,
                Name = item is { Name.Length: > 0 } it ? it.Name : $"<{f.FashionId}>",
                NameCol = QualityColOf(item?.Quality ?? 0),
                DyeCount = f.Dyes.Length > MaxDyeDots ? MaxDyeDots : f.Dyes.Length,
                Hex0 = "", Hex1 = "", Hex2 = "", Hex3 = "",
            };
            if (row.DyeCount > 0) { row.Dye0 = f.Dyes[0]; row.Hex0 = HexOf(f.Dyes[0]); }
            if (row.DyeCount > 1) { row.Dye1 = f.Dyes[1]; row.Hex1 = HexOf(f.Dyes[1]); }
            if (row.DyeCount > 2) { row.Dye2 = f.Dyes[2]; row.Hex2 = HexOf(f.Dyes[2]); }
            if (row.DyeCount > 3) { row.Dye3 = f.Dyes[3]; row.Hex3 = HexOf(f.Dyes[3]); }
            _wardrobeRows.Add(row);
        }
    }
}
