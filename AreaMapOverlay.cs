using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Utility;
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
///    用 MapUtil.GetMapCoordinates 把目标世界坐标转成归一化地图坐标，
///    再投影到 AreaMap addon 矩形中心。这种方式会随大地图的缩放/平移对齐，
///    但地图坐标归一化范围因版本略有差异，提供 AreaMapScaleFactor / FlipX / FlipY 校准，
///    且整段 try/catch，失败或关闭时自动回退到相对模式。
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

    /// <summary>像素级对齐绘制（实验性）。成功返回 true，任何前置条件不满足返回 false。</summary>
    private unsafe bool DrawPixelPerfect(AtkUnitBase* addon, IPlayerCharacter local)
    {
        var root = addon->RootNode;
        if (root == null) return false;

        var scale = root->ScaleX;
        var ax = addon->X;
        var ay = addon->Y;
        var aw = root->Width * scale;
        var ah = root->Height * scale;

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

        var draw = ImGui.GetForegroundDrawList();

        foreach (var obj in Service.ObjectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (pc.GameObjectId == local.GameObjectId) continue;
            if (!this.tracker.Classify(pc, out var kind)) continue;

            // 归一化地图坐标：X 东/西，Y 北/南
            var mc = MapUtil.GetMapCoordinates(pc, false);
            var sx = cx + (mc.X * halfW * sf * fx);
            var sy = cy - (mc.Y * halfH * sf * fy); // 北在上 => 屏幕 Y 取负

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
