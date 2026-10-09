using System;
using Stellar.Abstractions.Domain;

namespace Stellar.EntityInspector;

// Location tab — keeping a FAR player's social location fresh. IEntityDetail.RefreshSocialSnapshot originates the
// game's own full-mask (mask 0) Social.GetSocialData for any charId, and that reply carries scene_data; the framework
// cache stamps SocialLocation.ReceivedAtMs (see Social-Location-Probe.md). No UI opens, but the framework has NO
// throttle and the game's AsyncGetSocialData shows a tips toast on a non-zero errCode — so every request (auto and
// manual) goes through one per-target hard floor here. In-AOI targets have a live position and send nothing.
// Intervals are fixed code defaults (repo Rule 12: no auto-tuning).
public sealed partial class Plugin
{
    private const long LocAutoRefreshMs = 5_000;   // auto re-request period while on the social path
    private const long LocRefreshFloorMs = 5_000;  // hard floor per target, auto + manual combined (= manual cooldown)
    private const long LocAwaitMs = 10_000;        // a request with no reply by then stops counting as in flight
    private const long LocFetchHintMs = 10_000;    // "Fetching location…" until then, then the profile-card hint
    private const long NeverMs = -1_000_000_000L;   // "long ago" without the overflow of long.MinValue arithmetic

    private bool _locLive;                         // set by UpdateLocation: target position came from the live transform
    private EntityId _refreshTarget = EntityId.None;
    private long _refreshLastMs = NeverMs;          // Environment.TickCount64 of the last request for _refreshTarget
    private long _refreshFirstMs = long.MinValue;  // first request for this target (drives the fetching hint)
    private long _refreshAwaitSince = long.MinValue;
    private long _refreshBaseline;                 // ReceivedAtMs when the in-flight request went out
    private long _refreshSeenAt;                   // last ReceivedAtMs observed (new one ⇒ re-render)
    private bool _refreshEligible;

    private long LocTargetReceivedAt => _socialSnap?.Location?.ReceivedAtMs ?? 0;
    private bool RefreshAwaiting => _refreshAwaitSince != long.MinValue;
    private bool RefreshCooling => Environment.TickCount64 - _refreshLastMs < LocRefreshFloorMs;
    private bool CanShowRefresh => _target.IsPlayer && !IsSelf && !IsFrozen && !_locLive
        && _locState is LocState.Ok or LocState.Far or LocState.NoMap or LocState.Loading;

    // "Fetching location…" while the first request for a target is young and nothing has arrived yet.
    private bool LocFetching => _locState == LocState.Far && _refreshTarget == _target
        && _refreshFirstMs != long.MinValue && Environment.TickCount64 - _refreshFirstMs < LocFetchHintMs;

    // Called from RebuildLocation (10 Hz, only while the window is shown on the Location tab, never for a frozen view)
    // — so closing the window or leaving the tab stops the auto requests by itself.
    private void TickLocationRefresh()
    {
        long now = Environment.TickCount64;
        if (_refreshTarget != _target)
        {
            _refreshTarget = _target;
            _refreshLastMs = NeverMs;
            _refreshFirstMs = _refreshAwaitSince = long.MinValue;
            _refreshSeenAt = LocTargetReceivedAt;
            _refreshEligible = false;
        }

        long rx = LocTargetReceivedAt;
        if (rx != _refreshSeenAt)
        {
            _refreshSeenAt = rx;
            _lastSu = float.NaN;   // a reply landed: redraw even if the position didn't move
        }
        if (RefreshAwaiting && (rx != _refreshBaseline && rx > 0 || now - _refreshAwaitSince >= LocAwaitMs))
            _refreshAwaitSince = long.MinValue;

        bool eligible = CanShowRefresh;
        bool entered = eligible && !_refreshEligible;
        _refreshEligible = eligible;
        if (!eligible) return;
        if (entered || now - _refreshLastMs >= LocAutoRefreshMs) TryRequestLocation(now);
    }

    // Manual "Refresh" — same floor; a click during the cooldown is simply ignored (the button shows it disabled).
    private void RefreshLocationNow()
    {
        if (!CanShowRefresh) return;
        if (_refreshTarget != _target) TickLocationRefresh();
        TryRequestLocation(Environment.TickCount64);
    }

    private void TryRequestLocation(long now)
    {
        if (now - _refreshLastMs < LocRefreshFloorMs) return;
        _refreshLastMs = now;
        if (_refreshFirstMs == long.MinValue) _refreshFirstMs = now;
        _refreshAwaitSince = now;
        _refreshBaseline = LocTargetReceivedAt;
        try { _services.EntityDetail.RefreshSocialSnapshot(_target); }
        catch (Exception ex) { _services.Log.Warning($"[EntityInspector][Location] refresh failed: {ex.Message}"); }
    }

    private string RefreshLabel() => _loc.T(RefreshAwaiting ? "ei.location.refreshing" : "ei.location.refresh");
    private bool RefreshEnabled() => !RefreshAwaiting && !RefreshCooling;
}
