using System;
using System.Diagnostics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BardPerfectLoop;

public sealed class ActionExecutor
{
    private const double PendingGcdTimeoutSeconds = 2.0;

    private readonly Plugin plugin;
    private readonly Configuration configuration;

    private long lastAcceptedActionAt;
    private long pendingGcdAt;
    private float previousGcdRemaining = -1f;
    private bool pendingGcd;
    private bool activeGcdCycle;
    private bool inDowntime;
    private long downtimeStartedAt;

    public int OgcdsThisCycle { get; private set; }
    public string Status { get; private set; } = "未启动";
    public bool InDowntime => inDowntime;
    public double LastDowntimeSeconds { get; private set; }

    public ActionExecutor(Plugin plugin, Configuration configuration)
    {
        this.plugin = plugin;
        this.configuration = configuration;
    }

    public void Reset(string status = "执行器已重置")
    {
        lastAcceptedActionAt = 0;
        pendingGcdAt = 0;
        previousGcdRemaining = -1f;
        pendingGcd = false;
        activeGcdCycle = false;
        inDowntime = false;
        downtimeStartedAt = 0;
        OgcdsThisCycle = 0;
        Status = status;
    }

    public unsafe void Update()
    {
        if (!plugin.Engine.Armed || !plugin.Engine.Snapshot.Running)
            return;

        var manager = ActionManager.Instance();
        if (manager is null)
        {
            Status = "动作管理器不可用，未发送动作";
            return;
        }

        var target = plugin.ResolveBattleTarget();
        if (target is null)
        {
            EnterDowntime();
            return;
        }

        if (inDowntime)
            LeaveDowntime();

        var now = Stopwatch.GetTimestamp();
        var gcdRemaining = ReadGcdRemaining(manager, plugin.Engine.GcdReferenceActionId);

        if (pendingGcd)
        {
            var recastRestarted = ExecutionRules.DidGcdRecastRestart(
                previousGcdRemaining,
                gcdRemaining,
                configuration.GcdSeconds,
                configuration.GcdQueueWindowSeconds);
            if (recastRestarted)
            {
                pendingGcd = false;
                activeGcdCycle = true;
                OgcdsThisCycle = 0;
                lastAcceptedActionAt = now;
                Status = "GCD 已结算，本周期可插 0/2";
            }
            else if (ElapsedSeconds(pendingGcdAt, now) > PendingGcdTimeoutSeconds)
            {
                pendingGcd = false;
                Status = "GCD 排队未结算，已放弃旧动作并重算";
            }
        }

        if (!pendingGcd &&
            plugin.Engine.RecommendedGcdActionId != 0 &&
            gcdRemaining <= Math.Clamp(configuration.GcdQueueWindowSeconds, 0.05f, 0.50f) &&
            IntervalSatisfied(now) &&
            TryUse(manager, plugin.Engine.RecommendedGcdActionId, target))
        {
            pendingGcd = true;
            pendingGcdAt = now;
            Status = $"GCD 已送入游戏队列：{plugin.Engine.RecommendedGcdName}";
            previousGcdRemaining = gcdRemaining;
            return;
        }

        if (activeGcdCycle &&
            !pendingGcd &&
            plugin.Engine.RecommendedOgcdActionId != 0 &&
            (!plugin.Engine.RecommendedOgcdRequiresLateWeave ||
             gcdRemaining <= GuideAxisRules.LateWeaveGate(configuration.GcdSeconds)) &&
            ExecutionRules.CanAttemptOgcd(
                OgcdsThisCycle,
                gcdRemaining,
                SecondsSinceLastAcceptedAction(now),
                manager->AnimationLock,
                manager->ActionQueued) &&
            TryUse(manager, plugin.Engine.RecommendedOgcdActionId, target))
        {
            OgcdsThisCycle++;
            lastAcceptedActionAt = now;
            Status = $"能力技已被游戏接受：{plugin.Engine.RecommendedOgcdName}（本周期 {OgcdsThisCycle}/2）";
        }
        else if (OgcdsThisCycle >= ExecutionRules.MaxOgcdPerGcd)
        {
            Status = "本 GCD 已完成双插，等待下一个 GCD";
        }
        else if (activeGcdCycle &&
                 plugin.Engine.RecommendedOgcdRequiresLateWeave &&
                 gcdRemaining > GuideAxisRules.LateWeaveGate(configuration.GcdSeconds))
        {
            Status = "攻略团辅等待后半 GCD，争取覆盖 9/9";
        }

        previousGcdRemaining = gcdRemaining;
    }

    private void EnterDowntime()
    {
        if (!inDowntime)
        {
            inDowntime = true;
            downtimeStartedAt = Stopwatch.GetTimestamp();
            pendingGcd = false;
            activeGcdCycle = false;
            OgcdsThisCycle = 0;
        }

        Status = "目标不可选：停火，保留歌曲与真实 CD，等待 Boss 复现";
    }

    private void LeaveDowntime()
    {
        var now = Stopwatch.GetTimestamp();
        LastDowntimeSeconds = ElapsedSeconds(downtimeStartedAt, now);
        inDowntime = false;
        pendingGcd = false;
        activeGcdCycle = false;
        OgcdsThisCycle = 0;
        previousGcdRemaining = -1f;
        lastAcceptedActionAt = 0;
        Status = $"目标恢复（停火 {LastDowntimeSeconds:F1}s），已按当前 CD、歌曲与 DoT 重新规划";
    }

    private bool IntervalSatisfied(long now) =>
        SecondsSinceLastAcceptedAction(now) >= ExecutionRules.MinimumActionIntervalSeconds;

    private double SecondsSinceLastAcceptedAction(long now) =>
        lastAcceptedActionAt == 0 ? double.PositiveInfinity : ElapsedSeconds(lastAcceptedActionAt, now);

    private static double ElapsedSeconds(long startedAt, long now) =>
        startedAt == 0 ? double.PositiveInfinity : (now - startedAt) / (double)Stopwatch.Frequency;

    private static unsafe float ReadGcdRemaining(ActionManager* manager, uint gcdReferenceActionId)
    {
        if (!manager->IsRecastTimerActive(ActionType.Action, gcdReferenceActionId))
            return 0f;

        var total = manager->GetRecastTime(ActionType.Action, gcdReferenceActionId);
        var elapsed = manager->GetRecastTimeElapsed(ActionType.Action, gcdReferenceActionId);
        return Math.Max(0f, total - elapsed);
    }

    private static unsafe bool TryUse(ActionManager* manager, uint actionId, IBattleChara target)
    {
        var adjustedActionId = manager->GetAdjustedActionId(actionId);
        var status = manager->GetActionStatus(ActionType.Action, adjustedActionId, target.GameObjectId, false, false, null);
        if (status != 0)
            return false;

        return manager->UseAction(
            ActionType.Action,
            adjustedActionId,
            target.GameObjectId,
            0,
            ActionManager.UseActionMode.None,
            0,
            null);
    }
}
