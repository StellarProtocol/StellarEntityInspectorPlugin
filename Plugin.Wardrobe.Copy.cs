using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Wardrobe tab — the dye-zone chips and click-to-copy (in-game review 2026-11-13). A DYED chip is a round Bubble around a
// Bare selectable (hover = Text@0.12 round wash); a click copies its 6-char hex (no "#") to the clipboard through
// UnityEngine.GUIUtility.systemCopyBuffer (IL2CPP-safe, as ChatTools / CombatMeter). Feedback where you clicked (house rule
// 13): the chip turns green "Copied" for ~1 s, keyed by (fashion id, slot) + time so a re-render or rebind keeps it on the
// right chip; the panel footer says "✓ Copied D97D36 (Magical Love · slot 3)" for ~3 s. Default (undyed) chips are not
// clickable and have no hover. Chip width is fixed (the grid cell) in every state.
public sealed partial class Plugin
{
    private const long ChipFlashMs = 1_000, CopyFooterMs = 3_000;
    private const float ChipHoverWash = 0.12f;

    private long _copiedKey = -1;            // (fashionId << 8) | chip index of the last copied chip
    private long _copiedAtMs = long.MinValue / 2;
    private string _copyFooter = "";
    private bool _copyWarned;

    private static long ChipKey(int fashionId, int chip) => ((long)fashionId << 8) | (uint)chip;

    private bool ChipFlashing(int row, int chip)
        => row < _wardrobeRows.Count && _copiedKey == ChipKey(_wardrobeRows[row].FashionId, chip)
           && Environment.TickCount64 - _copiedAtMs < ChipFlashMs;

    private string CopyFooter() => Environment.TickCount64 - _copiedAtMs < CopyFooterMs ? _copyFooter : "";

    private HudElement DyeChip(int row, int chip)
    {
        int k = row * MaxDyeChips + chip;
        var hover = new HoverFlag();
        long hoverKey = -1;
        bool Dyed() => _chipDyed[k];
        bool Flash() => ChipFlashing(row, chip);
        bool Hot() => hover.On && Dyed() && row < _wardrobeRows.Count && hoverKey == ChipKey(_wardrobeRows[row].FashionId, chip);
        ColorRgba Fill()
        {
            if (Flash()) return Over(SuccessColor() with { A = ChipWashAlpha }, TileColor());
            var rest = Over(TextColor() with { A = 0.06f }, TileColor());
            return Hot() ? Over(TextColor() with { A = ChipHoverWash }, rest) : rest;
        }
        return new ColumnElement(new HudElement[]
        {
            new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(7f, DyeChipH),
                    new TextElement(() => _chipNum[k] ?? "", Color: () => MutedColor(), NoWrap: true, FontSize: 11) { Bold = true },
                    new SpacerElement(5f, 0f),
                    new ConditionalElement(() => Dyed(), new SwatchElement(() => _chipCol[k], Size: 12f)),
                    new ConditionalElement(() => !Dyed(), Glyph(HatchIcon, () => MutedColor())),
                    new SpacerElement(5f, 0f),
                    new TextElement(() => Flash() ? _loc.T("ei.wardrobe.copiedChip") : _chipText[k] ?? "",
                        Color: () => Flash() ? SuccessColor() : Dyed() ? TextColor() : MutedColor(), NoWrap: true,
                        FontSize: 11) { Bold = true },
                    new SpacerElement(7f, DyeChipH),
                }, Gap: 0.01f),
                OnClick: () => CopyChip(row, chip))
                {
                    Bare = true,
                    OnHover = over =>
                    {
                        hover.On = over;
                        hoverKey = row < _wardrobeRows.Count ? ChipKey(_wardrobeRows[row].FashionId, chip) : -1;
                    },
                })
            {
                FillColorFn = () => Fade(Fill()), CornerRadius = RoundRadius(DyeChipH), PaddingX = 0f, PaddingY = 0f,
            },
        }, Gap: 0.01f);
    }

    private void CopyChip(int row, int chip)
    {
        int k = row * MaxDyeChips + chip;
        if (row >= _wardrobeRows.Count || chip >= _wardrobeRows[row].ChipCount || !_chipDyed[k]) return;   // Default: inert
        var hex = _chipText[k];
        try { UnityEngine.GUIUtility.systemCopyBuffer = hex; }
        catch (Exception ex)
        {
            if (!_copyWarned) { _copyWarned = true; _services.Log.Warning($"[EntityInspector] clipboard copy failed: {ex.Message}"); }
            return;
        }
        var piece = _wardrobeRows[row];
        _copiedKey = ChipKey(piece.FashionId, chip);
        _copiedAtMs = Environment.TickCount64;
        _copyFooter = _loc.TFormat("ei.wardrobe.copied", hex, piece.Name, _chipNum[k]);
    }
}
