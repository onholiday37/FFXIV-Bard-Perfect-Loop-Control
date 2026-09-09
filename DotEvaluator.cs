using System;
using System.Collections.Generic;

namespace BardPerfectLoop;

public readonly record struct DotDecision(bool Refresh, bool Emergency, bool Snapshot, double Gain, string Reason);

public static class DotEvaluator
{
    public const float Duration = 45;
    public const float Tick = 3;
    // Unknown server tick phase has a fractional expectation, not Ceil(r / 3).
    public static double ExpectedTicks(float remaining, float lifetime = float.PositiveInfinity) =>
        Math.Max(0, Math.Min(remaining, lifetime)) / Tick;

    public static double ApplicationValue(float direct, float tickPotency, float lifetime, float multiplier) =>
        (direct + ExpectedTicks(Duration, lifetime) * tickPotency) * multiplier;

    public static DotDecision Refresh(float caustic, float storm, float gcd, float untilExecution,
        float lifetime, float oldCausticMultiplier, float oldStormMultiplier, float currentMultiplier,
        float buffRemaining, float fillerPotency, int causticPotency = 20, int stormPotency = 25,
        bool snapshotsKnown = false, float applicationDelay = 0.70f)
    {
        var c = Math.Max(0, caustic - untilExecution);
        var s = Math.Max(0, storm - untilExecution);
        var life = Math.Max(0, lifetime - untilExecution);
        if (c <= 0 || s <= 0)
            return new(false, false, false, 0, "DoT缺失，需要分别补上");

        // Marginal extension + snapshot replacement, NOT all old ticks subtracted
        // as a loss: the refreshed DoT keeps ticking on the same server phase.
        var gain = (ExpectedTicks(Duration, life) - ExpectedTicks(c, life)) * causticPotency * currentMultiplier +
                   (ExpectedTicks(Duration, life) - ExpectedTicks(s, life)) * stormPotency * currentMultiplier +
                   ExpectedTicks(c, life) * causticPotency * (currentMultiplier - oldCausticMultiplier) +
                   ExpectedTicks(s, life) * stormPotency * (currentMultiplier - oldStormMultiplier) -
                   (fillerPotency - 100) * currentMultiplier;
        var emergency = Math.Min(c, s) <= gcd + Math.Max(0, applicationDelay) + 0.20f;
        if (emergency && gain > 0)
            return new(true, true, false, gain, "再等一个GCD可能断DoT，续时净收益为正");

        // Extra buff snapshot costs an additional Iron Jaws relative to filler.
        // Only use it if a known stronger buff is about to expire and replacing
        // the OLD snapshot earns back that extra GCD opportunity cost.
        var snapshotGain = ExpectedTicks(c, life) * causticPotency * (currentMultiplier - oldCausticMultiplier) +
                           ExpectedTicks(s, life) * stormPotency * (currentMultiplier - oldStormMultiplier) -
                           (fillerPotency - 100) * currentMultiplier;
        var snapshot = snapshotsKnown && buffRemaining > untilExecution && buffRemaining <= untilExecution + gcd + 0.20f && snapshotGain > 0;
        return new(snapshot, false, snapshot, snapshot ? snapshotGain : gain,
            snapshot ? "已知增益即将结束，更新快照收益超过额外伶牙GCD成本" : "保留原DoT；等待安全末段，不无条件提前覆盖");
    }
}

/// <summary>Only own, client-confirmed applications establish a known snapshot.</summary>
public sealed class DotSnapshotLedger
{
    private readonly Dictionary<ulong, (float Caustic, float Storm, bool C, bool S)> entries = [];
    private readonly Dictionary<ulong, (uint Action, float Multiplier, double Time, float C, float S, double ObservedAt)> pending = [];
    private readonly Dictionary<ulong, (float C, float S, double At)> observed = [];
    public void Stage(ulong target, uint action, float multiplier, double time)
    {
        if (action is not (ActionCatalog.CausticBite or ActionCatalog.VenomousBite or ActionCatalog.Stormbite or ActionCatalog.Windbite or ActionCatalog.IronJaws)) return;
        if (pending.Count >= 16 && !pending.ContainsKey(target)) pending.Clear();
        observed.TryGetValue(target, out var previous);
        pending[target] = (action, multiplier, time, previous.C, previous.S, previous.At);
    }
    public void Observe(ulong target, float caustic, float storm, double now)
    {
        if (!double.IsFinite(now) || !float.IsFinite(caustic) || !float.IsFinite(storm))
        { entries.Remove(target); pending.Remove(target); observed.Remove(target); return; }
        if (observed.Count >= 16 && !observed.ContainsKey(target)) observed.Clear();
        var hadPrevious = observed.TryGetValue(target, out var previous);
        if (hadPrevious && now < previous.At)
        { entries.Remove(target); pending.Remove(target); }
        var expectedC = ExpectedRemaining(previous.C, previous.At, now);
        var expectedS = ExpectedRemaining(previous.S, previous.At, now);
        var confirmedC = false;
        var confirmedS = false;
        if (pending.TryGetValue(target, out var p))
        {
            var c = p.Action is ActionCatalog.CausticBite or ActionCatalog.VenomousBite or ActionCatalog.IronJaws;
            var s = p.Action is ActionCatalog.Stormbite or ActionCatalog.Windbite or ActionCatalog.IronJaws;
            if (now - p.Time is >= 0 and <= 2 &&
                (!c || caustic > 40 && caustic > ExpectedRemaining(p.C, p.ObservedAt, now) + 0.5f) &&
                (!s || storm > 40 && storm > ExpectedRemaining(p.S, p.ObservedAt, now) + 0.5f))
            { Record(target, p.Action, p.Multiplier); pending.Remove(target); confirmedC = c; confirmedS = s; }
            else if (now - p.Time > 2) pending.Remove(target);
        }
        // Expired or unattributed refreshed statuses cannot inherit an old buff.
        // A partially observed Iron Jaws remains unknown until both updates arrive.
        if (entries.TryGetValue(target, out var state))
        {
            // Compare against the timer NOW, not the last raw reading. During a
            // target switch 40 -> 25 after 30s is a refresh, not normal decay.
            var renewedC = !confirmedC && caustic > expectedC + 0.5f;
            var renewedS = !confirmedS && storm > expectedS + 0.5f;
            if (caustic <= 0 || renewedC) state = state with { C = false, Caustic = 1 };
            if (storm <= 0 || renewedS) state = state with { S = false, Storm = 1 };
            entries[target] = state;
        }
        observed[target] = (caustic, storm, now);
    }
    private static float ExpectedRemaining(float remaining, double observedAt, double now) =>
        (float)Math.Max(0, remaining - Math.Max(0, now - observedAt));
    public void Record(ulong target, uint action, float multiplier)
    {
        if (target == 0) return;
        if (entries.Count >= 16 && !entries.ContainsKey(target)) entries.Clear();
        entries.TryGetValue(target, out var state);
        if (action is ActionCatalog.CausticBite or ActionCatalog.VenomousBite or ActionCatalog.IronJaws)
            state = state with { Caustic = multiplier, C = true };
        if (action is ActionCatalog.Stormbite or ActionCatalog.Windbite or ActionCatalog.IronJaws)
            state = state with { Storm = multiplier, S = true };
        entries[target] = state;
    }
    public (float Caustic, float Storm, bool Known) Read(ulong target) => entries.TryGetValue(target, out var s)
        ? (s.C ? s.Caustic : 1, s.S ? s.Storm : 1, s.C && s.S) : (1, 1, false);
    public void Invalidate() { entries.Clear(); pending.Clear(); observed.Clear(); }
}
