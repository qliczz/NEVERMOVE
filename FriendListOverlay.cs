using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace NEVERMOVE;

/// <summary>
/// 好友位置信息辅助类（静态）。
///
/// 旧版曾尝试在原生「社交 → 好友」列表(AddonFriendList)的每行右侧直接覆盖文字，
/// 但依赖读取原生节点坐标，极其脆弱（节点偏移/坐标换算任一不对就整片不显示），
/// 两次修复仍未稳定。v1.0.5 起改为<b>独立 ImGui 窗口</b>（见 FriendListWindow），
/// 本类只保留「取数据 + 生成标签」的纯逻辑，供窗口调用，不再做任何原生绘制。
///
/// 位置分级（按可获取到的信息由精到粗）：
///   ① 同图：若好友已加载（同屏渲染）则给精确坐标 + 距离（绿）；仅同图但未加载则只标「同图」（绿）。
///   ② 同服异地：ZoneID 可知，给地图名（黄）。
///   ③ 跨服（同大区）：只知服务器名（橙）。
///   ④ 跨大区：只知服务器名（红）。
/// 跨服/跨大区无法获取精确坐标（游戏不下发），这与游戏内表现一致。
/// </summary>
public static class FriendListOverlay
{
    private static readonly ConcurrentDictionary<ushort, string> ZoneNames = new();
    private static readonly ConcurrentDictionary<ushort, string> WorldNames = new();
    private static readonly ConcurrentDictionary<ushort, uint> DataCenters = new();

    /// <summary>
    /// 为单个好友生成位置标签（文本 + ImGui 颜色 0xAABBGGRR）。无内容时 Text 为 null。
    /// </summary>
    public static (string? Text, uint Color) BuildTag(
        InfoProxyCommonList.CharacterData data,
        uint localTerritory,
        ushort localWorld,
        uint localDC,
        Vector3 localPos,
        Dictionary<ulong, Vector3> rendered,
        Configuration config)
    {
        var online = (data.State & InfoProxyCommonList.CharacterData.OnlineStatus.Online) != 0;
        if (!online)
        {
            return config.FriendListShowOffline ? ("离线", WorldOverlay.ToAbgr(new Vector4(0.53f, 0.53f, 0.53f, 1f))) : (null, 0);
        }

        var world = data.CurrentWorld;
        var location = data.Location;

        if (world == localWorld)
        {
            // 同服务器
            if (location != 0 && location == localTerritory)
            {
                // ① 同图
                if (config.FriendListShowCoords && rendered.TryGetValue(data.ContentId, out var p))
                {
                    var dist = Vector3.Distance(localPos, p);
                    return ($"同图 {p.X:F0},{p.Z:F0} {dist:F0}m", WorldOverlay.ToAbgr(new Vector4(0f, 1f, 0.4f, 1f)));
                }

                return ("同图", WorldOverlay.ToAbgr(new Vector4(0.2f, 0.9f, 0.5f, 1f)));
            }

            if (location != 0)
            {
                // ② 同服异地：ZoneID 可知
                var zone = GetZoneName(location);
                return (zone != null ? $"地图 {zone}" : "异地", WorldOverlay.ToAbgr(new Vector4(1f, 0.8f, 0.2f, 1f)));
            }

            // 同服但无区域信息（罕见，多见于特定副本/特殊场景）
            return ("同服", WorldOverlay.ToAbgr(new Vector4(1f, 0.8f, 0.2f, 1f)));
        }

        // 不同服务器
        var friendDC = GetDataCenter(world);
        var worldName = GetWorldName(world);
        var label = worldName != null ? worldName : "未知服务器";
        if (friendDC != 0 && friendDC == localDC)
        {
            // ③ 跨服（同大区）
            return ($"跨服 {label}", WorldOverlay.ToAbgr(new Vector4(1f, 0.6f, 0.2f, 1f)));
        }

        // ④ 跨大区
        return ($"跨大区 {label}", WorldOverlay.ToAbgr(new Vector4(1f, 0.27f, 0.27f, 1f)));
    }

    internal static string? GetZoneName(ushort zoneId)
        => ZoneNames.GetOrAdd(zoneId, LookupZoneName);

    private static string LookupZoneName(ushort zoneId)
    {
        try
        {
            var row = Sheets.GetRow("TerritoryType", zoneId);
            if (row == null) return $"地图#{zoneId}";
            var placeName = Sheets.GetRefValue(row, "PlaceName");
            return Sheets.GetStringProp(placeName, "Name") ?? $"地图#{zoneId}";
        }
        catch
        {
            return $"地图#{zoneId}";
        }
    }

    internal static string? GetWorldName(ushort worldId)
        => WorldNames.GetOrAdd(worldId, LookupWorldName);

    private static string LookupWorldName(ushort worldId)
    {
        try
        {
            var row = Sheets.GetRow("World", worldId);
            if (row == null) return $"服务器#{worldId}";
            return Sheets.GetStringProp(row, "Name") ?? $"服务器#{worldId}";
        }
        catch
        {
            return $"服务器#{worldId}";
        }
    }

    internal static uint GetDataCenter(ushort worldId)
        => DataCenters.GetOrAdd(worldId, LookupDataCenter);

    private static uint LookupDataCenter(ushort worldId)
    {
        try
        {
            var row = Sheets.GetRow("World", worldId);
            if (row == null) return 0;
            var dcRef = Sheets.GetRefValue(row, "DataCenter");
            return Sheets.GetUIntProp(dcRef, "RowId");
        }
        catch
        {
            return 0;
        }
    }

    // ===== Lumina 查表（运行时反射，兼容未预编译 GeneratedSheets 的 CN 构建）=====
    internal static class Sheets
    {
        private static Assembly? asm;
        private static MethodInfo? getExcelSheetMethod;

        private static Assembly? GetAsm()
        {
            if (asm != null) return asm;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "Lumina.Excel.GeneratedSheets")
                {
                    asm = a;
                    return a;
                }
            }

            try
            {
                asm = Assembly.Load("Lumina.Excel.GeneratedSheets");
            }
            catch
            {
                asm = null;
            }

            return asm;
        }

        private static Type? GetType(string name) => GetAsm()?.GetType("Lumina.Excel.GeneratedSheets." + name);

        public static object? GetRow(string sheetName, uint rowId)
        {
            var t = GetType(sheetName);
            if (t == null) return null;
            try
            {
                getExcelSheetMethod ??= typeof(IDataManager).GetMethods()
                    .First(m => m.Name == "GetExcelSheet" && m.IsGenericMethod);
                var sheet = getExcelSheetMethod.MakeGenericMethod(t).Invoke(Service.DataManager, null);
                if (sheet == null) return null;
                var getRow = sheet.GetType().GetMethod("GetRow", new[] { typeof(uint) });
                if (getRow == null) return null;
                return getRow.Invoke(sheet, new object[] { rowId });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读取形如 RowRef&lt;T&gt; 的引用列，返回其 .Value（目标行，可能为 null）。</summary>
        public static object? GetRefValue(object? row, string propName)
        {
            if (row == null) return null;
            var p = row.GetType().GetProperty(propName);
            var refObj = p?.GetValue(row);
            if (refObj == null) return null;
            var valueProp = refObj.GetType().GetProperty("Value");
            return valueProp?.GetValue(refObj);
        }

        public static string? GetStringProp(object? obj, string propName)
        {
            if (obj == null) return null;
            var p = obj.GetType().GetProperty(propName);
            var value = p?.GetValue(obj);
            var text = value?.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        public static uint GetUIntProp(object? obj, string propName)
        {
            if (obj == null) return 0;
            var p = obj.GetType().GetProperty(propName);
            var v = p?.GetValue(obj);
            return v switch
            {
                uint u => u,
                ushort us => us,
                int i => (uint)i,
                _ => 0,
            };
        }
    }
}
