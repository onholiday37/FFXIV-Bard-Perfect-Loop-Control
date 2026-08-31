using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using GaugeSong = Dalamud.Game.ClientState.JobGauge.Enums.Song;

namespace BardPerfectLoop;

public sealed class ShadowEngine
{
    private static readonly HashSet<uint> CausticStatuses = [124, 1200];
    private static readonly HashSet<uint> StormStatuses = [129, 1201];

    private readonly Configuration configuration;
    private readonly Plugin plugin;

    private long combatStartedAt;
    private bool wasInCombat;
    private GaugeSong lastKnownSong;

    public bool Armed { get; private set; }
    public ShadowSnapshot Snapshot { get; private set; } = ShadowSnapshot.Idle("完全控制未启动");
    public uint RecommendedGcdActionId { get; private set; }
    public uint RecommendedOgcdActionId { get; private set; }
    public string RecommendedGcdName { get; private set; } = string.Empty;
    public string RecommendedOgcdName { get; private set; } = string.Empty;
    public bool UsesLevel50Profile => plugin.EffectiveLevel is > 0 and <= 50;
    public uint GcdReferenceActionId => UsesLevel50Profile ? ActionCatalog.HeavyShot : ActionCatalog.BurstShot;
    public string ActiveProfileName => UsesLevel50Profile
        ? $"等级同步 {plugin.EffectiveLevel}：低等级自适应循环"
        : "高等级自定义循环";

    public ShadowEngine(Plugin plugin, Configuration configuration)
    {
        this.plugin = plugin;
        this.configuration = configuration;
    }

    public void Arm()
    {
        Armed = true;
        wasInCombat = false;
        lastKnownSong = GaugeSong.None;
        Snapshot = WaitingSnapshot("已启动，等待进入战斗");
    }

    public void Stop(string reason = "已手动停止")
    {
        Armed = false;
        wasInCombat = false;
        ClearRecommendations();
        Snapshot = ShadowSnapshot.Idle(reason);
    }

    public void ResetTimeline()
    {
        combatStartedAt = Stopwatch.GetTimestamp();
        if (Armed)
            Snapshot = WaitingSnapshot(plugin.IsInCombat ? "时间轴已重新对齐" : "已重置，等待进入战斗");
    }

    public void Update()
    {
        if (!configuration.Enabled)
        {
            Stop("插件提醒已关闭");
            return;
        }

        if (!Armed)
            return;

        if (!plugin.IsBard)
        {
            Stop("职业已变化，保险停止");
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
        {
            Stop("本地角色不可用，保险停止");
            return;
        }

        if (player.CurrentHp <= 0)
        {
            Stop("角色倒地，保险停止");
            return;
        }

        if (!plugin.IsInCombat)
        {
            if (wasInCombat && configuration.StopWhenCombatEnds)
            {
                Stop("战斗结束，保险停止");
                return;
            }

            wasInCombat = false;
            Snapshot = WaitingSnapshot("已启动，等待进入战斗");
            return;
        }

        if (!wasInCombat)
        {
            combatStartedAt = Stopwatch.GetTimestamp();
            wasInCombat = true;
        }

        Snapshot = BuildSnapshot();
    }

    private ShadowSnapshot BuildSnapshot()
    {
        var elapsed = Stopwatch.GetElapsedTime(combatStartedAt).TotalSeconds;
        var gcd = Math.Clamp(configuration.GcdSeconds, 1.5f, 3.5f);
        var gcdIndex = (long)Math.Floor(elapsed / gcd) + 1;
        var withinGcd = elapsed % gcd;
        var untilNextGcd = (float)(gcd - withinGcd);
        if (untilNextGcd >= gcd - 0.001f)
            untilNextGcd = 0;

        var gauge = Plugin.JobGauges.Get<BRDGauge>();
        var dots = AnalyzeDots();
        var song = AnalyzeSong(gauge);
        var cooldowns = AnalyzeMajorCooldowns();
        var nextGcdStep = FindRecommendation(ShadowActionKind.Gcd, dots, gauge, cooldowns);
        var nextOgcdStep = FindRecommendation(ShadowActionKind.Ogcd, dots, gauge, cooldowns);

        RecommendedGcdActionId = nextGcdStep?.ActionId ?? 0;
        RecommendedOgcdActionId = nextOgcdStep?.ActionId ?? 0;
        RecommendedGcdName = nextGcdStep?.Name ?? string.Empty;
        RecommendedOgcdName = nextOgcdStep?.Name ?? string.Empty;

        var nextGcd = nextGcdStep is null ? "无可用 GCD 建议" : FormatStep(nextGcdStep);
        var nextOgcd = nextOgcdStep is null ? "暂不插入能力技" : FormatStep(nextOgcdStep);
        var reason = nextGcdStep is null ? string.Empty : Explain(nextGcdStep, dots);

        if (song is null)
        {
            var songChoice = ChooseReadySong(lastKnownSong);
            if (songChoice is not null)
            {
                RecommendedOgcdActionId = songChoice.Value.ActionId;
                RecommendedOgcdName = $"开启{songChoice.Value.Name}的歌";
                nextOgcd = FormatAction(songChoice.Value.ActionId, RecommendedOgcdName);
            }
        }
        else if (song.UntilSwitch <= configuration.OgcdLookAheadSeconds)
        {
            var songChoice = ChooseReadySong(gauge.Song);
            if (songChoice is not null)
            {
                RecommendedOgcdActionId = songChoice.Value.ActionId;
                RecommendedOgcdName = $"切换{songChoice.Value.Name}";
                nextOgcd = FormatAction(songChoice.Value.ActionId, RecommendedOgcdName);
            }
        }

        return new ShadowSnapshot(
            true,
            true,
            "完全控制运行中",
            elapsed,
            gcdIndex,
            untilNextGcd,
            nextGcd,
            nextOgcd,
            reason,
            dots,
            song,
            cooldowns);
    }

    private RotationStep? FindRecommendation(
        ShadowActionKind kind,
        DotAnalysis? dots,
        BRDGauge gauge,
        CooldownAnalysis[] cooldowns)
    {
        if (UsesLevel50Profile)
            return FindLevelSyncedRecommendation(kind, dots, cooldowns);

        return configuration.Steps
            .Where(step => step.Enabled && step.Kind == kind)
            .OrderByDescending(step => step.Priority)
            .FirstOrDefault(step => ConditionSatisfied(step, dots, gauge, cooldowns));
    }

    private RotationStep? FindLevelSyncedRecommendation(
        ShadowActionKind kind,
        DotAnalysis? dots,
        CooldownAnalysis[] cooldowns)
    {
        var level = plugin.EffectiveLevel;
        if (kind == ShadowActionKind.Gcd)
        {
            var refreshWindow = configuration.DynamicDotRefreshWindow
                ? RotationMath.DynamicDotRefreshWindow(configuration.GcdSeconds)
                : Math.Clamp(configuration.DotRefreshLeadSeconds, 1f, 12f);
            return LevelSyncRules.SelectGcd(
                level,
                dots,
                refreshWindow,
                IsActionHighlighted(ActionCatalog.StraightShot));
        }

        var readyActionIds = cooldowns
            .Where(cooldown => cooldown.Ready)
            .Select(cooldown => cooldown.ActionId)
            .ToHashSet();
        return LevelSyncRules.SelectOgcd(level, readyActionIds);
    }

    private static RotationStep MakeStep(
        uint actionId,
        string name,
        ShadowActionKind kind,
        StepCondition condition,
        int priority) => LevelSyncRules.MakeStep(actionId, name, kind, condition, priority);

    private bool ConditionSatisfied(
        RotationStep step,
        DotAnalysis? dots,
        BRDGauge gauge,
        CooldownAnalysis[] cooldowns)
    {
        return step.Condition switch
        {
            StepCondition.Always => true,
            StepCondition.CooldownReady => cooldowns.FirstOrDefault(cd => cd.ActionId == step.ActionId) is { Ready: true },
            StepCondition.RefulgentReady => IsActionHighlighted(ActionCatalog.RefulgentArrow),
            StepCondition.CausticMissing => dots?.CausticMissing == true,
            StepCondition.StormMissing => dots?.StormMissing == true,
            StepCondition.DotRefreshDue => dots?.RefreshDue == true,
            StepCondition.RepertoireThree => gauge.Song == GaugeSong.WanderersMinuet && gauge.Repertoire >= 3,
            _ => false,
        };
    }

    private DotAnalysis? AnalyzeDots()
    {
        if (plugin.ResolveBattleTarget() is not IBattleChara target || Plugin.ObjectTable.LocalPlayer is null)
            return null;

        var sourceId = Plugin.ObjectTable.LocalPlayer.EntityId;
        var causticRemaining = 0f;
        var stormRemaining = 0f;

        foreach (var status in target.StatusList)
        {
            if (status.SourceId != sourceId || status.RemainingTime <= 0)
                continue;

            if (CausticStatuses.Contains(status.StatusId))
                causticRemaining = Math.Max(causticRemaining, status.RemainingTime);
            else if (StormStatuses.Contains(status.StatusId))
                stormRemaining = Math.Max(stormRemaining, status.RemainingTime);
        }

        var causticMissing = causticRemaining <= 0;
        var stormMissing = stormRemaining <= 0;
        var refreshWindow = configuration.DynamicDotRefreshWindow
            ? RotationMath.DynamicDotRefreshWindow(configuration.GcdSeconds)
            : Math.Clamp(configuration.DotRefreshLeadSeconds, 1f, 12f);
        var earliest = Math.Min(causticRemaining, stormRemaining);
        var refreshDue = !causticMissing && !stormMissing && earliest <= refreshWindow;
        var causticTicks = RotationMath.EstimateDotTicks(causticRemaining, configuration.DotTickSeconds);
        var stormTicks = RotationMath.EstimateDotTicks(stormRemaining, configuration.DotTickSeconds);

        return new DotAnalysis(
            target.Name.TextValue,
            UsesLevel50Profile ? "毒咬箭" : "烈毒咬箭",
            UsesLevel50Profile ? "风蚀箭" : "狂风蚀箭",
            causticRemaining,
            stormRemaining,
            causticTicks,
            stormTicks,
            RotationMath.RemainingDotPotency(causticTicks, UsesLevel50Profile ? 15 : configuration.CausticTickPotency),
            RotationMath.RemainingDotPotency(stormTicks, UsesLevel50Profile ? 20 : configuration.StormTickPotency),
            causticMissing,
            stormMissing,
            refreshDue);
    }

    private SongAnalysis? AnalyzeSong(BRDGauge gauge)
    {
        var current = SongName(gauge.Song);
        if (current is null || gauge.SongTimer <= 0)
            return null;

        lastKnownSong = gauge.Song;

        var next = NextSongName(gauge.Song, UsesLevel50Profile);
        var remaining = Math.Max(0, gauge.SongTimer / 1000f);
        var cutRemaining = UsesLevel50Profile ? 0f : gauge.Song switch
        {
            GaugeSong.WanderersMinuet => configuration.WandererCutRemaining,
            GaugeSong.MagesBallad => configuration.MageCutRemaining,
            GaugeSong.ArmysPaeon => configuration.ArmyCutRemaining,
            _ => 0,
        };

        return new SongAnalysis(
            current,
            next,
            remaining,
            cutRemaining,
            remaining - cutRemaining,
            gauge.Repertoire);
    }

    private unsafe CooldownAnalysis[] AnalyzeMajorCooldowns()
    {
        var manager = ActionManager.Instance();
        if (manager is null)
            return [];

        var steps = UsesLevel50Profile
            ? LevelSyncedCooldownSteps(plugin.EffectiveLevel)
            : configuration.Steps
                .Where(step => step.Enabled && step.Kind == ShadowActionKind.Ogcd && step.Condition == StepCondition.CooldownReady);

        return steps
            .GroupBy(step => step.ActionId)
            .Select(group => group.First())
            .Select(step => ReadCooldown(manager, step))
            .OrderBy(cooldown => cooldown.Remaining)
            .ToArray();
    }

    private static IEnumerable<RotationStep> LevelSyncedCooldownSteps(int level)
    {
        if (level >= 4)
            yield return MakeStep(ActionCatalog.RagingStrikes, "猛者强击", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 120);
        if (level >= 50)
            yield return MakeStep(ActionCatalog.BattleVoice, "战斗之声", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 115);
        if (level >= 38)
            yield return MakeStep(ActionCatalog.Barrage, "纷乱箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 110);
        if (level >= 12)
            yield return MakeStep(ActionCatalog.Bloodletter, "失血箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 40);
    }

    private unsafe CooldownAnalysis ReadCooldown(ActionManager* manager, RotationStep step)
    {
        var active = manager->IsRecastTimerActive(ActionType.Action, step.ActionId);
        if (!active)
            return new CooldownAnalysis(step.Name, step.ActionId, 0, true);

        var total = manager->GetRecastTime(ActionType.Action, step.ActionId);
        var elapsed = manager->GetRecastTimeElapsed(ActionType.Action, step.ActionId);
        var remaining = Math.Max(0, total - elapsed);
        return new CooldownAnalysis(
            step.Name,
            step.ActionId,
            remaining,
            remaining <= configuration.OgcdLookAheadSeconds);
    }

    private static unsafe bool IsActionHighlighted(uint actionId)
    {
        var manager = ActionManager.Instance();
        return manager is not null && manager->IsActionHighlighted(ActionType.Action, actionId);
    }

    private ShadowSnapshot WaitingSnapshot(string status) => new(
        true,
        false,
        status,
        0,
        0,
        0,
        "等待战斗",
        "等待战斗",
        string.Empty,
        null,
        null,
        []);

    private void ClearRecommendations()
    {
        RecommendedGcdActionId = 0;
        RecommendedOgcdActionId = 0;
        RecommendedGcdName = string.Empty;
        RecommendedOgcdName = string.Empty;
    }

    private (uint ActionId, string Name)? ChooseReadySong(GaugeSong previousSong)
    {
        if (UsesLevel50Profile)
        {
            var syncedCandidates = previousSong switch
            {
                GaugeSong.MagesBallad => new[]
                {
                    (ActionCatalog.ArmysPaeon, "军神"),
                    (ActionCatalog.MagesBallad, "贤者"),
                },
                GaugeSong.ArmysPaeon => new[]
                {
                    (ActionCatalog.MagesBallad, "贤者"),
                    (ActionCatalog.ArmysPaeon, "军神"),
                },
                _ => new[]
                {
                    (ActionCatalog.MagesBallad, "贤者"),
                    (ActionCatalog.ArmysPaeon, "军神"),
                },
            };

            foreach (var candidate in syncedCandidates)
            {
                if (IsSongLearned(candidate.Item1) && IsActionReadySoon(candidate.Item1))
                    return candidate;
            }

            return null;
        }

        var candidates = previousSong switch
        {
            GaugeSong.WanderersMinuet => new[]
            {
                (ActionCatalog.MagesBallad, "贤者"),
                (ActionCatalog.ArmysPaeon, "军神"),
                (ActionCatalog.WanderersMinuet, "旅神"),
            },
            GaugeSong.MagesBallad => new[]
            {
                (ActionCatalog.ArmysPaeon, "军神"),
                (ActionCatalog.WanderersMinuet, "旅神"),
                (ActionCatalog.MagesBallad, "贤者"),
            },
            GaugeSong.ArmysPaeon => new[]
            {
                (ActionCatalog.WanderersMinuet, "旅神"),
                (ActionCatalog.MagesBallad, "贤者"),
                (ActionCatalog.ArmysPaeon, "军神"),
            },
            _ => new[]
            {
                (ActionCatalog.WanderersMinuet, "旅神"),
                (ActionCatalog.MagesBallad, "贤者"),
                (ActionCatalog.ArmysPaeon, "军神"),
            },
        };

        foreach (var candidate in candidates)
        {
            if (IsActionReadySoon(candidate.Item1))
                return candidate;
        }

        return null;
    }

    private bool IsSongLearned(uint actionId) => actionId switch
    {
        ActionCatalog.MagesBallad => plugin.EffectiveLevel >= 30,
        ActionCatalog.ArmysPaeon => plugin.EffectiveLevel >= 40,
        ActionCatalog.WanderersMinuet => plugin.EffectiveLevel >= 52,
        _ => false,
    };

    private unsafe bool IsActionReadySoon(uint actionId)
    {
        var manager = ActionManager.Instance();
        if (manager is null || !manager->IsRecastTimerActive(ActionType.Action, actionId))
            return manager is not null;

        var total = manager->GetRecastTime(ActionType.Action, actionId);
        var elapsed = manager->GetRecastTimeElapsed(ActionType.Action, actionId);
        return Math.Max(0f, total - elapsed) <= configuration.OgcdLookAheadSeconds;
    }

    private string FormatStep(RotationStep step) => FormatAction(step.ActionId, step.Name);

    private string FormatAction(uint actionId, string actionName) =>
        actionId == 0 ? $"[热键栏未找到] {actionName}" : plugin.HotbarKeys.Resolve(actionId).Format(actionName);

    private static string Explain(RotationStep step, DotAnalysis? dots) => step.Condition switch
    {
        StepCondition.CausticMissing => $"目标缺少{step.Name}持续伤害",
        StepCondition.StormMissing => $"目标缺少{step.Name}持续伤害",
        StepCondition.DotRefreshDue when dots is not null =>
            $"{step.Name}即将进入刷新窗口；双 DoT 未跳伤害约 {dots.TotalRemainingPotency} 威力",
        StepCondition.RefulgentReady => $"{step.Name}触发可用",
        StepCondition.Always => "当前没有更高优先级条件，使用基础填充 GCD",
        _ => "满足当前循环条件",
    };

    private static string? SongName(GaugeSong song) => song switch
    {
        GaugeSong.WanderersMinuet => "旅神",
        GaugeSong.MagesBallad => "贤者",
        GaugeSong.ArmysPaeon => "军神",
        _ => null,
    };

    private static string NextSongName(GaugeSong song, bool levelSyncedProfile) => song switch
    {
        GaugeSong.WanderersMinuet => "贤者",
        GaugeSong.MagesBallad => "军神",
        GaugeSong.ArmysPaeon => levelSyncedProfile ? "贤者" : "旅神",
        _ => levelSyncedProfile ? "贤者" : "旅神",
    };
}
