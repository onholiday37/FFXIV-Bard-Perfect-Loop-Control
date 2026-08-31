using System;

namespace BardPerfectLoop;

public readonly record struct GuideSongPlan(
    string Name,
    float WandererCutRemaining,
    float MageCutRemaining,
    float ArmyCutRemaining)
{
    public float SingingSecondsFor(uint songActionId) => songActionId switch
    {
        ActionCatalog.WanderersMinuet => 45f - WandererCutRemaining,
        ActionCatalog.MagesBallad => 45f - MageCutRemaining,
        ActionCatalog.ArmysPaeon => 45f - ArmyCutRemaining,
        _ => 0f,
    };
}

/// <summary>
/// Pure rules extracted from the NGA 7.0 Bard guide. Timeline positions are targets;
/// the live engine still uses the game's real cooldowns, statuses and song gauge.
/// </summary>
public static class GuideAxisRules
{
    public const float SongDurationSeconds = 45f;
    public const float GuideGcdSplit = 2.485f;

    public static GuideSongPlan ResolveSongPlan(
        SongPlanMode mode,
        float gcdSeconds,
        float customWanderer,
        float customMage,
        float customArmy)
    {
        if (mode == SongPlanMode.GuideAuto)
            mode = gcdSeconds >= GuideGcdSplit ? SongPlanMode.Advanced369 : SongPlanMode.Standard3312;

        return mode switch
        {
            // The guide names the plans 3-3-12 / 3-6-9, but its actionable
            // in-game cut points are one second lower: 2/2/11 and 2/5/8.
            SongPlanMode.Standard3312 => new("攻略 3-3-12（43旅 / 43贤 / 34军）", 2f, 2f, 11f),
            SongPlanMode.Advanced369 => new("攻略 3-6-9（43旅 / 40贤 / 37军）", 2f, 5f, 8f),
            _ => new(
                "Custom 自定义",
                Math.Clamp(customWanderer, 0f, 20f),
                Math.Clamp(customMage, 0f, 20f),
                Math.Clamp(customArmy, 0f, 20f)),
        };
    }

    public static bool ShouldSnapshotDots(
        float causticRemaining,
        float stormRemaining,
        float ragingRemaining) =>
        causticRemaining > 0f &&
        stormRemaining > 0f &&
        causticRemaining < 35f &&
        stormRemaining < 35f &&
        ragingRemaining is > 0f and <= 5.5f;

    public static bool ShouldUseApex(byte soulVoice, bool inBurstWindow, float ragingCooldownRemaining) =>
        soulVoice >= 80 &&
        (inBurstWindow || ragingCooldownRemaining is >= 50f and <= 62f);

    public static bool ShouldHoldBloodletter(int charges, bool inBurstWindow, float ragingCooldownRemaining) =>
        charges < 3 && !inBurstWindow && ragingCooldownRemaining <= 30f;

    public static bool IsLateWeave(uint actionId) => actionId is
        ActionCatalog.BattleVoice or
        ActionCatalog.RadiantFinale or
        ActionCatalog.RagingStrikes;

    public static float LateWeaveGate(float gcdSeconds) =>
        Math.Clamp(gcdSeconds, 1.5f, 3.5f) * 0.52f;
}
