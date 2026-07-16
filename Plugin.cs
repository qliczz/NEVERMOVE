using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace NEVERMOVE;

/// <summary>
/// W.T.H.F. (Where's The Hell Friend) —— 在 小地图 / 大地图 / 游戏画面 / 原生好友列表 中突出显示好友究竟在哪。
/// 纯可视化 QoL 插件，不修改任何判定/内存，版本稳定（无游戏签名依赖）。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public string Name => "W.T.H.F.";

    private const string CommandName = "/nevermove";
    private const string CommandAlias = "/nm";

    private readonly Configuration configuration;
    private readonly WindowSystem windowSystem;
    private readonly ConfigWindow configWindow;

    // 防止启动/读条阶段就打开好友面板并读取未初始化的好友代理(导致 EntryCount 为垃圾值、
    // for 循环海量迭代卡死游戏)：窗口不立即打开，等登录完成后再恢复。
    private bool autoOpenDone;
    private readonly TargetTracker targetTracker;
    private readonly WorldOverlay worldOverlay;
    private readonly MiniMapOverlay miniMapOverlay;
    private readonly AreaMapOverlay areaMapOverlay;
    private readonly FriendListWindow friendListWindow;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        // 一次性填充所有 [PluginService] 服务（对齐 CN 构建的 API15 写法）。
        pluginInterface.Create<Service>();

        this.configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.configuration.Initialize(pluginInterface);

        this.targetTracker = new TargetTracker(this.configuration);
        this.worldOverlay = new WorldOverlay(this.configuration, this.targetTracker);
        this.miniMapOverlay = new MiniMapOverlay(this.configuration, this.targetTracker);
        this.areaMapOverlay = new AreaMapOverlay(this.configuration, this.targetTracker);
        this.friendListWindow = new FriendListWindow(this.configuration);

        this.configWindow = new ConfigWindow(this.configuration);
        this.windowSystem = new WindowSystem("W.T.H.F.");
        this.windowSystem.AddWindow(this.configWindow);
        this.windowSystem.AddWindow(this.friendListWindow);

        pluginInterface.UiBuilder.Draw += this.OnDraw;
        pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;

        Service.CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand) { HelpMessage = "打开 W.T.H.F. 设置界面" });
        Service.CommandManager.AddHandler(CommandAlias, new CommandInfo(this.OnCommand) { HelpMessage = "打开 W.T.H.F. 设置界面" });

        Service.Framework.Update += this.FrameworkOnUpdate;
        Service.ClientState.Login += this.OnClientLogin;

        Service.Log.Information("[W.T.H.F.] 已加载");
    }

    private void FrameworkOnUpdate(IFramework framework)
    {
        this.targetTracker.Update();
        this.targetTracker.ApplyOutlines();

        // 插件在已进入游戏时被加载(如 /xlplugins 重载)，Login 事件不会再触发，
        // 因此在这里等游戏就绪后一次性恢复好友面板开关。
        if (!this.autoOpenDone && Service.ClientState.IsLoggedIn)
        {
            this.autoOpenDone = true;
            if (this.configuration.EnableFriendListOverlay)
                this.friendListWindow.IsOpen = true;
        }
    }

    private void OnClientLogin()
    {
        // 游戏登录完成后再打开好友面板，避免在启动/读条阶段读取未初始化的好友代理而卡死游戏。
        this.autoOpenDone = true;
        if (this.configuration.EnableFriendListOverlay)
            this.friendListWindow.IsOpen = true;
    }

    private void OnDraw()
    {
        this.windowSystem.Draw();

        // 每个覆盖独立 try/catch：任一覆盖绘制异常都只影响自己，不会连累其余覆盖（
        // 例如早先的覆盖若抛异常，排在后面的好友列表覆盖仍应照常工作）。
        this.SafeDraw(this.configuration.EnableWorldOverlay, this.worldOverlay.Draw, "世界");
        this.SafeDraw(this.configuration.EnableMiniMapOverlay, this.miniMapOverlay.Draw, "小地图");
        this.SafeDraw(this.configuration.EnableAreaMapOverlay, this.areaMapOverlay.Draw, "大地图");
        // 好友位置信息改由独立 ImGui 窗口(FriendListWindow)呈现，已加入 WindowSystem，
        // 由 windowSystem.Draw() 统一绘制；不再在原生好友列表上做脆弱的坐标覆盖。
    }

    private static bool[] overlayErrored = new bool[4];

    private void SafeDraw(bool enabled, Action draw, string name)
    {
        if (!enabled) return;
        try
        {
            draw();
        }
        catch (Exception ex)
        {
            // 每种覆盖只报一次，避免刷屏。
            int idx = name == "世界" ? 0 : name == "小地图" ? 1 : name == "大地图" ? 2 : 3;
            if (!overlayErrored[idx])
            {
                overlayErrored[idx] = true;
                Service.Log.Error(ex, $"[W.T.H.F.] {name}覆盖绘制异常（已隔离，不影响其它覆盖）");
            }
        }
    }

    private void OnCommand(string command, string args) => this.configWindow.IsOpen = true;

    private void OpenConfigUi() => this.configWindow.IsOpen = true;

    public void Dispose()
    {
        Service.Framework.Update -= this.FrameworkOnUpdate;
        Service.ClientState.Login -= this.OnClientLogin;
        Service.PluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigUi;
        Service.PluginInterface.UiBuilder.Draw -= this.OnDraw;
        Service.CommandManager.RemoveHandler(CommandName);
        Service.CommandManager.RemoveHandler(CommandAlias);
        this.windowSystem.RemoveAllWindows();
        this.targetTracker.Dispose();

        Service.Log.Information("[W.T.H.F.] 已卸载");
    }
}
