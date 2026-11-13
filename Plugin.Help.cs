using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// The round "?" in the ToolHeader and the house help popover it opens (ported from StellarWardrobeLoadout Plugin.Help.cs).
// The toolkit has no tooltips, so what each tab shows and what the three data tags mean live behind the "?":
//   [32 px accent tile with a white bold "?"] [bold "Entity Inspector" / muted "ENTITY INSPECTOR · HELP"] … [round ✕]
//   then the help text in one raised tile, 360 wide.
// No framework title bar (the header carries the title and a close); the whole frame drags. Anchored under the clicked
// "?" (screen px → canvas units), re-clicking the "?" closes it.
public sealed partial class Plugin
{
    private IWindowControl _tipWindow = null!;
    private const float TipWidth = 360f;
    private const float TipIconSize = 32f;
    private const float HelpSize = 24f;
    private readonly HoverFlag _helpHover = new();
    private readonly ContrastCache _helpText = new();
    private WindowRect _tipRect;     // where the tip should sit (re-applied for a few frames after opening)
    private int _tipRepositionLeft;

    private IWindowControl RegisterTipWindow()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "entityinspector.help",
                Title:       "Entity Inspector help",   // fallback only (no title bar is drawn); localized below
                DefaultRect: new WindowRect(20f, 120f, TipWidth, 0f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.GlassMenu)
            {
                StartVisible = false, Closable = true, Draggable = true, ShowTitleBar = false,
                TitleProvider = () => _loc.T("ei.help.label"),
                ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                     && (_services.ClientState.UiState & GameUIState.Loading) == 0,
            },
            BuildTipRoot(),
            OnClose: CloseTip));

    private bool TipOpen => _tipWindow is { IsShown: true };

    private void CloseTip()
    {
        _tipRepositionLeft = 0;
        try { _tipWindow?.SetVisible(false); } catch { /* never throw */ }
    }

    // The "?" click: re-clicking the open tip closes it; otherwise anchor it under the "?", opening to the LEFT of it.
    private void ToggleTip(WindowRect r)
    {
        if (TipOpen) { CloseTip(); return; }
        float k = ScreenToCanvas();
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        float x = MathF.Max(10f, r.X + r.Width - TipWidth);
        float y = MathF.Min(r.Y + r.Height + 4f, CanvasHeight() - 40f);
        _tipRect = new WindowRect(x, y, TipWidth, 0f);
        _tipWindow.SetVisible(true);   // opens from hidden → remounts on top (no BringToFront needed, Rule 5)
        _tipWindow.SetRect(_tipRect);
        // The first-ever open mounts the window and applies its DefaultRect AFTER this SetRect → re-assert the anchored
        // rect for the next few frames (one-shot Posts, not a standing per-frame subscription).
        _tipRepositionLeft = 4;
        _services.Framework.Post(TipReposition);
    }

    private void TipReposition()
    {
        if (_tipRepositionLeft <= 0) return;
        _tipRepositionLeft--;
        if (TipOpen) _tipWindow.SetRect(_tipRect);
        if (_tipRepositionLeft > 0) _services.Framework.Post(TipReposition);
    }

    // The round "?" (24 × 24, R1/R4): Neutral over the window, lighter on hover, accent-tinted while its tip is open.
    private HudElement HelpButton() => new BubbleElement(new SelectableElement(
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HelpSize),
            new TextElement(() => "?", Color: () => _helpText.Get(TextColor(), HelpFill(), PillMinContrast), Width: HelpSize,
                Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: () => { })                       // the work is in OnClickWithRect (needs the button's rect)
        {
            Bare = true,
            OnHover = over => _helpHover.On = over,
            OnClickWithRect = ToggleTip,
        })
    {
        FillColorFn = () => Fade(TipOpen ? Over(AccentColor() with { A = 0.35f }, HelpFill())
                                 : _helpHover.On ? HoverFill(HelpFill()) : HelpFill()),
        CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f,
    };

    // The "?" sits on the window background (not a panel): the house Neutral built over the window.
    private ColorRgba HelpFill() => Over(Menu.MenuAccent with { A = NeutralAccentMix },
                                         Over(Menu.MenuMuted with { A = NeutralMix }, Menu.MenuBackground));

    private HudElement BuildTipRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            TipIcon(),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _loc.T("ei.window.title"), Color: () => TextColor(), NoWrap: true, FontSize: 14)
                    { Bold = true, Ellipsize = true },
                new TextElement(TipSubtitle(), Color: () => MutedColor(), NoWrap: true, FontSize: 11)
                    { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            RoundCloseButton(CloseTip),
        }, Gap: 10f),
        new BubbleElement(new TextElement(() => _loc.T("ei.help.body"), Color: () => TextColor()))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);

    // "ENTITY INSPECTOR · HELP", upper-cased once per language.
    private Func<string> TipSubtitle()
    {
        string? title = null, help = null, value = "";
        return () =>
        {
            var t = _loc.T("ei.window.title");
            var h = _loc.T("ei.help.label");
            if (!ReferenceEquals(t, title) || !ReferenceEquals(h, help))
            {
                title = t; help = h;
                value = (t + " · " + h).ToUpperInvariant();
            }
            return value;
        };
    }

    // 32 px accent tile with a white bold "?" (radius 7 keeps the sliced tile's min size under the 32 px side).
    private HudElement TipIcon() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, TipIconSize),
            new TextElement(() => "?", Color: () => White, Width: TipIconSize, Align: TextAlign.Center, NoWrap: true,
                FontSize: 18) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(AccentColor()), CornerRadius = 7f, PaddingX = 0f, PaddingY = 0f,
    }, Width: TipIconSize);
}
