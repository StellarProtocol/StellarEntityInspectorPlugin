using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Gear tab — UI (approved prototype, revised layout "option A" after the in-game review 2026-11-13). One GEAR panel
// (hint "click an item for details" + a source tag):
//   • the Weapon as a full-width 64 px tile row ON TOP: 48 px icon · [muted slot label / bold quality-coloured name] ·
//     spacer · bold GS line (+ the stage bar for self), right-aligned;
//   • the other 10 pieces as a 5 × 2 grid of equal flexible cells (≈112 px at the 980 px minimum, stretching evenly
//     when wider), 104 px tall: slot label (+ stage bar for self) · 40 px icon · name up to 2 lines · GS line at the bottom.
// Clicking a card opens the item popover beside it; the open card gets an accent ring.
// ⚠️ Equal cells need equal PREFERRED widths: an ellipsized TextElement reports the FULL string's natural width as its
// preferred width, so cells sized by their names came out unequal (a short name squeezed its card below the 58 px icon,
// the neighbour's tile painted over it — the blank grid icons seen in game) and the row clipped at the right edge. Every
// text in a card therefore has a small FIXED Width (its min = preferred); the Column still stretches it to the cell.
// Others' upgradeable pieces read "GS 260+" (no lock tag — the popover's Upgrade / Can reach rows explain it).
public sealed partial class Plugin
{
    private const float WeaponRowH = 64f, GridCardH = 104f, GridGap = 6f, CardTextW = 60f;
    private const int WeaponIconPx = 48, GridIconPx = 40;
    private const float PipW = 14f, PipH = 6f;
    private const int MaxPips = 3;                       // breakthrough tops out at stage 3 (3.7 tables)

    private HudElement BuildGearBody() => CaptionedPanel(
        () => _loc.T("ei.tab.gear"), () => _gearHasData ? _loc.T("ei.gear.hint") : "", DataTag(GearTag, TagCaptionH),
        new HudElement[]
        {
            new ScrollElement(new ColumnElement(new HudElement[]
            {
                // Far players with NO data at all (no broadcast, no social gear) get the note instead of empty slots.
                StateLine(() => _gearHasData ? "" : _isRemote ? _loc.T("ei.gear.emptyNote") : _loc.T("ei.gear.none"),
                    () => MutedColor()),
                new ConditionalElement(() => _gearHasData, new ColumnElement(new HudElement[]
                {
                    WeaponRow(),
                    GearGridRow(1),
                    GearGridRow(6),
                }, Gap: GridGap)),
            }, Gap: PanelGap), 300f),
        });

    // The card tile around any content: Tile fill (hover = Text@0.10 over it), r6, accent ring while its popover is open.
    private HudElement CardShell(int idx, HudElement content, float padX)
    {
        var hover = new HoverFlag();
        return new ColumnElement(new HudElement[]
        {
            new BubbleElement(new SelectableElement(content, OnClick: () => { })
                { Bare = true, OnHover = over => hover.On = over, OnClickWithRect = r => OpenGearDetail(idx, r) })
            {
                FillColorFn = () => Fade(hover.On && _gearCards[idx].Occupied ? HoverFill(TileColor()) : TileColor()),
                OutlineColorFn = () => _gdCard == idx && _gearDetailWindow.IsShown ? Fade(AccentColor()) : null,
                CornerRadius = TileRadius, PaddingX = padX, PaddingY = 3f,
            },
        }, Gap: 0.01f);
    }

    private HudElement WeaponRow() => CardShell(0, new RowElement(new HudElement[]
    {
        new SpacerElement(0f, WeaponRowH - 6f),
        new GameTextureElement(() => GearIcon(0), WeaponIconPx, WeaponIconPx, () => _gearUv[0]),
        new SpacerElement(10f, 0f),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => _gearCards[0].Slot ?? "", Color: () => MutedColor(), Width: CardTextW, NoWrap: true,
                FontSize: 11) { Bold = true, Ellipsize = true, Height = 16f },
            new TextElement(() => _gearCards[0].Name ?? "", Color: () => _gearCards[0].NameCol, Width: CardTextW, NoWrap: true,
                FontSize: 14) { Bold = true, Ellipsize = true, Height = 20f },
        }, Gap: 2f), Weight: 1f),
        new SpacerElement(10f, 0f),
        new ColumnElement(new HudElement[]
        {
            new TextElement(() => _gearCards[0].Bottom ?? "", Color: () => TextColor(), Width: 120f, Align: TextAlign.Right,
                NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true, Height = 18f },
            new RowElement(new HudElement[] { StageBar(0) }, Gap: 0.01f, Justify: RowJustify.Right),
        }, Gap: 3f),
        new SpacerElement(4f, 0f),
    }, Gap: 0.01f), padX: 8f);

    private HudElement GearGridRow(int first)
    {
        var cells = new HudElement[5];
        for (int i = 0; i < 5; i++) cells[i] = new CellElement(GridCard(first + i), Weight: 1f);
        return new RowElement(cells, Gap: GridGap);
    }

    // slot label (+ self stage bar at the right) · 40 px icon · name (2 lines, then …) · GS line, pinned to 104 px.
    private HudElement GridCard(int idx) => CardShell(idx, new RowElement(new HudElement[]
    {
        new SpacerElement(0f, GridCardH - 6f),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => _gearCards[idx].Slot ?? "", Color: () => MutedColor(), Width: 30f,
                    NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true, Height = 14f }, Weight: 1f),
                StageBar(idx),
            }, Gap: 2f),
            new RowElement(new HudElement[]
            {
                new GameTextureElement(() => GearIcon(idx), GridIconPx, GridIconPx, () => _gearUv[idx]),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            new TextElement(() => _gearCards[idx].Name ?? "", Color: () => _gearCards[idx].NameCol, Width: CardTextW,
                FontSize: 12) { Bold = true, Ellipsize = true, MaxLines = 2, Height = 30f },
            new TextElement(() => _gearCards[idx].Bottom ?? "", Color: () => MutedColor(), Width: CardTextW, NoWrap: true,
                FontSize: 11) { Bold = true, Ellipsize = true, Height = 14f },
        }, Gap: 1f), Weight: 1f),
    }, Gap: 0.01f), padX: 4f);

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
