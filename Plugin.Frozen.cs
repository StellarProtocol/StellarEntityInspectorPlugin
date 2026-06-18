using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.PluginContracts;

namespace Stellar.EntityInspector;

public sealed partial class Plugin
{
    // Frozen session-time snapshot supplied via the inter-plugin exchange (CombatMeter history → IFrozenEntityViewer).
    // When set, every data accessor below reads it instead of the live services; cleared on a live Open() / close.
    private FrozenEntity? _frozen;

    internal bool IsFrozen => _frozen is not null;
    private string FrozenLabel => _frozen?.SessionLabel ?? string.Empty;

    /// <summary>
    /// Inter-plugin entry point: show a frozen session-time snapshot in the inspector (the live 3D portrait and
    /// live stat polling are disabled — the snapshot is the source of truth). Returns true (we always render it).
    /// </summary>
    public bool ShowFrozen(FrozenEntity entity)
    {
        _frozen = entity;
        _target = entity.Id;
        _gearDetailWindow.SetVisible(false);
        UnsubscribeSelfStats();                 // frozen data is truth — no live stat poll
        RebuildSnapshots();
        _window.SetVisible(true);
        // Show the entity's 3D model. The portrait builds it from a social-data RPC (AsyncGetSocialData by charId),
        // so it renders for ANY player — online/offline, present/remote — exactly like a live inspect. It reflects
        // the entity's CURRENT appearance; the snapshot freezes stats/gear, not the live model.
        _services.EntityPortrait.Show(entity.Id);
        return true;
    }

    // --- data-source accessors: the frozen snapshot when viewing a session, else the live services ---
    private IReadOnlyList<SkillLevel> TargetSkills()
        => _frozen?.Skills ?? _services.CombatLookup.GetSkillLevels(_target);

    private IReadOnlyList<EquippedItem> TargetGear()
        => _frozen?.Gear ?? _services.EntityDetail.GetEquipment(_target);

    private IReadOnlyList<FashionEntry> TargetFashion()
        => _frozen?.Fashion ?? _services.EntityDetail.GetFashion(_target);

    private EntityVitals TargetVitals()
        => _frozen is { } f ? new EntityVitals(f.Hp, f.MaxHp, true) : _services.CombatLookup.GetVitals(_target);

    private string TargetName()
        => _frozen is { Name.Length: > 0 } f ? f.Name : _services.CombatLookup.GetEntityName(_target) ?? string.Empty;

    private long TargetFightPoint()
        => _frozen?.FightPoint ?? _services.CombatLookup.GetFightPoint(_target);
}
