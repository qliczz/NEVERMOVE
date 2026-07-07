using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace NEVERMOVE;

/// <summary>
/// NEVERMOVE —— 在 小地图 / 大地图 / 游戏画面 中突出显示在线好友位置。
/// 纯可视化 QoL 插件，不修改任何判定/内存，版本稳定（无游戏签名依赖）。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public string Name => "NEVERMOVE";

    private const string CommandName = "/nevermove";
    private const string CommandAlias = "/nm";

    private readonly Configuration configuration;
    private readonly WindowSystem windowSystem;
    private readonly ConfigWindow configWindow;
    private readonly TargetTracker targetTracker;
    private readonly WorldOverlay worldOverlay;
    private readonly MiniMapOverlay miniMapOverlay;
    private readonly AreaMapOverlay areaMapOverlay;

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

        this.configWindow = new ConfigWindow(this.configuration);
        this.windowSystem = new WindowSystem("NEVERMOVE");
        this.windowSystem.AddWindow(this.configWindow);

        pluginInterface.UiBuilder.Draw += this.OnDraw;
        pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;

        Service.CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand) { HelpMessage = "打开 NEVERMOVE 设置界面" });
        Service.CommandManager.AddHandler(CommandAlias, new CommandInfo(this.OnCommand) { HelpMessage = "打开 NEVERMOVE 设置界面" });

        Service.Framework.Update += this.FrameworkOnUpdate;

        Service.Log.Information("[NEVERMOVE] 已加载");
    }

    private void FrameworkOnUpdate(IFramework framework)
    {
        this.targetTracker.Update();
        this.targetTracker.ApplyOutlines();
    }

    private void OnDraw()
    {
        this.windowSystem.Draw();

        if (this.configuration.EnableWorldOverlay) this.worldOverlay.Draw();
        if (this.configuration.EnableMiniMapOverlay) this.miniMapOverlay.Draw();
        if (this.configuration.EnableAreaMapOverlay) this.areaMapOverlay.Draw();
    }

    private void OnCommand(string command, string args) => this.configWindow.IsOpen = true;

    private void OpenConfigUi() => this.configWindow.IsOpen = true;

    public void Dispose()
    {
        Service.Framework.Update -= this.FrameworkOnUpdate;
        Service.PluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigUi;
        Service.PluginInterface.UiBuilder.Draw -= this.OnDraw;
        Service.CommandManager.RemoveHandler(CommandName);
        Service.CommandManager.RemoveHandler(CommandAlias);
        this.windowSystem.RemoveAllWindows();
        this.targetTracker.Dispose();

        Service.Log.Information("[NEVERMOVE] 已卸载");
    }
}
