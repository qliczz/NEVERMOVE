using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;

namespace NEVERMOVE;

/// <summary>
/// 大地图（AreaMap）高亮，两种模式：
///
/// 1) 相对玩家模式（默认 / 稳健）：
///    以玩家为中心、正北朝上，按 AreaMapRangeYalms 画相对方位圆点。
///    不依赖任何地图视图内部偏移，版本升级不会失效。
///
    /// 2) 像素级对齐（AreaMapPixelPerfect=true，实验性）：
    ///    用 MapUtil.GetMapCoordinates 把目标世界坐标转成游戏地图坐标，
    ///    以 Atk2DAreaMap.MapCenterX/Y 为视图中心、Atk2DAreaMap.MapScale 为缩放档位，
    ///    投影到大地图矩形。这种方式会随大地图的缩放/平移实时对齐（圆点随缩放扩散/收缩）。
    ///    基础缩放由「地图纹理尺寸 × SizeFactor」自动推导（无需手调），
    ///    AreaMapScaleFactor / FlipX / FlipY 仅作微调校准，整段 try/catch，
    ///    失败或关闭时自动回退到相对模式。
    /// </summary>
public sealed class AreaMapOverlay
{
    private readonly Configuration config;
    private readonly TargetTracker tracker;

    public AreaMapOverlay(Configuration config, TargetTracker tracker)
    {
        this.config = config;
        this.tracker = tracker;
    }

    public unsafe void Draw()
    {
        var local = Service.ObjectTable.LocalPlayer;
        if (local == null) return;

        var addon = Service.GameGui.GetAddonByName<AtkUnitBase>("AreaMap", 1);
        if (addon == null || !addon->IsVisible) return;

        if (this.config.AreaMapPixelPerfect)
        {
            try
            {
                if (this.DrawPixelPerfect(addon, local)) return;
            }
            catch (Exception ex)
            {
                Service.Log.Debug(ex, "[NEVERMOVE] 像素级大地图对齐失败，回退到相对模式");
            }
        }

        this.DrawRelative(addon, local);
    }

    /// <summary>像素级对齐绘制（跟随大地图缩放）。成功返回 true，任何前置条件不满足返回 false。</summary>
    private unsafe bool DrawPixelPerfect(AtkUnitBase* addon, IPlayerCharacter local)
    {
        // 关键修复：Atk2DAreaMap 是 AddonAreaMap 内嵌在偏移 936 的子组件（public Atk2DAreaMap AreaMap;），
        // 绝不是 addon 基址本身。原先 (Atk2DAreaMap*)addon 会把 addon 头部当组件读，
        // 导致 am->MapScale 读到的是垃圾内存、永远不随地图缩放变化 —— 这正是「圆点不随缩放改变」的根因。
        var areaMapAddon = (AddonAreaMap*)addon;
        var am = &areaMapAddon->AreaMap;
        var root = addon->RootNode;
        if (root == null) return false;

        // AtkUnitBase 无 Width/Height，尺寸取自根节点（AtkResNode）。
        var ax = addon->X;
        var ay = addon->Y;
        var uiScale = root->ScaleX;
        var aw = root->Width * uiScale;
        var ah = root->Height * uiScale;

        // ImGui 主视口偏移（多屏修正）
        var viewport = ImGui.GetWindowViewport();
        var vpX = viewport.Pos.X;
        var vpY = viewport.Pos.Y;

        var cx = ax + (aw / 2f) + vpX;
        var cy = ay + (ah / 2f) + vpY;

        var halfW = aw / 2f;
        var halfH = ah / 2f;
        var sf = this.config.AreaMapScaleFactor;
        var fx = this.config.AreaMapFlipX ? -1f : 1f;
        var fy = this.config.AreaMapFlipY ? -1f : 1f;

        // 真正的大地图缩放档位由 Atk2DAreaMap.MapScale 给出（TelepotTown 约 0.28~0.57），
        // 它和 HUD/UI 的整体缩放（root->ScaleX）完全无关，必须始终参与计算，
        // 否则圆点不会随滚轮缩放而扩散/收缩。
        float zoom = am->MapScale;
        if (!float.IsFinite(zoom) || zoom <= 0f) zoom = 1f;

        // 视图中心地图坐标：跟随玩家时等于玩家坐标；平移地图时等于平移落点。
        // 用它与目标的地图坐标差做投影，平移时圆点才会正确跟随视图中心。
        float centerX = am->MapCenterX;
        float centerY = am->MapCenterY;
        if (centerX == 0f && centerY == 0f)
        {
            var pm = MapUtil.GetMapCoordinates(local, false);
            centerX = pm.X;
            centerY = pm.Y;
        }

        // 基础像素/地图坐标单位换算（zoom=1 时）：地图纹理像素尺寸 × SizeFactor / 4096。
        // 该换算来自游戏 WorldToMap 公式（ConvertWorldCoordXZToMapCoord = 0.02*offset + 2048/scale + 0.02*value + 1），
        // 整个地图坐标跨度 = 4096/scale，对应纹理像素尺寸 mapTexSize，故每地图坐标单位 = mapTexSize*scale/4096。
        // 这样无需手调即可正确铺满大地图，且天然随 MapScale 缩放。AreaMapScaleFactor(sf) 仅作微调。
        float sizeFactor = 100f;
        try
        {
            var agentMap = AgentMap.Instance();
            if (agentMap != null && agentMap->CurrentMapSizeFactor != 0)
                sizeFactor = agentMap->CurrentMapSizeFactor;
        }
        catch
        {
            // 保持 sizeFactor = 100
        }

        float mapTexSize = am->Width != 0 ? (float)am->Width : 1024f; // 地图组件基准尺寸即纹理像素尺寸
        float pxPerMapUnit = (mapTexSize * sizeFactor) / 4096f;

        var draw = ImGui.GetForegroundDrawList();

        foreach (var obj in Service.ObjectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (pc.GameObjectId == local.GameObjectId) continue;
            if (!this.tracker.Classify(pc, out var kind)) continue;

            // 目标地图坐标：X 东/西，Y 北/南（与游戏一致）。
            var mc = MapUtil.GetMapCoordinates(pc, false);
            var sx = cx + ((mc.X - centerX) * pxPerMapUnit * zoom * sf * fx);
            var sy = cy - ((mc.Y - centerY) * pxPerMapUnit * zoom * sf * fy); // 北在上 => 屏幕 Y 取负

            var color = WorldOverlay.ToAbgr(this.ColorFor(kind));
            draw.AddCircleFilled(new Vector2(sx, sy), this.config.AreaMapDotRadius, color);
            if (this.config.AreaMapShowName)
                draw.AddText(new Vector2(sx + this.config.AreaMapDotRadius + 2f, sy - 4f), color, pc.Name.TextValue);
        }

        return true;
    }

    /// <summary>相对玩家模式（回退 / 默认）。</summary>
    private unsafe void DrawRelative(AtkUnitBase* addon, IPlayerCharacter local)
    {
        var root = addon->RootNode;
        if (root == null) return;

        var scale = root->ScaleX;
        var ax = addon->X;
        var ay = addon->Y;
        var aw = root->Width * scale;
        var ah = root->Height * scale;

        var viewport = ImGui.GetWindowViewport();
        var vpX = viewport.Pos.X;
        var vpY = viewport.Pos.Y;

        var cx = ax + (aw / 2f) + vpX;
        var cy = ay + (ah / 2f) + vpY;
        var radius = MathF.Min(aw, ah) / 2f * 0.9f;
        var pxPerYalm = radius / MathF.Max(1f, this.config.AreaMapRangeYalms);

        var draw = ImGui.GetForegroundDrawList();

        foreach (var obj in Service.ObjectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (pc.GameObjectId == local.GameObjectId) continue;
            if (!this.tracker.Classify(pc, out var kind)) continue;

            var dx = pc.Position.X - local.Position.X;
            var dz = pc.Position.Z - local.Position.Z;

            var offX = dx * pxPerYalm;
            var offY = dz * pxPerYalm;

            var dist = MathF.Sqrt((offX * offX) + (offY * offY));
            if (dist > radius)
            {
                var k = radius / dist;
                offX *= k;
                offY *= k;
            }

            var sx = cx + offX;
            var sy = cy + offY;
            var color = WorldOverlay.ToAbgr(this.ColorFor(kind));

            draw.AddCircleFilled(new Vector2(sx, sy), this.config.AreaMapDotRadius, color);
            if (this.config.AreaMapShowName)
                draw.AddText(new Vector2(sx + this.config.AreaMapDotRadius + 2f, sy - 4f), color, pc.Name.TextValue);
        }
    }

    private Vector4 ColorFor(TargetTracker.TargetKind kind) => kind switch
    {
        TargetTracker.TargetKind.Friend => this.config.ColorFriend,
        TargetTracker.TargetKind.Party => this.config.ColorParty,
        TargetTracker.TargetKind.FreeCompany => this.config.ColorFreeCompany,
        _ => this.config.ColorFriend,
    };
}
