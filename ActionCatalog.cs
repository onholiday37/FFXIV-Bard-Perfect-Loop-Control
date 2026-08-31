using System.Collections.Generic;
using System.Linq;

namespace BardPerfectLoop;

public static class ActionCatalog
{
    public const uint HeavyShot = 97;
    public const uint StraightShot = 98;
    public const uint VenomousBite = 100;
    public const uint Windbite = 113;
    public const uint BurstShot = 16495;
    public const uint RefulgentArrow = 7409;
    public const uint CausticBite = 7406;
    public const uint Stormbite = 7407;
    public const uint IronJaws = 3560;
    public const uint PitchPerfect = 7404;
    public const uint WanderersMinuet = 3559;
    public const uint MagesBallad = 114;
    public const uint ArmysPaeon = 116;
    public const uint EmpyrealArrow = 3558;
    public const uint Barrage = 107;
    public const uint RagingStrikes = 101;
    public const uint BattleVoice = 118;
    public const uint RadiantFinale = 25785;
    public const uint Bloodletter = 110;

    public static IReadOnlyList<ActionDefinition> All { get; } =
    [
        new(CausticBite, "烈毒咬箭", ShadowActionKind.Gcd, StepCondition.CausticMissing, 120),
        new(Stormbite, "狂风蚀箭", ShadowActionKind.Gcd, StepCondition.StormMissing, 115),
        new(IronJaws, "伶牙俐齿", ShadowActionKind.Gcd, StepCondition.DotRefreshDue, 110),
        new(RefulgentArrow, "辉煌箭", ShadowActionKind.Gcd, StepCondition.RefulgentReady, 100),
        new(BurstShot, "爆发射击", ShadowActionKind.Gcd, StepCondition.Always, 10),
        new(PitchPerfect, "完美音调", ShadowActionKind.Ogcd, StepCondition.RepertoireThree, 130),
        new(EmpyrealArrow, "九天连箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 120),
        new(Barrage, "纷乱箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 110),
        new(RagingStrikes, "猛者强击", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 100),
        new(BattleVoice, "战斗之声", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 95),
        new(RadiantFinale, "光明神的最终乐章", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 90),
        new(Bloodletter, "失血箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 40),
    ];

    public static ActionDefinition? Find(uint actionId) => All.FirstOrDefault(action => action.Id == actionId);

    public static List<RotationStep> CreateDefaultSteps() => All
        .Select(action => new RotationStep
        {
            ActionId = action.Id,
            Name = action.Name,
            Kind = action.Kind,
            Condition = action.SuggestedCondition,
            Priority = action.SuggestedPriority,
            Enabled = true,
        })
        .ToList();
}
