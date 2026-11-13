using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Gear item popover (approved prototype): a house popover — GlassMenu, no title bar, 360 wide, draggable — anchored beside
// the clicked card. Header = 32 px icon tile · the item name in its quality colour · muted "<SLOT> · ANOTHER PLAYER / YOU"
// · round ✕; then SectionPanels BASIC (Profile tag for others) and ADVANCED.
//   Others: GS "260 base", Can reach "265 · 270" (every stage's GS), Req. level, Max perfection; ADVANCED — possible
//           rolls. (No rows for what the game never shares — user decision 2026-11-13.)
//   Self:   GS (upgraded), Upgrade "Stage n of max", Refine, Perfection, Gem; ADVANCED — rolled.
// Rows are snapshotted on open into two pooled row lists — no per-poll table walks.
public sealed partial class Plugin
{
    private const float DetailW = 360f;
    private const int MaxBasicRows = 24, MaxAdvancedRows = 36;

    private enum DetailKind : byte { Value, Line, Sub }

    private sealed class DetailRows
    {
        public readonly List<string> Labels, Values;
        public readonly List<DetailKind> Kinds;
        public readonly int Capacity;
        public DetailRows(int capacity)
        {
            Capacity = capacity;
            Labels = new List<string>(capacity); Values = new List<string>(capacity); Kinds = new List<DetailKind>(capacity);
        }
        public int Count => Labels.Count;
        public void Clear() { Labels.Clear(); Values.Clear(); Kinds.Clear(); }
        public void Add(DetailKind kind, string label, string value)
        {
            if (Labels.Count >= Capacity)
            {
                // Make truncation visible instead of silently dropping trailing rows.
                if (Labels[^1] != "…") { Labels[^1] = "…"; Values[^1] = ""; Kinds[^1] = DetailKind.Line; }
                return;
            }
            Labels.Add(label); Values.Add(value); Kinds.Add(kind);
        }
    }

    private IWindowControl _gearDetailWindow = null!;
    private readonly DetailRows _gdBasic = new(MaxBasicRows);
    private readonly DetailRows _gdAdv = new(MaxAdvancedRows);
    private DetailRows _gd = null!;           // the list AddGd writes to (basic, then advanced)
    private int _gdCard = -1;                 // the open card's index (icon + title)
    private string _gdTitle = "", _gdSubtitle = "", _gdAdvCaption = "";
    private ColorRgba _gdTitleCol = new(1f, 1f, 1f, 1f);
    private bool _gdOthers;                   // the popover shows another player's item (Profile tag + footnote)
    private UvRect _gdUv = new(0f, 0f, 1f, 1f);

    private IWindowControl RegisterGearDetailWindow()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "entityinspector.geardetail",
                Title:       _loc.T("ei.gd.title"),   // fallback only (no title bar is drawn)
                DefaultRect: new WindowRect(120f, 120f, DetailW, 0f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.GlassMenu)
            {
                StartVisible = false, Closable = true, Draggable = true, ShowTitleBar = false,
                TitleProvider = () => _loc.T("ei.gd.title"),
                // Gameplay HUD (item popover for the inspector) — draw only while in-world and not on a loading screen.
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            BuildGearDetailRoot(),
            OnClose: () => _gearDetailWindow.SetVisible(false)));

    private HudElement BuildGearDetailRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new CellElement(new BubbleElement(new RowElement(new HudElement[]
                {
                    new GameTextureElement(GearDetailIcon, 24, 24, () => _gdUv),
                }, Gap: 0.01f, Justify: RowJustify.Center))
            {
                FillColorFn = () => Fade(TileColor()), CornerRadius = 7f, PaddingX = 4f, PaddingY = 4f,
            }, Width: 32f),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _gdTitle, Color: () => _gdTitleCol, NoWrap: true, FontSize: 14)
                    { Bold = true, Ellipsize = true },
                new TextElement(() => _gdSubtitle, Color: () => MutedColor(), NoWrap: true, FontSize: 11)
                    { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            RoundCloseButton(() => _gearDetailWindow.SetVisible(false)),
        }, Gap: 10f),
        CaptionedPanel(() => _loc.T("ei.gd.basic"), NoHint,
            DataTag(() => _gdOthers ? TagKind.Profile : TagKind.None, TagCaptionH),
            new HudElement[] { DetailList(_gdBasic) }),
        new ConditionalElement(() => _gdAdv.Count > 0,
            SectionPanel(() => _gdAdvCaption, NoHint, DetailList(_gdAdv))),
        new ConditionalElement(() => _gdOthers,
            new TextElement(() => _loc.T("ei.gd.notPublicRolls"), Color: () => MutedColor())),
    }, Gap: 10f);

    private object? GearDetailIcon()
    {
        if (_gdCard < 0 || _gdCard >= _gearCards.Length || !_gearCards[_gdCard].Occupied) return null;
        return _services.GameAssets.LoadItemIcon(_gearCards[_gdCard].ItemId, out _gdUv);
    }

    // Card click: build the rows, then anchor the popover beside the clicked card (screen px → canvas units), clamped to
    // the screen. Clicking the open card again closes it.
    private void OpenGearDetail(int cardIdx, WindowRect cardRect)
    {
        var card = _gearCards[cardIdx];
        if (!card.Occupied) return;
        if (_gearDetailWindow.IsShown && _gdCard == cardIdx) { _gearDetailWindow.SetVisible(false); return; }
        _gdCard = cardIdx;
        BuildGearDetailRows(card);
        float k = ScreenToCanvas();
        var r = new WindowRect(cardRect.X * k, cardRect.Y * k, cardRect.Width * k, cardRect.Height * k);
        float estH = 120f + (_gdBasic.Count + _gdAdv.Count) * 22f + (_gdAdv.Count > 0 ? 50f : 0f);
        float x = r.X + r.Width + 8f;
        if (x + DetailW > CanvasWidthUnits() - 8f) x = r.X - DetailW - 8f;   // no room on the right → open to the left
        float y = System.MathF.Min(r.Y, CanvasHeight() - estH - 8f);
        _gearDetailWindow.SetVisible(true);
        _gearDetailWindow.SetRect(new WindowRect(System.MathF.Max(0f, x), System.MathF.Max(0f, y), DetailW, 0f));
        // Front on EVERY open (Rule 5 exception): stacking sorts layer first, activation second, and the inspector (HUD)
        // is lifted to the upper layer by its own BringToFront — a HUD popup opened from hidden stays on the lower layer,
        // behind it (KB WindowBuilder-Patterns.md "Window stacking model v2"). Not Topmost.
        _gearDetailWindow.BringToFront();
    }

    private void BuildGearDetailRows(GearCard card)
    {
        _gdBasic.Clear(); _gdAdv.Clear();
        _gdTitle = card.Name; _gdTitleCol = card.NameCol;
        var self = _gearSource == GearSource.Self;
        _gdOthers = !self;
        _gdSubtitle = (card.Slot + " · " + _loc.T(self ? "ei.gd.whoSelf" : "ei.gd.whoOther")).ToUpperInvariant();
        var row  = _services.GameData.Equip.GetEquipRow(card.ItemId);
        var inst = SelfInstanceOf(card);
        var ci = CultureInfo.InvariantCulture;

        _gd = _gdBasic;
        bool hasStages = card.StageGs is { Length: > 0 };
        if (self)
        {
            if (ShownGs(card) > 0) AddGd(_loc.T("ei.gd.gs"), ShownGs(card).ToString("N0", ci));
            if (hasStages) AddGd(_loc.T("ei.gd.upgrade"), _loc.TFormat("ei.gd.stageOf", card.Stage, card.MaxStage));
        }
        else
        {
            if (card.BaseGs > 0) AddGd(_loc.T("ei.gd.gs"), _loc.TFormat("ei.gd.gsBase", card.BaseGs.ToString("N0", ci)));
            if (hasStages) AddGd(_loc.T("ei.gd.canReach"), JoinGs(card.StageGs!));
        }
        if (row is { } r)
        {
            // Wear REQUIREMENT (table data) — labelled explicitly (a bare "Lv.45" read as strengthen level).
            if (r.WearLevel > 1) AddGd(_loc.T("ei.gd.reqLevel"), r.WearLevel.ToString(ci));
        }
        if (self)
        {
            if (card.Refine > 0) AddGd(_loc.T("ei.gd.refine"), card.Refine.ToString(ci));
            if (inst is { } pi && pi.Perfection.Max > 0)
                AddGd(_loc.T("ei.gd.perfection"), pi.Perfection.Value.ToString("N0", ci) + " / " + pi.Perfection.Max.ToString("N0", ci));
            AddEffectRows(inst);
        }
        else
        {
            // Upgrade stage / refine / perfection / gem are never shared for other players: no rows (show only what we
            // get — user decision 2026-11-13); the footnote says so. Max perfection is table data, so it stays.
            if (row is { PerfectCap: > 0 } pr) AddGd(_loc.T("ei.gd.maxPerfection"), pr.PerfectCap.ToString("N0", ci));
        }
        if (row is { } r2)
        {
            AddBasicSection(r2, inst);
            _gd = _gdAdv;
            AddAdvancedSection(r2, inst);
        }
        _gdAdvCaption = _loc.T(inst is { Attrs.Advanced.Count: > 0 } ? "ei.gd.advRolled" : "ei.gd.advPossible");
    }

    // "265 · 270" — every breakthrough stage's GS.
    private static string JoinGs(int[] stages)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < stages.Length; i++)
        {
            if (i > 0) sb.Append(" · ");
            sb.Append(stages[i].ToString("N0", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private void AddGd(string label, string value) => _gd.Add(DetailKind.Value, label, value);
    private void AddGdLine(string text) => _gd.Add(DetailKind.Line, text, "");
    private void AddGdSub(string text) => _gd.Add(DetailKind.Sub, text, "");

    // Self: the socketed gem (resolved to the gem item whose NAME carries the display level, "… Lv.2" — the wire
    // enchant_level is an internal index) + its effects; "No gem socketed." when none.
    private void AddEffectRows(GearInstance? inst)
    {
        if (inst?.Enchant is not { } en) { AddGd(_loc.T("ei.gd.gem"), _loc.T("ei.gd.noGemShort")); return; }
        if (_services.GameData.Equip.GetEnchantItem(en.ItemTypeId, en.Level) is { } gem)
        {
            var gemName = _services.GameData.Inventory.GetItem(gem.GemItemId)?.Name
                          ?? _loc.TFormat("ei.gd.gemFallback", gem.GemItemId);
            AddGd(_loc.T("ei.gd.gem"), gemName);
            foreach (var eff in gem.Effects)
                AddGd(AttrLabel(eff.AttrId), FormatAttr(AttrNumType(eff.AttrId), eff.Value));
            return;
        }
        var name = _services.GameData.Inventory.GetItem(en.ItemTypeId)?.Name ?? _loc.TFormat("ei.gd.gemFallback", en.ItemTypeId);
        AddGd(_loc.T("ei.gd.gem"), name + " · " + _loc.TFormat("ei.gd.lv", en.Level));
    }

    private GearInstance? SelfInstanceOf(GearCard card)
    {
        if (_gearSource != GearSource.Self || card.Uuid == 0) return null;
        foreach (var g in _services.Inventory.GetSelfGear())
            if (g.ItemUuid == card.Uuid) return g;
        return null;
    }
}
