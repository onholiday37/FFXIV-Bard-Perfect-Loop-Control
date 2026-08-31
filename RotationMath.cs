using System;

namespace BardPerfectLoop;

public static class RotationMath
{
    public static int EstimateDotTicks(float remainingSeconds, float tickSeconds)
    {
        if (remainingSeconds <= 0)
            return 0;

        var safeTickSeconds = Math.Clamp(tickSeconds, 1f, 10f);
        return Math.Max(0, (int)Math.Ceiling(remainingSeconds / safeTickSeconds));
    }

    public static int RemainingDotPotency(int ticksRemaining, int potencyPerTick) =>
        Math.Max(0, ticksRemaining) * Math.Max(0, potencyPerTick);

    public static float DynamicDotRefreshWindow(float gcdSeconds) =>
        Math.Clamp(gcdSeconds + 0.25f, 1.5f, 6f);
}
