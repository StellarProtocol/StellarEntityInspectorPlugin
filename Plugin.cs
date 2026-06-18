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
    private IWindowControl _window = null!;
    private IDisposable _inspectItem = null!;
    private IDisposable _inspectAction = null!;

    private EntityId _target = EntityId.None;
    private Tab _tab = Tab.Overview;

    private enum Tab { Overview, Gear, SkillBook, Wardrobe }

    public Plugin(IPluginServices services)
    {
        _services = services;
        _services.Log.Info("[EntityInspector] plugin constructed");

        _window = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "entityinspector.main",
                Title:       "Entity Inspector",
                DefaultRect: new WindowRect(80f, 80f, 820f, 600f),
                Category:    WindowCategory.HUD,
                // GlassMenu (was Party): the inspector is a full menu window like DataInspector /
                // ModuleOptimizer — the themed glass chrome with a real title bar (drag handle + ✕)
                // matches the game's menu visual language; the flat Party overlay chrome did not
                // (user-requested 2026-06-13).
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, HideUntilInWorld = true, Closable = true, Draggable = true,
              // MinWidth 780 (was 580): the gear tab's fixed accessory grid (6×76 + spacing ≈ 466px) +
              // the 260px portrait + paddings needs ≥ ~770px window or the right cards clip under the
              // RectMask2D (ux-ui review 2026-06-13, static arithmetic — the old 580 min predates the
              // gear grid). Default bumped 640→820 for the same reason.
              Resizable = true, MinWidth = 780f, MinHeight = 360f, MaxWidth = 1200f, MaxHeight = 1100f },
            BuildRoot(),
            OnClose: CloseInspector));

        _gearDetailWindow = RegisterGearDetailWindow();

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
    }

    /// <summary>Open the inspector on an entity (the IEntityContextMenu "Inspect" action).</summary>
    public void Open(EntityId entity)
    {
        if (!entity.IsPlayer) return;
        _frozen = null;                                // a live Open() leaves any frozen-session view
        _gearDetailWindow.SetVisible(false);           // retargeting must not leave a stale item popup
        _target = entity;
        RebuildSnapshots();                            // before subscribe: the broadcast snapshot decides which ids need polling
        if (IsSelf) SubscribeSelfStats();
        else UnsubscribeSelfStats();                   // retargeting self → other releases the poll set (review finding)
        _window.SetVisible(true);
        // Start the portrait directly (do NOT gate on _window.IsShown — it isn't necessarily true
        // synchronously right after SetVisible, which left the portrait box blank).
        _services.EntityPortrait.Show(entity);
    }

    private void CloseInspector()
    {
        _frozen = null;                        // closing a frozen-session view returns to live on next open
        _window.SetVisible(false);
        _gearDetailWindow.SetVisible(false);   // a floating detail popup must not outlive its inspector
        _services.EntityPortrait.Hide();       // release the portrait model when hidden
        UnsubscribeSelfStats();                // stop the self-stat poll set while the window is closed
    }

    public void Dispose()
    {
        _services.Framework.Update -= OnUpdate;
        _services.EntityPortrait.Hide();
        _inspectItem.Dispose();
        _inspectAction.Dispose();
        UnsubscribeSelfStats();
        _gearDetailWindow.Remove();
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
            _ovLabels.Clear(); _ovValues.Clear(); _imagines.Clear(); _skillRows.Clear(); _wardrobeRows.Clear();
            _lastSkillSnapshot = null; _lastFashionSnapshot = null;   // keep the dirty checks honest after a clear
            System.Array.Clear(_gearCards, 0, _gearCards.Length);
            return;
        }
        _targetAttrs = _frozen?.Attributes ?? _services.EntityDetail.GetAttributes(_target);
        _socialSnap = _frozen is null ? _services.EntityDetail.GetSocialSnapshot(_target) : null;   // no social for frozen
        _isRemote = !IsFrozen && _targetAttrs.Count == 0;   // no broadcast data → far/never-seen; frozen always has data
        _targetTalentSchool = ResolveTalentSchool();
        RebuildImagines();   // header chips are tab-independent
        switch (_tab)
        {
            case Tab.Overview:  RebuildOverview();  break;
            case Tab.Gear:      RebuildGear();      break;
            case Tab.SkillBook: RebuildSkillBook(); break;
            case Tab.Wardrobe:  RebuildWardrobe();  break;
        }
    }

    // Header imagine chips: (name, precomputed star string, icon skill id). Identity = first 2 Battle
    // Imagines in the loadout (same filter the CombatMeter uses). Star source = SkillLevel.Tier
    // (remodel_level) clamped 0-5 — .Level read 1★ for everything in-world 2026-06-13; ZDPS uses
    // RemodelLevel as the tier, confirming the banked check item. Strings are built HERE (10 Hz) so
    // the per-poll chip Funcs are pure cache reads.
    private readonly List<(string Name, string Stars, int SkillId)> _imagines = new(2);
    private static readonly string[] StarStrings =
        { "☆☆☆☆☆", "★☆☆☆☆", "★★☆☆☆", "★★★☆☆", "★★★★☆", "★★★★★" };

    private void RebuildImagines()
    {
        _imagines.Clear();
        foreach (var sl in TargetSkills())
        {
            if (_services.ResonanceData.GetImagineForSkill(sl.SkillId) is not { } info) continue;
            var stars = StarStrings[sl.Tier < 0 ? 0 : sl.Tier > 5 ? 5 : sl.Tier];
            _imagines.Add((info.Name, stars, info.SkillId));
            if (_imagines.Count == 2) break;
        }
    }

    private ColorRgba? MutedCol() => new ColorRgba(0.66f, 0.70f, 0.73f, 1f);

    private void SelectTab(Tab t)
    {
        _tab = t;
        _gearDetailWindow.SetVisible(false);           // popup belongs to the Gear tab's context
        RebuildSnapshots();
    }
}
