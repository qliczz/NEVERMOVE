using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Bindings.ImGui;

namespace NEVERMOVE;

/// <summary>
/// 游戏画面（世界）高亮：对每位在场的「好友/队友/部队成员」，在其脚下绘制一个彩色光圈，
/// 并在头顶显示名字 + 距离。通过 GameGui.WorldToScreen 把世界坐标投影到屏幕，
/// 用 ImGui 前景绘制层画出，完全不依赖游戏签名。
/// </summary>
public sealed class WorldOverlay
{
    private readonly Configuration config;
    private readonly TargetTracker tracker;

    public WorldOverlay(Configuration config, TargetTracker tracker)
    {
        this.config = config;
        this.tracker = tracker;
    }

    public void Draw()
    {
        var local = Service.ObjectTable.LocalPlayer;
        if (local == null) return;

        var draw = ImGui.GetForegroundDrawList();

        foreach (var obj in Service.ObjectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (pc.GameObjectId == local.GameObjectId) continue;

            var kind = this.tracker.Classify(pc);
            if (kind == null) continue;

            var color = ToAbgr(this.ColorFor(kind.Value));

            // 脚下光圈
            if (Service.GameGui.WorldToScreen(pc.Position, out var footScreen))
            {
                draw.AddCircle(footScreen, this.config.WorldRingRadius, color, 32, this.config.WorldRingThickness);
            }

            // 头顶名字 + 距离
            if (this.config.WorldShowName)
            {
                var head = pc.Position + new Vector3(0f, 2.2f, 0f);
                if (Service.GameGui.WorldToScreen(head, out var headScreen))
                {
                    var label = pc.Name.TextValue;
                    if (this.config.WorldShowDistance)
                    {
                        var dist = Vector3.Distance(local.Position, pc.Position);
                        label = $"{pc.Name.TextValue}  {dist:F1}m";
                    }

                    draw.AddText(headScreen - new Vector2(0f, 4f), color, label);
                }
            }
        }
    }

    private Vector4 ColorFor(TargetTracker.TargetKind kind) => kind switch
    {
        TargetTracker.TargetKind.Friend => this.config.ColorFriend,
        TargetTracker.TargetKind.Party => this.config.ColorParty,
        TargetTracker.TargetKind.FreeCompany => this.config.ColorFreeCompany,
        _ => this.config.ColorFriend,
    };

    /// <summary>Vector4(0-1, RGBA) -> ImGui 的 ABGR uint 颜色。</summary>
    internal static uint ToAbgr(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);
}
