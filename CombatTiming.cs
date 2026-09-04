using System;

namespace BardPerfectLoop;

public readonly record struct ChargeState(int Available, int Maximum, float UntilNext, float UntilFull)
{
    public float Bank => Math.Clamp(Available * 15f + (Available < Maximum ? 15f - UntilNext : 0f), 0f, Maximum * 15f);

    // Native charge count is authoritative. A multi-charge recast is NOT the
    // time until the next usable charge, and can remain active with 1/2 charges.
    public static ChargeState FromNative(int available, int maximum, bool active, float total, float elapsed)
    {
        maximum = Math.Clamp(maximum, 1, 3);
        available = Math.Clamp(available, 0, maximum);
        if (!active || available == maximum)
            return new(available, maximum, available == maximum ? 0 : 15, available == maximum ? 0 : (maximum - available) * 15);
        var next = 15f - Math.Max(0, elapsed) % 15f;
        return new(available, maximum, next, next + (maximum - available - 1) * 15f);
    }
}

/// <summary>Client execution observations, not UseAction's queued/accepted bool.</summary>
public sealed class ActionTimeline
{
    private bool initialized;
    private ushort sequence;
    private float remaining;
    public int Ogcds { get; private set; } = 2;
    public long GcdCount { get; private set; }
    public bool ActiveCycle { get; private set; }
    public double LastExecutionAt { get; private set; } = double.NegativeInfinity;
    public string Fault { get; private set; } = string.Empty;
    public int Revision { get; private set; }

    public void Reset()
    {
        initialized = ActiveCycle = false;
        Ogcds = 2;
        GcdCount = 0;
        LastExecutionAt = double.NegativeInfinity;
        Fault = string.Empty;
        Revision++;
    }

    public int Observe(ushort currentSequence, float gcdRemaining, float gcdTotal, double now)
    {
        if (!initialized)
        {
            initialized = true;
            sequence = currentSequence;
            remaining = gcdRemaining;
            return 0; // Joining mid-GCD cannot prove unused weave slots.
        }

        var delta = (ushort)(currentSequence - sequence);
        var restarted = gcdRemaining > remaining + 0.30f && gcdRemaining > Math.Max(0.7f, gcdTotal * 0.5f);
        if (delta > 32)
        {
            Fault = "动作序列跳变，停止自动执行";
            delta = 0;
        }
        if (restarted)
        {
            ActiveCycle = true;
            GcdCount++;
            // If the sequence is unavailable, reserve this whole cycle.
            Ogcds = delta > 0 ? Math.Max(0, delta - 1) : 2;
        }
        else if (delta > 0)
            Ogcds += delta; // Manual abilities/items count too; never add a third.

        if (delta > 0 || restarted)
        {
            LastExecutionAt = now;
            Revision++;
        }
        sequence = currentSequence;
        remaining = gcdRemaining;
        return delta;
    }

    public float EarliestWeave(double now, float animationLock) =>
        Math.Max(Math.Max(0, animationLock), (float)Math.Max(0, 0.70 - (now - LastExecutionAt)));

    public void RecordExecuted(bool gcd, double now)
    {
        if (gcd) { ActiveCycle = true; GcdCount++; Ogcds = 0; }
        else Ogcds++;
        LastExecutionAt = now;
        Revision++;
    }
}

public static class CombatTiming
{
    public static float NextSongTick(float remaining)
    {
        if (remaining <= 3f || remaining > 45f)
            return float.PositiveInfinity;
        var phase = remaining % 3f;
        // At an exact boundary, the status update may still be in flight.
        return phase < 0.025f ? 0.025f : phase;
    }

    public static bool Fits(float start, float nextGcd, float lockSeconds, float margin) =>
        start >= 0 && start + Math.Max(0.70f, lockSeconds) + Math.Max(0.02f, margin) <= nextGcd;
}
