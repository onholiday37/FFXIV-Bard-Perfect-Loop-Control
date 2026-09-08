using System;

namespace BardPerfectLoop;

public static class DamageFactors
{
    // Xivgear's non-tank main-stat factor, at the supported expansion level caps.
    // This is one factor in damage, not a conversion from potency to final DPS.
    public static float MainStatRatio(int level, int before, int after)
    {
        if (before <= 0 || after <= before) return 1;
        var (baseStat, power) = level switch { 50 => (202, 75), 60 => (218, 114), 70 => (292, 125),
            80 => (340, 165), 90 => (390, 195), 100 => (440, 237), _ => (0, 0) };
        if (baseStat == 0) return 1; // Unknown scaling: do not invent a potion snapshot improvement.
        var oldFactor = 100 + Math.Truncate((double)power * (before - baseStat) / baseStat);
        var newFactor = 100 + Math.Truncate((double)power * (after - baseStat) / baseStat);
        return oldFactor > 0 ? (float)Math.Clamp(newFactor / oldFactor, 1, 1.3) : 1;
    }
}
