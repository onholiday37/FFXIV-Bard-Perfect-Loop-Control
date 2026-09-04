using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using GaugeSong = Dalamud.Game.ClientState.JobGauge.Enums.Song;

namespace BardPerfectLoop;

public sealed partial class ShadowEngine
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
    public bool RecommendedOgcdRequiresLateWeave { get; private set; }
    public int OgcdLimitThisCycle { get; private set; } = ExecutionRules.MaxOgcdPerGcd;
    public bool UsesLevel50Profile => plugin.EffectiveLevel is > 0 and <= 50;
    public uint GcdReferenceActionId => plugin.EffectiveLevel < 76 ? ActionCatalog.HeavyShot : ActionCatalog.BurstShot;
    public string ActiveProfileName => UsesLevel50Profile
        ? $"等级同步 {plugin.EffectiveLevel}：低等级自适应循环"
        : configuration.GuidePerfectAxis
            ? $"{ScenarioRules.Resolve(configuration.Scenario).Name}：{ResolveSongPlan().Name}"
            : "高等级自定义条件循环";

    public ShadowEngine(Plugin plugin, Configuration configuration)
    {
        this.plugin = plugin;
        this.configuration = configuration;
    }

    public void Arm()
    {
        Armed = true;
        dotLedger.Invalidate();
        lifeTarget = 0;
        pendingEmpyrealAt = double.NegativeInfinity;
        observedActionLock = 0.70f;
        wasInCombat = false;
        lastKnownSong = GaugeSong.None;
        OgcdLimitThisCycle = ExecutionRules.MaxOgcdPerGcd;
        Snapshot = WaitingSnapshot("已启动，等待进入战斗");
    }

    public void Stop(string reason = "已手动停止")
    {
        Armed = false;
        wasInCombat = false;
        ClearRecommendations();
        CurrentWeavePlan = WeavePlan.Empty(reason);
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
        var timing = LiveCombatReader.Gcd(GcdReferenceActionId, configuration.GcdSeconds);
        var gcdIndex = plugin.Executor.Timeline.GcdCount;
        var untilNextGcd = timing.Remaining;

        var gauge = Plugin.JobGauges.Get<BRDGauge>();
        OgcdLimitThisCycle = gauge.Song == GaugeSong.ArmysPaeon && gauge.Repertoire >= 4
            ? 1
            : ExecutionRules.MaxOgcdPerGcd;
        var dots = AnalyzeDots();
        var song = AnalyzeSong(gauge);
        var cooldowns = AnalyzeMajorCooldowns();
        var useGuideAxis = configuration.GuidePerfectAxis || UsesLevel50Profile;
        var nextGcdStep = useGuideAxis
            ? FindGuideGcd(dots, gauge)
            : FindRecommendation(ShadowActionKind.Gcd, dots, gauge, cooldowns);
        var nextOgcdStep = FindGuideOgcd(gauge);

        RecommendedGcdActionId = nextGcdStep?.ActionId ?? 0;
        RecommendedOgcdActionId = nextOgcdStep?.ActionId ?? 0;
        RecommendedGcdName = nextGcdStep?.Name ?? string.Empty;
        RecommendedOgcdName = nextOgcdStep?.Name ?? string.Empty;
        RecommendedOgcdRequiresLateWeave = false;

        var nextGcd = nextGcdStep is null ? "无可用 GCD 建议" : FormatStep(nextGcdStep);
        var nextOgcd = nextOgcdStep is null ? "暂不插入能力技" : FormatStep(nextOgcdStep);
        var reason = nextGcdStep is null ? string.Empty : useGuideAxis ? LastGcdReason : Explain(nextGcdStep, dots);

        reason += $"；{LastDotDecision.Reason}；{CurrentWeavePlan.Reason}";

        return new ShadowSnapshot(
            true,
            true,
            configuration.ShadowOnly ? "影子观察运行中（不发送技能）" : "完全控制运行中",
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

    private RotationStep? FindGuideGcd(DotAnalysis? dots, BRDGauge gauge)
    {
        var target = plugin.ResolveBattleTarget();
        if (target is null) return null;
        var timing = LiveCombatReader.Gcd(GcdReferenceActionId, configuration.GcdSeconds);
        var snapshots = dotLedger.Read(target.GameObjectId);
        var buffLeft = new[] { PlayerStatusRemaining(ActionCatalog.Buffs.RagingStrikes),
            PlayerStatusRemaining(ActionCatalog.Buffs.BattleVoice), PlayerStatusRemaining(ActionCatalog.Buffs.RadiantFinale) }
            .Where(t => t > 0).DefaultIfEmpty(0).Min();
        var state = new GcdState
        {
            Level = plugin.EffectiveLevel, Gcd = timing.Total, ExecuteIn = timing.Remaining, ApplicationDelay = EffectiveActionLock,
            Lifetime = TargetLifetime(), KnownEnd = configuration.TargetLifetimeSeconds > 0,
            Caustic = dots?.CausticRemaining ?? 0, Storm = dots?.StormRemaining ?? 0,
            CausticMultiplier = snapshots.Caustic, StormMultiplier = snapshots.Storm,
            SnapshotsKnown = snapshots.Known, Multiplier = CurrentMultiplier(timing.Remaining), BuffLeft = buffLeft,
            RagingLeft = PlayerStatusRemaining(ActionCatalog.Buffs.RagingStrikes), RagingReady = ReadCooldownRemaining(ActionCatalog.RagingStrikes),
            BarrageLeft = PlayerStatusRemaining(ActionCatalog.Buffs.Barrage), HawksEyeLeft = PlayerStatusRemaining(ActionCatalog.Buffs.HawksEye),
            BlastLeft = PlayerStatusRemaining(ActionCatalog.Buffs.BlastArrowReady), ResonantLeft = PlayerStatusRemaining(ActionCatalog.Buffs.ResonantArrowReady),
            EncoreLeft = PlayerStatusRemaining(ActionCatalog.Buffs.RadiantEncoreReady), Soul = gauge.SoulVoice,
            Song = gauge.Song switch { GaugeSong.WanderersMinuet => BardSong.Wanderer, GaugeSong.MagesBallad => BardSong.Mage, GaugeSong.ArmysPaeon => BardSong.Army, _ => BardSong.None },
            SongRemaining = gauge.SongTimer / 1000f,
            CausticPotency = plugin.EffectiveLevel < 64 ? 15 : configuration.CausticTickPotency,
            StormPotency = plugin.EffectiveLevel < 64 ? 20 : configuration.StormTickPotency,
        };
        var result = GcdPlanner.Select(state);
        LastGcdReason = result.Reason;
        LastDotDecision = result.Dots;
        return result.Action == 0 ? null : MakeStep(result.Action, result.Action switch
        {
            ActionCatalog.VenomousBite => "毒咬箭", ActionCatalog.Windbite => "风蚀箭",
            ActionCatalog.HeavyShot => "强力射击", ActionCatalog.StraightShot => "直线射击",
            _ => WeavePlanner.Name(result.Action),
        }, ShadowActionKind.Gcd, StepCondition.Always, 100);
    }

    private RotationStep? FindGuideOgcd(BRDGauge gauge)
    {
        ReplanOgcd();
        return CurrentWeavePlan.First is { } first
            ? MakeStep(first.ActionId, WeavePlanner.Name(first.ActionId), ShadowActionKind.Ogcd, StepCondition.CooldownReady, 100)
            : null;
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
            .Where(step => kind != ShadowActionKind.Ogcd || !plugin.Executor.RejectedActions.Contains(step.ActionId) && IsActionReadyNow(step.ActionId))
            .OrderByDescending(step => step.Priority)
            .FirstOrDefault(step => IsActionLearned(step.ActionId) && ConditionSatisfied(step, dots, gauge, cooldowns));
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
            StepCondition.Always => IsActionLearned(step.ActionId),
            StepCondition.CooldownReady => cooldowns.FirstOrDefault(cd => cd.ActionId == step.ActionId) is { Ready: true },
            StepCondition.RefulgentReady => IsActionHighlighted(ActionCatalog.RefulgentArrow),
            StepCondition.CausticMissing => dots?.CausticMissing == true,
            StepCondition.StormMissing => dots?.StormMissing == true,
            StepCondition.DotRefreshDue => dots?.RefreshDue == true,
            StepCondition.DotSnapshotDue when dots is not null => GuideAxisRules.ShouldSnapshotDots(
                dots.CausticRemaining,
                dots.StormRemaining,
                PlayerStatusRemaining(ActionCatalog.Buffs.RagingStrikes)),
            StepCondition.RepertoireThree => gauge.Song == GaugeSong.WanderersMinuet && gauge.Repertoire >= 3,
            StepCondition.SoulVoiceEighty => gauge.SoulVoice >= 80,
            StepCondition.BlastArrowReady => HasPlayerStatus(ActionCatalog.Buffs.BlastArrowReady),
            StepCondition.ResonantArrowReady => HasPlayerStatus(ActionCatalog.Buffs.ResonantArrowReady),
            StepCondition.RadiantEncoreReady => HasPlayerStatus(ActionCatalog.Buffs.RadiantEncoreReady),
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

        dotLedger.Observe(target.GameObjectId, causticRemaining, stormRemaining, ActionObserver.Now);
        var causticMissing = causticRemaining <= 0;
        var stormMissing = stormRemaining <= 0;
        var refreshWindow = configuration.DynamicDotRefreshWindow
            ? RotationMath.DynamicDotRefreshWindow(configuration.GcdSeconds)
            : Math.Clamp(configuration.DotRefreshLeadSeconds, 1f, 12f);
        var earliest = Math.Min(causticRemaining, stormRemaining);
        var refreshDue = !causticMissing && !stormMissing && earliest <= refreshWindow;
        var causticTicks = DotEvaluator.ExpectedTicks(causticRemaining);
        var stormTicks = DotEvaluator.ExpectedTicks(stormRemaining);

        return new DotAnalysis(
            target.Name.TextValue,
            plugin.EffectiveLevel < 64 ? "毒咬箭" : "烈毒咬箭",
            plugin.EffectiveLevel < 64 ? "风蚀箭" : "狂风蚀箭",
            causticRemaining,
            stormRemaining,
            causticTicks,
            stormTicks,
            causticTicks * (plugin.EffectiveLevel < 64 ? 15 : configuration.CausticTickPotency),
            stormTicks * (plugin.EffectiveLevel < 64 ? 20 : configuration.StormTickPotency),
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
        var plan = ResolveSongPlan();
        var cutRemaining = UsesLevel50Profile ? 0f : gauge.Song switch
        {
            GaugeSong.WanderersMinuet => plan.WandererCutRemaining,
            GaugeSong.MagesBallad => plan.MageCutRemaining,
            GaugeSong.ArmysPaeon => plan.ArmyCutRemaining,
            _ => 0,
        };
        var songActionId = gauge.Song switch
        {
            GaugeSong.WanderersMinuet => ActionCatalog.WanderersMinuet,
            GaugeSong.MagesBallad => ActionCatalog.MagesBallad,
            GaugeSong.ArmysPaeon => ActionCatalog.ArmysPaeon,
            _ => 0u,
        };

        return new SongAnalysis(
            UsesLevel50Profile ? "50级双歌循环" : plan.Name,
            current,
            next,
            remaining,
            cutRemaining,
            UsesLevel50Profile ? 45f : plan.SingingSecondsFor(songActionId),
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
            : configuration.GuidePerfectAxis
                ? GuideCooldownSteps(plugin.EffectiveLevel)
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
        if (level >= 92)
            yield return MakeStep(ActionCatalog.HeartbreakShot, "碎心箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 41);
        else if (level >= 12)
            yield return MakeStep(ActionCatalog.Bloodletter, "失血箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 40);
    }

    private static IEnumerable<RotationStep> GuideCooldownSteps(int level)
    {
        var actions = new (uint Id, string Name)[]
        {
            (ActionCatalog.EmpyrealArrow, "九天连箭"),
            (ActionCatalog.RagingStrikes, "猛者强击"),
            (ActionCatalog.BattleVoice, "战斗之声"),
            (ActionCatalog.RadiantFinale, "光明神的最终乐章"),
            (ActionCatalog.Barrage, "纷乱箭"),
            (ActionCatalog.Sidewinder, "侧风诱导箭"),
            (level >= 92 ? ActionCatalog.HeartbreakShot : ActionCatalog.Bloodletter,
                level >= 92 ? "碎心箭" : "失血箭"),
        };

        foreach (var action in actions)
        {
            if (level >= ActionCatalog.MinimumLevel(action.Id))
                yield return MakeStep(action.Id, action.Name, ShadowActionKind.Ogcd, StepCondition.CooldownReady, 100);
        }
    }

    private unsafe CooldownAnalysis ReadCooldown(ActionManager* manager, RotationStep step)
    {
        var remaining = LiveCombatReader.Cooldown(step.ActionId, plugin.EffectiveLevel);
        return new(step.Name, step.ActionId, remaining, remaining <= 0.001f);
    }

    private static unsafe bool IsActionHighlighted(uint actionId)
    {
        var manager = ActionManager.Instance();
        return manager is not null && manager->IsActionHighlighted(ActionType.Action, actionId);
    }

    private GuideSongPlan ResolveSongPlan() => GuideAxisRules.ResolveSongPlan(
        configuration.SongPlan,
        configuration.GcdSeconds,
        configuration.WandererCutRemaining,
        configuration.MageCutRemaining,
        configuration.ArmyCutRemaining);

    private bool IsActionLearned(uint actionId) => plugin.EffectiveLevel >= ActionCatalog.MinimumLevel(actionId);

    private static bool HasPlayerStatus(ushort statusId) => PlayerStatusRemaining(statusId) > 0f;

    private static float PlayerStatusRemaining(ushort statusId)
    {
        if (Plugin.ObjectTable.LocalPlayer is not IBattleChara player)
            return 0f;

        var remaining = 0f;
        foreach (var status in player.StatusList)
        {
            if (status.StatusId == statusId && status.RemainingTime > remaining)
                remaining = status.RemainingTime;
        }

        return remaining;
    }

    private float ReadCooldownRemaining(uint actionId) => LiveCombatReader.Cooldown(actionId, plugin.EffectiveLevel);

    private bool IsActionReadyNow(uint actionId) => plugin.ResolveBattleTarget() is { } target &&
        LiveCombatReader.CanUseNow(actionId, plugin.EffectiveLevel, target.GameObjectId);

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
        RecommendedOgcdRequiresLateWeave = false;
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
        ActionCatalog.MagesBallad or ActionCatalog.ArmysPaeon or ActionCatalog.WanderersMinuet => IsActionLearned(actionId),
        _ => false,
    };

    private unsafe bool IsActionReadySoon(uint actionId)
    {
        if (!IsActionLearned(actionId))
            return false;

        var manager = ActionManager.Instance();
        if (manager is null)
            return false;

        var adjustedActionId = manager->GetAdjustedActionId(actionId);
        if (!manager->IsRecastTimerActive(ActionType.Action, adjustedActionId))
            return true;

        var total = manager->GetRecastTime(ActionType.Action, adjustedActionId);
        var elapsed = manager->GetRecastTimeElapsed(ActionType.Action, adjustedActionId);
        return Math.Max(0f, total - elapsed) <= 8f;
    }

    private string FormatStep(RotationStep step) => FormatAction(step.ActionId, step.Name);

    private string FormatAction(uint actionId, string actionName) =>
        actionId == 0 ? $"[热键栏未找到] {actionName}" : plugin.HotbarKeys.Resolve(actionId).Format(actionName);

    private static string Explain(RotationStep step, DotAnalysis? dots) => step.Condition switch
    {
        StepCondition.CausticMissing => $"目标缺少{step.Name}持续伤害",
        StepCondition.StormMissing => $"目标缺少{step.Name}持续伤害",
        StepCondition.DotRefreshDue when dots is not null =>
            $"{step.Name}即将进入刷新窗口；双 DoT 期望剩余约 {dots.TotalRemainingPotency:F1} 基础威力",
        StepCondition.DotSnapshotDue when dots is not null =>
            $"已知增益快照比较；双 DoT 尚有约 {dots.TotalRemainingPotency:F1} 基础威力",
        StepCondition.RefulgentReady => $"{step.Name}触发可用",
        StepCondition.SoulVoiceEighty => "魂音达到80以上，进入攻略规定的绝峰/爆破窗口",
        StepCondition.BlastArrowReady => "爆破箭触发可用",
        StepCondition.ResonantArrowReady => "纷乱箭后的共鸣箭触发可用",
        StepCondition.RadiantEncoreReady => "光明神的返场余音触发可用",
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
