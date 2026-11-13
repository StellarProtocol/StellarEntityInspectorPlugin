using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// The right column's pill tab track + the six tab bodies (approved prototype): a 32 px recessed track (r15) holding six
// equal-width 28 px pill tabs (r13). Active = accent@0.25 fill + accent bold label; hover = Text@0.08. The label is bold
// in both states (only its colour is live), so a tab never changes size.
// Bodies: six KeepAlive Conditionals alone in their own Column (framework 2.30.0): each is built + laid out in the
// background after the window mounts (Prebuild) and PARKED when hidden instead of deactivated, so switching tabs is
// instant (KB WindowBuilder-Patterns "KeepAlive tabs") and the window never jumps — every branch takes the slot's size.
public sealed partial class Plugin
{
    private const float TabTrackH = 32f, TabH = 28f, TabTrackPad = 2f;
    private const float TabActiveWash = 0.25f, TabHoverWash = 0.08f;

    private static readonly (Tab Tab, string Key)[] TabDefs =
    {
        (Tab.Overview, "ei.tab.overview"), (Tab.Gear, "ei.tab.gear"), (Tab.SkillBook, "ei.tab.skills"),
        (Tab.Wardrobe, "ei.tab.wardrobe"), (Tab.Location, "ei.tab.location"), (Tab.Party, "ei.tab.party"),
    };

    private HudElement BuildTabTrack()
    {
        var cells = new HudElement[TabDefs.Length];
        for (int i = 0; i < TabDefs.Length; i++) cells[i] = new CellElement(TabPill(TabDefs[i].Tab, TabDefs[i].Key), Weight: 1f);
        return new BubbleElement(new RowElement(cells, Gap: 2f))
        {
            FillColorFn = () => DisabledShade,   // recessed: translucent black over the window (fades with it by itself)
            CornerRadius = RoundRadius(TabTrackH), PaddingX = TabTrackPad, PaddingY = TabTrackPad,
        };
    }

    // One tab: Column → the Bubble fills the cell width (a Bubble hugs in a Row, fills in a Column), the Bare selectable
    // inside carries the click + hover, the centred bold label sits between 28 px struts.
    private HudElement TabPill(Tab tab, string key)
    {
        var hover = new HoverFlag();
        var activeText = new ContrastCache();
        ColorRgba ActiveFill() => Over(AccentColor() with { A = TabActiveWash }, Over(DisabledShade, Menu.MenuBackground));
        return new ColumnElement(new HudElement[]
        {
            new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(4f, TabH),
                    new CellElement(DropBold(new TextElement(() => _loc.T(key),
                        Color: () => _tab == tab ? activeText.Get(AccentColor(), ActiveFill(), PillMinContrast) : TextColor(),
                        Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }), Weight: 1f),
                    new SpacerElement(4f, TabH),
                }, Gap: 0.01f, Justify: RowJustify.Center),
                OnClick: () => SelectTab(tab)) { Bare = true, OnHover = over => hover.On = over })
            {
                FillColorFn = () => _tab == tab ? Fade(Menu.MenuAccent with { A = TabActiveWash })
                                  : hover.On ? Fade(Menu.MenuText with { A = TabHoverWash }) : Transparent,
                CornerRadius = RoundRadius(TabH), PaddingX = 0f, PaddingY = 0f,
            },
        }, Gap: 0.01f);
    }

    private HudElement BuildTabBodies() => new ColumnElement(new HudElement[]
    {
        TabBody(Tab.Overview,  BuildOverviewBody(),  "overview"),
        TabBody(Tab.Gear,      BuildGearBody(),      "gear"),
        TabBody(Tab.SkillBook, BuildSkillBookBody(), "skills"),
        TabBody(Tab.Wardrobe,  BuildWardrobeBody(),  "wardrobe"),
        TabBody(Tab.Location,  BuildLocationBody(),  "location"),
        TabBody(Tab.Party,     BuildPartyBody(),     "party"),
    }, Gap: 0.01f);

    private HudElement TabBody(Tab tab, HudElement body, string name)
        => new ConditionalElement(() => _tab == tab, body, Fill: true)
        {
            KeepAlive = true, Prebuild = true, PrebuildName = "entityinspector." + name,
        };

    private void SelectTab(Tab t)
    {
        if (_tab == t) return;
        _tab = t;
        _gearDetailWindow.SetVisible(false);           // the item popup belongs to the Gear tab's context
        if (t == Tab.Party) RequestPartyRefresh();     // a current roster on every tab open (floored per target)
        if (t == Tab.Location) RequestOnlineStatus();  // online / last seen, asked on every tab open
        RebuildSnapshots();                            // the newly shown tab's data now, not on the next 10 Hz tick
    }
}
