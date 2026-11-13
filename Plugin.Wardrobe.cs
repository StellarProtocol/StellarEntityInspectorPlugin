using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Wardrobe tab (approved prototype, row style A) — what the inspected player is wearing: one WEARING panel (hint
// "N pieces · N dyed" + a Profile / Nearby tag for the source). Source: IEntityDetail.GetFashion (broadcast attr 201,
// any AOI player), falling back to the cached social reply's fashion_data for far players.
//   • Dyeable piece = a 50 px tile: icon 24 + name (quality colour, bold) + muted part label; under it (indented 32) one
//     20 px chip per dye zone: muted slot number · 12 px swatch · hex, or a hatched swatch + muted "Default" when undyed.
//   • Piece that can't be dyed (empty ColorPart) = one 26 px row with muted "Can't be dyed" at the right.
// Zones = FashionTable[id].ColorPart (+ColorPartfree) in the game's slot order (Plugin.LuaTables.cs); a zone's colour =
// the FashionEntry.Dyes[i] whose DyeAreas[i] is that zone. Fail-safe: zones unknown (Lua read failed / pending) → the
// chips show only the dyed areas the wire carried, numbered in the same order. Real default colours = later experiment.
public sealed partial class Plugin
{
    private const int MaxWardrobeRows = 16;
    private const int MaxDyeChips = 12;          // EFashionColorAreaType has 16 areas; real pieces carry ≤ 8 zones
    private const float DyeChipW = 100f, DyeChipH = 20f, DyeIndent = 32f;

    private struct WardrobeRow
    {
        public int FashionId; public string Name; public string Part; public ColorRgba NameCol;
        public bool Dyeable;     // false = the 26 px single-line row
        public bool NoDye;       // the piece has no dye zones at all ("Can't be dyed")
        public int ChipCount;
    }

    private readonly List<WardrobeRow> _wardrobeRows = new(MaxWardrobeRows);
    private readonly UvRect[] _wardrobeUv = new UvRect[MaxWardrobeRows];
    private readonly string[] _chipNum = new string[MaxWardrobeRows * MaxDyeChips];
    private readonly string[] _chipText = new string[MaxWardrobeRows * MaxDyeChips];
    private readonly ColorRgba[] _chipCol = new ColorRgba[MaxWardrobeRows * MaxDyeChips];
    private readonly bool[] _chipDyed = new bool[MaxWardrobeRows * MaxDyeChips];
    private readonly List<int> _zoneScratch = new(16);
    private IReadOnlyList<FashionEntry>? _lastFashionSnapshot;   // tracker swaps wholesale — reference = dirty check
    private int _wdLuaVer = -1;
    private object? _wdLang;
    private bool _wdFromProfile;
    private string _wdHint = "", _wdFarLine = "";

    private static readonly string[] SlotNumbers = BuildSlotNumbers();
    private static string[] BuildSlotNumbers()
    {
        var a = new string[MaxDyeChips + 1];
        for (int i = 0; i < a.Length; i++) a[i] = i.ToString(CultureInfo.InvariantCulture);
        return a;
    }

    private HudElement BuildWardrobeBody()
    {
        var rows = new HudElement[MaxWardrobeRows];
        for (var i = 0; i < MaxWardrobeRows; i++) rows[i] = WardrobeRowElement(i);
        return CaptionedPanel(() => _loc.T("ei.wardrobe.caption"), () => _wdHint, DataTag(WardrobeTag, TagCaptionH),
            new HudElement[]
            {
                new ScrollElement(new ColumnElement(new HudElement[]
                {
                    new ConditionalElement(() => _wdFarLine.Length > 0 && _wardrobeRows.Count > 0,
                        new TextElement(() => _wdFarLine, Color: () => CautionColor())),
                    new ConditionalElement(() => _wardrobeRows.Count == 0, WardrobeEmptyBox()),
                    new ListElement(() => _wardrobeRows.Count, rows) { Gap = 2f },
                }, Gap: 6f), 300f),
            });
    }

    private TagKind WardrobeTag()
    {
        if (IsSelf || IsFrozen) return TagKind.None;
        if (_wardrobeRows.Count == 0) return _isRemote ? TagKind.Nearby : TagKind.None;
        return _wdFromProfile ? TagKind.Profile : TagKind.Nearby;
    }

    // Empty: a quiet box. ⚠️ The prototype draws it DASHED; BubbleElement only has a solid 1 px outline, so the box uses a
    // faint solid ring (toolkit limit, as Wardrobe's empty state).
    private HudElement WardrobeEmptyBox() => new BubbleElement(new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 40f),
        new TextElement(() => _loc.T("ei.wardrobe.emptyTitle"), Color: () => TextColor(), Align: TextAlign.Center,
            FontSize: 13) { Bold = true },
        new TextElement(() => _loc.T("ei.wardrobe.emptyBody"), Color: () => MutedColor(), Align: TextAlign.Center),
        new SpacerElement(0f, 40f),
    }, Gap: 4f))
    {
        FillColorFn = () => Transparent,
        OutlineColorFn = () => Fade(MutedColor() with { A = 0.45f }),
        CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
    };

    private HudElement WardrobeRowElement(int idx)
    {
        bool Live() => idx < _wardrobeRows.Count;
        var chips = new HudElement[MaxDyeChips];
        for (int c = 0; c < MaxDyeChips; c++) chips[c] = DyeChip(idx * MaxDyeChips + c);
        var icon = new GameTextureElement(() => WardrobeIcon(idx), 24, 24, () => _wardrobeUv[idx]);
        return new ColumnElement(new HudElement[]
        {
            // Two-line tile (dyeable piece).
            new ConditionalElement(() => Live() && _wardrobeRows[idx].Dyeable, new BubbleElement(new ColumnElement(new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, 24f),
                    icon,
                    new SpacerElement(8f, 0f),
                    WardrobeNameAndPart(idx),
                }, Gap: 0.01f),
                new RowElement(new HudElement[]
                {
                    new SpacerElement(DyeIndent, 0f),
                    new CellElement(new ListElement(() => Live() ? _wardrobeRows[idx].ChipCount : 0, chips,
                        Columns: MaxDyeChips, CellWidth: DyeChipW, CellHeight: DyeChipH) { FillWidth = true, Gap = 4f }, Weight: 1f),
                }, Gap: 0.01f),
            }, Gap: 2f))
            {
                FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 6f, PaddingY = 2f,
            }),
            // One-line row (can't be dyed / zones unknown and undyed).
            new ConditionalElement(() => Live() && !_wardrobeRows[idx].Dyeable, new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, 26f),
                new GameTextureElement(() => WardrobeIcon(idx), 20, 20, () => _wardrobeUv[idx]),
                new SpacerElement(8f, 0f),
                WardrobeNameAndPart(idx),
                new ConditionalElement(() => Live() && _wardrobeRows[idx].NoDye,
                    new TextElement(() => _loc.T("ei.wardrobe.cantDye"), Color: () => MutedColor(), NoWrap: true)),
            }, Gap: 0.01f))
            {
                FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 6f, PaddingY = 0f,
            }),
        }, Gap: 0.01f);
    }

    private HudElement WardrobeNameAndPart(int idx) => new CellElement(new RowElement(new HudElement[]
    {
        new TextElement(() => idx < _wardrobeRows.Count ? _wardrobeRows[idx].Name : "",
            Color: () => idx < _wardrobeRows.Count ? _wardrobeRows[idx].NameCol : TextColor(), NoWrap: true, FontSize: 13)
            { Bold = true, Ellipsize = true },
        new SpacerElement(8f, 0f),
        new TextElement(() => idx < _wardrobeRows.Count ? _wardrobeRows[idx].Part : "", Color: () => MutedColor(), NoWrap: true),
        new SpacerElement(0f, 0f),
    }, Gap: 0.01f), Weight: 1f);

    // One dye-zone chip (20 px, r9): muted slot number · 12 px swatch (or the hatched undyed glyph) · hex / "Default".
    private HudElement DyeChip(int k) => new ColumnElement(new HudElement[]
    {
        new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(7f, DyeChipH),
            new TextElement(() => _chipNum[k] ?? "", Color: () => MutedColor(), NoWrap: true, FontSize: 11) { Bold = true },
            new SpacerElement(5f, 0f),
            new ConditionalElement(() => _chipDyed[k], new SwatchElement(() => _chipCol[k], Size: 12f)),
            new ConditionalElement(() => !_chipDyed[k], Glyph(HatchIcon, () => MutedColor())),
            new SpacerElement(5f, 0f),
            new TextElement(() => _chipText[k] ?? "", Color: () => _chipDyed[k] ? TextColor() : MutedColor(), NoWrap: true,
                FontSize: 11) { Bold = true },
        }, Gap: 0.01f))
        {
            FillColorFn = () => Fade(Over(TextColor() with { A = 0.06f }, TileColor())),
            CornerRadius = RoundRadius(DyeChipH), PaddingX = 0f, PaddingY = 0f,
        },
    }, Gap: 0.01f);

    private object? WardrobeIcon(int idx)
    {
        if (idx >= _wardrobeRows.Count) return null;
        return _services.GameAssets.LoadItemIcon(_wardrobeRows[idx].FashionId, out _wardrobeUv[idx]);
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

    private void RebuildWardrobe()
    {
        // The fashion list reference only changes on a new attr-201 broadcast / social reply, so steady-state ticks skip
        // the building entirely; a Lua zone/part read landing (or a language switch) re-runs it once.
        var snapshot = TargetFashion();
        bool profile = false;
        if (snapshot.Count == 0 && _socialSnap is { Fashion.Count: > 0 } s) { snapshot = s.Fashion; profile = true; }
        var lang = _loc.T("ei.wardrobe.caption");
        _wdFarLine = profile && _isRemote ? FarOutfitLine() : "";
        if (ReferenceEquals(snapshot, _lastFashionSnapshot) && _wdLuaVer == _luaVersion && ReferenceEquals(lang, _wdLang))
            return;
        _lastFashionSnapshot = snapshot; _wdLuaVer = _luaVersion; _wdLang = lang; _wdFromProfile = profile;

        _wardrobeRows.Clear();
        int dyed = 0;
        foreach (var f in snapshot)
        {
            if (_wardrobeRows.Count >= MaxWardrobeRows) break;
            var item = _services.GameData.Inventory.GetItem(f.FashionId);
            int row = _wardrobeRows.Count, chips = FillDyeChips(row, f, out bool anyDyed, out bool noDye);
            if (anyDyed) dyed++;
            _wardrobeRows.Add(new WardrobeRow
            {
                FashionId = f.FashionId,
                Name = item is { Name.Length: > 0 } it ? it.Name : "<" + f.FashionId.ToString(CultureInfo.InvariantCulture) + ">",
                Part = RegionName(f.Slot) ?? "",
                NameCol = QualityColOf(item?.Quality ?? 0),
                Dyeable = chips > 0, NoDye = noDye, ChipCount = chips,
            });
        }
        _wdHint = _wardrobeRows.Count == 0 ? "" : _loc.TFormat("ei.wardrobe.hint", _wardrobeRows.Count, dyed);
    }

    // Fill row `row`'s chips; returns the chip count. noDye = the table says the piece has no zones.
    private int FillDyeChips(int row, FashionEntry f, out bool anyDyed, out bool noDye)
    {
        anyDyed = false; noDye = false;
        _zoneScratch.Clear();
        var zones = DyeZones(f.FashionId);
        if (zones is not null)
        {
            if (zones.Length == 0) { noDye = true; return 0; }
            _zoneScratch.AddRange(zones);
        }
        else if (f.DyeAreas.Length > 0)
        {
            // Zones unknown: the dyed areas the wire carried, in the game's order (no "Default" chips — we can't know).
            foreach (var a in f.DyeAreas) if (!_zoneScratch.Contains(a)) _zoneScratch.Add(a);
            _zoneScratch.Sort(CompareDyeArea);
        }
        else
        {
            // No area keys at all: the dyes positionally (the old reader shape).
            for (int i = 0; i < f.Dyes.Length && i < MaxDyeChips; i++)
            {
                SetChip(row, i, i + 1, f.Dyes[i]);
                anyDyed = true;
            }
            return System.Math.Min(f.Dyes.Length, MaxDyeChips);
        }
        int n = System.Math.Min(_zoneScratch.Count, MaxDyeChips);
        for (int i = 0; i < n; i++)
        {
            int area = _zoneScratch[i], hit = -1;
            for (int d = 0; d < f.DyeAreas.Length && d < f.Dyes.Length; d++)
                if (f.DyeAreas[d] == area) { hit = d; break; }
            if (hit >= 0) { SetChip(row, i, i + 1, f.Dyes[hit]); anyDyed = true; }
            else SetDefaultChip(row, i, i + 1);
        }
        return n;
    }

    private void SetChip(int row, int chip, int number, ColorRgba color)
    {
        int k = row * MaxDyeChips + chip;
        _chipNum[k] = SlotNumbers[number < SlotNumbers.Length ? number : 0];
        _chipCol[k] = color with { A = 1f };
        _chipText[k] = HexOf(color);
        _chipDyed[k] = true;
    }

    private void SetDefaultChip(int row, int chip, int number)
    {
        int k = row * MaxDyeChips + chip;
        _chipNum[k] = SlotNumbers[number < SlotNumbers.Length ? number : 0];
        _chipText[k] = _loc.T("ei.wardrobe.default");
        _chipDyed[k] = false;
    }

    private string _wdFarCache = "";
    private long _wdFarAge = long.MinValue;

    // "Far · outfit from their profile, N min ago" (amber) — rebuilt only when the minute changes.
    private string FarOutfitLine()
    {
        long age = ProfileAgeMinutes();
        if (age == _wdFarAge && _wdFarCache.Length > 0) return _wdFarCache;
        _wdFarAge = age;
        _wdFarCache = age < 0 ? _loc.T("ei.wardrobe.far")
                    : age == 0 ? _loc.T("ei.wardrobe.farNow")
                    : _loc.TFormat("ei.wardrobe.farAge", age);
        return _wdFarCache;
    }
}
