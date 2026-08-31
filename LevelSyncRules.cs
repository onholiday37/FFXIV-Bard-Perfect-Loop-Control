using System;
using System.Collections.Generic;

namespace BardPerfectLoop;

public static class LevelSyncRules
{
    public static RotationStep SelectGcd(
        int level,
        DotAnalysis? dots,
        float refreshWindow,
        bool straightShotReady)
    {
        if (level >= 30 && dots is not null && (dots.StormMissing || dots.StormRemaining <= refreshWindow))
            return MakeStep(
                ActionCatalog.Windbite,
                "风蚀箭",
                ShadowActionKind.Gcd,
                dots.StormMissing ? StepCondition.StormMissing : StepCondition.DotRefreshDue,
                130);

        if (level >= 6 && dots is not null && (dots.CausticMissing || dots.CausticRemaining <= refreshWindow))
            return MakeStep(
                ActionCatalog.VenomousBite,
                "毒咬箭",
                ShadowActionKind.Gcd,
                dots.CausticMissing ? StepCondition.CausticMissing : StepCondition.DotRefreshDue,
                125);

        if (level >= 2 && straightShotReady)
            return MakeStep(ActionCatalog.StraightShot, "直线射击", ShadowActionKind.Gcd, StepCondition.RefulgentReady, 110);

        return MakeStep(ActionCatalog.HeavyShot, "强力射击", ShadowActionKind.Gcd, StepCondition.Always, 10);
    }

    public static RotationStep? SelectOgcd(int level, IReadOnlySet<uint> readyActionIds)
    {
        var candidates = new[]
        {
            (ActionCatalog.RagingStrikes, "猛者强击", 4),
            (ActionCatalog.BattleVoice, "战斗之声", 50),
            (ActionCatalog.Barrage, "纷乱箭", 38),
            (ActionCatalog.Bloodletter, "失血箭", 12),
        };

        foreach (var candidate in candidates)
        {
            if (level >= candidate.Item3 && readyActionIds.Contains(candidate.Item1))
                return MakeStep(candidate.Item1, candidate.Item2, ShadowActionKind.Ogcd, StepCondition.CooldownReady, 100);
        }

        return null;
    }

    public static RotationStep MakeStep(
        uint actionId,
        string name,
        ShadowActionKind kind,
        StepCondition condition,
        int priority) => new()
        {
            ActionId = actionId,
            Name = name,
            Kind = kind,
            Condition = condition,
            Priority = priority,
            Enabled = true,
        };
}
