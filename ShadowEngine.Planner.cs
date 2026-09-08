using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.JobGauge.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using GaugeSong = Dalamud.Game.ClientState.JobGauge.Enums.Song;

namespace BardPerfectLoop;

public sealed partial class ShadowEngine
{
    private readonly DotSnapshotLedger dotLedger = new();
    private double pendingEmpyrealAt = double.NegativeInfinity;
    private int repertoireAtEmpyreal;
    private float soulAtEmpyreal;
    private float finaleMultiplier = 1.02f;
    private int lastCoda;
    private ulong lifeTarget;
    private double lifeTargetAt;
    private float observedActionLock = 0.70f;
    public WeavePlan CurrentWeavePlan { get; private set; } = WeavePlan.Empty("未启动");
    public string LastPlannerInputs { get; private set; } = string.Empty;
    public float EffectiveActionLock => Math.Max(observedActionLock, configuration.ActionLockSeconds);
    public int ActualRepertoire => Plugin.JobGauges.Get<BRDGauge>().Repertoire;
    public double PlannerMilliseconds { get; private set; }
    public DotDecision LastDotDecision { get; private set; }
    public string LastGcdReason { get; private set; } = string.Empty;
    public void ResetTargetEstimate() { lifeTarget = 0; }
    private bool KnownTargetWindow => configuration.TargetLifetimeSeconds > 0 || plugin.Battlefield.IsEncounterBoss && plugin.Battlefield.Encounter.PredictedEnd;

    internal void OnClientExecuted(uint action, ulong target, double time, int repertoireBefore, int soulBefore, int codaBefore)
    {
        if (action == ActionCatalog.EmpyrealArrow)
        {
            pendingEmpyrealAt = repertoireBefore >= 0 ? time : double.NegativeInfinity;
            repertoireAtEmpyreal = repertoireBefore;
            soulAtEmpyreal = soulBefore;
        }
        if (action == ActionCatalog.RadiantFinale)
            finaleMultiplier = 1 + 0.02f * Math.Clamp(codaBefore >= 0 ? codaBefore : lastCoda, 1, 3);
        dotLedger.Stage(target, action, CurrentMultiplier(), time);
    }

    private float CurrentMultiplier(float offset = 0) =>
        (plugin.Consumables.PotionRemaining > offset ? plugin.Consumables.PotionMultiplier : 1) *
        (PlayerStatusRemaining(ActionCatalog.Buffs.RagingStrikes) > offset ? 1.15f : 1) *
        (PlayerStatusRemaining(ActionCatalog.Buffs.RadiantFinale) > offset ? finaleMultiplier : 1) *
        (PlayerStatusRemaining(ActionCatalog.Buffs.BattleVoice) > offset
            ? (1 + 0.25f * Math.Min(1, configuration.AssumedDirectHitRate + 0.20f)) / (1 + 0.25f * configuration.AssumedDirectHitRate) : 1);

    private float TargetLifetime()
    {
        var target = plugin.ResolveBattleTarget();
        if (target is null) return 0;
        if (lifeTarget != target.GameObjectId) { lifeTarget = target.GameObjectId; lifeTargetAt = ActionObserver.Now; }
        var estimate = configuration.TargetLifetimeSeconds <= 0 ? plugin.Battlefield.AddLifetimeEstimate : Math.Max(0, configuration.TargetLifetimeSeconds - (float)(ActionObserver.Now - lifeTargetAt));
        if (plugin.Battlefield.IsEncounterBoss && plugin.Battlefield.Encounter.PredictedEnd)
            estimate = Math.Min(estimate, Math.Max(0.01f, plugin.Battlefield.Encounter.UptimeRemaining));
        return estimate;
    }

    public unsafe void ReplanOgcd()
    {
        var manager = ActionManager.Instance();
        if (manager is not null)
        {
            var since = ActionObserver.Now - plugin.Executor.Timeline.LastExecutionAt;
            if (since is >= 0 and < 1.4 && manager->AnimationLock is > 0.001f and < 1.5f &&
                nowSinceItem() > 2) // Potions have a longer lock; do not permanently inflate every normal action.
                observedActionLock = Math.Max(observedActionLock, Math.Min(1.5f, (float)since + manager->AnimationLock + 0.02f));
        }
        var target = plugin.ResolveBattleTarget();
        if (manager is null || target is null)
        {
            CurrentWeavePlan = WeavePlan.Empty("无可攻击目标：停火并保留真实CD");
            RecommendedOgcdActionId = 0;
            return;
        }
        var gauge = Plugin.JobGauges.Get<BRDGauge>();
        var timing = LiveCombatReader.Gcd(GcdReferenceActionId, configuration.GcdSeconds);
        var now = ActionObserver.Now;
        var timeline = plugin.Executor.Timeline;
        var remaining = gauge.SongTimer / 1000f;
        var song = gauge.Song switch { GaugeSong.WanderersMinuet => BardSong.Wanderer, GaugeSong.MagesBallad => BardSong.Mage, GaugeSong.ArmysPaeon => BardSong.Army, _ => BardSong.None };
        if (remaining <= 0) song = BardSong.None;
        var songAnalysis = AnalyzeSong(gauge);
        var next = ChooseReadySong(gauge.Song == GaugeSong.None ? lastKnownSong : gauge.Song);
        var songSwitchIn = songAnalysis?.UntilSwitch ?? 0;
        var phase = plugin.Battlefield.Encounter;
        if (song == BardSong.None && IsActionLearned(ActionCatalog.WanderersMinuet) &&
            ReadCooldownRemaining(ActionCatalog.WanderersMinuet) <= 0.001f && ReadCooldownRemaining(ActionCatalog.RagingStrikes) <= 8)
            next = (ActionCatalog.WanderersMinuet, "旅神");
        if (configuration.Scenario == RotationScenario.Uwu && phase.Phase != UwuPhase.Unknown)
        {
            if (song == BardSong.None && phase.HoldBurst && phase.BurstIn >= 10)
            {
                // Keep WM for the delayed burst when a filler song is actually available.
                if (ReadCooldownRemaining(ActionCatalog.MagesBallad) <= 0) next = (ActionCatalog.MagesBallad, "贤者");
                else if (ReadCooldownRemaining(ActionCatalog.ArmysPaeon) <= 0) next = (ActionCatalog.ArmysPaeon, "军神");
            }
            else if (!phase.HoldBurst && song != BardSong.Wanderer && ReadCooldownRemaining(ActionCatalog.WanderersMinuet) <= 0 && ReadCooldownRemaining(ActionCatalog.RagingStrikes) <= 4)
            { next = (ActionCatalog.WanderersMinuet, "旅神"); songSwitchIn = 0; }
        }
        var pending = float.PositiveInfinity;
        if (song == BardSong.Wanderer && repertoireAtEmpyreal < 3 && now - pendingEmpyrealAt < 1.2 && gauge.Repertoire == repertoireAtEmpyreal && gauge.SoulVoice == soulAtEmpyreal)
            pending = Math.Max(0.05f, 0.70f - (float)(now - pendingEmpyrealAt));
        else pendingEmpyrealAt = double.NegativeInfinity;

        // Availability exclusions are cooldown-independent for look-ahead.
        // Actual sending still validates cooldown+status with both checks enabled.
        var excluded = new HashSet<uint>(plugin.Executor.RejectedActions);
        foreach (var action in ActionCatalog.All.Where(a => a.Kind == ShadowActionKind.Ogcd))
        {
            if (!IsActionLearned(action.Id)) { excluded.Add(action.Id); continue; }
            if (action.Id == ActionCatalog.PitchPerfect || action.Id == ActionCatalog.RadiantFinale) continue;
            if (manager->GetActionStatus(ActionType.Action, manager->GetAdjustedActionId(action.Id),
                LiveCombatReader.TargetFor(action.Id, target.GameObjectId), false, true, null) != 0)
                excluded.Add(action.Id);
        }
        lastCoda = gauge.Coda.Count(c => c != GaugeSong.None);
        var plannerState = new PlannerState
        {
            Level = plugin.EffectiveLevel, Gcd = timing.Total, NextGcd = Math.Max(0, timing.Remaining),
            Earliest = timeline.EarliestWeave(now, manager->AnimationLock),
            Slots = timeline.ActiveCycle ? Math.Max(0, OgcdLimitThisCycle - timeline.Ogcds) : 0,
            FutureSlots = OgcdLimitThisCycle, Lock = EffectiveActionLock, Margin = configuration.WeaveSafetyMargin,
            Song = song, SongRemaining = remaining, SongSwitchIn = songSwitchIn,
            NextSongAction = next?.ActionId ?? 0, NextSongReady = next.HasValue ? ReadCooldownRemaining(next.Value.ActionId) : float.PositiveInfinity,
            NextNaturalTick = CombatTiming.NextSongTick(remaining), Repertoire = gauge.Repertoire, Soul = gauge.SoulVoice,
            PendingRepertoireIn = pending, Charges = LiveCombatReader.Charges(plugin.EffectiveLevel >= 92 ? ActionCatalog.HeartbreakShot : ActionCatalog.Bloodletter, plugin.EffectiveLevel),
            EmpyrealReady = ReadCooldownRemaining(ActionCatalog.EmpyrealArrow), PitchReady = ReadCooldownRemaining(ActionCatalog.PitchPerfect),
            SidewinderReady = ReadCooldownRemaining(ActionCatalog.Sidewinder), RagingReady = ReadCooldownRemaining(ActionCatalog.RagingStrikes),
            VoiceReady = ReadCooldownRemaining(ActionCatalog.BattleVoice), FinaleReady = ReadCooldownRemaining(ActionCatalog.RadiantFinale), BarrageReady = ReadCooldownRemaining(ActionCatalog.Barrage),
            RagingLeft = PlayerStatusRemaining(ActionCatalog.Buffs.RagingStrikes), VoiceLeft = PlayerStatusRemaining(ActionCatalog.Buffs.BattleVoice),
            NextSongGrantsNewCoda = next.HasValue && !gauge.Coda.Contains(next.Value.ActionId switch { ActionCatalog.WanderersMinuet => GaugeSong.WanderersMinuet, ActionCatalog.MagesBallad => GaugeSong.MagesBallad, _ => GaugeSong.ArmysPaeon }),
            FinaleLeft = PlayerStatusRemaining(ActionCatalog.Buffs.RadiantFinale), FinaleMultiplier = HasPlayerStatus(ActionCatalog.Buffs.RadiantFinale) ? finaleMultiplier : 1 + 0.02f * Math.Max(1, lastCoda), Coda = lastCoda,
            PotionLeft = plugin.Consumables.PotionRemaining, PotionMultiplier = plugin.Consumables.PotionMultiplier,
            BarrageActive = HasPlayerStatus(ActionCatalog.Buffs.Barrage), ResonantActive = HasPlayerStatus(ActionCatalog.Buffs.ResonantArrowReady), HawksEye = HasPlayerStatus(ActionCatalog.Buffs.HawksEye),
            ModernBurst = ScenarioRules.Resolve(configuration.Scenario).UsesModernBurstOrder,
            Opener = timeline.GcdCount < 4, BurstEarliest = timeline.GcdCount < 2 ? timing.Remaining + EffectiveActionLock : 0,
            HoldBurstUntil = plugin.Battlefield.Encounter.HoldBurst ? plugin.Battlefield.Encounter.BurstIn : 0,
            Aoe = plugin.Battlefield.Coverage,
            BaselineDirectHit = Math.Clamp(configuration.AssumedDirectHitRate, 0, 1),
            BackgroundPotencyPerSecond = Math.Max(plugin.EffectiveLevel >= 76 ? 241 : 172,
                (plugin.EffectiveLevel >= 82 ? 140 : plugin.EffectiveLevel >= 18 ? 110 : 0) * plugin.Battlefield.Coverage.Cone12) / timing.Total + 15,
            TargetAvailable = true, TargetLifetime = TargetLifetime(), KnownTargetEnd = KnownTargetWindow, Excluded = excluded,
        };
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (plannerState.Slots == 0 || manager->ActionQueued)
            CurrentWeavePlan = WeavePlan.Empty("当前没有空余插入位置，等待下一个GCD");
        else if (configuration.GuidePerfectAxis || UsesLevel50Profile)
            CurrentWeavePlan = WeavePlanner.Plan(plannerState);
        else
        {
            var step = FindRecommendation(ShadowActionKind.Ogcd, AnalyzeDots(), gauge, AnalyzeMajorCooldowns());
            if (song == BardSong.None || plannerState.SongSwitchIn <= timing.Total + 0.5f)
                CurrentWeavePlan = WeavePlanner.Plan(plannerState with { Excluded = excluded.Union(ActionCatalog.All.Where(a => a.Kind == ShadowActionKind.Ogcd && a.Id != ActionCatalog.PitchPerfect).Select(a => a.Id)).ToHashSet() });
            else CurrentWeavePlan = step is not null && !excluded.Contains(step.ActionId) && plannerState.Slots > 0 &&
                CombatTiming.Fits(plannerState.Earliest, plannerState.NextGcd, plannerState.Lock, plannerState.Margin)
                ? new([new(step.ActionId, plannerState.Earliest, step.ActionId == ActionCatalog.PitchPerfect ? gauge.Repertoire : 0)], 0, "自定义条件列表；保留真实CD与双插保护")
                : WeavePlan.Empty("自定义条件未满足或无安全插入位置");
        }
        PlannerMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        RecommendedOgcdActionId = CurrentWeavePlan.First?.ActionId ?? 0;
        RecommendedOgcdName = RecommendedOgcdActionId == 0 ? string.Empty : WeavePlanner.Name(RecommendedOgcdActionId);
        RecommendedOgcdRequiresLateWeave = false;
        LastPlannerInputs = $"GCD={timing.Remaining:F3}/{timing.Total:F3} PP={gauge.Repertoire} EA={plannerState.EmpyrealReady:F3} H={plannerState.Charges.Available}/{plannerState.Charges.Maximum} nextH={plannerState.Charges.UntilNext:F2} tick={plannerState.NextNaturalTick:F2} song={song} RS={plannerState.RagingLeft:F2} compute={PlannerMilliseconds:F2}ms";
    }
    private double nowSinceItem() => ActionObserver.Now - plugin.Consumables.LastItemExecutedAt;
}
