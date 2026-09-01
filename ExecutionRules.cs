using System;

namespace BardPerfectLoop;

public static class ExecutionRules
{
    public const int MaxOgcdPerGcd = 2;
    public const double MinimumActionIntervalSeconds = 0.70;
    public const float MinimumRemainingForWeave = 0.72f;

    public static bool CanAttemptOgcd(
        int ogcdsThisCycle,
        float gcdRemaining,
        double secondsSinceLastAcceptedAction,
        float animationLock,
        bool actionQueued,
        int maxOgcds = MaxOgcdPerGcd) =>
        ogcdsThisCycle < Math.Clamp(maxOgcds, 1, MaxOgcdPerGcd) &&
        gcdRemaining > MinimumRemainingForWeave &&
        secondsSinceLastAcceptedAction >= MinimumActionIntervalSeconds &&
        animationLock <= 0.01f &&
        !actionQueued;

    public static bool DidGcdRecastRestart(
        float previousRemaining,
        float currentRemaining,
        float configuredGcd,
        float queueWindow) =>
        previousRemaining >= 0f &&
        previousRemaining <= Math.Max(0.10f, queueWindow + 0.05f) &&
        currentRemaining >= Math.Max(1.0f, configuredGcd * 0.65f);
}
