using System;

namespace BardPerfectLoop;

public enum ShadowActionKind
{
    Gcd,
    Ogcd,
}

public enum SongPlanMode
{
    Standard3312 = 0,
    Advanced369 = 1,
    Custom = 2,
    GuideAuto = 3,
}

public enum RotationScenario
{
    CurrentStandard = 0,
    Standard249 = 1,
    Advanced369 = 2,
    DowntimeRecovery = 3,
    LegacyNga = 4,
    Custom = 5,
    Uwu = 6,
}

public enum StepCondition
{
    Always,
    CooldownReady,
    RefulgentReady,
    CausticMissing,
    StormMissing,
    DotRefreshDue,
    RepertoireThree,
    DotSnapshotDue,
    SoulVoiceEighty,
    BlastArrowReady,
    ResonantArrowReady,
    RadiantEncoreReady,
    MultipleTargets,
    AoeProcReady,
}

public sealed class RotationStep
{
    public Guid Id = Guid.NewGuid();
    public uint ActionId;
    public string Name = string.Empty;
    public ShadowActionKind Kind;
    public StepCondition Condition;
    public int Priority;
    public bool Enabled = true;

    public RotationStep Clone() => new()
    {
        Id = Guid.NewGuid(),
        ActionId = ActionId,
        Name = Name,
        Kind = Kind,
        Condition = Condition,
        Priority = Priority,
        Enabled = Enabled,
    };
}

public sealed record ActionDefinition(
    uint Id,
    string Name,
    ShadowActionKind Kind,
    StepCondition SuggestedCondition,
    int SuggestedPriority);

public sealed record DotAnalysis(
    string TargetName,
    string CausticName,
    string StormName,
    float CausticRemaining,
    float StormRemaining,
    double CausticTicksRemaining,
    double StormTicksRemaining,
    double CausticRemainingPotency,
    double StormRemainingPotency,
    bool CausticMissing,
    bool StormMissing,
    bool RefreshDue)
{
    public double TotalRemainingPotency => CausticRemainingPotency + StormRemainingPotency;
}

public sealed record CooldownAnalysis(string Name, uint ActionId, float Remaining, bool Ready);

public sealed record SongAnalysis(
    string PlanName,
    string CurrentSong,
    string NextSong,
    float Remaining,
    float PlannedCutRemaining,
    float PlannedSingingSeconds,
    float UntilSwitch,
    byte Repertoire);

public sealed record ShadowSnapshot(
    bool Armed,
    bool Running,
    string Status,
    double ElapsedSeconds,
    long GcdIndex,
    float UntilNextGcd,
    string NextGcd,
    string NextOgcd,
    string Reason,
    DotAnalysis? Dots,
    SongAnalysis? Song,
    CooldownAnalysis[] MajorCooldowns)
{
    public static ShadowSnapshot Idle(string status) => new(
        false,
        false,
        status,
        0,
        0,
        0,
        "—",
        "—",
        string.Empty,
        null,
        null,
        []);
}
