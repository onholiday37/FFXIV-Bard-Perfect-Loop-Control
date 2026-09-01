using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BardPerfectLoop.Windows;

public sealed class ShadowOverlayWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public ShadowOverlayWindow(Plugin plugin)
        : base("吟游完美轴·完全控制##BardPerfectLoopControlOverlay", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
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
        ImGui.TextColored(new Vector4(1f, 0.25f, 0.15f, 1f), "完全控制：运行时会自动释放技能；每个 GCD 最多双插");
        ImGui.TextDisabled("右上角 X：立即停止自动执行并隐藏窗口");
        ImGui.TextUnformatted(snapshot.Status);
        ImGui.TextWrapped($"执行器：{plugin.Executor.Status}");
        ImGui.Separator();

        DrawScenarioButtons();
        ImGui.Separator();

        if (!plugin.Engine.Armed)
        {
            if (ImGui.Button("启动完全控制"))
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
            plugin.Executor.Reset("已重新规划，等待下一个 GCD");
        }
        ImGui.SameLine();
        if (ImGui.Button("编辑循环"))
            plugin.ToggleConfig();

        ImGui.Spacing();
        ImGui.Text($"战斗时间：{snapshot.ElapsedSeconds:F2}s   GCD序号：{snapshot.GcdIndex}   下一GCD：{snapshot.UntilNextGcd:F2}s");
        ImGui.Text($"本 GCD 能力技：{plugin.Executor.OgcdsThisCycle}/{plugin.Engine.OgcdLimitThisCycle}   最短动作间隔：0.70s");
        ImGui.Text($"循环配置：{plugin.Engine.ActiveProfileName}");

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
            ImGui.Text($"{dots.CausticName}：{dots.CausticRemaining:F1}s / 预计 {dots.CausticTicksRemaining} 跳 / {dots.CausticRemainingPotency} 威力");
            ImGui.Text($"{dots.StormName}：{dots.StormRemaining:F1}s / 预计 {dots.StormTicksRemaining} 跳 / {dots.StormRemainingPotency} 威力");
            ImGui.Text($"DoT 尚未结算总威力：{dots.TotalRemainingPotency}");
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
