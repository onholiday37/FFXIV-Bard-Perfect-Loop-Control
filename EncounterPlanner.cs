using System;
using System.Collections.Generic;
using System.Linq;

namespace BardPerfectLoop;

public enum UwuPhase { Unknown, Garuda, Ifrit, Titan, Intermission, Ultima }
public readonly record struct EncounterActor(ulong Id, uint DataId, bool Targetable, uint Hp, uint MaxHp, uint Cast, float CastElapsed);
public sealed record EncounterDecision(UwuPhase Phase, string Label, float PhaseSeconds, bool HoldBurst,
    float BurstIn, float UptimeRemaining, bool PredictedEnd, string Reason)
{
    public static EncounterDecision None => new(UwuPhase.Unknown, "普通场景", 0, false, 0, 300, false, "无副本阶段限制");
}
public readonly record struct UwuOptions(float MinimumWindow = 18, float ReturnDelay = 1, float TitanDelay = 30, float UltimaDelay = 0, bool HoldOnAdds = true);

/// <summary>Event-anchored timing. Published timeline coordinates such as 600/1000 are
/// synchronization labels, not seconds since pull. Never apply them as a fight stopwatch.</summary>
public sealed class EncounterPlanner
{
    public const ushort Territory = 777;
    public const uint Garuda = 0x2212, Sister = 0x2213, Razor = 0x2214, Satin = 0x2215, Spiny = 0x2216;
    public const uint Titan = 0x2217, Bomb = 0x2218, Gaol = 0x2219, Ifrit = 0x221A, Nail = 0x221B;
    public const uint Lahabrea = 0x221C, Bit = 0x221D, Ultima = 0x221E;
    private UwuPhase phase;
    private double phaseAt, firstTargetableAt = double.NaN, returnedAt = double.NegativeInfinity;
    private bool previouslyTargetable;
    private int returns;
    private uint lastCast;
    private double lastCastStart = double.NegativeInfinity;
    private double disappearAt = double.PositiveInfinity;
    private double forecastExpires;
    private float predictedGap;
    private bool seenAnnihilation, seenSuppression;
    public EncounterDecision Decision { get; private set; } = EncounterDecision.None;

    public void Reset()
    {
        phase = UwuPhase.Unknown; phaseAt = 0; firstTargetableAt = double.NaN;
        returnedAt = double.NegativeInfinity; previouslyTargetable = false; returns = 0;
        lastCast = 0; lastCastStart = double.NegativeInfinity;
        disappearAt = double.PositiveInfinity; forecastExpires = 0; predictedGap = 0;
        seenAnnihilation = seenSuppression = false; Decision = EncounterDecision.None;
    }

    public EncounterDecision Update(double now, uint territory, bool enabled, IReadOnlyList<EncounterActor> actors, UwuOptions options)
    {
        if (!enabled || territory != Territory) { Reset(); return Decision; }
        // Higher-phase actors must be alive AND active. Untargetable phase previews are not a phase change.
        var active = actors.Where(a => a.Hp > 0 && (a.Targetable || a.Cast != 0)).ToArray();
        var detected = active.Select(a => PhaseFor(a.DataId)).DefaultIfEmpty(UwuPhase.Unknown).Max();
        if (detected > phase)
        {
            phase = detected; phaseAt = now; firstTargetableAt = double.NaN; previouslyTargetable = false;
            returns = 0; disappearAt = double.PositiveInfinity; lastCast = 0;
        }
        var boss = actors.FirstOrDefault(a => a.DataId == BossFor(phase) && a.Hp > 0);
        var available = boss.Id != 0 && boss.Targetable;
        if (available && !previouslyTargetable)
        {
            if (double.IsNaN(firstTargetableAt)) firstTargetableAt = now;
            else returns++;
            returnedAt = now;
            // Actual return always supersedes a stale disappearance prediction.
            disappearAt = double.PositiveInfinity;
            predictedGap = 0;
            if (phase == UwuPhase.Titan && returns == 0) Forecast(now + 22.3, 7.5f);
            if (phase == UwuPhase.Titan && returns == 1) Forecast(now + 46, 7.5f);
            if (phase == UwuPhase.Ultima && returns == 0) Forecast(now + 27.9, 19.5f);
        }
        previouslyTargetable = available;
        if (boss.Cast != 0 && (boss.Cast != lastCast || Math.Abs(now - boss.CastElapsed - lastCastStart) > 1))
        {
            lastCast = boss.Cast; lastCastStart = now - boss.CastElapsed;
            switch (boss.Cast)
            {
                // Garuda's first Slipstream anchors the first two brief jumps.
                case 11091 when phase == UwuPhase.Garuda && double.IsNaN(firstTargetableAt) == false && now - firstTargetableAt < 12:
                    phaseAt = lastCastStart - 6.3; Forecast(phaseAt + 34.9, 4.3f); break;
                case 11126 when phase == UwuPhase.Ultima: Forecast(lastCastStart + 7.4, 19.5f); break;
                case 11596 when phase == UwuPhase.Ultima:
                    seenAnnihilation = true; Forecast(lastCastStart + 7.4, 4.2f); break;
                case 11597 when phase == UwuPhase.Ultima:
                    seenSuppression = true; Forecast(lastCastStart + 7.4, 45); break;
            }
        }
        if (available && phase == UwuPhase.Garuda && returns == 1 && !double.IsFinite(disappearAt))
            Forecast(phaseAt + 69.5, 4.3f);
        // A forecast which did not happen expires. Never park at zero forever.
        if (available && now > forecastExpires) disappearAt = double.PositiveInfinity;
        var uptime = double.IsFinite(disappearAt) ? Math.Max(0, (float)(disappearAt - now)) : 300;
        var known = double.IsFinite(disappearAt);
        var local = double.IsNaN(firstTargetableAt) ? 0 : (float)(now - firstTargetableAt);
        var delay = phase == UwuPhase.Titan ? options.TitanDelay : phase == UwuPhase.Ultima ? options.UltimaDelay : 0;
        var burstIn = Math.Max(0, delay - local);
        burstIn = Math.Max(burstIn, Math.Max(0, options.ReturnDelay - (float)(now - returnedAt)));
        var hold = !available || phase is UwuPhase.Unknown or UwuPhase.Intermission;
        var reason = hold ? "Boss 不可攻击或处于转场：保留长CD爆发，已选小怪仍可按机制输出" : "可攻击窗口足够：团辅与个人爆发自动排程";
        if (known && uptime < options.MinimumWindow)
        {
            hold = true; burstIn = Math.Max(burstIn, uptime + predictedGap + options.ReturnDelay);
            reason = $"预计 {uptime:F1}s 后上天，长CD爆发推迟至实际复现";
        }
        if (options.HoldOnAdds && active.Any(a => a.DataId == Nail) && phase == UwuPhase.Ifrit)
        { hold = true; reason = "火神柱子阶段：保留长CD，手选指定柱子，防止提前推阶段"; }
        if (burstIn > 0) { hold = true; if (!known) reason = $"阶段爆发延后 {burstIn:F1}s；CD和安全插入窗口仍需满足"; }
        if (hold && burstIn <= 0) burstIn = 30; // A rolling gate, released by actual state change.
        var label = phase switch
        {
            UwuPhase.Garuda => "风神", UwuPhase.Ifrit => "火神", UwuPhase.Titan => "土神",
            UwuPhase.Intermission => "浮游炮/拉哈布雷亚", UwuPhase.Ultima when seenSuppression => "神兵·压制后",
            UwuPhase.Ultima when seenAnnihilation => "神兵·究极歼灭", UwuPhase.Ultima => "神兵", _ => "等待识别绝神兵阶段",
        };
        return Decision = new(phase, label, local, hold, burstIn, available ? uptime : 0, known, reason);
    }

    private void Forecast(double at, float gap) { disappearAt = at; predictedGap = gap; forecastExpires = at + 2; }
    public static UwuPhase PhaseFor(uint dataId) => dataId switch
    {
        Garuda => UwuPhase.Garuda, Ifrit => UwuPhase.Ifrit, Titan => UwuPhase.Titan,
        Bit or Lahabrea => UwuPhase.Intermission, Ultima => UwuPhase.Ultima, _ => UwuPhase.Unknown,
    };
    public static uint BossFor(UwuPhase phase) => phase switch
    { UwuPhase.Garuda => Garuda, UwuPhase.Ifrit => Ifrit, UwuPhase.Titan => Titan, UwuPhase.Ultima => Ultima, _ => 0 };
    public static bool Sensitive(uint dataId) => dataId is Spiny or Nail or Gaol or Bomb;
}
