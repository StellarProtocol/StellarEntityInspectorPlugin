using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.EntityInspector;

// Location tab — online / last-seen status (user-approved 2026-11-13). Source = the framework's SocialSnapshot.Presence
// (fw enhance/social-online-status, PR #165): basic_data.offline_time (Unix ms, 0 = online) + personal_state ids, cached
// per section so a thin reply never wipes it. It rides the profile replies the inspector already asks for (the Location
// refresh = a full-mask request) — no request of our own. Self = always online.
// personal_state ids are ChatStatusTable rows (StatusName, Priority; 1 = Online, 6 = Offline): the highest-priority one
// besides those two is shown ("Online · In the dungeon"), read plugin-side once per id (Plugin.LuaTables.cs).
// Unknown presence → "Checking status…" only while a location refresh is in flight, else an empty line (fail-safe).
public sealed partial class Plugin
{
    private enum OnlineState : byte { Unknown, Pending, Online, Offline }

    private string _onLine = "";
    private OnlineState _onLineState = (OnlineState)255;
    private long _onLineAge = long.MinValue;
    private object? _onLineLang;
    private string? _onLineLabel;

    private SocialPresence? TargetPresence() => IsFrozen ? null : _socialSnap?.Presence;

    private OnlineState CurrentOnlineState()
    {
        if (!_target.IsPlayer || IsFrozen) return OnlineState.Unknown;
        if (IsSelf) return OnlineState.Online;
        if (TargetPresence() is { } p) return p.IsOnline ? OnlineState.Online : OnlineState.Offline;
        return RefreshAwaiting && _refreshTarget == _target ? OnlineState.Pending : OnlineState.Unknown;
    }

    // Offline + a drawn map → the "Last known position" pill (Plugin.Location.Ui.cs). The map itself draws normally
    // (user decision 2026-11-13: no dimming).
    private bool LocShowsLastKnown => CurrentOnlineState() == OnlineState.Offline;

    // The status line text (rich text: bold online part), rebuilt only when state / minute / label / language changed.
    private string OnlineStatusLine()
    {
        var st = CurrentOnlineState();
        long age = st == OnlineState.Offline ? OfflineAgeMinutes() : -1;
        var label = st == OnlineState.Online && !IsSelf ? PersonalStatusLabel() : "";
        var lang = _loc.T("ei.location.online");
        if (st == _onLineState && age == _onLineAge && ReferenceEquals(lang, _onLineLang)
            && ReferenceEquals(label, _onLineLabel)) return _onLine;
        _onLineState = st; _onLineAge = age; _onLineLang = lang; _onLineLabel = label;
        _onLine = st switch
        {
            OnlineState.Online  => "● <b>" + (label.Length > 0 ? _loc.TFormat("ei.location.onlineWith", RichSafe(label))
                                                                : _loc.T("ei.location.online")) + "</b>",
            OnlineState.Offline => "● " + _loc.TFormat("ei.location.offline", AgeText(age)),
            OnlineState.Pending => "● " + _loc.T("ei.location.checking"),
            _                   => "",
        };
        return _onLine;
    }

    private ColorRgba? OnlineStatusColor() => CurrentOnlineState() switch
    {
        OnlineState.Online  => SuccessColor(),
        OnlineState.Pending => MutedColor() with { A = 0.6f },
        _                   => MutedColor(),
    };

    private long OfflineAgeMinutes()
    {
        long now = _services.CombatSnapshot.ServerNowMs, off = TargetPresence()?.OfflineTimeMs ?? 0;
        if (now <= 0 || off <= 0) return -1;
        return Math.Max(0L, now - off) / 60_000L;
    }

    // just now / N min ago / N h ago / N days ago (the game's own buckets: union_vm.lua GetLastTimeDesignText).
    private string AgeText(long minutes) => minutes < 1 ? _loc.T("ei.location.seenNow")
        : minutes < 60 ? _loc.TFormat("ei.location.seenMin", minutes)
        : minutes < 60 * 24 ? _loc.TFormat("ei.location.seenHours", minutes / 60)
        : _loc.TFormat("ei.location.seenDays", minutes / (60 * 24));

    // Highest-priority personal status other than Online (1) / Offline (6); "" when none or not readable yet.
    // Allocation-free: the chosen row's name is a cached string, so the caller's reference compare holds steady.
    private string PersonalStatusLabel()
    {
        if (TargetPresence() is not { } p) return "";
        IReadOnlyList<int> ids = p.PersonalStates;
        string best = ""; int bestPrio = int.MinValue;
        for (int i = 0; i < ids.Count; i++)
        {
            int id = ids[i];
            if (id is 0 or 1 or 6) continue;
            if (ChatStatus(id) is not { } row || row.Name.Length == 0) continue;
            if (row.Priority > bestPrio) { bestPrio = row.Priority; best = row.Name; }
        }
        return best;
    }
}
