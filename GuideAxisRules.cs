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
/// Pure song-axis and priority rules. Timeline positions are targets; the live
/// engine still uses the game's real cooldowns, statuses and song gauge.
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
            // Existing presets target 43/43/34 and 43/40/37 seconds of singing.
            // These are chosen points within guide windows, not a universal
            // one-second conversion between the gauge and displayed timer.
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

    public static bool ShouldUseCurrentApex(
        byte soulVoice,
        bool inBurstWindow,
        bool inMagesBallad,
        float songRemaining) =>
        soulVoice >= 80 &&
        (inBurstWindow ||
         inMagesBallad && (soulVoice >= 100 || songRemaining <= 21f));

    public static uint SelectImmediateOgcd(
        bool pitchPerfectMustSpend,
        bool empyrealArrowReady,
        bool chargeShotReady,
        uint chargeShotActionId)
    {
        if (pitchPerfectMustSpend)
            return ActionCatalog.PitchPerfect;
        if (empyrealArrowReady)
            return ActionCatalog.EmpyrealArrow;
        if (chargeShotReady)
            return chargeShotActionId;
        return 0;
    }

    public static bool IsLateWeave(uint actionId) => actionId is
        ActionCatalog.BattleVoice or
        ActionCatalog.RadiantFinale or
        ActionCatalog.RagingStrikes;

    public static bool IsLateWeave(uint actionId, bool modernBurstOrder) => modernBurstOrder
        ? actionId == ActionCatalog.RagingStrikes
        : IsLateWeave(actionId);

    public static float LateWeaveGate(float gcdSeconds) =>
        Math.Clamp(gcdSeconds, 1.5f, 3.5f) * 0.52f;
}
