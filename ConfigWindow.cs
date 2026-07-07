using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace NEVERMOVE;

/// <summary>
/// NEVERMOVE 设置窗口，用 /nevermove 或 /nm 打开，也会在 Dalamud 插件列表的齿轮里打开。
/// </summary>
public sealed class ConfigWindow : Window
{
    private readonly Configuration config;

    public ConfigWindow(Configuration config) : base("NEVERMOVE 设置##config")
    {
        this.config = config;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 380),
            MaximumSize = new Vector2(720, 920),
        };
    }

    public override void Draw()
    {
        var c = this.config;

        // 注意：ImGui 的 Checkbox/SliderFloat/ColorEdit4 等需要 ref/out 参数，
        // 但 C# 不允许把「属性」直接当 ref 传（CS0206），故每个控件都用局部变量中转。
        if (ImGui.BeginTabBar("##nevermove_tabs"))
        {
            if (ImGui.BeginTabItem("总开关"))
            {
                var world = c.EnableWorldOverlay;
                if (ImGui.Checkbox("游戏画面高亮（世界）", ref world)) c.EnableWorldOverlay = world;
                var minimap = c.EnableMiniMapOverlay;
                if (ImGui.Checkbox("小地图高亮", ref minimap)) c.EnableMiniMapOverlay = minimap;
                var areamap = c.EnableAreaMapOverlay;
                if (ImGui.Checkbox("大地图高亮", ref areamap)) c.EnableAreaMapOverlay = areamap;
                var onlyOpen = c.OnlyOpenWorld;
                if (ImGui.Checkbox("仅大世界启用（副本/地牢/绝境战/团队/危命不启用）", ref onlyOpen)) c.OnlyOpenWorld = onlyOpen;
                ImGui.Separator();
                ImGui.TextWrapped("提示：只有与你在同一场景的对象才会被高亮。");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("高亮对象"))
            {
                var friend = c.EnableFriend;
                if (ImGui.Checkbox("好友（在线）", ref friend)) c.EnableFriend = friend;
                var party = c.EnableParty;
                if (ImGui.Checkbox("队友 / 小队", ref party)) c.EnableParty = party;
                var fc = c.EnableFreeCompany;
                if (ImGui.Checkbox("部队成员（同场景）", ref fc)) c.EnableFreeCompany = fc;
                ImGui.Separator();
                ImGui.TextWrapped("优先级：好友 > 队友 > 部队。重叠时取优先级最高的颜色。");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("颜色"))
            {
                var cf = c.ColorFriend;
                if (ImGui.ColorEdit4("好友颜色", ref cf)) c.ColorFriend = cf;
                var cp = c.ColorParty;
                if (ImGui.ColorEdit4("队友颜色", ref cp)) c.ColorParty = cp;
                var cfc = c.ColorFreeCompany;
                if (ImGui.ColorEdit4("部队颜色", ref cfc)) c.ColorFreeCompany = cfc;
                ImGui.TextWrapped("颜色在三处高亮（世界/小地图/大地图）统一生效。");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("游戏画面"))
            {
                var r = c.WorldRingRadius;
                if (ImGui.SliderFloat("光圈半径", ref r, 8f, 60f, "%.0f")) c.WorldRingRadius = r;
                var t = c.WorldRingThickness;
                if (ImGui.SliderFloat("光圈粗细", ref t, 1f, 10f, "%.0f")) c.WorldRingThickness = t;
                var sn = c.WorldShowName;
                if (ImGui.Checkbox("显示名字", ref sn)) c.WorldShowName = sn;
                var sd = c.WorldShowDistance;
                if (ImGui.Checkbox("显示距离(m)", ref sd)) c.WorldShowDistance = sd;
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("小地图"))
            {
                var dr = c.MiniMapDotRadius;
                if (ImGui.SliderFloat("圆点半径", ref dr, 2f, 12f, "%.0f")) c.MiniMapDotRadius = dr;
                var msn = c.MiniMapShowName;
                if (ImGui.Checkbox("显示名字", ref msn)) c.MiniMapShowName = msn;
                ImGui.Separator();
                ImGui.TextWrapped("圆点的位置/大小/旋转已自动跟随游戏小地图（缩放档位与锁定状态），无需手动设置。");
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("大地图"))
            {
                var pp = c.AreaMapPixelPerfect;
                if (ImGui.Checkbox("像素级对齐（实验性）", ref pp)) c.AreaMapPixelPerfect = pp;
                var adr = c.AreaMapDotRadius;
                if (ImGui.SliderFloat("圆点半径", ref adr, 2f, 14f, "%.0f")) c.AreaMapDotRadius = adr;
                var ary = c.AreaMapRangeYalms;
                if (ImGui.SliderFloat("相对模式范围(ym)", ref ary, 30f, 1000f, "%.0f")) c.AreaMapRangeYalms = ary;
                var asn = c.AreaMapShowName;
                if (ImGui.Checkbox("显示名字", ref asn)) c.AreaMapShowName = asn;
                ImGui.Separator();
                ImGui.TextWrapped("像素级对齐会用 MapUtil 投影到大地图矩形，可能需校准；任何异常都会自动回退到相对模式。");
                var sf = c.AreaMapScaleFactor;
                if (ImGui.SliderFloat("缩放倍率", ref sf, 0.1f, 5f, "%.2f")) c.AreaMapScaleFactor = sf;
                var fx = c.AreaMapFlipX;
                if (ImGui.Checkbox("翻转 X 方向", ref fx)) c.AreaMapFlipX = fx;
                var fy = c.AreaMapFlipY;
                if (ImGui.Checkbox("翻转 Y 方向", ref fy)) c.AreaMapFlipY = fy;
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("描边光效"))
            {
                var en = c.EnableOutline;
                if (ImGui.Checkbox("启用模型描边光效（原生轮廓）", ref en)) c.EnableOutline = en;
                ImGui.TextWrapped("复用游戏原生「选中目标」的轮廓光效，叠加在角色模型上，与脚下彩色光圈互不冲突。");
                ImGui.Separator();
                var names = new[] { "关", "红", "绿", "蓝", "黄", "橙", "品红", "黑" };
                int of = c.OutlineColorFriend;
                if (ImGui.Combo("好友描边色", ref of, names, names.Length)) c.OutlineColorFriend = (byte)of;
                int op = c.OutlineColorParty;
                if (ImGui.Combo("队友描边色", ref op, names, names.Length)) c.OutlineColorParty = (byte)op;
                int ofc = c.OutlineColorFreeCompany;
                if (ImGui.Combo("部队描边色", ref ofc, names, names.Length)) c.OutlineColorFreeCompany = (byte)ofc;
                ImGui.TextWrapped("注：原生描边颜色为游戏内置的 7 种预设，无法选任意 RGB；若需任意颜色，可用「游戏画面」里的彩色光圈/名字（已支持自定义 RGB）。");
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        c.Save();
    }
}
