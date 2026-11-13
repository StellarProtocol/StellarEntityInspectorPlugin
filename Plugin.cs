using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

/// <summary>
/// ZDPS-style per-entity character panel. Opened by right-clicking a CombatMeter row (the meter renders an
/// "Inspect" item this plugin registers via <see cref="IEntityContextMenu"/>). Shows one entity's identity,
/// full broadcast attributes, gear, and skill book — for any in-AOI entity, sourced from
/// <see cref="IEntityDetail"/> / <see cref="ICombatLookup"/> cross-referenced with game-data tables. Derived
/// combat stats (ATK/secondary) are self-only (the wire broadcasts those as 0 for others); the live 3D portrait
/// works for any player (social-data model via <see cref="IEntityPortrait"/>). State is snapshotted on a
/// throttled tick so the element Funcs never scan/allocate per poll.
/// </summary>
public sealed partial class Plugin : IStellarPlugin, Stellar.PluginContracts.IFrozenEntityViewer
{
    public string Name => "EntityInspector";

    private readonly IPluginServices _services;
    private readonly ILocalization _loc;
    private IWindowControl _window = null!;
    private IDisposable _inspectItem = null!;
    private IDisposable _inspectAction = null!;

    private EntityId _target = EntityId.None;
    private Tab _tab = Tab.Overview;
    private bool _showWeapon;   // portrait weapon visibility (persisted: config section "portrait", key "showWeapon")

    // Portrait lighting is hardcoded to the "Vivid" custom profile (preset 4) — the picker was removed, so there is
    // no config for lighting anymore. (See PortraitModelHost.Lighting.cs: 0..4 = the custom profiles; 4 = Vivid.)
    private const int VividPreset = 4;

    private enum Tab { Overview, Gear, SkillBook, Wardrobe, Location, Party }

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc = services.Localization;
        _services.Log.Info("[EntityInspector] plugin constructed");

        // Restore the portrait weapon-visibility choice and apply it to the (global) portrait service up front.
        _showWeapon = _services.Config.GetSection("portrait").Get<bool>("showWeapon", false);
        _services.EntityPortrait.ShowWeapon = _showWeapon;

        // Location tab view: full region map (default) vs zoom-around-markers.
        _locFullMap = _services.Config.GetSection("location").Get<bool>("fullMap", true);

        // Portrait lighting is fixed to Vivid (no picker, no config) — always apply it up front.
        _services.EntityPortrait.LightPreset = VividPreset;

        InitOverview();   // the Overview panels' row pools must exist before BuildRoot() runs

        _window = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "entityinspector.main",
                Title:       _loc.T("ei.window.title"),
                DefaultRect: new WindowRect(80f, 80f, 930f, 620f),
                Category:    WindowCategory.HUD,
                // GlassMenu (was Party): the inspector is a full menu window like DataInspector /
                // ModuleOptimizer — the themed glass chrome with a real title bar (drag handle + ✕)
                // matches the game's menu visual language; the flat Party overlay chrome did not
                // (user-requested 2026-06-13).
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, Closable = true, Draggable = true,
              // Gameplay HUD: inspects in-world entities — draw only while in-world and not on a loading screen.
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0,
              // House-style redesign (approved 2026-11-13): default = minimum = 930 × 620 — the 340 px PORTRAIT panel +
              // the two-column Overview / 6-tab track need it; the Location map keeps scaling when it grows.
              // Prebuild: the window (and its KeepAlive tabs) is built in the background, so the first open and every tab
              // switch are instant (KB Plugin-Performance-Rules §5).
              Prebuild = true,
              Resizable = true, MinWidth = 930f, MinHeight = 620f, MaxWidth = 1400f, MaxHeight = 1100f },
            BuildRoot(),
            OnClose: CloseInspector));

        _gearDetailWindow = RegisterGearDetailWindow();
        _tipWindow = RegisterTipWindow();

        // Register the "Inspect" item into the meter's row context menu — visible only for player rows.
        _inspectItem = _services.EntityContextMenu.Register("Inspect", id => id.IsPlayer, Open);

        // Second trigger path: contribute an "Inspect" button to the game's native profile card action bar
        // via the generic IProfileCardActions service. The framework injects + styles the button (magnifier
        // icon + label) and, on click, resolves the carded player and invokes Open (which guards IsPlayer).
        _inspectAction = _services.ProfileCardActions.Register(
            new ProfileCardActionSpec("inspect", "Inspect", BuildMagnifierPng(), Open));

        // Offer the frozen-entity viewer capability over the inter-plugin exchange — CombatMeter's history Inspect
        // button consumes it to render a past session's snapshot here, falling back to its own view when absent.
        _services.Exchange.Provide<Stellar.PluginContracts.IFrozenEntityViewer>(this);

        _services.Framework.Update += OnUpdate;

        if (VerticalRotationPatch.Install(_services.Harmony.Create("vertrot"), _services.Log.Info))
            _services.Log.Info("[EntityInspector] vertical rotation patch installed");
        else
            _services.Log.Warning("[EntityInspector] vertical rotation patch failed — portrait drag will stay clamped");
    }

    /// <summary>Open the inspector on an entity (the IEntityContextMenu "Inspect" action).</summary>
    public void Open(EntityId entity)
    {
        if (!entity.IsPlayer) return;
        _frozen = null;                                // a live Open() leaves any frozen-session view
        _gearDetailWindow.SetVisible(false);           // retargeting must not leave a stale item popup
        _target = entity;
        ResetLocationView();                        // new subject: the map starts from the saved preset again
        VerticalRotationPatch.Activate(true);
        VerticalRotationPatch.ResetPitch();
        RebuildSnapshots();                            // before subscribe: the broadcast snapshot decides which ids need polling
        if (IsSelf) SubscribeSelfStats();
        else UnsubscribeSelfStats();                   // retargeting self → other releases the poll set (review finding)
        _window.SetVisible(true);
        _window.BringToFront();
        // Start the portrait directly (do NOT gate on _window.IsShown — it isn't necessarily true
        // synchronously right after SetVisible, which left the portrait box blank).
        _services.EntityPortrait.Show(entity);
        _services.EntityPortrait.ShowWeapon = _showWeapon;   // re-assert the (global) choice for the new subject
        _services.EntityPortrait.LightPreset = VividPreset;  // re-assert the fixed Vivid lighting for the new subject
    }

    private void CloseInspector()
    {
        _frozen = null;                        // closing a frozen-session view returns to live on next open
        _window.SetVisible(false);
        _gearDetailWindow.SetVisible(false);   // a floating detail popup must not outlive its inspector
        CloseTip();                            // nor its help popover
        _services.EntityPortrait.Hide();       // release the portrait model when hidden
        VerticalRotationPatch.Activate(false);
        UnsubscribeSelfStats();                // stop the self-stat poll set while the window is closed
        DropLocationBase();                    // free the cached map art (up to 16 MB) while closed
    }

    public void Dispose()
    {
        _services.Framework.Update -= OnUpdate;
        _services.EntityPortrait.Hide();
        VerticalRotationPatch.Uninstall();
        _inspectItem.Dispose();
        _inspectAction.Dispose();
        UnsubscribeSelfStats();
        DisposeLocation();
        _gearDetailWindow.Remove();
        _tipWindow.Remove();
        _window.Remove();
    }

    private bool IsSelf => _target.IsPlayer && _target == _services.CombatSnapshot.LocalEntityId;

    // Snapshot the visible tab at the framework's capped cadence (~10 Hz) so the window's element Funcs read
    // cached lists instead of copying the attr map / scanning equipment every poll.
    private const float SnapshotIntervalS = 0.1f;
    private float _accum;

    private void OnUpdate(float dt)
    {
        TickAutoOpenFlag(dt);
        if (!_window.IsShown) return;
        VerticalRotationPatch.ApplyPitch(); // per-tick ZModel2RTMono write (after ZModel2RT.Update clamps)
        TickLocationView();                 // per frame: a map drag/scroll re-renders now, not at 10 Hz
        _accum += dt;
        if (_accum < SnapshotIntervalS) return;
        _accum = 0f;
        RebuildSnapshots();
    }

    // Verification affordance (same class as STELLAR_AUTONAV): synthetic mouse buttons are invisible to
    // UnityEngine.Input on this Wine/Proton stack, so automated in-world checks can't right-click a meter
    // row. Dropping `stellar/inspect-self.flag` (relative to the game CWD) before launch makes the
    // inspector open on SELF once the local entity exists. One File.Exists per ~5 s, stops after ~2 min
    // or first trigger; the flag file is deleted on consume so it never re-fires.
    private const string AutoOpenFlagPath = "stellar/inspect-self.flag";
    private float _flagAccum;
    private int _flagChecks;

    private void TickAutoOpenFlag(float dt)
    {
        if (_flagChecks >= 24) return;
        _flagAccum += dt;
        if (_flagAccum < 5f) return;
        _flagAccum = 0f;
        _flagChecks++;
        try
        {
            if (!System.IO.File.Exists(AutoOpenFlagPath)) return;
            var self = _services.CombatSnapshot.LocalEntityId;
            if (!self.IsPlayer) return;                      // not in-world yet — keep polling
            // The flag's content selects the tab ("gear" → Gear; empty/anything else → Overview),
            // because tab BUTTONS can't be clicked by automation (synthetic mouse is Input-invisible).
            var tab = System.IO.File.ReadAllText(AutoOpenFlagPath).Trim().ToLowerInvariant();
            System.IO.File.Delete(AutoOpenFlagPath);
            _flagChecks = 24;
            if (tab == "gear") _tab = Tab.Gear;
            else if (tab == "skills") _tab = Tab.SkillBook;
            else if (tab == "wardrobe") _tab = Tab.Wardrobe;
            else if (tab == "location") _tab = Tab.Location;
            else if (tab == "party") _tab = Tab.Party;
            _services.Log.Info($"[EntityInspector] inspect-self.flag consumed — opening self (tab={_tab})");
            Open(self);
        }
        catch { _flagChecks = 24; }                          // permissions/path oddity: give up quietly
    }

    // Snapshot of the target's full broadcast attr map — copied once per rebuild (the header + Attributes tab
    // both read it, so this avoids re-copying the dict on every element poll).
    private IReadOnlyDictionary<int, long> _targetAttrs = EmptyAttrs;
    private static readonly IReadOnlyDictionary<int, long> EmptyAttrs = new Dictionary<int, long>();

    // Cached Social.GetSocialData reply for the target (keyed by charId). Section richness depends on
    // the MASK the game requested: nameplate/avatar queries carry identity only (Name + Level), while
    // the native ID card fetches mask 0 = ALL sections (fight point, profession, gear, fashion, guild,
    // party, master score) — so a carded player's snapshot is near-complete even at distance. _isRemote
    // = no AOI broadcast for this target, which gates the "available when nearby" hints on the
    // proximity-only tabs (live stats + skills stay AOI-gated; no request API exists for them).
    private SocialSnapshot? _socialSnap;
    private bool _isRemote;
    // Talent-school id of the target (from spec → ProfessionSpecs.TalentSchool) — keys the v2 school-lib
    // gear-attr lookup so raid/spec gear shows the right advanced-roll ranges. 0 = unresolved.
    private int _targetTalentSchool;

    private void RebuildSnapshots()
    {
        if (!_target.IsPlayer)
        {
            _targetAttrs = EmptyAttrs;
            _socialSnap = null; _isRemote = false; _targetTalentSchool = 0;
            ClearOverview(); _imagines.Clear(); _skillRows.Clear(); _wardrobeRows.Clear();
            _lastSkillSnapshot = null; _lastFashionSnapshot = null;   // keep the dirty checks honest after a clear
            _locState = LocState.None;
            SetPartyState(PartyState.Loading);
            System.Array.Clear(_gearCards, 0, _gearCards.Length);
            _gearSig = int.MinValue; _gearHasData = false; _gearSource = GearSource.None;
            _statusMain = _statusTail = _hdrName = _hdrClass = _hdrNameLine = _statusLine = ""; _stKind = (StatusKind)255;
            _hpLastHp = -1; _hpFrac = _shieldFrac = 0f; _hpLabel = "";
            return;
        }
        _targetAttrs = _frozen?.Attributes ?? _services.EntityDetail.GetAttributes(_target);
        _socialSnap = _frozen is null ? _services.EntityDetail.GetSocialSnapshot(_target) : null;   // no social for frozen
        _isRemote = !IsFrozen && _targetAttrs.Count == 0;   // no broadcast data → far/never-seen; frozen always has data
        _targetTalentSchool = ResolveTalentSchool();
        BeginLuaTick();      // per-tick budget for the plugin-side game-table reads (Plugin.LuaTables.cs)
        RefreshUiScale();    // picks the glyph bitmaps for the current canvas scale
        RebuildImagines();   // the header + PORTRAIT panel are tab-independent
        RebuildVitals();
        RebuildHeader();
        switch (_tab)
        {
            case Tab.Overview:  RebuildOverview();  break;
            case Tab.Gear:      RebuildGear();      break;
            case Tab.SkillBook: RebuildSkillBook(); break;
            case Tab.Wardrobe:  RebuildWardrobe();  break;
            case Tab.Location:  RebuildLocation();  break;
            case Tab.Party:     RebuildParty();     break;
        }
    }
}
