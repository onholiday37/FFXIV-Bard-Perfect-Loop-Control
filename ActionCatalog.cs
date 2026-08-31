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
    public const uint ApexArrow = 16496;
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
    public const uint Sidewinder = 3562;
    public const uint BlastArrow = 25784;
    public const uint HeartbreakShot = 36975;
    public const uint ResonantArrow = 36976;
    public const uint RadiantEncore = 36977;

    public static class Buffs
    {
        public const ushort RagingStrikes = 125;
        public const ushort Barrage = 128;
        public const ushort BattleVoice = 141;
        public const ushort BlastArrowReady = 2692;
        public const ushort RadiantFinale = 2722;
        public const ushort HawksEye = 3861;
        public const ushort ResonantArrowReady = 3862;
        public const ushort RadiantEncoreReady = 3863;
    }

    public static IReadOnlyList<ActionDefinition> All { get; } =
    [
        new(CausticBite, "烈毒咬箭", ShadowActionKind.Gcd, StepCondition.CausticMissing, 120),
        new(Stormbite, "狂风蚀箭", ShadowActionKind.Gcd, StepCondition.StormMissing, 115),
        new(IronJaws, "伶牙俐齿", ShadowActionKind.Gcd, StepCondition.DotRefreshDue, 110),
        new(RadiantEncore, "光明神的返场余音", ShadowActionKind.Gcd, StepCondition.RadiantEncoreReady, 108),
        new(BlastArrow, "爆破箭", ShadowActionKind.Gcd, StepCondition.BlastArrowReady, 107),
        new(ResonantArrow, "共鸣箭", ShadowActionKind.Gcd, StepCondition.ResonantArrowReady, 106),
        new(ApexArrow, "绝峰箭", ShadowActionKind.Gcd, StepCondition.SoulVoiceEighty, 105),
        new(RefulgentArrow, "辉煌箭", ShadowActionKind.Gcd, StepCondition.RefulgentReady, 100),
        new(BurstShot, "爆发射击", ShadowActionKind.Gcd, StepCondition.Always, 10),
        new(PitchPerfect, "完美音调", ShadowActionKind.Ogcd, StepCondition.RepertoireThree, 130),
        new(EmpyrealArrow, "九天连箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 120),
        new(Barrage, "纷乱箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 110),
        new(RagingStrikes, "猛者强击", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 100),
        new(BattleVoice, "战斗之声", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 95),
        new(RadiantFinale, "光明神的最终乐章", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 90),
        new(Sidewinder, "侧风诱导箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 80),
        new(Bloodletter, "失血箭", ShadowActionKind.Ogcd, StepCondition.CooldownReady, 40),
    ];

    public static ActionDefinition? Find(uint actionId) => All.FirstOrDefault(action => action.Id == actionId);

    public static int MinimumLevel(uint actionId) => actionId switch
    {
        HeavyShot => 1,
        StraightShot => 2,
        RagingStrikes => 4,
        VenomousBite => 6,
        Bloodletter => 12,
        Windbite => 30,
        MagesBallad => 30,
        Barrage => 38,
        ArmysPaeon => 40,
        BattleVoice => 50,
        WanderersMinuet or PitchPerfect => 52,
        EmpyrealArrow => 54,
        IronJaws => 56,
        Sidewinder => 60,
        CausticBite or Stormbite => 64,
        RefulgentArrow => 70,
        BurstShot => 76,
        ApexArrow => 80,
        BlastArrow => 86,
        RadiantFinale => 90,
        HeartbreakShot => 92,
        ResonantArrow => 96,
        RadiantEncore => 100,
        _ => int.MaxValue,
    };

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
