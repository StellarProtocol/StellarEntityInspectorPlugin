using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Overview tab — UI. One scroll of SectionPanels; each panel lays its rows out in TWO columns of label (muted) / value
// (right-aligned) — a pooled ListElement of row PAIRS, so a panel's height follows its row count without per-frame
// building. A missing value for a nearby player shows the inline "Not shared" tag in its value cell instead of the text.
// Far: a muted note opens the scroll ("Detailed stats sync only while this player is near you.").
public sealed partial class Plugin
{
    private const float StatLineH = 22f;    // one pair line (fits the 18 px inline tag)
    private const float StatColumnGap = 18f;
    // FIXED column geometry (in-game review 2026-11-13): weighted cells size to their content first, so the two columns
    // drifted per row. Every row now uses the same label + value widths, so all left values right-align on one x and all
    // right labels start on one x. Row = 2 × (134 + 6 + 138) + 18 = 574 px; the window minimum is sized for it (house rule
    // 18: widen, don't cram): at 980 px the right column ≈ 980 − 24 − 340 − 8 = 608, − 20 panel padding − the scrollbar
    // inset ≈ 574. A wider window leaves the slack at the right.
    // The value cell fits the widest inline "Not shared" tag of any shipped locale (house rule 10). Tag = 6 + 12 icon + 4
    // + bold-11 label + 7 px; label estimates at ~6.3 px/Latin char, 11 px/CJK char: fil "Hindi ibinabahagi" ≈ 107 → 136,
    // id "Tidak dibagikan" ≈ 95 → 124, en "Not shared" ≈ 63 → 92, th "ไม่แชร์" ≈ 40 → 69, ja "非共有" / ko "비공개" ≈ 33 → 62.
    // Labels keep 134 px so long stat names ("Lucky Strike DMG Multiplier") stay readable.
    private const float StatLabelW = 134f, StatValueW = 138f, StatLabelGap = 6f;

    private HudElement BuildOverviewBody()
    {
        var items = new HudElement[3 + _statPanels.Length];
        int n = 0;
        items[n++] = StateLine(() => _isRemote && !IsFrozen ? _loc.T("ei.overview.syncNote") : "", () => MutedColor());
        items[n++] = new ConditionalElement(() => _identityPanel.Count > 0, StatPanel(_identityPanel));
        foreach (var p in _statPanels) items[n++] = StatPanel(p);
        items[n++] = new ConditionalElement(() => _elementalPanel.Count > 0, StatPanel(_elementalPanel));
        return new ScrollElement(new ColumnElement(items, Gap: PanelGap), 360f);
    }

    private HudElement StatPanel(OvPanel p)
    {
        int pairs = (p.Capacity + 1) / 2;
        var slots = new HudElement[pairs];
        for (int k = 0; k < pairs; k++)
        {
            int left = 2 * k, right = 2 * k + 1;
            slots[k] = FixedLine(StatLineH, new RowElement(new HudElement[]
            {
                StatCell(p, left),
                new SpacerElement(StatColumnGap, 0f),
                right < p.Capacity ? StatCell(p, right) : new SpacerElement(StatLabelW + StatLabelGap + StatValueW, 0f),
                new SpacerElement(),   // flexible: a wider window's slack goes to the right, never between columns
            }, Gap: 0.01f));
        }
        return CaptionedPanel(() => _loc.T(p.TitleKey), () => p.Hint, DataTag(() => p.CaptionTag, TagCaptionH),
            new HudElement[] { new ListElement(() => (p.Count + 1) / 2, slots) { Gap = 0.01f } });
    }

    // [label, fixed width, ellipsized] [value cell, fixed width: right-aligned text | right-aligned inline Not-shared tag].
    // Rows past Count read as empty.
    private HudElement StatCell(OvPanel p, int i)
    {
        bool Live() => i < p.Count;
        return new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => Live() ? p.Labels[i] : "", Color: () => MutedColor(), NoWrap: true)
                { Ellipsize = true }, Width: StatLabelW),
            new SpacerElement(StatLabelGap, 0f),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    DataTag(() => Live() && p.States[i] == RowState.NotShared ? TagKind.NotShared : TagKind.None, TagInlineH),
                }, Gap: 0.01f, Justify: RowJustify.Right),
                new ConditionalElement(() => !Live() || p.States[i] != RowState.NotShared,
                    new TextElement(() => Live() ? p.Values[i] : "",
                        Color: () => Live() && p.States[i] == RowState.Far ? FaintText() : TextColor(),
                        Align: TextAlign.Right, NoWrap: true) { Ellipsize = true }),
            }, Gap: 0.01f), Width: StatValueW),
        }, Gap: 0.01f);
    }
}
