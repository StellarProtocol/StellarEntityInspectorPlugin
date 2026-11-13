using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Overview tab — UI. One scroll of SectionPanels; a panel renders only when it has rows (other players: only the stats
// that actually arrived — Plugin.Overview.cs). Each panel lays its rows out as a pooled ListElement of row PAIRS, so a
// panel's height follows its row count without per-frame building, and the window size never changes.
// A fixed-height muted line opens the scroll ("Detailed stats sync only while this player is near you." when far).
public sealed partial class Plugin
{
    private const float StatLineH = 22f;
    private const float StatColumnGap = 18f;
    // Column geometry (in-game review 2026-11-13, round 3): every row is two EQUAL halves that share the available width,
    // so the columns line up on every row AND a wider window widens the labels instead of piling slack at the right edge.
    // Equal halves need equal PREFERRED widths — a weighted cell is its preferred width + a share of the slack, and an
    // ellipsized text reports its FULL string as preferred — so every text here has a fixed Width (= its preferred):
    //   stat rows:  label 134 (its minimum) in a weighted cell that takes the extra · 6 · value cell fixed 138;
    //   row at the 980 px minimum = 2 × (134 + 6 + 138) + 18 = 574 ≈ 980 − 24 − 340 − 8 − 20 panel − scrollbar inset.
    //   IDENTITY:   label fixed 110 · 6 · value 168 (its minimum) in a weighted cell — same 284 half, so both line up;
    //               Collections spans the whole row.
    private const float StatLabelW = 134f, StatValueW = 138f, StatLabelGap = 6f;
    private const float IdLabelW = 110f, IdValueMinW = 168f;
    private const float StatHalfW = StatLabelW + StatLabelGap + StatValueW;

    private HudElement BuildOverviewBody()
    {
        var items = new HudElement[3 + _statPanels.Length];
        int n = 0;
        items[n++] = StateLine(() => _isRemote && !IsFrozen ? _loc.T("ei.overview.syncNote") : "", () => MutedColor());
        items[n++] = new ConditionalElement(() => _identityPanel.Count > 0 || _idCollections.Length > 0, IdentityPanel());
        foreach (var p in _statPanels) items[n++] = new ConditionalElement(() => p.Count > 0, StatPanel(p));
        items[n++] = new ConditionalElement(() => _elementalPanel.Count > 0, StatPanel(_elementalPanel));
        return new ScrollElement(new ColumnElement(items, Gap: PanelGap), 360f);
    }

    // [Cell(weight 1): left half] [gap] [Cell(weight 1): right half] — equal preferred halves → equal widths.
    private static HudElement PairLine(HudElement left, HudElement right) => FixedLine(StatLineH, new RowElement(new HudElement[]
    {
        new CellElement(left, Weight: 1f),
        new SpacerElement(StatColumnGap, 0f),
        new CellElement(right, Weight: 1f),
    }, Gap: 0.01f));

    private HudElement StatPanel(OvPanel p)
    {
        int pairs = (p.Capacity + 1) / 2;
        var slots = new HudElement[pairs];
        for (int k = 0; k < pairs; k++)
        {
            int left = 2 * k, right = 2 * k + 1;
            slots[k] = PairLine(StatCell(p, left), right < p.Capacity ? StatCell(p, right) : new SpacerElement(StatHalfW, 0f));
        }
        return CaptionedPanel(() => _loc.T(p.TitleKey), NoHint, DataTag(() => p.CaptionTag, TagCaptionH),
            new HudElement[] { new ListElement(() => (p.Count + 1) / 2, slots) { Gap = 0.01f } });
    }

    // [label: weighted cell, fixed-Width text, ellipsized] [value: fixed 138 cell, right-aligned]. Slots past Count are empty.
    private HudElement StatCell(OvPanel p, int i)
    {
        bool Live() => i < p.Count;
        return new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => Live() ? p.Labels[i] : "", Color: () => MutedColor(), Width: StatLabelW,
                NoWrap: true) { Ellipsize = true }, Weight: 1f),
            new SpacerElement(StatLabelGap, 0f),
            new CellElement(new TextElement(() => Live() ? p.Values[i] : "", Color: () => TextColor(), Align: TextAlign.Right,
                NoWrap: true) { Ellipsize = true }, Width: StatValueW),
        }, Gap: 0.01f);
    }

    // IDENTITY: paired rows with a narrow label and the value taking the rest of its half, then Collections across the row.
    private HudElement IdentityPanel()
    {
        var p = _identityPanel;
        int pairs = (p.Capacity + 1) / 2;
        var slots = new HudElement[pairs];
        for (int k = 0; k < pairs; k++)
        {
            int left = 2 * k, right = 2 * k + 1;
            slots[k] = PairLine(IdentityCell(left), right < p.Capacity ? IdentityCell(right) : new SpacerElement(StatHalfW, 0f));
        }
        return CaptionedPanel(() => _loc.T(p.TitleKey), NoHint, DataTag(() => p.CaptionTag, TagCaptionH), new HudElement[]
        {
            new ListElement(() => (p.Count + 1) / 2, slots) { Gap = 0.01f },
            new ConditionalElement(() => _idCollections.Length > 0, FixedLine(StatLineH, IdentityRow(
                () => _loc.T("ei.field.collections"), () => _idCollections))),
        });
    }

    private HudElement IdentityCell(int i)
        => IdentityRow(() => i < _identityPanel.Count ? _identityPanel.Labels[i] : "",
                       () => i < _identityPanel.Count ? _identityPanel.Values[i] : "");

    private HudElement IdentityRow(Func<string> label, Func<string> value) => new RowElement(new HudElement[]
    {
        new CellElement(new TextElement(label, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }, Width: IdLabelW),
        new SpacerElement(StatLabelGap, 0f),
        new CellElement(new TextElement(value, Color: () => TextColor(), Width: IdValueMinW, Align: TextAlign.Right, NoWrap: true)
            { Ellipsize = true }, Weight: 1f),
    }, Gap: 0.01f);
}
