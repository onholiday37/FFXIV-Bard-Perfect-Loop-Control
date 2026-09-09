using System;

namespace BardPerfectLoop;

public enum PendingActionResult { None, Waiting, Cancelled, Uncertain }

/// <summary>Read-only reconciliation. Never clears the game's queue or invents an execution.</summary>
public sealed class PendingActionWatch
{
    public uint Action { get; private set; }
    public bool Active => Action != 0;
    private double acceptedAt, emptySince = double.NaN;
    private ushort sequenceBefore;
    private float gcdBefore;
    private bool queuedGcd, executionEvidence;

    public void Begin(uint action, double now, bool observedInQueue, ushort sequence, float remainingGcd)
    {
        Action = action; acceptedAt = now; queuedGcd = observedInQueue;
        sequenceBefore = sequence; gcdBefore = remainingGcd;
        executionEvidence = false; emptySince = double.NaN;
    }
    public void Clear() { Action = 0; emptySince = double.NaN; executionEvidence = false; }

    public PendingActionResult Poll(double now, bool anyActionQueued, ushort sequence, float remainingGcd,
        float animationLock, bool casting, bool observerFailed)
    {
        if (!Active) return PendingActionResult.None;
        var elapsed = now - acceptedAt;
        if (observerFailed || !double.IsFinite(elapsed) || elapsed < 0 ||
            !float.IsFinite(remainingGcd) || !float.IsFinite(gcdBefore) || !float.IsFinite(animationLock))
            return PendingActionResult.Uncertain;
        // Sticky evidence: an execution remains possible even when its cooldown later expires.
        var expected = Math.Max(0, gcdBefore - elapsed);
        executionEvidence |= sequence != sequenceBefore || remainingGcd > expected + 0.10;
        if (!queuedGcd || executionEvidence)
            return elapsed > 2 ? PendingActionResult.Uncertain : PendingActionResult.Waiting;
        if (anyActionQueued || casting || animationLock > .001f)
        { emptySince = double.NaN; return PendingActionResult.Waiting; }
        if (double.IsNaN(emptySince)) emptySince = now;
        // Allow a cross-frame observer/queue update to arrive before declaring cancellation.
        if (now - emptySince < .15 || elapsed < Math.Max(.2, gcdBefore + .15))
            return PendingActionResult.Waiting;
        return PendingActionResult.Cancelled;
    }
}
