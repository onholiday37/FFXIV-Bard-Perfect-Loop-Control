using System;
using System.Collections.Generic;
using System.Linq;

namespace BardPerfectLoop;

/// <summary>
/// Bounded receding-horizon search. Searches ordered double/single/empty weaves
/// in this and two following GCD windows, including waits for real cooldowns.
/// Natural procs branch at 80/20; they are never treated as guaranteed stacks.
/// The score is expected potency plus an explicitly approximate terminal value,
/// NOT an exact encounter DPS prediction or a proof of global optimality.
/// </summary>
public static class WeavePlanner
{
    private const int Width = 24;
    private const int Depth = 6;
    private const float Epsilon = 0.001f;

    public static WeavePlan Plan(PlannerState input)
    {
        if (!input.TargetAvailable || input.TargetLifetime <= 0)
            return WeavePlan.Empty("无可攻击目标：不追赶旧轴");
        var horizon = Math.Min(input.TargetLifetime, input.NextGcd + input.Gcd * 2);
        if (horizon <= 0 || !float.IsFinite(horizon))
            return WeavePlan.Empty("时间数据不可用");
        var root = new Node(input);
        var best = root;
        var bestScore = Evaluate(root, input, horizon);
        var beam = new List<Node> { root };
        for (var depth = 0; depth < Depth && beam.Count > 0; depth++)
        {
            var children = new List<(Node Node, double Score)>();
            foreach (var node in beam)
            {
                // An empty window is a real candidate, not a forced filler oGCD.
                var skip = node.Copy();
                skip.NextWindow(input);
                if (skip.Time < horizon)
                    children.Add((skip, Evaluate(skip, input, horizon)));

                // Waiting for the next natural/pending repertoire event is a
                // separate candidate, rather than prematurely spending two.
                var resourceEvent = Math.Min(node.NextTick, node.PendingProc);
                if (node.Song == BardSong.Wanderer && resourceEvent > node.Time + 0.001f && resourceEvent < node.WindowEnd - input.Lock - input.Margin)
                {
                    var wait = node.Copy();
                    wait.Advance(resourceEvent, input);
                    wait.Earliest = Math.Max(wait.Earliest, resourceEvent);
                    children.Add((wait, Evaluate(wait, input, horizon)));
                }

                if (node.Slots <= 0)
                    continue;
                for (var action = 0; action < 9; action++)
                {
                    var id = Id(action, input);
                    if (id == 0 || input.Excluded.Contains(id) || !input.Aoe.Allows(id) || input.Level < ActionCatalog.MinimumLevel(id))
                        continue;
                    var at = Earliest(action, node, input);
                    if (!float.IsFinite(at) || at >= horizon || !CombatTiming.Fits(at, node.WindowEnd, input.Lock, input.Margin))
                        continue;
                    var next = node.Copy();
                    next.Advance(at, input);
                    if (!Legal(action, next, input))
                        continue;
                    var stacks = (int)Math.Round(next.Resources.Sum(r => r.Probability * r.Repertoire));
                    next.Actions.Add(new(id, at, action == 0 ? stacks : 0));
                    next.Apply(action, input);
                    next.Earliest = at + Math.Max(0.70f, input.Lock);
                    next.Slots--;
                    children.Add((next, Evaluate(next, input, horizon)));
                }
            }
            foreach (var child in children)
            {
                if (child.Score > bestScore + 0.00001)
                {
                    best = child.Node;
                    bestScore = child.Score;
                }
            }
            beam = children.OrderByDescending(c => c.Score).Take(Width).Select(c => c.Node).ToList();
        }
        var actions = best.Actions.ToArray();
        if (actions.Length == 0)
            return WeavePlan.Empty("保留插入位置：无收益更高的安全能力技组合");
        var first = actions[0];
        var names = string.Join(" → ", actions.Take(4).Select(a => $"{Name(a.ActionId)} +{a.At:F2}s"));
        var reason = $"联合排程：{names}；含自然诗心分支、充能/诗心期末价值和真实CD";
        if (first.At >= input.NextGcd)
            reason = "本窗口留空，" + reason;
        return new(actions, bestScore - Evaluate(root, input, horizon), reason);
    }

    public static string Name(uint id) => ActionCatalog.Find(id)?.Name ?? id switch
    {
        ActionCatalog.WanderersMinuet => "旅神歌",
        ActionCatalog.MagesBallad => "贤者歌",
        ActionCatalog.ArmysPaeon => "军神歌",
        _ => id.ToString(),
    };

    private static uint Id(int action, PlannerState s) => action switch
    {
        0 => ActionCatalog.PitchPerfect, 1 => ActionCatalog.EmpyrealArrow,
        2 => s.ChargeAction, 3 => ActionCatalog.Sidewinder,
        4 => ActionCatalog.RadiantFinale, 5 => ActionCatalog.BattleVoice,
        6 => ActionCatalog.RagingStrikes, 7 => ActionCatalog.Barrage,
        8 => s.NextSongAction, _ => 0,
    };

    private static float Earliest(int action, Node n, PlannerState s)
    {
        var at = Math.Max(n.Time, n.Earliest);
        if (action < 8 && action != 2)
            at = Math.Max(at, n.Ready[action]);
        if (action == 2)
            at = Math.Max(at, n.Time + n.Resources.Max(r => Math.Max(0, 15 - r.Bank)));
        if (action is 4 or 5 or 6)
            at = Math.Max(at, s.BurstEarliest);
        if (action is 4 or 5 or 6 or 7)
            at = Math.Max(at, s.HoldBurstUntil);
        if (action == 6 || !s.ModernBurst && action is 4 or 5)
            at = Math.Max(at, n.WindowEnd - s.Gcd * 0.52f);
        if (action == 8)
            at = Math.Max(at, Math.Max(s.NextSongReady, n.Song == BardSong.None ? 0 : s.SongSwitchIn - 0.25f));
        return at;
    }

    private static bool Legal(int action, Node n, PlannerState s)
    {
        var t = n.Time;
        if (action is 4 or 5 or 6 or 7 && t < s.HoldBurstUntil) return false;
        var burstSoon = n.Ready[6] - t <= s.Gcd * 1.2f || n.RagingEnd > t || s.KnownTargetEnd && s.TargetLifetime - t < 22;
        // A new song must precede repertoire-generating attacks in the opener.
        if (action != 8 && n.Song == BardSong.None && s.NextSongAction != 0 && s.NextSongReady <= t && !n.SongChanged)
            return false;
        // Protect burst staging from starvation by flexible charged attacks.
        var staged = StagedBuff(n, s);
        if (staged >= 0 && action != staged && action != 8 && t >= s.BurstEarliest &&
            !(action == 0 && (n.Resources.All(r => r.Repertoire == 3) && n.NextTick <= n.WindowEnd + s.Lock || s.SongSwitchIn <= n.WindowEnd)) &&
            !(staged == 6 && t < n.WindowEnd - s.Gcd * 0.52f))
            return false;
        return action switch
        {
            0 => n.Song == BardSong.Wanderer && n.Resources.All(r => r.Repertoire > 0),
            1 => n.Ready[1] <= t + Epsilon && (!s.KnownTargetEnd || t + s.EmpyrealEffectDelay < s.TargetLifetime),
            2 => n.Resources.All(r => r.Bank >= 15 - Epsilon),
            3 => n.RagingEnd > t || n.Ready[6] - t > 12 || s.KnownTargetEnd && s.TargetLifetime - t < 12,
            4 => n.Song != BardSong.None && n.Coda > 0 && burstSoon &&
                 !(n.Coda < 3 && !n.SongChanged && s.NextSongGrantsNewCoda && s.NextSongAction != 0 &&
                   s.SongSwitchIn - t <= 2 * s.Gcd && s.NextSongReady - t <= 2 * s.Gcd &&
                   !(s.KnownTargetEnd && s.TargetLifetime - t <= 2 * s.Gcd)) &&
                 (s.ModernBurst || s.Level < 50 || n.VoiceEnd > t || n.Ready[5] - t > 8 || s.Excluded.Contains(ActionCatalog.BattleVoice)),
            5 => burstSoon && (!s.ModernBurst || s.Level < 90 || n.Coda == 0 || n.FinaleEnd > t || n.Ready[4] - t > 8 || s.Excluded.Contains(ActionCatalog.RadiantFinale)),
            6 => burstSoon && (s.Level < 50 || n.VoiceEnd > t || n.Ready[5] - t > 8 || s.Excluded.Contains(ActionCatalog.BattleVoice)) &&
                 (s.Level < 90 || n.Coda == 0 || n.FinaleEnd > t || n.Ready[4] - t > 8 || s.Excluded.Contains(ActionCatalog.RadiantFinale)),
            7 => !n.BarrageUsed && !s.BarrageActive && !s.ResonantActive &&
                 (!s.HawksEye || n.WindowEnd > s.NextGcd + Epsilon) &&
                 (n.RagingEnd > n.WindowEnd || n.Ready[6] - t > 30 || s.KnownTargetEnd && s.TargetLifetime - t < 12) &&
                 s.TargetLifetime > n.WindowEnd,
            8 => !n.SongChanged && (n.Song != BardSong.Wanderer || n.Resources.All(r => r.Repertoire == 0) || t >= s.SongRemaining),
            _ => false,
        };
    }

    private static int StagedBuff(Node n, PlannerState s)
    {
        var t = n.Time;
        if (t < s.HoldBurstUntil || n.Song == BardSong.None || n.Ready[6] - t > s.Gcd * 1.2f || n.RagingEnd > t) return -1;
        bool Available(int a) => s.Level >= ActionCatalog.MinimumLevel(Id(a, s)) && !s.Excluded.Contains(Id(a, s)) && n.Ready[a] <= t;
        if (s.ModernBurst)
        {
            if (n.Coda > 0 && Available(4)) return 4;
            if (Available(5)) return 5;
        }
        else
        {
            if (Available(5)) return 5;
            if (n.Coda > 0 && Available(4)) return 4;
        }
        return Available(6) ? 6 : -1;
    }

    private static double Evaluate(Node original, PlannerState s, float horizon)
    {
        var n = original.Copy();
        n.Advance(horizon, s);
        var ending = s.KnownTargetEnd && horizon >= s.TargetLifetime - Epsilon;
        var score = n.Damage;
        if (!ending)
        {
            // Avoid horizon artifacts: unused resources still have future value.
            score += n.Resources.Sum(r => r.Probability * (r.Repertoire * 118 * s.Aoe.PitchMultiplier + r.Bank / 15 * s.ChargePotency + r.Soul * 8));
            score -= Remaining(n.Ready[1], horizon) * s.EmpyrealPotency / 15;
            score -= Remaining(n.Ready[3], horizon) * s.SidewinderPotency / 60;
            // Remaining buff coverage extends beyond the short search horizon.
            var end = Math.Min(s.TargetLifetime, horizon + 20);
            score += n.IntegratedBackground(horizon, end, s) - (end - horizon) * s.BackgroundPotencyPerSecond;
            score -= Remaining(n.Ready[6], horizon) * s.BackgroundPotencyPerSecond * 3 / 120;
            score -= Remaining(n.Ready[5], horizon) * s.BackgroundPotencyPerSecond * 0.95 / 120;
            score -= Remaining(n.Ready[4], horizon) * (s.BackgroundPotencyPerSecond * (s.FinaleMultiplier - 1) * 20 + EncoreValue(s)) / 110;
            score -= Remaining(n.Ready[7], horizon) * BarrageValue(s) / 120;
        }
        // Tie break only, not an invented potency bonus or a hard priority.
        return score - original.Actions.Sum(a => a.At * 0.00001);
    }

    private static float Remaining(float ready, float now) => float.IsFinite(ready) ? Math.Max(0, ready - now) : 0;
    private static float BarrageValue(PlannerState s)
    {
        if (Math.Max(s.Aoe.Circle5, s.Aoe.Cone12) <= 1)
            return s.Level >= 96 ? 2 * 280 + 640 - 220 : s.Level >= 70 ? 2 * 260 : 2 * 200;
        var proc = s.Level >= 94 ? 280 : s.Level >= 70 ? 260 : 200;
        var filler = Math.Max(proc, (s.Level >= 82 ? 140 : 110) * s.Aoe.Cone12);
        var barrage = Math.Max(proc * 3, s.Level >= 72 ? 300 * s.Aoe.Circle5 : 0);
        var resonant = s.Level >= 96 && s.Aoe.Circle5 > 0 ? 640 * (1 + 0.5f * (s.Aoe.Circle5 - 1)) - filler : 0;
        return Math.Max(0, barrage - filler) + Math.Max(0, resonant);
    }
    private static float EncoreValue(PlannerState s, int? coda = null) => s.Level >= 100 ? ((coda ?? s.Coda) >= 3 ? 1100 : (coda ?? s.Coda) == 2 ? 800 : 700) - 220 : 0;
    private readonly record struct Resource(double Probability, int Repertoire, float Bank, float Soul);

    private sealed class Node
    {
        public float Time, Earliest, WindowEnd, NextTick, SongEnd, RagingEnd, VoiceEnd, FinaleEnd, PendingProc;
        public int Slots;
        public int Coda;
        public float FinaleMultiplier;
        public BardSong Song;
        public bool SongChanged, BarrageUsed;
        public double Damage;
        public float[] Ready;
        public List<Resource> Resources;
        public List<PlannedWeave> Actions = [];
        public Node(PlannerState s)
        {
            Earliest = s.Earliest; WindowEnd = s.NextGcd; Slots = Math.Clamp(s.Slots, 0, 2);
            Song = s.Song; SongEnd = s.SongRemaining; NextTick = s.NextNaturalTick;
            PendingProc = s.PendingRepertoireIn;
            RagingEnd = s.RagingLeft; VoiceEnd = s.VoiceLeft; FinaleEnd = s.FinaleLeft;
            Coda = s.Coda; FinaleMultiplier = s.FinaleMultiplier;
            Ready = [s.PitchReady, s.EmpyrealReady, 0, s.SidewinderReady, s.FinaleReady, s.VoiceReady, s.RagingReady, s.BarrageReady];
            // Army stacks are haste, not spendable Pitch Perfect stacks.
            Resources = [new(1, s.Song == BardSong.Wanderer ? Math.Clamp(s.Repertoire, 0, 3) : 0, s.Charges.Bank, s.Soul)];
        }
        private Node(Node n)
        {
            Time = n.Time; Earliest = n.Earliest; WindowEnd = n.WindowEnd; Slots = n.Slots;
            Song = n.Song; SongEnd = n.SongEnd; NextTick = n.NextTick; PendingProc = n.PendingProc;
            RagingEnd = n.RagingEnd; VoiceEnd = n.VoiceEnd; FinaleEnd = n.FinaleEnd;
            Coda = n.Coda; FinaleMultiplier = n.FinaleMultiplier;
            SongChanged = n.SongChanged; BarrageUsed = n.BarrageUsed; Damage = n.Damage;
            Ready = (float[])n.Ready.Clone(); Resources = new(n.Resources); Actions = new(n.Actions);
        }
        public Node Copy() => new(this);
        public void NextWindow(PlannerState s)
        {
            Advance(WindowEnd, s);
            Earliest = WindowEnd + Math.Max(0.70f, s.Lock);
            WindowEnd += s.Gcd;
            Slots = Math.Clamp(s.FutureSlots, 1, 2);
        }
        public void Advance(float to, PlannerState s)
        {
            if (to < Time) return;
            while (Math.Min(NextTick, PendingProc) <= to)
            {
                var at = Math.Min(NextTick, PendingProc);
                if (!float.IsFinite(at)) break;
                AdvanceContinuous(at, s);
                if (PendingProc <= NextTick)
                {
                    Proc(s, 1);
                    PendingProc = float.PositiveInfinity;
                }
                else
                {
                    if (at <= SongEnd - 2.99f)
                        Proc(s, Math.Clamp(s.NaturalProcProbability, 0, 1));
                    NextTick += 3;
                    if (NextTick > SongEnd - 2.99f) NextTick = float.PositiveInfinity;
                }
            }
            AdvanceContinuous(to, s);
            if (Time >= SongEnd && Song != BardSong.None)
            {
                Song = BardSong.None;
                Resources = Resources.Select(r => r with { Repertoire = 0 }).ToList();
            }
        }
        private void AdvanceContinuous(float to, PlannerState s)
        {
            var dt = Math.Max(0, to - Time);
            Damage += IntegratedBackground(Time, to, s);
            Resources = Resources.Select(r => r with { Bank = Math.Min(s.Charges.Maximum * 15, r.Bank + dt) }).ToList();
            Time = to;
        }
        public double IntegratedBackground(float from, float to, PlannerState s)
        {
            if (to <= from) return 0;
            var cuts = new[] { from, to, RagingEnd, VoiceEnd, FinaleEnd, s.PotionLeft }.Where(x => x >= from && x <= to).Distinct().Order().ToArray();
            double result = 0;
            for (var i = 1; i < cuts.Length; i++)
                result += (cuts[i] - cuts[i - 1]) * s.BackgroundPotencyPerSecond * Multiplier((cuts[i] + cuts[i - 1]) / 2, s);
            return result;
        }
        private double Multiplier(float at, PlannerState s) =>
            (RagingEnd > at ? 1.15 : 1) * (FinaleEnd > at ? FinaleMultiplier : 1) *
            (s.PotionLeft > at ? s.PotionMultiplier : 1) *
            (VoiceEnd > at ? (1 + 0.25 * Math.Min(1, s.BaselineDirectHit + 0.20)) / (1 + 0.25 * s.BaselineDirectHit) : 1);
        private void Proc(PlannerState s, float chance)
        {
            if (Song == BardSong.None || Time >= SongEnd || chance <= 0) return;
            var next = new List<Resource>();
            foreach (var r in Resources)
            {
                if (chance < 1) next.Add(r with { Probability = r.Probability * (1 - chance) });
                next.Add(r with
                {
                    Probability = r.Probability * chance,
                    Repertoire = Song == BardSong.Wanderer ? Math.Min(3, r.Repertoire + 1) : r.Repertoire,
                    Bank = Song == BardSong.Mage ? Math.Min(s.Charges.Maximum * 15, r.Bank + 7.5f) : r.Bank,
                    Soul = s.Level >= 80 ? Math.Min(100, r.Soul + 5) : r.Soul,
                });
            }
            // Two look-ahead GCDs produce only a few exact stochastic branches.
            Resources = next.GroupBy(r => (r.Repertoire, r.Bank, r.Soul))
                .Select(g => g.First() with { Probability = g.Sum(r => r.Probability) }).ToList();
        }
        public void Apply(int action, PlannerState s)
        {
            var multiplier = Multiplier(Time, s);
            switch (action)
            {
                case 0:
                    Damage += Resources.Sum(r => r.Probability * PitchPotency(r.Repertoire)) * s.Aoe.PitchMultiplier * multiplier;
                    Resources = Resources.Select(r => r with { Repertoire = 0 }).ToList(); Ready[0] = Time + 1;
                    break;
                case 1:
                    Damage += s.EmpyrealPotency * multiplier; Ready[1] = Time + 15;
                    if (s.Level >= 68) PendingProc = Time + Math.Max(0.01f, s.EmpyrealEffectDelay);
                    break;
                case 2:
                    Damage += s.ChargePotency * multiplier;
                    Resources = Resources.Select(r => r with { Bank = Math.Max(0, r.Bank - 15) }).ToList();
                    break;
                case 3: Damage += s.SidewinderPotency * multiplier; Ready[3] = Time + 60; break;
                case 4:
                    FinaleEnd = Time + 20; Ready[4] = Time + 110;
                    FinaleMultiplier = 1 + 0.02f * Coda;
                    if (s.TargetLifetime > WindowEnd) Damage += EncoreValue(s, Coda) * Multiplier(WindowEnd, s);
                    Coda = 0;
                    break;
                case 5: VoiceEnd = Time + 20; Ready[5] = Time + 120; break;
                case 6: RagingEnd = Time + 20; Ready[6] = Time + 120; break;
                case 7:
                    // Value of the proc GCDs, not direct damage by Barrage itself.
                    Damage += BarrageValue(s) * Multiplier(WindowEnd, s);
                    Ready[7] = Time + 120; BarrageUsed = true; break;
                case 8:
                    Song = s.NextSongAction switch
                    {
                        ActionCatalog.WanderersMinuet => BardSong.Wanderer,
                        ActionCatalog.MagesBallad => BardSong.Mage,
                        _ => BardSong.Army,
                    };
                    SongChanged = true; SongEnd = Time + 45; NextTick = Time + 3;
                    if (s.Level >= 90 && s.NextSongGrantsNewCoda) Coda = Math.Min(3, Coda + 1);
                    Resources = Resources.Select(r => r with { Repertoire = 0 }).ToList();
                    break;
            }
        }
    }
    public static int PitchPotency(int stacks) => stacks switch { 1 => 100, 2 => 220, >= 3 => 360, _ => 0 };
}
