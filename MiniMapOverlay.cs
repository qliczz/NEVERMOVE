using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace NEVERMOVE;

/// <summary>
/// 小地图高亮（学习 MiniMappingway 的成熟做法，做到像素级对齐）。
///
/// 思路：读取真实的小地图 addon <c>_NaviMap</c> 的屏幕矩形与视图参数
/// （整体缩放 Scale、节点 8 的旋转角、节点 18 的缩放档位 Zoom、节点 4 的锁定状态），
/// 用游戏同样的世界→小地图投影数学把每位目标落点算到小地图纹理上，再用 ImGui 前景层画圆点。
/// 这样圆点会随小地图的旋转 / 缩放档位实时精确对齐，而不是近似。
/// </summary>
public sealed class MiniMapOverlay
{
    private readonly Configuration config;
    private readonly TargetTracker tracker;

    // FFXIV 小地图纹理基准尺寸（px）。addon 整体缩放后实际尺寸 = 基准 * Scale。
    private const float NaviMapBaseSize = 218f;

    public MiniMapOverlay(Configuration config, TargetTracker tracker)
    {
        this.config = config;
        this.tracker = tracker;
    }

    public unsafe void Draw()
    {
        var local = Service.ObjectTable.LocalPlayer;
        if (local == null) return;

        var naviMap = Service.GameGui.GetAddonByName<AtkUnitBase>("_NaviMap");
        if (naviMap == null || !naviMap->IsVisible) return;
        if (naviMap->VisibilityFlags != 0) return;       // 被隐藏/淡出
        if (Service.GameGui.GameUiHidden) return;

        var naviScale = naviMap->Scale;
        var mapSize = NaviMapBaseSize * naviScale;
        var minimapRadius = mapSize * 0.315f;            // 小地图有效半径比例（与游戏一致）

        // 读取旋转 / 缩放档位 / 锁定状态；任一节点读取失败则降级到「玩家朝向 + 默认缩放」。
        var rotation = 0f;
        var zoom = 1f;
        var isLocked = false;
        try
        {
            rotation = naviMap->GetNodeById(8)->Rotation;
            var imageNode = (AtkImageNode*)naviMap->GetNodeById(18)->GetComponent()->GetImageNodeById(6);
            zoom = imageNode->ScaleX;
            isLocked = ((AtkComponentCheckBox*)naviMap->GetNodeById(4)->GetComponent())->IsChecked;
        }
        catch
        {
            rotation = local.Rotation;
            zoom = 1f;
        }

        // 区域缩放：部分地图 1 世界单位 ≠ 1 yalm（房屋/旅馆等），用 Map 表的 SizeFactor 修正。
        var zoneScale = 1f;
        try
        {
            var mapSheet = Service.DataManager.GetExcelSheet<Map>();
            if (mapSheet != null && mapSheet.TryGetRow(AgentMap.Instance()->CurrentMapId, out var mapRow) && mapRow.SizeFactor != 0)
                zoneScale = (float)mapRow.SizeFactor / 100f;
        }
        catch
        {
            // 保持 zoneScale = 1
        }

        // addon 的 X/Y 是「游戏窗口坐标」，叠加 ImGui 主视口偏移得到绝对屏幕坐标（多屏修正）。
        var viewport = ImGui.GetWindowViewport();
        var vpX = viewport.Pos.X;
        var vpY = viewport.Pos.Y;

        var playerCircle = new Vector2(naviMap->X + (mapSize / 2f) + vpX, naviMap->Y + (mapSize / 2f) + vpY);
        playerCircle.Y -= 5f; // 对齐小地图实际圆心（纹理略偏上）

        var playerPos = new Vector2(local.Position.X, local.Position.Z);

        var draw = ImGui.GetForegroundDrawList();

        foreach (var obj in Service.ObjectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (pc.GameObjectId == local.GameObjectId) continue;
            if (!this.tracker.Classify(pc, out var kind)) continue;

            var color = WorldOverlay.ToAbgr(this.ColorFor(kind));

            // 相对玩家的世界偏移（注意用 Player - Person，北=+Z 在屏幕上方）
            var rel = new Vector2(playerPos.X - pc.Position.X, playerPos.Y - pc.Position.Z);
            rel *= zoneScale * naviScale * zoom;

            var circlePos = playerCircle - rel;

            // 小地图未锁定时随玩家朝向旋转（锁定时玩家永远朝上，无需旋转）
            if (!isLocked)
                circlePos = RotateAround(playerCircle, circlePos, rotation);

            // 超出小地图半径则夹到边缘
            var d = Vector2.Distance(playerCircle, circlePos);
            if (d > minimapRadius)
            {
                var dir = circlePos - playerCircle;
                dir *= minimapRadius / d;
                circlePos = playerCircle + dir;
            }

            draw.AddCircleFilled(circlePos, this.config.MiniMapDotRadius, color);
            if (this.config.MiniMapShowName)
                draw.AddText(circlePos + new Vector2(this.config.MiniMapDotRadius + 2f, -4f), color, pc.Name.TextValue);
        }
    }

    private static Vector2 RotateAround(Vector2 center, Vector2 pos, float angle)
    {
        var c = MathF.Cos(angle);
        var s = MathF.Sin(angle);
        var dx = pos.X - center.X;
        var dy = pos.Y - center.Y;
        return new Vector2(center.X + (c * dx) - (s * dy), center.Y + (s * dx) + (c * dy));
    }

    private Vector4 ColorFor(TargetTracker.TargetKind kind) => kind switch
    {
        TargetTracker.TargetKind.Friend => this.config.ColorFriend,
        TargetTracker.TargetKind.Party => this.config.ColorParty,
        TargetTracker.TargetKind.FreeCompany => this.config.ColorFreeCompany,
        _ => this.config.ColorFriend,
    };
}
