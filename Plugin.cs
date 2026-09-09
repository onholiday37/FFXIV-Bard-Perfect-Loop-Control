using System;
using BardPerfectLoop.Windows;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BardPerfectLoop;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/brdcontrol";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IJobGauges JobGauges { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;

    internal Configuration Configuration { get; }
    internal HotbarKeyResolver HotbarKeys { get; } = new();
    internal TargetTracker TargetTracker { get; } = new();
    internal ShadowEngine Engine { get; }
    internal ActionExecutor Executor { get; }
    internal ActionObserver Observer { get; }
    internal LiveBattlefield Battlefield { get; }
    internal ConsumableController Consumables { get; }
    internal WindowSystem WindowSystem { get; } = new("BardPerfectLoopControl");

    private readonly ControlWindow controlWindow;
    private readonly ShadowOverlayWindow overlayWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.EnsureDefaults();
        Engine = new ShadowEngine(this, Configuration);
        Executor = new ActionExecutor(this, Configuration);
        Observer = new ActionObserver();
        Battlefield = new LiveBattlefield(this);
        Consumables = new ConsumableController(this);

        controlWindow = new ControlWindow(this);
        overlayWindow = new ShadowOverlayWindow(this);
        WindowSystem.AddWindow(controlWindow);
        WindowSystem.AddWindow(overlayWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "吟游完美轴半自动：start 启动；stop 停止；reset 重新规划；config 打开编辑器。",
        });

        Framework.Update += OnFrameworkUpdate;
        DutyState.DutyWiped += OnDutyEnded;
        DutyState.DutyCompleted += OnDutyEnded;
        PluginInterface.UiBuilder.Draw += DrawWindows;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfig;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        DutyState.DutyWiped -= OnDutyEnded;
        DutyState.DutyCompleted -= OnDutyEnded;
        Observer.Dispose();
        PluginInterface.UiBuilder.Draw -= DrawWindows;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        CommandManager.RemoveHandler(CommandName);
        WindowSystem.RemoveAllWindows();
        controlWindow.Dispose();
        overlayWindow.Dispose();
    }

    internal bool IsBard => PlayerState.IsLoaded && PlayerState.ClassJob.IsValid && PlayerState.ClassJob.RowId == 23;
    internal bool IsInCombat => Condition[ConditionFlag.InCombat];
    internal int EffectiveLevel => PlayerState.IsLoaded ? Math.Max(1, (int)PlayerState.EffectiveLevel) : 0;
    internal IBattleChara? ResolveBattleTarget() => TargetTracker.Resolve();

    internal void ToggleConfig() => controlWindow.Toggle();

    internal void SelectScenario(RotationScenario scenario)
    {
        Configuration.ApplyScenario(scenario);
        if (Engine.Armed)
        {
            Engine.ResetTimeline();
            Executor.Replan($"已切换：{ScenarioRules.Resolve(scenario).ShortName}；保留本场执行记录");
        }
    }

    internal void StartControl()
    {
        TargetTracker.Reset();
        Battlefield.Reset();
        Consumables.OnStart();
        Executor.Reset("等待第一个可执行 GCD");
        Engine.Arm();
    }

    internal void StopControl(string reason = "已手动停止")
    {
        Engine.Stop(reason);
        Executor.Reset(reason);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            if (Engine.Armed && ObjectTable.LocalPlayer is { } player && (player.IsDead || player.CurrentHp == 0))
            {
                // Clear death-interrupted requests before their normal timeout can stop control.
                Engine.Update();
                return;
            }
            Executor.Observe();
            Consumables.Poll();
            if (Engine.Armed && IsBard && ObjectTable.LocalPlayer is { CurrentHp: > 0 }) Battlefield.Update();
            Engine.Update();
            Executor.Update();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "吟游完美轴半自动更新失败");
            StopControl("内部错误，保险停止");
        }
    }

    private void OnDutyEnded(object? args)
    {
        StopControl("副本结束或团灭，已停止；下一次开怪请手动启动");
        Battlefield.Reset();
    }

    private void DrawWindows()
    {
        overlayWindow.IsOpen = Configuration.ShowOverlay;
        WindowSystem.Draw();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "start":
            case "启动":
                StartControl();
                Configuration.ShowOverlay = true;
                Configuration.Save();
                ChatGui.Print($"[吟游完美轴] {(Configuration.ShadowOnly ? "影子观察（不发技能）" : "半自动")}：{ScenarioRules.Resolve(Configuration.Scenario).Name}已启动；每个 GCD 最多双插，动作间隔至少 0.70 秒。");
                break;
            case "stop":
            case "停止":
                StopControl();
                ChatGui.Print("[吟游完美轴·半自动] 已停止。");
                break;
            case "reset":
            case "重置":
                Engine.ResetTimeline();
                Executor.Replan("已重新规划，保留本场执行记录与插入次数");
                ChatGui.Print("[吟游完美轴·半自动] 已按当前状态重新规划。");
                break;
            case "overlay":
            case "提示":
                Configuration.ShowOverlay = !Configuration.ShowOverlay;
                Configuration.Save();
                break;
            case "config":
            case "设置":
            default:
                ToggleConfig();
                break;
        }
    }
}
