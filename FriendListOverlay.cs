using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace NEVERMOVE;

/// <summary>
/// 好友列表位置覆盖：在原生「社交 → 好友」(AddonFriendList) 的每一行右侧，实时叠加该好友的位置信息。
/// 数据来自 InfoProxyCommonList（好友列表数据代理），每行通过 AtkComponentListItemRenderer.ListItemIndex
/// 映射到数据数组下标，通过 RowTemplateNode 拿到行节点屏幕坐标。每帧读取即自动刷新。
///
/// 位置分级（按可获取到的信息由精到粗）：
///   ① 同图：若好友已加载（同屏渲染）则给精确坐标 + 距离（绿）；仅同图但未加载则只标「同图」（绿）。
///   ② 同服异地：ZoneID 可知，给地图名（黄）。
///   ③ 跨服（同大区）：只知服务器名（橙）。
///   ④ 跨大区：只知服务器名（红）。
/// 跨服/跨大区无法获取精确坐标（游戏不下发），这与游戏内表现一致。
/// </summary>
public sealed class FriendListOverlay
{
    private readonly Configuration config;

    public FriendListOverlay(Configuration config)
    {
        this.config = config;
    }

    public unsafe void Draw()
    {
        if (!this.config.EnableFriendListOverlay) return;

        var local = Service.ObjectTable.LocalPlayer;
        if (local == null) return;

        // 原生好友列表 addon（社交→好友）
        var addon = Service.GameGui.GetAddonByName<AddonFriendList>("FriendList", 1);
        if (addon == null || !addon->IsVisible) return;
        var list = addon->FriendList;
        if (list == null) return;

        // 好友列表数据代理
        var info = InfoModule.Instance()->GetInfoProxyById(InfoProxyId.FriendList);
        if (info == null) return;
        var proxy = (InfoProxyCommonList*)info;
        var entryCount = proxy->EntryCount;
        if (entryCount == 0) return;

        // 本地上下文
        var localTerritory = Service.ClientState.TerritoryType;
        var localWorld = (ushort)local.CurrentWorld.RowId;
        var localDC = GetDataCenter(localWorld);
        var localPos = local.Position;

        // 已加载（同屏渲染）角色 ContentId -> 世界坐标，用于同图精确坐标
        Dictionary<ulong, Vector3> rendered;
        if (this.config.FriendListShowCoords)
        {
            rendered = new Dictionary<ulong, Vector3>();
            foreach (var obj in Service.ObjectTable)
            {
                if (obj is IPlayerCharacter pc)
                {
                    var c = (Character*)pc.Address;
                    rendered[c->ContentId] = pc.Position;
                }
            }
        }
        else
        {
            rendered = new Dictionary<ulong, Vector3>();
        }

        // 好友列表 addon 屏幕原点 + 主视口偏移（多屏修正）。原生节点坐标必须换算到屏幕空间才画得对。
        var viewport = ImGui.GetWindowViewport();
        var vpX = viewport.Pos.X;
        var vpY = viewport.Pos.Y;
        var addonX = addon->X;
        var addonY = addon->Y;

        var draw = ImGui.GetForegroundDrawList();
        var itemCount = list->GetItemCount();
        for (var i = 0; i < itemCount; i++)
        {
            var renderer = list->GetItemRenderer(i);
            if (renderer == null) continue;
            var dataIndex = renderer->ListItemIndex;
            if (dataIndex < 0 || dataIndex >= entryCount) continue;

            var data = proxy->CharDataSpan[dataIndex];
            // 行节点用 AtkComponentBase.OwnerNode（无论 RowTemplateNodeCount 是否为 1 都有效）。
            var node = (AtkResNode*)((AtkComponentBase*)renderer)->OwnerNode;
            if (node == null) continue;

            var tag = BuildTag(data, localTerritory, localWorld, localDC, localPos, rendered);
            if (tag.Text == null) continue;

            var pos = GetNodePosition(node, addonX, addonY, vpX, vpY);
            var rightX = pos.X + node->Width - 6f;
            var y = pos.Y + 3f;

            var textSize = ImGui.CalcTextSize(tag.Text);
            var bgMin = new Vector2(rightX - textSize.X - 8f, y - 1f);
            var bgMax = new Vector2(rightX + 2f, y + textSize.Y + 2f);
            draw.AddRectFilled(bgMin, bgMax, 0xCC000000);
            draw.AddText(new Vector2(rightX - textSize.X - 4f, y), tag.Color, tag.Text);
        }
    }

    private (string? Text, uint Color) BuildTag(
        InfoProxyCommonList.CharacterData data,
        uint localTerritory,
        ushort localWorld,
        uint localDC,
        Vector3 localPos,
        Dictionary<ulong, Vector3> rendered)
    {
        var online = (data.State & InfoProxyCommonList.CharacterData.OnlineStatus.Online) != 0;
        if (!online)
        {
            return this.config.FriendListShowOffline ? ("离线", WorldOverlay.ToAbgr(new Vector4(0.53f, 0.53f, 0.53f, 1f))) : (null, 0);
        }

        var world = data.CurrentWorld;
        var location = data.Location;

        if (world == localWorld)
        {
            // 同服务器
            if (location != 0 && location == localTerritory)
            {
                // ① 同图
                if (this.config.FriendListShowCoords && rendered.TryGetValue(data.ContentId, out var p))
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

    // ===== 原生节点 -> 屏幕坐标（沿父链累加 X/Y，并乘父级 ScaleX/Y，最后加 addon 屏幕原点 + 视口偏移）=====
    private static unsafe Vector2 GetNodePosition(AtkResNode* node, float addonX, float addonY, float vpX, float vpY)
    {
        float x = node->X;
        float y = node->Y;
        var parent = node->ParentNode;
        while (parent != null)
        {
            // 父节点对其子节点应用 ScaleX/ScaleY，故子节点坐标要先乘父级缩放再加父节点位置。
            x = parent->X + (x * parent->ScaleX);
            y = parent->Y + (y * parent->ScaleY);
            parent = parent->ParentNode;
        }

        // 累加到的坐标是相对 addon 根节点（其屏幕原点即 addon->X/Y）；再加主视口偏移得到最终屏幕坐标。
        return new Vector2(addonX + vpX + x, addonY + vpY + y);
    }

    // ===== Lumina 查表（运行时反射，兼容未预编译 GeneratedSheets 的 CN 构建）=====
    private static string? GetZoneName(ushort zoneId)
    {
        try
        {
            var row = Sheets.GetRow("TerritoryType", zoneId);
            if (row == null) return null;
            var placeName = Sheets.GetRefValue(row, "PlaceName");
            return Sheets.GetStringProp(placeName, "Name") ?? $"地图#{zoneId}";
        }
        catch
        {
            return null;
        }
    }

    private static string? GetWorldName(ushort worldId)
    {
        try
        {
            var row = Sheets.GetRow("World", worldId);
            if (row == null) return null;
            return Sheets.GetStringProp(row, "Name") ?? $"服务器#{worldId}";
        }
        catch
        {
            return null;
        }
    }

    private static uint GetDataCenter(ushort worldId)
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
}

/// <summary>
/// 运行时反射访问 Lumina 生成的 Excel 表。CN 构建不预编译 GeneratedSheets 程序集，
/// 故在运行时从已加载的 "Lumina.Excel.GeneratedSheets" 程序集取类型，并用泛型 GetExcelSheet&lt;T&gt; 反射调用。
/// 只依赖编译期可用的 Lumina.Excel.IExcelSheet / ExcelRow。
/// </summary>
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
        return p?.GetValue(obj) as string;
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
