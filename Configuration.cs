using System;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace NEVERMOVE;

/// <summary>
/// 高亮对象分三类：好友(Friend) / 队友(Party) / 部队成员(FreeCompany)。
/// 每一类有独立的「是否启用」与「颜色」，颜色在三处高亮（世界/小地图/大地图）统一生效。
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    // ===== 三大高亮总开关 =====
    public bool EnableWorldOverlay { get; set; } = true; // 游戏画面（世界）高亮
    public bool EnableMiniMapOverlay { get; set; } = true; // 小地图高亮
    public bool EnableAreaMapOverlay { get; set; } = true; // 大地图高亮

    // ===== 高亮对象类别开关 =====
    public bool EnableFriend { get; set; } = true;
    public bool EnableParty { get; set; } = true;
    public bool EnableFreeCompany { get; set; } = true;

    // ===== 每类对象颜色（三处高亮统一）=====
    public Vector4 ColorFriend { get; set; } = new(1f, 0.85f, 0f, 1f);      // 金
    public Vector4 ColorParty { get; set; } = new(0.35f, 0.7f, 1f, 1f);     // 蓝
    public Vector4 ColorFreeCompany { get; set; } = new(0.6f, 1f, 0.4f, 1f); // 绿

    // ===== 游戏画面（世界）高亮 =====
    public float WorldRingRadius { get; set; } = 24f;
    public float WorldRingThickness { get; set; } = 3f;
    public bool WorldShowName { get; set; } = true;
    public bool WorldShowDistance { get; set; } = true;

    // ===== 小地图高亮 =====
    // 注：小地图的「显示范围」与「旋转」现在直接读取游戏 _NaviMap addon 的缩放档位与锁定状态，
    // 因此不再需要手动设置范围/旋转，圆点会随游戏实时对齐。
    public float MiniMapDotRadius { get; set; } = 5f;
    public bool MiniMapShowName { get; set; } = false;

    // ===== 大地图高亮 =====
    public float AreaMapDotRadius { get; set; } = 6f;
    public float AreaMapRangeYalms { get; set; } = 200f; // 相对模式下的显示范围（ym）
    public bool AreaMapShowName { get; set; } = true;

    // 像素级对齐（实验性）：用 MapUtil.GetMapCoordinates 投影到大地图矩形。
    // 失败或关闭时自动回退到「相对玩家」稳健模式。地图归一化范围因版本略有差异，可用下方三项校准。
    public bool AreaMapPixelPerfect { get; set; } = false;
    // 以下三个为校准参数：若像素级对齐出现偏移/方向相反，在游戏内调这三项即可。
    public float AreaMapScaleFactor { get; set; } = 1f; // 整体缩放倍率（默认 1，可微调大小）
    public bool AreaMapFlipX { get; set; } = false;     // X 方向是否翻转
    public bool AreaMapFlipY { get; set; } = true;      // Y 方向是否翻转（默认 true：北在上）

    // ===== 区域限制 =====
    // 仅在大世界（开放地图）启用；副本 / 地牢 / 绝境战 / 团队(Raid) / 危命任务(FATE) 等场景不启用。
    public bool OnlyOpenWorld { get; set; } = true;

    // ===== 好友列表位置覆盖 =====
    // 在原生「社交 → 好友」列表每行右侧实时覆盖位置信息（自动随好友状态刷新）。
    public bool EnableFriendListOverlay { get; set; } = true;
    public bool FriendListShowCoords { get; set; } = true;   // 同图且已加载时显示精确坐标+距离
    public bool FriendListShowOffline { get; set; } = false; // 离线好友是否标出（灰色）

    // ===== 原生描边光效（世界画面）=====
    // 复用游戏原生「选中目标」的轮廓光效（GameObject.Highlight），与脚下的彩色光圈互不冲突。
    // 颜色为游戏内置的 7 种预设（非任意 RGB）：0=关 1=红 2=绿 3=蓝 4=黄 5=橙 6=品红 7=黑。
    public bool EnableOutline { get; set; } = true;
    public byte OutlineColorFriend { get; set; } = 4;      // 黄
    public byte OutlineColorParty { get; set; } = 3;       // 蓝
    public byte OutlineColorFreeCompany { get; set; } = 2; // 绿

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        this.pluginInterface = pi;
    }

    public void Save() => this.pluginInterface?.SavePluginConfig(this);
}
