using System;
using System.Collections.Generic;

namespace BardPerfectLoop;

public enum BardSong { None, Wanderer, Mage, Army }

public sealed record PlannerState
{
    public int Level { get; init; } = 100;
    public float Gcd { get; init; } = 2.49f;
    public float NextGcd { get; init; } = 2.49f;
    public float Earliest { get; init; } = 0.70f;
    public int Slots { get; init; } = 2;
    public int FutureSlots { get; init; } = 2;
    public float Lock { get; init; } = 0.70f;
    public float Margin { get; init; } = 0.08f;
    public bool TargetAvailable { get; init; } = true;
    public float TargetLifetime { get; init; } = 300;
    public bool KnownTargetEnd { get; init; }
    public BardSong Song { get; init; } = BardSong.Wanderer;
    public float SongRemaining { get; init; } = 30;
    public float SongSwitchIn { get; init; } = 28;
    public uint NextSongAction { get; init; } = ActionCatalog.MagesBallad;
    public float NextSongReady { get; init; }
    public float NextNaturalTick { get; init; } = 3;
    public float NaturalProcProbability { get; init; } = 0.8f;
    public int Repertoire { get; init; }
    public float Soul { get; init; }
    public float PendingRepertoireIn { get; init; } = float.PositiveInfinity;
    public float EmpyrealEffectDelay { get; init; } = 0.65f;
    public ChargeState Charges { get; init; } = new(1, 3, 10, 25);
    public float EmpyrealReady { get; init; } = 10;
    public float PitchReady { get; init; }
    public float SidewinderReady { get; init; } = 30;
    public float RagingReady { get; init; } = 60;
    public float VoiceReady { get; init; } = 60;
    public float FinaleReady { get; init; } = 60;
    public float BarrageReady { get; init; } = 60;
    public float RagingLeft { get; init; }
    public float VoiceLeft { get; init; }
    public float FinaleLeft { get; init; }
    public float PotionLeft { get; init; }
    public float PotionMultiplier { get; init; } = 1;
    public float FinaleMultiplier { get; init; } = 1.06f;
    public int Coda { get; init; } = 3;
    public bool NextSongGrantsNewCoda { get; init; } = true;
    public bool BarrageActive { get; init; }
    public bool ResonantActive { get; init; }
    public bool HawksEye { get; init; }
    public bool ModernBurst { get; init; } = true;
    public bool Opener { get; init; }
    public float BurstEarliest { get; init; }
    public float HoldBurstUntil { get; init; }
    public AoeCoverage Aoe { get; init; } = AoeCoverage.Single;
    public float BaselineDirectHit { get; init; } = 0.20f;
    public float BackgroundPotencyPerSecond { get; init; } = 110;
    public IReadOnlySet<uint> Excluded { get; init; } = new HashSet<uint>();
    public uint ChargeAction => Level >= 45 && !Excluded.Contains(ActionCatalog.RainOfDeath) && Aoe.Circle8 * 100 > SingleChargePotency ? ActionCatalog.RainOfDeath : Level >= 92 ? ActionCatalog.HeartbreakShot : ActionCatalog.Bloodletter;
    private float SingleChargePotency => Level >= 92 ? 180 : 130;
    public float ChargePotency => ChargeAction == ActionCatalog.RainOfDeath ? Aoe.Circle8 * 100 : SingleChargePotency;
    public float EmpyrealPotency => Level >= 94 ? 260 : 240;
    public float SidewinderPotency => Level >= 94 ? 400 : 300;
}

public readonly record struct PlannedWeave(uint ActionId, float At, int ExpectedRepertoire);
public sealed record WeavePlan(PlannedWeave[] Actions, double Score, string Reason)
{
    public static WeavePlan Empty(string reason) => new([], 0, reason);
    public PlannedWeave? First => Actions.Length == 0 ? null : Actions[0];
}
