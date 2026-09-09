using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BardPerfectLoop;

public sealed class ActionExecutor
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly Queue<string> trace = new();
    private readonly Dictionary<uint, double> rejectedUntil = [];
    private readonly ExecutionSession session = new();
    private PendingActionWatch pending => session.Pending;
    private bool inDowntime;
    private double downtimeAt;
    public ActionTimeline Timeline => session.Timeline;
    public int OgcdsThisCycle => Timeline.Ogcds;
    public string Status { get; private set; } = "未启动";
    public bool InDowntime => inDowntime;
    public double LastDowntimeSeconds { get; private set; }
    public string DiagnosticText => string.Join(Environment.NewLine, trace);
    public IReadOnlySet<uint> RejectedActions => rejectedUntil.Where(p => p.Value > ActionObserver.Now).Select(p => p.Key).ToHashSet();

    public ActionExecutor(Plugin plugin, Configuration configuration)
    {
        this.plugin = plugin; this.configuration = configuration;
    }
    public void Reset(string status = "执行器已重置")
    {
        session.Reset(ActionObserver.Now); rejectedUntil.Clear(); inDowntime = false; Status = status;
        Trace(status);
    }
    public void Replan(string status)
    {
        session.Replan();
        // Keep pending execution, rejection backoff and manual weave use intact.
        Status = status;
        Trace($"REPLAN revision={session.PlanRevision} gcd={Timeline.GcdCount} slots={Timeline.Ogcds} pending={pending.Action}; {status}");
    }
    private void Trace(string message)
    {
        trace.Enqueue($"{ActionObserver.Now:F3} {message}");
        while (trace.Count > 300) trace.Dequeue();
    }

    internal void SuspendForDeath(string status = "角色倒地，等待复活")
    {
        session.SuspendForDeath(ActionObserver.Now); rejectedUntil.Clear();
        Status = status;
        Trace(Status);
    }

    internal void Observe()
    {
        while (plugin.Observer.TryRead(out var action))
        {
            if (!plugin.Engine.Armed || action.Time < session.StartedAt) continue;
            var gcd = action.Type == ActionType.Action && ActionCatalog.IsGcd(action.Id);
            Timeline.RecordExecuted(gcd, action.Time);
            // Manual execution invalidates a queued prediction too.
            pending.Clear();
            if (action.Type == ActionType.Action)
                plugin.Engine.OnClientExecuted(action.Id, action.Target, action.Time, action.RepertoireBefore, action.SoulBefore, action.CodaBefore);
            if (action.Type == ActionType.Item)
            {
                Timeline.ReserveRemainingWeaves();
                plugin.Consumables.ObserveItem(action.Id, action.Time);
            }
            Trace($"CLIENT_EXECUTED id={action.Id} gcd={gcd} slots={Timeline.Ogcds} | {plugin.Engine.LastPlannerInputs}");
        }
        if (plugin.Observer.Failed && plugin.Engine.Armed)
            plugin.StopControl("动作观察器异常，已停止，未继续发送技能");
    }

    public unsafe void Update()
    {
        if (!plugin.Engine.Armed) return;
        if (Plugin.ObjectTable.LocalPlayer is not { IsDead: false, CurrentHp: > 0 })
        { Status = "角色倒地或暂不可用，等待复活"; return; }
        if (configuration.ShadowOnly) { Status = "仅影子观察：不发送任何技能；可手动操作检查建议"; return; }
        if (plugin.Engine.WaitingForRevival)
        { Status = plugin.Engine.Snapshot.Status; return; }
        if (plugin.Consumables.BlocksActions) { Status = "药食请求等待执行或药效确认"; return; }
        if (!plugin.IsInCombat && !plugin.Engine.Snapshot.Running) { plugin.Consumables.TryFood(); return; }
        if (!plugin.Engine.Snapshot.Running) return;
        var manager = ActionManager.Instance();
        if (manager is null) { Status = "动作管理器不可用"; return; }
        var now = ActionObserver.Now;
        if (pending.Active)
        {
            var result = pending.Poll(now, manager->ActionQueued, manager->LastUsedActionSequence,
                LiveCombatReader.Gcd(plugin.Engine.GcdReferenceActionId, configuration.GcdSeconds).Remaining,
                manager->AnimationLock, Plugin.ObjectTable.LocalPlayer?.IsCasting == true, plugin.Observer.Failed);
            if (result == PendingActionResult.Cancelled)
            {
                Trace($"QUEUE_CANCELLED id={pending.Action}; empty queue, unchanged sequence, no GCD restart; replan next frame");
                pending.Clear();
                Timeline.ReserveRemainingWeaves(); // A cancelled GCD grants no new weave budget.
                Status = "排队技能已取消且未见执行迹象；保持启动，等待可攻击目标后重新规划";
            }
            else if (result == PendingActionResult.Uncertain)
                plugin.StopControl("已接受的动作未观察到执行，停止以防重复发送");
            else Status = "等待排队技能执行或确认取消；暂不发送其他技能";
            return;
        }
        var target = plugin.ResolveBattleTarget();
        if (target is null)
        {
            if (!inDowntime) { inDowntime = true; downtimeAt = now; Trace("TARGET_UNAVAILABLE"); }
            Status = "无可攻击目标：停火，检查索敌/射程，等待原目标或手动选择新目标";
            return;
        }
        if (inDowntime)
        {
            LastDowntimeSeconds = now - downtimeAt; inDowntime = false;
            Trace($"TARGET_RETURN downtime={LastDowntimeSeconds:F2}; real state replanned");
        }
        if (manager->ActionQueued) { Status = "已有游戏动作排队，保留玩家队列"; return; }

        // Opening potion precedes our first GCD/buffs; later potions keep normal weave rules.
        if (plugin.Consumables.TryOpeningPotion())
        { Status = plugin.Consumables.PotionStatus; return; }
        if (ActionObserver.Now - plugin.Consumables.LastItemExecutedAt < configuration.PotionLockSeconds)
        { Status = "药食动作锁未结束，等待后继续输出"; return; }

        var timing = LiveCombatReader.Gcd(plugin.Engine.GcdReferenceActionId, configuration.GcdSeconds);
        if (timing.Remaining <= Math.Clamp(configuration.GcdQueueWindowSeconds, 0.05f, 0.50f))
        {
            if (Timeline.EarliestWeave(now, 0) > timing.Remaining + 0.001f) return;
            if (plugin.Engine.RecommendedGcdActionId != 0)
                TryUse(manager, plugin.Engine.RecommendedGcdActionId, target, true);
            return;
        }
        if (!Timeline.ActiveCycle || Timeline.Ogcds >= plugin.Engine.OgcdLimitThisCycle)
        {
            Status = "等待下一个GCD；不补第三插或加速期的第二插";
            return;
        }
        if (Timeline.EarliestWeave(now, manager->AnimationLock) > 0f ||
            !CombatTiming.Fits(0, timing.Remaining, plugin.Engine.EffectiveActionLock, configuration.WeaveSafetyMargin))
            return;

        // A potion consumes the whole remaining weave budget. It is never a hidden third action.
        if (Timeline.Ogcds == 0 && CombatTiming.Fits(0, timing.Remaining, configuration.PotionLockSeconds, configuration.WeaveSafetyMargin) &&
            plugin.Consumables.TryPotion())
        { Status = plugin.Consumables.PotionStatus; return; }
        if (ActionObserver.Now - plugin.Consumables.LastItemExecutedAt < configuration.PotionLockSeconds) return;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var choice = plugin.Engine.CurrentWeavePlan.First;
            if (choice is null || choice.Value.At > 0f)
            {
                Status = plugin.Engine.CurrentWeavePlan.Reason;
                return;
            }
            if (choice.Value.ActionId == ActionCatalog.PitchPerfect && plugin.Engine.ActualRepertoire < choice.Value.ExpectedRepertoire)
            {
                Status = "等待实际诗心到账，不按预测层数提前释放音调";
                return;
            }
            if (TryUse(manager, choice.Value.ActionId, target, false)) return;
            if (manager->ActionQueued || manager->AnimationLock > 0.01f || pending.Active) return;
            plugin.Engine.ReplanOgcd();
        }
    }

    private unsafe bool TryUse(ActionManager* manager, uint action, IBattleChara target, bool gcd)
    {
        if (plugin.Engine.WaitingForRevival) return false;
        var now = ActionObserver.Now;
        if (rejectedUntil.TryGetValue(action, out var blocked) && blocked > now) return false;
        var adjusted = manager->GetAdjustedActionId(action);
        plugin.Battlefield.Update(true);
        // Re-read the gauge and phase: stale/custom recommendations cannot bypass the axis.
        if (SongContinuity.IsSong(action))
        {
            if (!plugin.Engine.CanSendSong(action, out var songReason))
            { Status = $"等待切歌点或重新选歌：{songReason}"; return false; }
            Trace($"SONG_DUE {songReason}");
        }
        if (!plugin.Battlefield.Allows(action)) return Reject(action, "群攻范围含保护目标或未进入命中范围");
        if (plugin.Battlefield.Encounter.HoldBurst && action is ActionCatalog.RagingStrikes or ActionCatalog.BattleVoice or ActionCatalog.RadiantFinale or ActionCatalog.Barrage)
            return Reject(action, "阶段爆发暂缓，等待实际复现窗口");
        if (plugin.EffectiveLevel < ActionCatalog.MinimumLevel(action)) return Reject(action, "not learned");
        // Only intentional GCD queueing skips the active-recast status check.
        if (!gcd && LiveCombatReader.Cooldown(action, plugin.EffectiveLevel) > 0f)
            return false; // A future-ready plan is a wait, NOT a rejected action.
        var targetId = LiveCombatReader.TargetFor(action, target.GameObjectId);
        var status = manager->GetActionStatus(ActionType.Action, adjusted, targetId, !gcd, true, null);
        if (status != 0) return Reject(action, $"status={status}");
        if (gcd && manager->AnimationLock > 0.5f) return false;
        var sequenceBefore = manager->LastUsedActionSequence;
        var gcdBefore = LiveCombatReader.Gcd(plugin.Engine.GcdReferenceActionId, configuration.GcdSeconds).Remaining;
        var requestedAt = ActionObserver.Now;
        var accepted = manager->UseAction(ActionType.Action, adjusted, targetId, 0, ActionManager.UseActionMode.None, 0, null);
        if (!accepted) return Reject(action, "native=false");
        var queuedGcd = gcd && manager->ActionQueued && manager->QueuedActionType == ActionType.Action && manager->QueuedActionId == adjusted;
        pending.Begin(action, requestedAt, queuedGcd, sequenceBefore, gcdBefore);
        Status = $"已接受/排队：{WeavePlanner.Name(action)}；等待客户端执行记录";
        Trace($"ACCEPTED id={action} queued={manager->ActionQueued} | {plugin.Engine.CurrentWeavePlan.Reason}");
        Observe();
        return true;
    }
    private bool Reject(uint action, string reason)
    {
        rejectedUntil[action] = ActionObserver.Now + 0.20;
        Trace($"REJECTED id={action} {reason}");
        Status = $"{WeavePlanner.Name(action)}暂不可用，重算其他安全候选";
        return false;
    }
}
