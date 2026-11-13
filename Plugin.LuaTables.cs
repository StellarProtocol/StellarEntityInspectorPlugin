using System;
using System.Collections.Generic;
using System.Globalization;

namespace Stellar.EntityInspector;

// Game-table reads the framework doesn't expose, done plugin-side through the game's own Lua table managers (ILua):
//   • title name        — ProfileImageTable[titleId].Name (the ID card's title: idcard_view.lua:609-623; id 6 = none)
//   • breakthrough GS   — EquipTableMap.EquipIdBreakThroughTimeMap[equipId][stage] → EquipBreakThroughTable.EquipGs
//                         (item_binder.lua:575-585; KB Gear-Breakthrough-Visibility.md)
//   • dye zones         — FashionTable[fashionId].ColorPart (+ ColorPartfree) (fashion_colour_disc_sub_view.lua:310-322;
//                         KB Fashion-Dye-Areas.md)
//   • wardrobe part name — ItemTypeTable[region].Name, HeadWearSecond shown as Headwear (fashion_vm.lua GetRegionName)
//   • advanced style     — FashionAdvancedTable[id] {Name, Quality, Icon, FashionId}: unlocked alternate styles (9-digit
//                          ids = base ×100 + NN) are FashionTable rows but NOT ItemTable rows (style_icon_loop_item.lua:31)
//   • personal status    — ChatStatusTable[id] {StatusName, Priority} (friends_main_vm.lua GetFriendsStatus)
// Idioms copied from the game Lua: `Z.TableMgr.GetRow(<mgr>, key, true)` (true = no logError on a miss) and
// `require("table.EquipTableMap")` — the DOT separator the game itself uses (equip_config_data.lua:8), so we get the SAME
// cached module table (a "/" require would load a phantom second copy).
//
// Perf (KB Plugin-Performance-Rules §4): the reader function is compiled ONCE into a _G global; each lookup is a
// one-line call chunk + a typed string readback, once per id (cached for the session, misses included). At most
// LuaLookupsPerTick new ids per 10 Hz rebuild, so a 16-piece outfit never lands in one frame. Never on the per-frame
// path. Every failure is fail-safe: an id that can't be read stays "unknown" and the UI hides the extra info.
public sealed partial class Plugin
{
    private const string LuaFn = "__stellar_ei_tbl";
    private const string LuaOut = "__stellar_ei_out";
    private const int LuaLookupsPerTick = 6;
    private const int LuaMaxAttempts = 2;   // a key whose read failed this often is cached as unknown ("?")

    private enum LuaKind { Title = 1, Breakthrough = 2, DyeZones = 3, RegionName = 4, AdvancedStyle = 5, ChatStatus = 6 }

    // kind → id → raw reply: "" (none), "?" (unknown), or the payload. Pending keys are simply absent.
    private readonly Dictionary<long, string> _luaCache = new();
    private readonly Dictionary<long, int> _luaAttempts = new();
    private readonly Dictionary<long, int[]?> _luaIntCache = new();   // parsed int lists (breakthrough GS, dye zones)
    private bool _luaDefined;
    private int _luaBudget;     // lookups left this tick (reset by BeginLuaTick)
    private int _luaVersion;    // bumps whenever a new reply lands — rebuild dirty checks include it

    private const string LuaDefine = "pcall(function() rawset(_G,'" + LuaFn + "',function(kind,id)\n" +
        "local TM = Z.TableMgr\n" +
        "if kind == 1 then\n" +
        "  local r = TM.GetRow('ProfileImageTableMgr', id, true)\n" +
        "  if not r then return '?' end\n" +
        "  return tostring(r.Name or '')\n" +
        "elseif kind == 2 then\n" +
        "  local ok, map = pcall(require, 'table.EquipTableMap')\n" +
        "  if not ok or type(map) ~= 'table' or not map.EquipIdBreakThroughTimeMap then return '?' end\n" +
        "  local m = map.EquipIdBreakThroughTimeMap[id]\n" +
        "  if not m then return '' end\n" +
        "  local out = {}\n" +
        "  for s = 1, 9 do\n" +
        "    local rid = m[s]\n" +
        "    if not rid then break end\n" +
        "    local row = TM.GetRow('EquipBreakThroughTableMgr', rid, true)\n" +
        "    if not row then return '?' end\n" +
        "    out[#out + 1] = tostring(math.floor(row.EquipGs or 0))\n" +
        "  end\n" +
        "  return table.concat(out, ',')\n" +
        "elseif kind == 3 then\n" +
        "  local r = TM.GetRow('FashionTableMgr', id, true)\n" +
        "  if not r then return '?' end\n" +
        "  local out = {}\n" +
        "  if r.ColorPart then for _, a in ipairs(r.ColorPart) do out[#out + 1] = tostring(math.floor(a)) end end\n" +
        "  if r.ColorPartfree then for _, a in ipairs(r.ColorPartfree) do out[#out + 1] = tostring(math.floor(a)) end end\n" +
        "  return table.concat(out, ',')\n" +
        "elseif kind == 4 then\n" +
        "  local region = (id == 718) and 713 or id\n" +
        "  local r = TM.GetRow('ItemTypeTableMgr', region, true)\n" +
        "  if not r then return '?' end\n" +
        "  return tostring(r.Name or '')\n" +
        "elseif kind == 5 then\n" +
        "  local r = TM.GetRow('FashionAdvancedTableMgr', id, true)\n" +
        "  if not r then return '' end\n" +
        "  return tostring(r.Name or '') .. '\\t' .. tostring(math.floor(r.Quality or 0)) .. '\\t' ..\n" +
        "         tostring(r.Icon or '') .. '\\t' .. tostring(math.floor(r.FashionId or 0))\n" +
        "elseif kind == 6 then\n" +
        "  local r = TM.GetRow('ChatStatusTableMgr', id, true)\n" +
        "  if not r then return '?' end\n" +
        "  return tostring(r.StatusName or '') .. '\\t' .. tostring(math.floor(r.Priority or 0))\n" +
        "end\n" +
        "return '?'\n" +
        "end) end)";

    private static long LuaKey(LuaKind kind, int id) => ((long)kind << 32) | (uint)id;

    // Called once at the start of every 10 Hz rebuild: refills the per-tick lookup budget.
    private void BeginLuaTick() => _luaBudget = LuaLookupsPerTick;

    // The raw reply for (kind, id): null while pending (or Lua isn't ready yet), "?" when it can't be read, else the
    // payload. A miss triggers at most one synchronous lookup (budgeted per tick).
    private string? LuaLookup(LuaKind kind, int id)
    {
        if (id <= 0) return "?";
        long key = LuaKey(kind, id);
        if (_luaCache.TryGetValue(key, out var hit)) return hit;
        if (_luaBudget <= 0) return null;
        var lua = _services.Lua;
        if (!lua.Ready) return null;
        _luaBudget--;
        string? reply = null;
        try
        {
            if (!_luaDefined) { lua.DoString(LuaDefine); _luaDefined = true; }
            lua.DoString("rawset(_G,'" + LuaOut + "',nil) pcall(function() local f = rawget(_G,'" + LuaFn +
                         "') if f then rawset(_G,'" + LuaOut + "', f(" + ((int)kind).ToString(CultureInfo.InvariantCulture) +
                         "," + id.ToString(CultureInfo.InvariantCulture) + ")) end end)");
            reply = lua.ReadGlobalString(LuaOut);
        }
        catch (Exception ex)
        {
            if (_luaAttempts.Count == 0) _services.Log.Warning($"[EntityInspector][Lua] table read failed: {ex.Message}");
        }
        if (reply is null)
        {
            // Nothing parked: the reader function may be gone (a Lua state reset) — define it again next time, and give
            // up on this key after a couple of tries so a table that never answers doesn't cost a chunk every tick.
            _luaDefined = false;
            _luaAttempts.TryGetValue(key, out var n);
            if (++n < LuaMaxAttempts) { _luaAttempts[key] = n; return null; }
            reply = "?";
        }
        _luaCache[key] = reply;
        _luaVersion++;
        return reply;
    }

    // Title display name, "" when the player has no title, null when unknown/pending (the row then hides).
    private const int TitleIdNone = 6;   // idcard_view.lua TITLE_ID_NONE

    private string? TitleName(int titleId)
    {
        if (titleId <= 0 || titleId == TitleIdNone) return "";
        var r = LuaLookup(LuaKind.Title, titleId);
        return r is null or "?" ? null : r;
    }

    // Upgraded GS per breakthrough stage (index 0 = stage 1); empty = the piece has no stages; null = unknown/pending.
    private int[]? BreakthroughGs(int equipId) => LuaIntList(LuaKind.Breakthrough, equipId, sortForDye: false);

    // The piece's dye zones (EFashionColorAreaType ids) in the game's slot order; empty = can't be dyed; null = unknown.
    private int[]? DyeZones(int fashionId) => LuaIntList(LuaKind.DyeZones, fashionId, sortForDye: true);

    // Wardrobe part label for a region (fashion slot) code; null = unknown/pending.
    private string? RegionName(int region)
    {
        var r = LuaLookup(LuaKind.RegionName, region);
        return r is null or "?" ? null : r;
    }

    // An alternate (advanced) wardrobe style: Icon is a raw asset address like ItemTable.Icon ("…_icon_f_vampire01up02");
    // BaseId = the base piece. null = pending / not an advanced style / unreadable.
    private readonly record struct AdvancedStyleRow(string Name, int Quality, string Icon, int BaseId);
    private readonly System.Collections.Generic.Dictionary<int, AdvancedStyleRow?> _advCache = new();

    private AdvancedStyleRow? AdvancedStyle(int fashionId)
    {
        if (_advCache.TryGetValue(fashionId, out var hit)) return hit;
        var raw = LuaLookup(LuaKind.AdvancedStyle, fashionId);
        if (raw is null) return null;                      // pending — not cached, asked again next tick
        AdvancedStyleRow? row = null;
        var f = raw.Split('\t');
        if (f.Length == 4)
        {
            int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var q);
            int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b);
            row = new AdvancedStyleRow(f[0], q, f[2], b);
        }
        _advCache[fashionId] = row;
        return row;
    }

    // A ChatStatusTable row (personal status label + its priority). null = pending / unreadable.
    private readonly record struct ChatStatusRow(string Name, int Priority);
    private readonly System.Collections.Generic.Dictionary<int, ChatStatusRow?> _chatStatusCache = new();

    private ChatStatusRow? ChatStatus(int id)
    {
        if (_chatStatusCache.TryGetValue(id, out var hit)) return hit;
        var raw = LuaLookup(LuaKind.ChatStatus, id);
        if (raw is null) return null;                      // pending — asked again next tick
        ChatStatusRow? row = null;
        var f = raw.Split('\t');
        if (f.Length == 2 && int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prio))
            row = new ChatStatusRow(f[0], prio);
        _chatStatusCache[id] = row;
        return row;
    }

    private int[]? LuaIntList(LuaKind kind, int id, bool sortForDye)
    {
        long key = LuaKey(kind, id);
        if (_luaIntCache.TryGetValue(key, out var parsed)) return parsed;
        var raw = LuaLookup(kind, id);
        if (raw is null) return null;
        if (raw == "?") { _luaIntCache[key] = null; return null; }
        var parts = raw.Length == 0 ? Array.Empty<string>() : raw.Split(',');
        var list = new List<int>(parts.Length);
        foreach (var p in parts)
            if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                list.Add((int)d);
        if (sortForDye) list.Sort(CompareDyeArea);
        var arr = list.ToArray();
        _luaIntCache[key] = arr;
        return arr;
    }

    // The game's dye-slot order (fashion_colour_disc_sub_view.lua:292-322): by area id, but Socks (5-8) and UnderWear
    // (13-16) sort as id+100, i.e. last. The slot number the player sees is the 1-based position in this order.
    private static int DyeSortKey(int area) => area is >= 5 and <= 8 or >= 13 and <= 16 ? area + 100 : area;
    private static int CompareDyeArea(int a, int b) => DyeSortKey(a).CompareTo(DyeSortKey(b));
}
