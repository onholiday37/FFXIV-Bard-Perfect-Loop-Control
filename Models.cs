using System;

namespace BardPerfectLoop;

public enum ShadowActionKind
{
    Gcd,
    Ogcd,
}

public enum SongPlanMode
{
    Standard3312,
    Advanced369,
    Custom,
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
    int CausticTicksRemaining,
    int StormTicksRemaining,
    int CausticRemainingPotency,
    int StormRemainingPotency,
    bool CausticMissing,
    bool StormMissing,
    bool RefreshDue)
{
    public int TotalRemainingPotency => CausticRemainingPotency + StormRemainingPotency;
}

public sealed record CooldownAnalysis(string Name, uint ActionId, float Remaining, bool Ready);

public sealed record SongAnalysis(
    string CurrentSong,
    string NextSong,
    float Remaining,
    float PlannedCutRemaining,
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
