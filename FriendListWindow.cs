using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace NEVERMOVE;

/// <summary>
/// 好友位置面板（独立 ImGui 窗口，替代失效的原生好友列表覆盖）。
///
/// 用 /nevermove 或设置齿轮打开；在「设置 → 好友列表」里用「启用好友位置面板」开关。
/// 稳定可靠：直接遍历好友数据代理(InfoProxyCommonList) + 已加载角色坐标，用普通 ImGui 表格渲染，
/// 不依赖任何原生节点坐标换算，因此一定显示得出来。
///
/// 位置分级与 FriendListOverlay.BuildTag 一致：
///   ① 同图（绿，含坐标/距离） → ② 同服异地（黄，地图名） → ③ 跨服同大区（橙，服务器名） → ④ 跨大区（红，服务器名）。
/// </summary>
public sealed class FriendListWindow : Window
{
    private readonly Configuration config;

    public FriendListWindow(Configuration config)
        : base("W.T.H.F. 好友位置##friendlist")
    {
        this.config = config;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 240),
            MaximumSize = new Vector2(620, 900),
        };
        // 启动时不立即打开：由 Plugin 在登录完成后才恢复开关，避免读条阶段访问未初始化代理卡死游戏。
        this.IsOpen = false;
    }

    public override unsafe void Draw()
    {
        // 由配置统一控制显隐：关闭时收起窗口，避免空窗口占屏。
        if (!this.config.EnableFriendListOverlay)
        {
            this.IsOpen = false;
            return;
        }

        this.IsOpen = true;

        // 游戏尚未就绪(加载/标题界面)时不读取好友代理：此时 InfoModule/代理未初始化，
        // EntryCount 可能是垃圾值，下面的 for 循环会陷入海量无效迭代而卡死游戏。
        if (!Service.ClientState.IsLoggedIn)
        {
            ImGui.TextUnformatted("（游戏加载中…）");
            return;
        }

        var local = Service.ObjectTable.LocalPlayer;
        if (local == null)
        {
            ImGui.TextUnformatted("（尚未进入游戏角色）");
            return;
        }

        var infoModule = InfoModule.Instance();
        if (infoModule == null)
        {
            ImGui.TextUnformatted("（好友数据暂不可用）");
            return;
        }

        var info = infoModule->GetInfoProxyById(InfoProxyId.FriendList);
        if (info == null)
        {
            ImGui.TextUnformatted("（好友列表数据暂不可用）");
            return;
        }

        var proxy = (InfoProxyCommonList*)info;
        // 防御：即使是已登录状态，也限制遍历上限，避免任何异常数据导致卡死。
        var entryCount = Math.Min(proxy->EntryCount, 500u);
        if (entryCount == 0)
        {
            ImGui.TextUnformatted("（没有好友，或好友列表尚未加载）");
            return;
        }

        var localTerritory = Service.ClientState.TerritoryType;
        var localWorld = (ushort)local.CurrentWorld.RowId;
        var localDC = FriendListOverlay.GetDataCenter(localWorld);
        var localPos = local.Position;

        // 已加载（同屏渲染）角色 ContentId -> 世界坐标，用于同图精确坐标
        Dictionary<ulong, Vector3> rendered = new();
        if (this.config.FriendListShowCoords)
        {
            foreach (var obj in Service.ObjectTable)
            {
                if (obj is IPlayerCharacter pc)
                {
                    var c = (Character*)pc.Address;
                    rendered[c->ContentId] = pc.Position;
                }
            }
        }

        int onlineCount = 0;
        if (ImGui.BeginTable("##FriendListPanel", 2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("好友", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("位置", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableHeadersRow();

            for (int i = 0; i < entryCount; i++)
            {
                var entry = proxy->GetEntry((uint)i);
                if (entry == null) continue;
                var data = *entry;

                bool online = (data.State & InfoProxyCommonList.CharacterData.OnlineStatus.Online) != 0;
                if (online) onlineCount++;

                var tag = FriendListOverlay.BuildTag(data, localTerritory, localWorld, localDC, localPos, rendered, this.config);
                if (tag.Text == null) continue; // 离线且未开启显示离线

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(data.NameString);
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, tag.Color);
                ImGui.TextUnformatted(tag.Text);
                ImGui.PopStyleColor();
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"在线 {onlineCount} / 共 {entryCount} 位好友");
        ImGui.TextWrapped("位置：同图(绿)→同服异地(黄,地图名)→跨服同大区(橙,服务器名)→跨大区(红)。仅已加载好友能拿精确坐标。");
    }
}
