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
        OgcdLimitThisCycle = gauge.Song == GaugeSong.ArmysPaeon && gauge.Repertoire >= 4
            ? 1
            : ExecutionRules.MaxOgcdPerGcd;
        var dots = AnalyzeDots();
        var song = AnalyzeSong(gauge);
        var cooldowns = AnalyzeMajorCooldowns();
        var useGuideAxis = configuration.GuidePerfectAxis && !UsesLevel50Profile;
        var nextGcdStep = useGuideAxis
            ? FindGuideGcd(dots, gauge)
            : FindRecommendation(ShadowActionKind.Gcd, dots, gauge, cooldowns);
        var nextOgcdStep = useGuideAxis
            ? FindGuideOgcd(gauge)
            : FindRecommendation(ShadowActionKind.Ogcd, dots, gauge, cooldowns);

        RecommendedGcdActionId = nextGcdStep?.ActionId ?? 0;
        RecommendedOgcdActionId = nextOgcdStep?.ActionId ?? 0;
        RecommendedGcdName = nextGcdStep?.Name ?? string.Empty;
        RecommendedOgcdName = nextOgcdStep?.Name ?? string.Empty;
        RecommendedOgcdRequiresLateWeave = nextOgcdStep is not null && GuideAxisRules.IsLateWeave(
            nextOgcdStep.ActionId,
            ScenarioRules.Resolve(configuration.Scenario).UsesModernBurstOrder);

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
                RecommendedOgcdRequiresLateWeave = false;
                nextOgcd = FormatAction(songChoice.Value.ActionId, RecommendedOgcdName);
            }
        }
        else if (song.UntilSwitch <= configuration.OgcdLookAheadSeconds)
        {
            if (gauge.Song == GaugeSong.WanderersMinuet && gauge.Repertoire > 0 && IsActionLearned(ActionCatalog.PitchPerfect))
            {
                RecommendedOgcdActionId = ActionCatalog.PitchPerfect;
                RecommendedOgcdName = $"切歌前释放{gauge.Repertoire}层完美音调";
                RecommendedOgcdRequiresLateWeave = false;
                nextOgcd = FormatAction(RecommendedOgcdActionId, RecommendedOgcdName);
            }
            else if (gauge.Song == GaugeSong.MagesBallad && IsActionReadyNow(ActionCatalog.EmpyrealArrow))
            {
                RecommendedOgcdActionId = ActionCatalog.EmpyrealArrow;
                RecommendedOgcdName = "切歌前释放九天连箭";
                RecommendedOgcdRequiresLateWeave = false;
                nextOgcd = FormatAction(RecommendedOgcdActionId, RecommendedOgcdName);
            }
            else if (ChooseReadySong(gauge.Song) is { } songChoice)
            {
                RecommendedOgcdActionId = songChoice.ActionId;
                RecommendedOgcdName = $"切换{songChoice.Name}";
                RecommendedOgcdRequiresLateWeave = false;
                nextOgcd = FormatAction(songChoice.ActionId, RecommendedOgcdName);
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

    private RotationStep FindGuideGcd(DotAnalysis? dots, BRDGauge gauge)
    {
        var level = plugin.EffectiveLevel;
        var stormAction = level >= 64 ? ActionCatalog.Stormbite : ActionCatalog.Windbite;
        var causticAction = level >= 64 ? ActionCatalog.CausticBite : ActionCatalog.VenomousBite;
        var stormName = level >= 64 ? "狂风蚀箭" : "风蚀箭";
        var causticName = level >= 64 ? "烈毒咬箭" : "毒咬箭";
        var refreshWindow = configuration.DynamicDotRefreshWindow
            ? RotationMath.DynamicDotRefreshWindow(configuration.GcdSeconds)
            : Math.Clamp(configuration.DotRefreshLeadSeconds, 1f, 12f);

        // NGA opener applies Stormbite first, then Caustic Bite.
        if (level >= 30 && dots?.StormMissing == true)
            return MakeStep(stormAction, stormName, ShadowActionKind.Gcd, StepCondition.StormMissing, 200);
        if (level >= 6 && dots?.CausticMissing == true)
            return MakeStep(causticAction, causticName, ShadowActionKind.Gcd, StepCondition.CausticMissing, 195);

        if (HasPlayerStatus(ActionCatalog.Buffs.Barrage))
        {
            var action = level >= 70 ? ActionCatalog.RefulgentArrow : ActionCatalog.StraightShot;
            var name = level >= 70 ? "纷乱辉煌箭" : "纷乱直线射击";
            return MakeStep(action, name, ShadowActionKind.Gcd, StepCondition.RefulgentReady, 190);
        }

        var ragingRemaining = PlayerStatusRemaining(ActionCatalog.Buffs.RagingStrikes);
        var radiantRemaining = PlayerStatusRemaining(ActionCatalog.Buffs.RadiantFinale);
        if (IsActionLearned(ActionCatalog.RadiantEncore) &&
            HasPlayerStatus(ActionCatalog.Buffs.RadiantEncoreReady) &&
            ragingRemaining > 0f &&
            radiantRemaining < 16f)
            return MakeStep(ActionCatalog.RadiantEncore, "光明神的返场余音", ShadowActionKind.Gcd, StepCondition.RadiantEncoreReady, 185);

        if (IsActionLearned(ActionCatalog.BlastArrow) && HasPlayerStatus(ActionCatalog.Buffs.BlastArrowReady))
            return MakeStep(ActionCatalog.BlastArrow, "爆破箭", ShadowActionKind.Gcd, StepCondition.BlastArrowReady, 180);

        var ragingCooldown = ReadCooldownRemaining(ActionCatalog.RagingStrikes);
        var inBurstWindow = ragingRemaining > 0f;
        var scenario = ScenarioRules.Resolve(configuration.Scenario);
        var songRemaining = Math.Max(0f, gauge.SongTimer / 1000f);
        var useApex = scenario.UsesCurrentApexRules
            ? GuideAxisRules.ShouldUseCurrentApex(
                gauge.SoulVoice,
                inBurstWindow,
                gauge.Song == GaugeSong.MagesBallad,
                songRemaining)
            : GuideAxisRules.ShouldUseApex(gauge.SoulVoice, inBurstWindow, ragingCooldown);
        if (IsActionLearned(ActionCatalog.ApexArrow) &&
            useApex)
            return MakeStep(ActionCatalog.ApexArrow, $"绝峰箭（魂音 {gauge.SoulVoice}）", ShadowActionKind.Gcd, StepCondition.SoulVoiceEighty, 175);

        if (IsActionLearned(ActionCatalog.ResonantArrow) && HasPlayerStatus(ActionCatalog.Buffs.ResonantArrowReady))
            return MakeStep(ActionCatalog.ResonantArrow, "共鸣箭", ShadowActionKind.Gcd, StepCondition.ResonantArrowReady, 170);

        var snapshotDue = scenario.UsesLegacyDotSnapshot && dots is not null && GuideAxisRules.ShouldSnapshotDots(
            dots.CausticRemaining,
            dots.StormRemaining,
            ragingRemaining);
        if (level >= 56 && dots is not null && (snapshotDue || dots.RefreshDue))
            return MakeStep(
                ActionCatalog.IronJaws,
                snapshotDue ? "伶牙俐齿（猛者末段截毒）" : "伶牙俐齿",
                ShadowActionKind.Gcd,
                snapshotDue ? StepCondition.DotSnapshotDue : StepCondition.DotRefreshDue,
                165);

        if (level < 56 && dots is not null)
        {
            if (level >= 30 && dots.StormRemaining <= refreshWindow)
                return MakeStep(stormAction, stormName, ShadowActionKind.Gcd, StepCondition.DotRefreshDue, 160);
            if (level >= 6 && dots.CausticRemaining <= refreshWindow)
                return MakeStep(causticAction, causticName, ShadowActionKind.Gcd, StepCondition.DotRefreshDue, 155);
        }

        if (level >= 2 && (HasPlayerStatus(ActionCatalog.Buffs.HawksEye) || IsActionHighlighted(ActionCatalog.RefulgentArrow)))
        {
            var action = level >= 70 ? ActionCatalog.RefulgentArrow : ActionCatalog.StraightShot;
            var name = level >= 70 ? "辉煌箭" : "直线射击";
            return MakeStep(action, name, ShadowActionKind.Gcd, StepCondition.RefulgentReady, 150);
        }

        var filler = level >= 76 ? ActionCatalog.BurstShot : ActionCatalog.HeavyShot;
        return MakeStep(filler, level >= 76 ? "爆发射击" : "强力射击", ShadowActionKind.Gcd, StepCondition.Always, 10);
    }

    private RotationStep? FindGuideOgcd(BRDGauge gauge)
    {
        var songActive = gauge.Song != GaugeSong.None && gauge.SongTimer > 0;

        if (gauge.Song == GaugeSong.WanderersMinuet &&
            IsActionLearned(ActionCatalog.PitchPerfect) &&
            (gauge.Repertoire >= 3 ||
             gauge.Repertoire >= 2 && ReadCooldownRemaining(ActionCatalog.EmpyrealArrow) < 2f))
            return MakeStep(ActionCatalog.PitchPerfect, "完美音调", ShadowActionKind.Ogcd, StepCondition.RepertoireThree, 250);

        // The guide's central rule: Empyreal Arrow must not drift, and two uses
        // should land inside a 20-second party-buff window whenever cooldown permits.
        if (songActive && IsActionReadyNow(ActionCatalog.EmpyrealArrow))
            return MakeStep(ActionCatalog.EmpyrealArrow, "九天连箭（好了就打）", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 245);

        // One GCD of lead is reserved so the party buffs can be staged before
        // Raging Strikes without drifting the real cooldown anchor.
        var ragingReady = ReadCooldownRemaining(ActionCatalog.RagingStrikes) <= 2.2f;
        var ragingRemaining = PlayerStatusRemaining(ActionCatalog.Buffs.RagingStrikes);
        var battleVoiceRemaining = PlayerStatusRemaining(ActionCatalog.Buffs.BattleVoice);
        var radiantRemaining = PlayerStatusRemaining(ActionCatalog.Buffs.RadiantFinale);
        var battleVoiceCooldown = ReadCooldownRemaining(ActionCatalog.BattleVoice);
        var radiantCooldown = ReadCooldownRemaining(ActionCatalog.RadiantFinale);

        if (songActive && ragingReady)
        {
            var scenario = ScenarioRules.Resolve(configuration.Scenario);
            if (scenario.UsesModernBurstOrder)
            {
                if (IsActionReadyNow(ActionCatalog.RadiantFinale))
                    return MakeStep(ActionCatalog.RadiantFinale, "光明神的最终乐章（当前轴先开）", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 240);

                var radiantStarted = !IsActionLearned(ActionCatalog.RadiantFinale) ||
                                     radiantRemaining > 0f ||
                                     radiantCooldown > 90f;
                if (radiantStarted && IsActionReadyNow(ActionCatalog.BattleVoice))
                    return MakeStep(ActionCatalog.BattleVoice, "战斗之声（与光明神双插）", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 235);

                var battleVoiceStarted = !IsActionLearned(ActionCatalog.BattleVoice) ||
                                         battleVoiceRemaining > 0f ||
                                         battleVoiceCooldown > 100f;
                if (radiantStarted && battleVoiceStarted && IsActionReadyNow(ActionCatalog.RagingStrikes))
                    return MakeStep(ActionCatalog.RagingStrikes, "猛者强击（下一GCD后半插）", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 230);
            }
            else
            {
                if (IsActionReadyNow(ActionCatalog.BattleVoice))
                    return MakeStep(ActionCatalog.BattleVoice, "战斗之声（旧NGA顺序）", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 240);

                var battleVoiceStarted = !IsActionLearned(ActionCatalog.BattleVoice) ||
                                         battleVoiceRemaining > 0f ||
                                         battleVoiceCooldown > 100f;
                if (battleVoiceStarted && IsActionReadyNow(ActionCatalog.RadiantFinale))
                    return MakeStep(ActionCatalog.RadiantFinale, "光明神的最终乐章（旧NGA顺序）", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 235);

                var radiantStarted = !IsActionLearned(ActionCatalog.RadiantFinale) ||
                                     radiantRemaining > 0f ||
                                     radiantCooldown > 90f;
                if (battleVoiceStarted && radiantStarted && IsActionReadyNow(ActionCatalog.RagingStrikes))
                    return MakeStep(ActionCatalog.RagingStrikes, "猛者强击（旧NGA最后开启）", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 230);
            }
        }

        if (ragingRemaining > 0f && IsActionReadyNow(ActionCatalog.Barrage) && !HasPlayerStatus(ActionCatalog.Buffs.ResonantArrowReady))
            return MakeStep(ActionCatalog.Barrage, "纷乱箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 225);

        if (ragingRemaining > 0f && IsActionReadyNow(ActionCatalog.Sidewinder))
            return MakeStep(ActionCatalog.Sidewinder, "侧风诱导箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 220);

        if (IsActionReadyNow(ActionCatalog.Bloodletter) &&
            (ragingRemaining > 0f || ReadCooldownRemaining(ActionCatalog.RagingStrikes) > 30f))
            return MakeStep(ActionCatalog.Bloodletter, plugin.EffectiveLevel >= 92 ? "碎心箭" : "失血箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 100);

        if (IsActionReadyNow(ActionCatalog.Sidewinder) && ReadCooldownRemaining(ActionCatalog.RagingStrikes) > 30f)
            return MakeStep(ActionCatalog.Sidewinder, "侧风诱导箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 90);

        return null;
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
            plugin.EffectiveLevel < 64 ? "毒咬箭" : "烈毒咬箭",
            plugin.EffectiveLevel < 64 ? "风蚀箭" : "狂风蚀箭",
            causticRemaining,
            stormRemaining,
            causticTicks,
            stormTicks,
            RotationMath.RemainingDotPotency(causticTicks, plugin.EffectiveLevel < 64 ? 15 : configuration.CausticTickPotency),
            RotationMath.RemainingDotPotency(stormTicks, plugin.EffectiveLevel < 64 ? 20 : configuration.StormTickPotency),
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
        if (level >= 12)
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
            (ActionCatalog.Bloodletter, level >= 92 ? "碎心箭" : "失血箭"),
        };

        foreach (var action in actions)
        {
            if (level >= ActionCatalog.MinimumLevel(action.Id))
                yield return MakeStep(action.Id, action.Name, ShadowActionKind.Ogcd, StepCondition.CooldownReady, 100);
        }
    }

    private unsafe CooldownAnalysis ReadCooldown(ActionManager* manager, RotationStep step)
    {
        if (!IsActionLearned(step.ActionId))
            return new CooldownAnalysis(step.Name, step.ActionId, float.MaxValue, false);

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

    private unsafe float ReadCooldownRemaining(uint actionId)
    {
        if (!IsActionLearned(actionId))
            return float.MaxValue;

        var manager = ActionManager.Instance();
        if (manager is null || !manager->IsRecastTimerActive(ActionType.Action, actionId))
            return 0f;

        return Math.Max(
            0f,
            manager->GetRecastTime(ActionType.Action, actionId) -
            manager->GetRecastTimeElapsed(ActionType.Action, actionId));
    }

    private unsafe bool IsActionReadyNow(uint actionId)
    {
        if (!IsActionLearned(actionId) || plugin.ResolveBattleTarget() is not IBattleChara target)
            return false;

        var manager = ActionManager.Instance();
        if (manager is null)
            return false;

        var adjusted = manager->GetAdjustedActionId(actionId);
        return manager->GetActionStatus(ActionType.Action, adjusted, target.GameObjectId, false, false, null) == 0;
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
        StepCondition.DotSnapshotDue when dots is not null =>
            $"猛者末段按攻略截毒；覆盖前双 DoT 尚有约 {dots.TotalRemainingPotency} 威力，已计入取舍",
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
