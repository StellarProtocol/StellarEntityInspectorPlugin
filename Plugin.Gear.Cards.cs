using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Gear tab — UI (approved prototype): one GEAR panel (hint "click an item for details" + a source tag), the big Weapon
// card (150 × 160) beside a 5-column grid of tile cards (2 rows, r6). Each card: muted slot label · item icon · item name
// in its quality colour · the bottom line (GS, plus a lock tag or a stage bar). Clicking a card opens the item popover
// anchored beside it. Cards are fixed-height (struts) so a name/GS change never moves the grid.
// ⚠️ Grid cells FLEX (equal weights) instead of the prototype's fixed 84 px: at the minimum window the right column is
// too narrow for 150 + 5 × 84 + gaps, so the cells shrink to fit. Cards are ~1.5× the prototype's height (in-game review
// 2026-11-13: the small cards left most of the tab empty) — 44 px icons, names wrap to 2 lines before ellipsizing.
public sealed partial class Plugin
{
    private const float WeaponCardW = 150f, WeaponCardH = 256f;
    private const float GridGap = 4f;
    private const float SmallCardH = (WeaponCardH - GridGap) / 2f;   // two rows = the weapon card's height
    private const float PipW = 14f, PipH = 6f;
    private const int MaxPips = 3;                                   // breakthrough tops out at stage 3 (3.7 tables)

    private HudElement BuildGearBody() => CaptionedPanel(
        () => _loc.T("ei.tab.gear"), () => _gearHasData ? _loc.T("ei.gear.hint") : "", DataTag(GearTag, TagCaptionH),
        new HudElement[]
        {
            new ScrollElement(new ColumnElement(new HudElement[]
            {
                // Far players with NO data at all (no broadcast, no social gear) get the note instead of empty slots.
                StateLine(() => _gearHasData ? "" : _isRemote ? _loc.T("ei.gear.emptyNote") : _loc.T("ei.gear.none"),
                    () => MutedColor()),
                new ConditionalElement(() => _gearHasData, new RowElement(new HudElement[]
                {
                    new CellElement(GearCardElement(0, big: true), Width: WeaponCardW),
                    new CellElement(new ColumnElement(new HudElement[] { GearGridRow(1), GearGridRow(6) }, Gap: GridGap),
                        Weight: 1f),
                }, Gap: PanelGap)),
            }, Gap: PanelGap), 300f),
        });

    private HudElement GearGridRow(int first)
    {
        var cells = new HudElement[5];
        for (int i = 0; i < 5; i++) cells[i] = new CellElement(GearCardElement(first + i, big: false), Weight: 1f);
        return new RowElement(cells, Gap: GridGap);
    }

    // A tile card: Bubble (Tile, r6, hover = Text@0.10 over it) → Bare selectable → a column pinned to the card height by
    // a 0-width strut. Wrapped in a Column so the bubble FILLS its grid cell (a Bubble hugs in a Row, fills in a Column).
    private HudElement GearCardElement(int idx, bool big)
    {
        var hover = new HoverFlag();
        float h = big ? WeaponCardH : SmallCardH;
        int icon = big ? 80 : 44;
        var content = new ColumnElement(new HudElement[]
        {
            new TextElement(() => _gearCards[idx].Slot ?? "", Color: () => MutedColor(), NoWrap: true, FontSize: big ? 12 : 11)
                { Bold = true, Ellipsize = true, Height = big ? 16f : 14f },
            new RowElement(new HudElement[]
            {
                new GameTextureElement(() => GearIcon(idx), icon * 4 / 3, icon, () => _gearUv[idx]),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            new TextElement(() => _gearCards[idx].Name ?? "", Color: () => _gearCards[idx].NameCol,
                FontSize: big ? 13 : 12) { Bold = true, Ellipsize = true, MaxLines = 2, Height = big ? 34f : 30f },
            GearBottomRow(idx, big),
            StageBar(idx),
        }, Gap: big ? 6f : 3f);
        return new ColumnElement(new HudElement[]
        {
            new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, h - 6f),
                    new CellElement(content, Weight: 1f),
                }, Gap: 0.01f),
                OnClick: () => { })
                { Bare = true, OnHover = over => hover.On = over, OnClickWithRect = r => OpenGearDetail(idx, r) })
            {
                FillColorFn = () => Fade(hover.On && _gearCards[idx].Occupied ? HoverFill(TileColor()) : TileColor()),
                CornerRadius = TileRadius, PaddingX = big ? 8f : 4f, PaddingY = 3f,
            },
        }, Gap: 0.01f);
    }

    // "GS 260" / "GS 260+" [lock] / "GS 270 · R12". The lock is the Not-shared glyph on its tag wash, icon-only (a
    // labelled tag can't fit an ~80 px card).
    private HudElement GearBottomRow(int idx, bool big) => new RowElement(new HudElement[]
    {
        new CellElement(new TextElement(() => _gearCards[idx].Bottom ?? "", Color: () => MutedColor(), NoWrap: true,
            FontSize: big ? 12 : 11) { Bold = true, Ellipsize = true, Height = big ? 16f : 14f }, Weight: 1f),
        new ConditionalElement(() => _gearCards[idx].Lock, new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(2f, 16f),
                Glyph(LockIcon, () => Mix(MutedColor(), TextColor(), 0.45f)),
                new SpacerElement(2f, 16f),
            }, Gap: 0.01f))
        {
            FillColorFn = () => Fade(Over(TextColor() with { A = NotSharedWashAlpha }, TileColor())),
            CornerRadius = RoundRadius(16f), PaddingX = 0f, PaddingY = 0f, MaxWidth = 24f,
        }),
    }, Gap: 2f);

    // Self only: breakthrough stage n of max as 14 × 6 pips (accent = reached). Radius side/2 − 1 (= 2) per house rule 1:
    // the prototype's r3 on a 6 px pip would make the sliced bubble 8 px tall.
    private HudElement StageBar(int idx)
    {
        var pips = new HudElement[MaxPips];
        for (int i = 0; i < MaxPips; i++)
        {
            int p = i;
            pips[i] = new ConditionalElement(() => p < _gearCards[idx].MaxStage, new BubbleElement(new SpacerElement(PipW, PipH))
            {
                FillColorFn = () => Fade(p < _gearCards[idx].Stage ? AccentColor() : TextColor() with { A = 0.18f }),
                CornerRadius = RoundRadius(PipH), PaddingX = 0f, PaddingY = 0f, MaxWidth = PipW,
            });
        }
        return new ConditionalElement(() => _gearCards[idx].MaxStage > 0, new RowElement(pips, Gap: 3f));
    }
}
