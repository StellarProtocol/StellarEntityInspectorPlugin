using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Location tab — UI (approved prototype + "Location empty states" amendment 2026-11-13). One LOCATION panel:
//   map (or, when no map can be drawn, a same-size dashed box: pin tile · bold title · muted explanation)
//   bar row: [Full map] [Focus] (Neutral, map shown only) · spacer · [Refresh] (Primary, far targets only)
//   zone line (bold "<area> · Line N") and the muted distance · age line, each whenever known.
// The map host and the empty box are two Fill Conditionals sharing the panel's leftover height, so switching between them
// never moves the window. Caption: hint "drag: pan · wheel: zoom" while the map shows; Profile tag when the location came
// from their profile reply.
// ⚠️ The amendment's border is DASHED; BubbleElement only has a solid 1 px outline, so the box uses a faint solid ring
// (toolkit limit, as the Wardrobe empty states).
public sealed partial class Plugin
{
    private const float LocPillLabelW = 96f;   // longest "Full map" / "Focus" / "Refresh" / "Refreshing…" in any locale
    private const float LocTileSize = 32f;
    private bool _locFromSocial;               // set by UpdateLocation: the target's position came from the profile reply

    private HudElement BuildLocationBody() => CaptionedPanel(
        () => _loc.T("ei.tab.location"), () => _locState == LocState.Ok ? _loc.T("ei.location.hint") : "",
        DataTag(() => _locFromSocial && !IsSelf ? TagKind.Profile : TagKind.None, TagCaptionH),
        new HudElement[]
        {
            new ConditionalElement(() => _locState == LocState.Ok,
                new RenderTextureHostElement(() => _locRaster?.Texture, LocPaneMin, LocPaneMin,
                    OnDrag: OnLocationDrag, OnScroll: OnLocationScroll, OnViewportResize: OnLocationPaneResize),
                Fill: true),
            new ConditionalElement(() => _locState != LocState.Ok, LocationEmptyBox(), Fill: true),
            new RowElement(new HudElement[]
            {
                new ConditionalElement(() => _locState == LocState.Ok, new RowElement(new HudElement[]
                {
                    NeutralPill(() => _loc.T("ei.location.fullMap"), ShowFullMap, LocPillLabelW),
                    NeutralPill(() => _loc.T("ei.location.focus"), FocusTarget, LocPillLabelW),
                }, Gap: 6f)),
                new SpacerElement(),
                new ConditionalElement(() => CanShowRefresh,
                    PrimaryPill(RefreshLabel, RefreshLocationNow, LocPillLabelW, RefreshEnabled)),
                new SpacerElement(0f, PillH),   // the bar keeps its height when it has no pill
            }, Gap: 6f),
            StateLine(() => _locZone, () => TextColor(), bold: true),
            StateLine(LocDetailLine, () => MutedColor()),
        });

    // Same-size placeholder for the map: vertically centred via two Fill spacers (a Fill Conditional is the toolkit's
    // only flexible-height spacer).
    private HudElement LocationEmptyBox() => new BubbleElement(new ColumnElement(new HudElement[]
    {
        new ConditionalElement(() => true, new SpacerElement(0f, 1f), Fill: true),
        new RowElement(new HudElement[]
        {
            new CellElement(new BubbleElement(new RowElement(new HudElement[]
                {
                    Glyph(PinIcon, () => AccentColor()),
                }, Gap: 0.01f, Justify: RowJustify.Center))
            {
                FillColorFn = () => Fade(Over(AccentColor() with { A = ChipWashAlpha }, PanelColor())),
                CornerRadius = 7f, PaddingX = (LocTileSize - 16f) / 2f, PaddingY = (LocTileSize - 16f) / 2f,
            }, Width: LocTileSize),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        new SpacerElement(0f, 6f),
        new TextElement(LocEmptyTitle, Color: () => TextColor(), Align: TextAlign.Center, FontSize: 13) { Bold = true },
        new ConditionalElement(() => LocEmptyBody().Length > 0,
            new TextElement(LocEmptyBody, Color: () => MutedColor(), Align: TextAlign.Center)),
        new ConditionalElement(() => true, new SpacerElement(0f, 1f), Fill: true),
    }, Gap: 4f))
    {
        FillColorFn = () => Transparent,
        OutlineColorFn = () => Fade(TextColor() with { A = 0.15f }),
        CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
    };

    private bool LocIsFetching => _locState == LocState.Far && (LocFetching || RefreshAwaiting);

    private string LocEmptyTitle() => _loc.T(_locState switch
    {
        LocState.NoMap       => "ei.location.noMap",
        LocState.Loading     => "ei.location.loading",
        LocState.Unavailable => "ei.location.unavailable",
        LocState.Frozen      => "ei.location.frozen",
        LocState.Far         => LocIsFetching ? "ei.location.fetching" : "ei.location.noLocation",
        _                    => "ei.location.fetching",
    });

    private string LocEmptyBody() => _locState switch
    {
        LocState.NoMap       => _loc.T("ei.location.noMapBody"),
        LocState.Unavailable => _loc.T("ei.location.unavailableBody"),
        LocState.Frozen      => _loc.T("ei.location.frozenBody"),
        LocState.Far         => LocIsFetching ? "" : _loc.T("ei.location.noLocationBody"),
        _                    => "",
    };

    // "84 m NE · as of 3 min ago" — distance and/or age, recomposed only when a part changed (both are rebuilt at 10 Hz
    // by UpdateLocation, so reference equality tracks them).
    private string? _ldLine, _ldAge;
    private string _ldOut = "";

    private string LocDetailLine()
    {
        if (ReferenceEquals(_locLine, _ldLine) && ReferenceEquals(_locAge, _ldAge)) return _ldOut;
        _ldLine = _locLine; _ldAge = _locAge;
        _ldOut = _locLine.Length > 0 && _locAge.Length > 0 ? _locLine + " · " + _locAge : _locLine + _locAge;
        return _ldOut;
    }
}
