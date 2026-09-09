using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BardPerfectLoop.Windows;

public sealed class ShadowOverlayWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public ShadowOverlayWindow(Plugin plugin)
        : base("吟游完美轴·半自动##BardPerfectLoopControlOverlay", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        IsOpen = plugin.Configuration.ShowOverlay;
        ShowCloseButton = true;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(430, 260),
            MaximumSize = new Vector2(680, 760),
        };
    }

    public void Dispose() { }

    public override void OnClose()
    {
        plugin.StopControl("已通过窗口 X 停止");
        plugin.Configuration.ShowOverlay = false;
        plugin.Configuration.Save();
    }

    public override void Draw()
    {
        var snapshot = plugin.Engine.Snapshot;
        ImGui.TextColored(new Vector4(1f, 0.25f, 0.15f, 1f), plugin.Configuration.ShadowOnly
            ? "影子观察：不发送技能，只观察手动动作并给出建议"
            : "半自动：输出、进攻团辅、已选药食自动；走位/防御辅助手动");
        ImGui.TextDisabled("右上角 X：立即停止自动执行并隐藏窗口");
        ImGui.TextUnformatted(snapshot.Status);
        ImGui.TextWrapped($"执行器：{plugin.Executor.Status}");
        ImGui.Separator();

        DrawScenarioButtons();
        ImGui.Separator();

        if (!plugin.Engine.Armed)
        {
            if (ImGui.Button("启动半自动"))
                plugin.StartControl();
            ImGui.SameLine();
            if (ImGui.Button("打开循环编辑器"))
                plugin.ToggleConfig();
            return;
        }

        if (ImGui.Button("停止"))
            plugin.StopControl();
        ImGui.SameLine();
        if (ImGui.Button("重新对齐时间轴"))
        {
            plugin.Engine.ResetTimeline();
            plugin.Executor.Replan("已重新规划，保留本场执行记录与插入次数");
        }
        ImGui.SameLine();
        if (ImGui.Button("编辑循环"))
            plugin.ToggleConfig();

        ImGui.Spacing();
        ImGui.Text($"战斗时间：{snapshot.ElapsedSeconds:F2}s   GCD序号：{snapshot.GcdIndex}   下一GCD：{snapshot.UntilNextGcd:F2}s");
        ImGui.Text($"本 GCD 能力技：{plugin.Executor.OgcdsThisCycle}/{plugin.Engine.OgcdLimitThisCycle}   最短动作间隔：0.70s");
        ImGui.Text($"1.1 计算耗时：{plugin.Engine.PlannerMilliseconds:F2}ms   安全动作锁估计：{plugin.Engine.EffectiveActionLock:F2}s");
        if (ImGui.SmallButton("复制最近执行诊断"))
            ImGui.SetClipboardText(plugin.Executor.DiagnosticText);
        ImGui.Text($"循环配置：{plugin.Engine.ActiveProfileName}");
        ImGui.TextWrapped(plugin.Battlefield.Summary);
        if (plugin.Configuration.Scenario == RotationScenario.Uwu)
        {
            ImGui.Text($"{plugin.Battlefield.Encounter.Label}  阶段 {plugin.Battlefield.Encounter.PhaseSeconds:F1}s");
            ImGui.TextWrapped(plugin.Battlefield.Encounter.Reason);
        }
        ImGui.TextWrapped($"药：{plugin.Consumables.PotionStatus}");

        ImGui.SetWindowFontScale(1.28f);
        ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), $"下一 GCD：{snapshot.NextGcd}");
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), $"能力技/切歌：{snapshot.NextOgcd}");
        ImGui.SetWindowFontScale(1f);
        if (!string.IsNullOrWhiteSpace(snapshot.Reason))
            ImGui.TextWrapped($"原因：{snapshot.Reason}");

        if (snapshot.Song is not null)
        {
            ImGui.Separator();
            var song = snapshot.Song;
            ImGui.Text($"歌轴：{song.PlanName}");
            ImGui.Text($"歌曲：{song.CurrentSong}  剩余 {song.Remaining:F1}s  诗音 {song.Repertoire}");
            ImGui.Text($"计划唱满 {song.PlannedSingingSeconds:F0}s：{Math.Max(0, song.UntilSwitch):F1}s 后切 {song.NextSong}（量谱剩 {song.PlannedCutRemaining:F0}s）");
        }

        if (snapshot.Dots is not null)
        {
            ImGui.Separator();
            var dots = snapshot.Dots;
            ImGui.Text($"目标：{dots.TargetName}");
            ImGui.Text($"{dots.CausticName}：{dots.CausticRemaining:F1}s / 期望 {dots.CausticTicksRemaining:F2} 跳 / {dots.CausticRemainingPotency:F1} 基础威力");
            ImGui.Text($"{dots.StormName}：{dots.StormRemaining:F1}s / 期望 {dots.StormTicksRemaining:F2} 跳 / {dots.StormRemainingPotency:F1} 基础威力");
            ImGui.Text($"DoT 期望剩余基础威力：{dots.TotalRemainingPotency:F1}（服务器跳伤相位未知）");
        }

        if (snapshot.MajorCooldowns.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("真实冷却（时间轴以此为准）");
            foreach (var cooldown in snapshot.MajorCooldowns)
                ImGui.Text($"{cooldown.Name}：{(cooldown.Ready ? "READY" : $"{cooldown.Remaining:F1}s")}");
        }
    }

    private void DrawScenarioButtons()
    {
        ScenarioButton("通用3312", RotationScenario.CurrentStandard);
        ImGui.SameLine();
        ScenarioButton("2.49标准", RotationScenario.Standard249);
        ImGui.SameLine();
        ScenarioButton("2.50进阶", RotationScenario.Advanced369);
        ImGui.SameLine();
        ScenarioButton("上天恢复", RotationScenario.DowntimeRecovery);
        ImGui.SameLine();
        ScenarioButton("绝神兵", RotationScenario.Uwu);
    }

    private void ScenarioButton(string label, RotationScenario scenario)
    {
        var selected = plugin.Configuration.Scenario == scenario;
        if (selected)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.55f, 0.75f, 1f));

        if (ImGui.SmallButton(label))
            plugin.SelectScenario(scenario);

        if (selected)
            ImGui.PopStyleColor();
    }
}
