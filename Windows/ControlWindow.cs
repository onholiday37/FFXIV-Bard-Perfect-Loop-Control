using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BardPerfectLoop.Windows;

public sealed class ControlWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private int selectedCatalogIndex;

    public ControlWindow(Plugin plugin)
        : base("吟游完美轴·完全控制设置##BardPerfectLoopControlConfig", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 620),
            MaximumSize = new Vector2(1100, 920),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var changed = false;

        ImGui.TextColored(new Vector4(1f, 0.25f, 0.15f, 1f), "警告：这是完全控制版，启动后会自动释放技能。第三方插件可能导致封号，后果自负。");
        changed |= ImGui.Checkbox("启用插件", ref config.Enabled);
        changed |= ImGui.Checkbox("显示控制状态窗", ref config.ShowOverlay);
        changed |= ImGui.Checkbox("战斗结束自动停止", ref config.StopWhenCombatEnds);

        ImGui.SameLine();
        if (!plugin.Engine.Armed)
        {
            if (ImGui.Button("手动启动"))
                plugin.StartControl();
        }
        else if (ImGui.Button("立即停止"))
        {
            plugin.StopControl();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("场景轴预设");
        changed |= ImGui.Checkbox("启用实时完美轴（推荐）", ref config.GuidePerfectAxis);
        ImGui.TextDisabled("点一个小按钮即可换整套场景；运行中切换会丢弃旧时间点，并从当前真实状态重新排轴。");

        ScenarioButton("通用 3-3-12", RotationScenario.CurrentStandard, config);
        ImGui.SameLine();
        ScenarioButton("2.49 标准", RotationScenario.Standard249, config);
        ImGui.SameLine();
        ScenarioButton("2.50 进阶 3-6-9", RotationScenario.Advanced369, config);
        ImGui.SameLine();
        ScenarioButton("Boss 上天/断轴", RotationScenario.DowntimeRecovery, config);

        ScenarioButton("旧 NGA 7.2 对照", RotationScenario.LegacyNga, config);
        ImGui.SameLine();
        ScenarioButton("Custom 自定义", RotationScenario.Custom, config);

        var scenario = ScenarioRules.Resolve(config.Scenario);
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 1f, 1f), $"当前场景：{scenario.Name}");
        ImGui.TextWrapped(scenario.Summary);

        var gcd = config.GcdSeconds;
        if (ImGui.SliderFloat("自定义 GCD（秒）", ref gcd, 2.30f, 2.60f, "%.2f"))
        {
            config.GcdSeconds = gcd;
            changed = true;
        }

        var resolvedPlan = GuideAxisRules.ResolveSongPlan(
            config.SongPlan,
            config.GcdSeconds,
            config.WandererCutRemaining,
            config.MageCutRemaining,
            config.ArmyCutRemaining);
        ImGui.Text($"当前歌轴：{SongPlanName(config.SongPlan)} → {resolvedPlan.Name}");
        ImGui.TextDisabled($"实际切点：旅神剩 {resolvedPlan.WandererCutRemaining:F0}s / 贤者剩 {resolvedPlan.MageCutRemaining:F0}s / 军神剩 {resolvedPlan.ArmyCutRemaining:F0}s");
        ImGui.TextDisabled("攻略名称按诗心判定习惯写作3-3-12/3-6-9；游戏量谱的可执行切点分别是2-2-11/2-5-8。");
        changed |= CutSlider("旅神剩余秒数切歌", ref config.WandererCutRemaining, config);
        changed |= CutSlider("贤者剩余秒数切歌", ref config.MageCutRemaining, config);
        changed |= CutSlider("军神剩余秒数切歌", ref config.ArmyCutRemaining, config);

        ImGui.TextDisabled("按键由游戏当前热键栏自动读取；无需在插件中绑定，移动技能或修改键位后会自动更新。");

        var lookAhead = config.OgcdLookAheadSeconds;
        if (ImGui.SliderFloat("能力技 CD 提前观察（秒）", ref lookAhead, 0f, 1.5f, "%.2f"))
        {
            config.OgcdLookAheadSeconds = lookAhead;
            changed = true;
        }

        var queueWindow = config.GcdQueueWindowSeconds;
        if (ImGui.SliderFloat("GCD 排队窗口（秒）", ref queueWindow, 0.05f, 0.50f, "%.2f"))
        {
            config.GcdQueueWindowSeconds = queueWindow;
            changed = true;
        }
        ImGui.TextDisabled("固定规则：每个已结算 GCD 最多插入 2 个能力技；军神满层加速时最多单插；动作之间至少间隔 0.70 秒。");
        ImGui.TextDisabled("Boss 不可选时停火；同一目标复现后按当前 CD、歌曲、诗音和 DoT 重新规划，不追赶旧轴。");
        if (plugin.Engine.UsesLevel50Profile)
            ImGui.TextColored(new Vector4(0.35f, 0.85f, 1f, 1f), $"当前有效等级 {plugin.EffectiveLevel}：自动使用低等级技能与双歌循环，高等级自定义列表暂不参与执行。");

        ImGui.Separator();
        ImGui.TextUnformatted("DoT 持续伤害计算");
        changed |= ImGui.Checkbox("刷新窗口随 GCD 自动计算（GCD + 0.25秒）", ref config.DynamicDotRefreshWindow);
        if (!config.DynamicDotRefreshWindow)
        {
            var refreshLead = config.DotRefreshLeadSeconds;
            if (ImGui.SliderFloat("固定 DoT 刷新窗口（秒）", ref refreshLead, 1f, 12f, "%.1f"))
            {
                config.DotRefreshLeadSeconds = refreshLead;
                changed = true;
            }
        }

        var tickSeconds = config.DotTickSeconds;
        if (ImGui.SliderFloat("每跳间隔（秒）", ref tickSeconds, 1f, 5f, "%.1f"))
        {
            config.DotTickSeconds = tickSeconds;
            changed = true;
        }

        var causticPotency = config.CausticTickPotency;
        if (ImGui.SliderInt("毒咬每跳威力", ref causticPotency, 0, 100))
        {
            config.CausticTickPotency = causticPotency;
            changed = true;
        }

        var stormPotency = config.StormTickPotency;
        if (ImGui.SliderInt("风蚀每跳威力", ref stormPotency, 0, 100))
        {
            config.StormTickPotency = stormPotency;
            changed = true;
        }
        ImGui.TextDisabled("剩余跳数按每3秒一次估算，并明确计入未结算威力；不会只看直接伤害。");

        ImGui.Separator();
        ImGui.TextUnformatted("可视化条件循环（关闭攻略完美轴后生效）");
        DrawStepEditor(config, ref changed);

        if (changed)
            config.Save();
    }

    private void DrawStepEditor(Configuration config, ref bool changed)
    {
        selectedCatalogIndex = Math.Clamp(selectedCatalogIndex, 0, ActionCatalog.All.Count - 1);
        var selectedAction = ActionCatalog.All[selectedCatalogIndex];

        if (ImGui.BeginCombo("插入技能", selectedAction.Name))
        {
            for (var index = 0; index < ActionCatalog.All.Count; index++)
            {
                var action = ActionCatalog.All[index];
                if (ImGui.Selectable($"{action.Name}（{KindName(action.Kind)}）", selectedCatalogIndex == index))
                    selectedCatalogIndex = index;
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("添加到循环"))
        {
            config.Steps.Add(new RotationStep
            {
                ActionId = selectedAction.Id,
                Name = selectedAction.Name,
                Kind = selectedAction.Kind,
                Condition = selectedAction.SuggestedCondition,
                Priority = selectedAction.SuggestedPriority,
                Enabled = true,
            });
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("恢复默认循环"))
        {
            config.Steps = ActionCatalog.CreateDefaultSteps();
            changed = true;
        }

        var removeIndex = -1;
        var moveFrom = -1;
        var moveTo = -1;

        for (var index = 0; index < config.Steps.Count; index++)
        {
            var step = config.Steps[index];
            ImGui.PushID(step.Id.ToString());
            ImGui.Separator();

            if (ImGui.Checkbox("启用", ref step.Enabled))
                changed = true;
            ImGui.SameLine();
            ImGui.Text($"{step.Name}  [{KindName(step.Kind)}]");

            var priority = step.Priority;
            if (ImGui.SliderInt("优先级", ref priority, 0, 200))
            {
                step.Priority = priority;
                changed = true;
            }

            if (ImGui.BeginCombo("条件", ConditionName(step.Condition)))
            {
                foreach (var condition in Enum.GetValues<StepCondition>())
                {
                    if (ImGui.Selectable(ConditionName(condition), step.Condition == condition))
                    {
                        step.Condition = condition;
                        changed = true;
                    }
                }
                ImGui.EndCombo();
            }

            if (index > 0 && ImGui.Button("上移"))
            {
                moveFrom = index;
                moveTo = index - 1;
            }
            if (index > 0)
                ImGui.SameLine();
            if (index < config.Steps.Count - 1 && ImGui.Button("下移"))
            {
                moveFrom = index;
                moveTo = index + 1;
            }
            if (index < config.Steps.Count - 1)
                ImGui.SameLine();
            if (ImGui.Button("删除"))
                removeIndex = index;

            ImGui.PopID();
        }

        if (removeIndex >= 0)
        {
            config.Steps.RemoveAt(removeIndex);
            changed = true;
        }
        else if (moveFrom >= 0 && moveTo >= 0)
        {
            (config.Steps[moveFrom], config.Steps[moveTo]) = (config.Steps[moveTo], config.Steps[moveFrom]);
            changed = true;
        }
    }

    private static bool CutSlider(string label, ref float value, Configuration config)
    {
        var changed = ImGui.SliderFloat(label, ref value, 0f, 20f, "%.1f");
        if (changed)
        {
            config.SongPlan = SongPlanMode.Custom;
            config.Scenario = RotationScenario.Custom;
        }
        return changed;
    }

    private void ScenarioButton(string label, RotationScenario scenario, Configuration config)
    {
        var selected = config.Scenario == scenario;
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.55f, 0.75f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.2f, 0.65f, 0.88f, 1f));
        }

        if (ImGui.Button(label))
            plugin.SelectScenario(scenario);

        if (selected)
            ImGui.PopStyleColor(2);
    }

    private static string SongPlanName(SongPlanMode mode) => mode switch
    {
        SongPlanMode.GuideAuto => "旧NGA自动轴",
        SongPlanMode.Standard3312 => "Standard 3-3-12",
        SongPlanMode.Advanced369 => "Advanced 3-6-9",
        _ => "Custom 自定义",
    };

    private static string KindName(ShadowActionKind kind) => kind == ShadowActionKind.Gcd ? "GCD" : "能力技";

    private static string ConditionName(StepCondition condition) => condition switch
    {
        StepCondition.Always => "始终作为填充技能",
        StepCondition.CooldownReady => "真实冷却已转好",
        StepCondition.RefulgentReady => "辉煌箭触发可用",
        StepCondition.CausticMissing => "目标缺少毒咬 DoT",
        StepCondition.StormMissing => "目标缺少风蚀 DoT",
        StepCondition.DotRefreshDue => "双 DoT 进入计算刷新窗口",
        StepCondition.DotSnapshotDue => "猛者末段按收益截毒",
        StepCondition.RepertoireThree => "旅神达到3层诗音",
        StepCondition.SoulVoiceEighty => "魂音达到80",
        StepCondition.BlastArrowReady => "爆破箭触发可用",
        StepCondition.ResonantArrowReady => "共鸣箭触发可用",
        StepCondition.RadiantEncoreReady => "光明神返场触发可用",
        _ => condition.ToString(),
    };
}
