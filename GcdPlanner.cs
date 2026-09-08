using System;
using System.Collections.Generic;

namespace BardPerfectLoop;

public sealed record GcdState
{
    public int Level { get; init; } = 100;
    public bool TargetAvailable { get; init; } = true;
    public float Gcd { get; init; } = 2.49f;
    public float ExecuteIn { get; init; }
    public float ApplicationDelay { get; init; } = 0.70f;
    public float Lifetime { get; init; } = 300;
    public bool KnownEnd { get; init; }
    public float Caustic { get; init; }
    public float Storm { get; init; }
    public float CausticMultiplier { get; init; } = 1;
    public float StormMultiplier { get; init; } = 1;
    public bool SnapshotsKnown { get; init; }
    public float Multiplier { get; init; } = 1;
    public float BuffLeft { get; init; }
    public float RagingLeft { get; init; }
    public float RagingReady { get; init; } = 60;
    public float BarrageLeft { get; init; }
    public float HawksEyeLeft { get; init; }
    public float BlastLeft { get; init; }
    public float ResonantLeft { get; init; }
    public float EncoreLeft { get; init; }
    public float Soul { get; init; }
    public BardSong Song { get; init; }
    public float SongRemaining { get; init; }
    public int CausticPotency { get; init; } = 20;
    public int StormPotency { get; init; } = 25;
    public AoeCoverage Aoe { get; init; } = AoeCoverage.Single;
    public IReadOnlySet<uint> Excluded { get; init; } = new HashSet<uint>();
}

public sealed record GcdChoice(uint Action, string Reason, DotDecision Dots);

public static class GcdPlanner
{
    public static GcdChoice Select(GcdState s)
    {
        if (!s.TargetAvailable || s.Lifetime <= s.ExecuteIn) return new(0, "无可攻击目标", default);
        var e = Math.Max(0, s.ExecuteIn);
        var life = s.Lifetime - e;
        var c = Math.Max(0, s.Caustic - e);
        var w = Math.Max(0, s.Storm - e);
        var filler = s.Level >= 76 ? ActionCatalog.BurstShot : ActionCatalog.HeavyShot;
        var proc = s.Level >= 70 ? ActionCatalog.RefulgentArrow : ActionCatalog.StraightShot;
        var singleProc = s.Level >= 94 ? 280 : s.Level >= 70 ? 260 : 200;
        var fillerPotency = s.HawksEyeLeft > e ? singleProc : s.Level >= 94 ? 241 : s.Level >= 76 ? 221 : 172;
        var conePotency = (s.Level >= 82 ? 140 : 110) * s.Aoe.Cone12;
        if (s.Level >= 18 && conePotency > fillerPotency)
        { filler = s.Level >= 82 ? ActionCatalog.Ladonsbite : ActionCatalog.QuickNock; fillerPotency = conePotency; }
        if (s.Level >= 72 && s.Aoe.Circle5 * (s.BarrageLeft > e ? 300 : 200) > singleProc * (s.BarrageLeft > e ? 3 : 1))
            proc = ActionCatalog.Shadowbite;
        if (s.HawksEyeLeft > e && proc == ActionCatalog.Shadowbite) fillerPotency = Math.Max(fillerPotency, 200 * s.Aoe.Circle5);
        var dots = DotEvaluator.Refresh(s.Caustic, s.Storm, s.Gcd, e, s.Lifetime,
            s.CausticMultiplier, s.StormMultiplier, s.Multiplier, s.BuffLeft, fillerPotency,
            s.CausticPotency, s.StormPotency, s.SnapshotsKnown, s.ApplicationDelay);
        GcdChoice Pick(uint action, string reason)
        {
            if (!s.Excluded.Contains(action)) return new(action, reason, dots);
            // A failed GCD must not keep winning every frame and stall the rotation.
            var fallback = !s.Excluded.Contains(filler) ? filler : s.Level >= 76 ? ActionCatalog.BurstShot : ActionCatalog.HeavyShot;
            return new(s.Excluded.Contains(fallback) ? 0 : fallback, "建议动作被客户端拒绝，暂用可执行基础GCD", dots);
        }

        // Rescue expiring high-value proc GCDs, even if burst was interrupted.
        if (s.Aoe.Circle5 > 0 && s.Level >= 100 && s.EncoreLeft > e && s.EncoreLeft <= e + s.Gcd + 0.15f) return Pick(ActionCatalog.RadiantEncore, "返场余音即将过期");
        if (s.Aoe.Circle5 > 0 && s.Level >= 96 && s.ResonantLeft > e && s.ResonantLeft <= e + s.Gcd + 0.15f) return Pick(ActionCatalog.ResonantArrow, "共鸣箭即将过期");
        if (s.Aoe.Line25 > 0 && s.Level >= 86 && s.BlastLeft > e && s.BlastLeft <= e + s.Gcd + 0.15f) return Pick(ActionCatalog.BlastArrow, "爆破箭即将过期");
        if (s.BarrageLeft > e && s.BarrageLeft <= e + s.Gcd + 0.15f && s.Level >= 38) return Pick(proc, "纷乱即将过期");

        if (s.Level >= 30 && w <= s.ApplicationDelay && DotEvaluator.ApplicationValue(s.Level >= 64 ? 100 : 60, s.StormPotency, Math.Max(0, life - s.ApplicationDelay), s.Multiplier) > fillerPotency * s.Multiplier)
            return Pick(s.Level >= 64 ? ActionCatalog.Stormbite : ActionCatalog.Windbite, "补风DoT，已计入目标剩余可攻击时间内的持续伤害");
        if (s.Level >= 6 && c <= s.ApplicationDelay && DotEvaluator.ApplicationValue(s.Level >= 64 ? 150 : 100, s.CausticPotency, Math.Max(0, life - s.ApplicationDelay), s.Multiplier) > fillerPotency * s.Multiplier)
            return Pick(s.Level >= 64 ? ActionCatalog.CausticBite : ActionCatalog.VenomousBite, "补毒DoT，已计入目标剩余可攻击时间内的持续伤害");

        if (s.Level >= 56 && dots.Refresh) return Pick(ActionCatalog.IronJaws, dots.Reason);
        if (s.Level < 56)
        {
            if (s.Level >= 30 && w <= s.Gcd + s.ApplicationDelay + 0.2f && DotEvaluator.ApplicationValue(60, s.StormPotency, life, s.Multiplier) - DotEvaluator.ExpectedTicks(w, life) * s.StormPotency * s.StormMultiplier > fillerPotency * s.Multiplier)
                return Pick(ActionCatalog.Windbite, "低等级分别续风DoT");
            if (s.Level >= 6 && c <= s.Gcd + s.ApplicationDelay + 0.2f && DotEvaluator.ApplicationValue(100, s.CausticPotency, life, s.Multiplier) - DotEvaluator.ExpectedTicks(c, life) * s.CausticPotency * s.CausticMultiplier > fillerPotency * s.Multiplier)
                return Pick(ActionCatalog.VenomousBite, "低等级分别续毒DoT");
        }

        if (s.BarrageLeft > e && s.Level >= 38) return Pick(proc, "消耗纷乱增益");
        var burst = s.RagingLeft > e;
        var holdForBuff = !burst && s.RagingReady <= 2 * s.Gcd && (!s.KnownEnd || life > 3 * s.Gcd);
        if (s.Aoe.Circle5 > 0 && s.Level >= 100 && s.EncoreLeft > e && (!holdForBuff || s.EncoreLeft <= e + 3 * s.Gcd))
            return Pick(ActionCatalog.RadiantEncore, "释放返场余音；不因猛者缺失而永久搁置");
        if (s.Aoe.Line25 > 0 && s.Level >= 86 && s.BlastLeft > e && (!holdForBuff || s.BlastLeft <= e + 3 * s.Gcd))
            return Pick(ActionCatalog.BlastArrow, "释放爆破箭");
        if (s.Aoe.Circle5 > 0 && s.Level >= 96 && s.ResonantLeft > e && (!holdForBuff || s.ResonantLeft <= e + 3 * s.Gcd))
            return Pick(ActionCatalog.ResonantArrow, "释放共鸣箭");
        var apexThreshold = s.Level >= 86 ? 80 : 100;
        if (s.Aoe.Line25 > 0 && s.Level >= 80 && s.Soul >= apexThreshold &&
            (burst || s.Soul >= 100 && !holdForBuff || s.Song == BardSong.Mage && s.SongRemaining - e <= 21 || s.KnownEnd && life < 3 * s.Gcd))
            return Pick(ActionCatalog.ApexArrow, "魂音窗口/防满槽，绝峰与爆破一起安排");
        if (s.Aoe.Line25 > 0 && s.Level >= 80 && s.KnownEnd && life <= s.Gcd && s.Soul >= 40)
            return Pick(ActionCatalog.ApexArrow, "目标末段兑现剩余魂音");
        if (s.Level >= 2 && s.HawksEyeLeft > e && (proc == ActionCatalog.Shadowbite ? 200 * s.Aoe.Circle5 : singleProc) >= conePotency)
            return Pick(proc, "消耗实际辉煌/直线/影噬触发，已比较群体总威力");
        return Pick(filler, AoeCoverage.Shape(filler) is not null ? $"群攻命中 {s.Aoe.Cone12} 个已接战目标，总直接威力 {conePotency}" : "连续基础GCD；无收益更高的可用动作");
    }
}
