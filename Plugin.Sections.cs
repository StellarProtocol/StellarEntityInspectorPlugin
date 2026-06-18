using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Shared section-list row builder, used by BOTH the Overview tab and the gear-detail popup so their
// section styling stays identical (it was duplicated in each — extracted 2026-06-13). Each pooled slot
// renders as ONE of two variants: a section HEADER (breathing room above + bold accent title + rule
// line — plain gold text alone didn't read as a header, user-flagged in-world) or a normal label/value
// stat row. The caller supplies accessors bound to its own label/value lists + header predicate.
public sealed partial class Plugin
{
    private HudElement BuildSectionRow(
        System.Func<string> label, System.Func<string> value, System.Func<bool> isHeader, float labelWidth)
        => new ColumnElement(new HudElement[]
        {
            new ConditionalElement(isHeader, new ColumnElement(new HudElement[]
            {
                new SpacerElement(Height: 8f),
                new TextElement(label, AccentCol, Emphasis: true),
                new SeparatorElement(),
            }, Gap: 2f)),
            new ConditionalElement(() => !isHeader(), new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(label, MutedCol), Width: labelWidth),
                new CellElement(new TextElement(value, Align: TextAlign.Right), Weight: 1f),
            })),
        }, Gap: 0f);
}
