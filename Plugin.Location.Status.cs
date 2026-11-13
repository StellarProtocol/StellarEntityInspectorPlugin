using System;
using System.Globalization;
using Stellar.Abstractions.Domain;

namespace Stellar.EntityInspector;

// Location tab — online / last-seen status (user-approved 2026-11-13, plugin-only). Source = the game's own social reply,
// requested with the FULL mask: Z.VMMgr.GetVM("social").AsyncGetSocialData(0, charId, token) (social_vm.lua). ⚠️ NOT the
// basic-mask AsyncGetSocialDataTypeBasic: the framework's social cache is last-reply-wins, so a thin basic reply replaced
// a carded player's full snapshot (ability score 0, profession 0 → "Lv 60 · 0" and an empty class tile, 2026-11-13).
// Run in
// Z.CoroUtil.create_coro_xpcall with Z.CancelSource.NeverCancelToken (interaction_action.lua:500). basicData.offlineTime
// == 0 → online, else the Unix-MS time they went offline (union_vm.lua GetLastTimeDesignText: (ServerTime − it) / 1000 =
// seconds → same domain as CombatSnapshot.ServerNowMs). basicData.personalState = status ids into ChatStatusTable
// (StatusName, Priority; 1 = Online, 6 = Offline): the highest-priority non-online one is shown ("Online · In the dungeon").
// Lua side: two functions compiled ONCE into _G; the coroutine stores { s = 0 pending / 1 done / 2 failed, off, lbl } in
// package.loaded["__stellar_ei_status"][charId] (the registry-backed loaded table, shared by every chunk even if a
// chunk's _ENV differed — the AgentBridge saw a _G write vanish between chunks). The reader copies one entry into three
// _G scalars that C# reads back type-strict via ILua (number / number / string) in the SAME tick it runs the reader.
// Requested on Location tab open, manual Refresh and the far auto-refresh (10 s), only while the Location tab shows; one
// request in flight per target; self = always online. Anything unreadable → the line stays empty (fail-safe).
public sealed partial class Plugin
{
    private const string StFnReq = "__stellar_ei_streq", StFnRead = "__stellar_ei_stread";
    private const string StOutS = "__stellar_ei_st_s", StOutOff = "__stellar_ei_st_off", StOutLbl = "__stellar_ei_st_lbl";
    private const long StatusTimeoutMs = 15_000;

    private const string StatusDefine = "pcall(function()\n" +
        "rawset(_G,'" + StFnReq + "',function(cid)\n" +
        "  local store = package.loaded['__stellar_ei_status']\n" +
        "  if type(store) ~= 'table' then store = {}; package.loaded['__stellar_ei_status'] = store end\n" +
        "  local e = store[cid]\n" +
        "  if e and e.s == 0 then return end\n" +
        "  e = { s = 0 }; store[cid] = e\n" +
        "  Z.CoroUtil.create_coro_xpcall(function()\n" +
        "    local vm = Z.VMMgr.GetVM('social')\n" +
        "    local d = vm.AsyncGetSocialData(0, cid, Z.CancelSource.NeverCancelToken)\n" +
        "    local b = d and d.basicData\n" +
        "    if not b then e.s = 2; return end\n" +
        "    e.off = tonumber(b.offlineTime) or 0\n" +
        "    local lbl = ''\n" +
        "    if e.off == 0 and type(b.personalState) == 'table' then\n" +
        "      local tm = Z.TableMgr.GetTable('ChatStatusTableMgr'); local best\n" +
        "      for _, v in pairs(b.personalState) do\n" +
        "        if v and v ~= 0 and v ~= 1 and v ~= 6 then\n" +
        "          local r = tm.GetRow(v, true)\n" +
        "          if r and (not best or (r.Priority or 0) > (best.Priority or 0)) then best = r end\n" +
        "        end\n" +
        "      end\n" +
        "      if best then lbl = tostring(best.StatusName or '') end\n" +
        "    end\n" +
        "    e.lbl = lbl; e.s = 1\n" +
        "  end, function() e.s = 2 end)()\n" +
        "end)\n" +
        "rawset(_G,'" + StFnRead + "',function(cid)\n" +
        "  local store = package.loaded['__stellar_ei_status']\n" +
        "  local e = type(store) == 'table' and store[cid] or nil\n" +
        "  rawset(_G,'" + StOutS + "', e and e.s or -1)\n" +
        "  rawset(_G,'" + StOutOff + "', e and e.off or 0)\n" +
        "  rawset(_G,'" + StOutLbl + "', e and e.lbl or '')\n" +
        "end)\n" +
        "end)";

    private enum OnlineState : byte { Unknown, Pending, Online, Offline }
    private OnlineState _onState;
    private EntityId _onTarget = EntityId.None;
    private long _onSinceMs, _onOfflineMs;
    private string _onLabel = "";
    private bool _onDefined;
    private string _onLine = "";
    private OnlineState _onLineState = (OnlineState)255;
    private long _onLineAge = long.MinValue;
    private object? _onLineLang;
    private string? _onLineLabel;

    // Offline + a drawn map → the "Last known position" pill (Plugin.Location.Ui.cs). The map itself draws normally
    // (user decision 2026-11-13: no dimming).
    private bool LocShowsLastKnown => _onState == OnlineState.Offline && _onTarget == _target && !IsSelf;

    // Ask for the target's status (tab open / Refresh / far auto-refresh). Only while the Location tab shows.
    private void RequestOnlineStatus()
    {
        if (_tab != Tab.Location || !_window.IsShown || !_target.IsPlayer || IsFrozen) return;
        if (_onTarget != _target) { _onTarget = _target; _onState = OnlineState.Unknown; _onLabel = ""; }
        if (IsSelf) { _onState = OnlineState.Online; return; }
        if (_onPolling) return;                                      // one request in flight per target
        var lua = _services.Lua;
        if (!lua.Ready) return;
        try
        {
            if (!_onDefined) { lua.DoString(StatusDefine); _onDefined = true; }
            lua.DoString(StFnReq + "(" + CharIdOf(_target).ToString(CultureInfo.InvariantCulture) + ")");
            if (_onState == OnlineState.Unknown) _onState = OnlineState.Pending;   // a refresh keeps showing the last answer
            _onSinceMs = Environment.TickCount64;
            _onPolling = true;
        }
        catch (Exception ex) { _services.Log.Warning($"[EntityInspector][Status] request failed: {ex.Message}"); }
    }

    private bool _onPolling;

    // 10 Hz while the Location tab shows: read the answer back while a request is out.
    private void TickOnlineStatus()
    {
        if (_onTarget != _target) { _onState = OnlineState.Unknown; _onPolling = false; RequestOnlineStatus(); }
        if (!_onPolling || IsSelf) return;
        var lua = _services.Lua;
        if (!lua.Ready) return;
        try
        {
            lua.DoString(StFnRead + "(" + CharIdOf(_target).ToString(CultureInfo.InvariantCulture) + ")");
            if (!lua.TryReadGlobalNumber(StOutS, out var s) || s < 0)
            {
                _onDefined = false;   // reader gone (state reset?) — define again on the next request
                if (Environment.TickCount64 - _onSinceMs > StatusTimeoutMs) StatusFailed();
                return;
            }
            if (s == 0) { if (Environment.TickCount64 - _onSinceMs > StatusTimeoutMs) StatusFailed(); return; }
            _onPolling = false;
            if (s != 1) { StatusFailed(); return; }
            lua.TryReadGlobalNumber(StOutOff, out var off);
            _onOfflineMs = (long)off;
            _onLabel = lua.ReadGlobalString(StOutLbl) ?? "";
            _onState = _onOfflineMs == 0 ? OnlineState.Online : OnlineState.Offline;
        }
        catch { StatusFailed(); }
    }

    private void StatusFailed()
    {
        _onPolling = false;
        if (_onState == OnlineState.Pending) _onState = OnlineState.Unknown;   // keep a previous answer, else empty line
    }

    private static long CharIdOf(EntityId id) => id.Value >> 16;   // EntityId = charId << 16 | 640

    // The status line text (rich text: bold online part), rebuilt only when the state / minute / language changed.
    private string OnlineStatusLine()
    {
        var st = _onTarget == _target ? _onState : OnlineState.Unknown;
        long age = st == OnlineState.Offline ? OfflineAgeMinutes() : -1;
        var lang = _loc.T("ei.location.online");
        if (st == _onLineState && age == _onLineAge && ReferenceEquals(lang, _onLineLang)
            && ReferenceEquals(_onLabel, _onLineLabel)) return _onLine;
        _onLineState = st; _onLineAge = age; _onLineLang = lang; _onLineLabel = _onLabel;
        _onLine = st switch
        {
            OnlineState.Online  => "● <b>" + (_onLabel.Length > 0 ? _loc.TFormat("ei.location.onlineWith", RichSafe(_onLabel))
                                                                  : _loc.T("ei.location.online")) + "</b>",
            OnlineState.Offline => "● " + _loc.TFormat("ei.location.offline", AgeText(age)),
            OnlineState.Pending => "● " + _loc.T("ei.location.checking"),
            _                   => "",
        };
        return _onLine;
    }

    private ColorRgba? OnlineStatusColor() => (_onTarget == _target ? _onState : OnlineState.Unknown) switch
    {
        OnlineState.Online  => SuccessColor(),
        OnlineState.Pending => MutedColor() with { A = 0.6f },
        _                   => MutedColor(),
    };

    private long OfflineAgeMinutes()
    {
        long now = _services.CombatSnapshot.ServerNowMs;
        if (now <= 0 || _onOfflineMs <= 0) return -1;
        return Math.Max(0L, now - _onOfflineMs) / 60_000L;
    }

    // just now / N min ago / N h ago / N days ago (the game's own buckets: union_vm.lua GetLastTimeDesignText).
    private string AgeText(long minutes) => minutes < 1 ? _loc.T("ei.location.seenNow")
        : minutes < 60 ? _loc.TFormat("ei.location.seenMin", minutes)
        : minutes < 60 * 24 ? _loc.TFormat("ei.location.seenHours", minutes / 60)
        : _loc.TFormat("ei.location.seenDays", minutes / (60 * 24));
}
