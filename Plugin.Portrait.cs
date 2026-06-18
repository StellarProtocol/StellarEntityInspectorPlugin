using Stellar.Abstractions.Services;
namespace Stellar.EntityInspector;

// Header portrait slot: a live, posed, full-outfit 3D render of the inspected player — produced by the game's
// own UI-model pipeline (social-data model → ZModel2RT render feature, the same machinery behind the game's
// character preview boxes), surfaced through IEntityPortrait. Works for self and other players; non-player
// targets show a neutral placeholder of the same size so header geometry never shifts.
public sealed partial class Plugin
{
    // Left pane: fixed width, with Height used as the MIN — the box now flex-fills the window's content height
    // (BuildRenderHost sets flexibleHeight) and the character is aspect-preserved (no stretching on resize).
    private const int PortraitW = 260, PortraitH = 380;

    private HudElement BuildPortrait()
        => new ConditionalElement(() => _target.IsPlayer,
            new RenderTextureHostElement(() => _services.EntityPortrait.Texture, PortraitW, PortraitH,
                OnDrag:           (dx, dy) => _services.EntityPortrait.Orbit(dx, dy),  // click-drag spins the model
                OnScroll:         d        => _services.EntityPortrait.Zoom(d),        // scroll zooms
                OnPan:            (dx, dy) => _services.EntityPortrait.Pan(dx, dy),     // shift+drag pans the camera
                OnViewportResize: (w, h)   => _services.EntityPortrait.SetViewport(w, h),   // RT tracks the pane size
                TransparentBackground: true),   // only the character draws — no dark box behind (user request 2026-06-13)
            new CellElement(new TextElement(() => "◍", MutedCol, Emphasis: true), Width: PortraitW));
}
