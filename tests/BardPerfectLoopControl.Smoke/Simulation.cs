using BardPerfectLoop;

internal static class Simulation
{
    public static void Run()
    {
        foreach (var scenario in new[] { (2.47f, false), (2.48f, false), (2.49f, false), (2.50f, false), (2.50f, true) })
        foreach (var downtime in new[] { false, true })
        {
            var (gcd, advanced) = scenario;
            var old = new List<Result>(); var modern = new List<Result>();
            for (var seed = 1; seed <= 10; seed++)
            {
                old.Add(new World(gcd, seed, false, downtime, advanced).Run());
                modern.Add(new World(gcd, seed, true, downtime, advanced).Run());
            }
            var a = old.Average(r => r.Damage); var b = modern.Average(r => r.Damage);
            Console.WriteLine($"SIM gcd={gcd:F2} axis={(advanced ? "369" : "3312")} downtime={downtime} seeds=10 duration=300 baseline={a:F1} v1={b:F1} delta={(b/a-1)*100:F2}% dots={modern.Average(r=>r.DotDamage):F1} EA={modern.Average(r=>r.EaCount):F2} H={modern.Average(r=>r.HCount):F2} PP={modern.Average(r=>r.PpCount):F2} overflow={modern.Average(r=>r.Overflow):F2} min={modern.Min(r=>r.Damage):F1} max={modern.Max(r=>r.Damage):F1}");
        }
        Console.WriteLine("SIM units are expected own-buff-weighted action potency; not game DPS. Baseline uses the old priority with corrected readiness, same GCD evaluator. No auto-attacks, gear scaling, party buffs, damage variance, latency or actual game execution.");
    }

    public static void RunLong()
    {
        foreach (var gcd in new[] { 2.47f, 2.48f, 2.49f, 2.50f })
        foreach (var advanced in new[] { false, true })
        foreach (var seed in new[] { 1, 2 })
        {
            var result = new World(gcd, seed, true, false, advanced, 1200).Run();
            var starts = result.WandererStarts;
            if (starts.Length < 9) throw new Exception("LONG song cycle stalled");
            var intervals = starts.Zip(starts.Skip(1), (a, b) => b - a).ToArray();
            if (intervals.Any(t => t < 119.99f || t > 130)) throw new Exception("LONG unexpected song cycle interval");
            var drift = starts[^1] - starts[0] - (starts.Length - 1) * 120;
            Console.WriteLine($"LONG gcd={gcd:F2} axis={(advanced ? "369" : "3312")} seed={seed} duration=1200 WM={starts.Length} interval={intervals.Min():F3}..{intervals.Max():F3}s cumulativeDrift={drift:F3}s damageUnits={result.Damage:F1}");
        }
        Console.WriteLine("LONG PASS: 16 x 1200s; no early song, invalid weave or stalled cycle. Drift is measured, not eliminated.");
    }

    private sealed record Result(double Damage, double DotDamage, int EaCount, int HCount, int PpCount, int Overflow, float[] WandererStarts);
    private sealed class World
    {
        private readonly float End;
        private readonly float baseGcd;
        private readonly Random random;
        private readonly bool modern, downtime, advanced;
        private readonly int seed;
        private float now, nextGcd, lastGcd, lastAction = -10, nextDotTick = 1.1f;
        private int slots, gcdCount, repertoire, army, soul, overflow;
        private float museEnd, songStart, songEnd, nextSongTick = float.PositiveInfinity, pendingEmpyreal = float.PositiveInfinity;
        private float pendingEmpyrealDamage;
        private BardSong song;
        private readonly HashSet<uint> coda = [];
        private readonly Dictionary<uint, float> ready = [];
        private readonly Dictionary<uint, int> counts = [];
        private float bank = 45, causticEnd, stormEnd, cMult = 1, sMult = 1, rageEnd, voiceEnd, finaleEnd, finaleMult = 1.02f;
        private float barrageEnd, hawkEnd, blastEnd, resonantEnd, encoreEnd;
        private int encoreCoda;
        private double damage, dotDamage;
        private readonly List<string> opener = [];
        private readonly List<string> songTrace = [];
        private readonly List<float> wandererStarts = [];
        private (float At, uint Id, float Multiplier)? pendingDot;
        public World(float gcd, int seed, bool modern, bool downtime, bool advanced, float duration = 300)
        { baseGcd = gcd; random = new(seed); this.seed = seed; this.modern = modern; this.downtime = downtime; this.advanced = advanced; End = duration; }
        private bool TargetAt(float t) => !downtime || t < 78 || t >= 98;
        private float Cd(uint id) => Math.Max(0, ready.GetValueOrDefault(id) - now);
        private float Remaining(float until) => Math.Max(0, until - now);
        private float Multiplier => (rageEnd > now ? 1.15f : 1) * (voiceEnd > now ? 1.1f / 1.05f : 1) * (finaleEnd > now ? finaleMult : 1);
        private int Limit => song == BardSong.Army && army >= 4 ? 1 : 2;
        private GuideSongPlan SongPlan => GuideAxisRules.ResolveSongPlan(
            advanced ? SongPlanMode.Advanced369 : SongPlanMode.Standard3312, baseGcd, 0, 0, 0);
        private float CutRemaining => song switch
        {
            BardSong.Wanderer => SongPlan.WandererCutRemaining,
            BardSong.Mage => SongPlan.MageCutRemaining,
            BardSong.Army => SongPlan.ArmyCutRemaining, _ => 0,
        };
        private uint NextSongId => song switch { BardSong.Wanderer => ActionCatalog.MagesBallad, BardSong.Mage => ActionCatalog.ArmysPaeon, BardSong.Army => ActionCatalog.WanderersMinuet, _ => ActionCatalog.WanderersMinuet };

        public Result Run()
        {
            for (var iteration = 0; now < End && iteration < 20000; iteration++)
            {
                if (!TargetAt(now)) { Advance(Math.Min(98, now + 0.7f)); continue; }
                if (now >= nextGcd - 0.0001f)
                {
                    var g = new GcdState
                    {
                        Gcd = baseGcd, Caustic = Remaining(causticEnd), Storm = Remaining(stormEnd),
                        CausticMultiplier = cMult, StormMultiplier = sMult, SnapshotsKnown = causticEnd > 0 && stormEnd > 0,
                        Multiplier = Multiplier, BuffLeft = new[] { Remaining(rageEnd), Remaining(voiceEnd), Remaining(finaleEnd) }.Where(x=>x>0).DefaultIfEmpty(0).Min(),
                        RagingLeft = Remaining(rageEnd), RagingReady = Cd(ActionCatalog.RagingStrikes),
                        BarrageLeft = Remaining(barrageEnd), HawksEyeLeft = Remaining(hawkEnd), BlastLeft = Remaining(blastEnd),
                        ResonantLeft = Remaining(resonantEnd), EncoreLeft = Remaining(encoreEnd), Soul = soul,
                        Song = song, SongRemaining = Remaining(songEnd), KnownEnd = true, Lifetime = End - now,
                    };
                    ExecuteGcd(GcdPlanner.Select(g).Action);
                    var speed = song == BardSong.Army ? 1 - 0.04f * army : museEnd > now ? 0.88f : 1;
                    lastGcd = now; nextGcd = now + MathF.Floor(baseGcd * speed * 100) / 100;
                    slots = 0; gcdCount++; lastAction = now;
                    continue;
                }
                if (slots >= Limit || now + 0.78f > nextGcd)
                { Advance(nextGcd); continue; }
                if (now < lastAction + 0.70f - 0.0001f)
                { Advance(Math.Min(nextGcd, lastAction + 0.70f)); continue; }
                var p = new PlannerState
                {
                    Gcd = nextGcd - lastGcd, NextGcd = nextGcd - now, Earliest = 0, Slots = Limit - slots, FutureSlots = Limit,
                    Song = song, SongRemaining = Remaining(songEnd), SongSwitchIn = song == BardSong.None ? 0 : SongContinuity.SwitchIn(Remaining(songEnd), CutRemaining),
                    NextSongAction = NextSongId, NextSongReady = Cd(NextSongId), NextNaturalTick = Math.Max(0, nextSongTick - now),
                    Repertoire = repertoire, Soul = soul, PendingRepertoireIn = pendingEmpyreal - now,
                    Charges = new((int)(bank / 15), 3, bank >= 45 ? 0 : 15 - bank % 15, 45 - bank),
                    EmpyrealReady = Cd(ActionCatalog.EmpyrealArrow), PitchReady = Cd(ActionCatalog.PitchPerfect), SidewinderReady = Cd(ActionCatalog.Sidewinder),
                    RagingReady = Cd(ActionCatalog.RagingStrikes), VoiceReady = Cd(ActionCatalog.BattleVoice), FinaleReady = Cd(ActionCatalog.RadiantFinale), BarrageReady = Cd(ActionCatalog.Barrage),
                    RagingLeft = Remaining(rageEnd), VoiceLeft = Remaining(voiceEnd), FinaleLeft = Remaining(finaleEnd), FinaleMultiplier = finaleEnd > now ? finaleMult : 1 + coda.Count * 0.02f,
                    Coda = coda.Count, NextSongGrantsNewCoda = !coda.Contains(NextSongId), BarrageActive = barrageEnd > now, ResonantActive = resonantEnd > now, HawksEye = hawkEnd > now,
                    BurstEarliest = gcdCount < 2 ? nextGcd - now + 0.70f : 0, KnownTargetEnd = true, TargetLifetime = End - now,
                };
                var plan = modern ? WeavePlanner.Plan(p) : Legacy(p);
                if (plan.First is { } first && first.At <= 0)
                {
                    if (slots >= Limit || now + 0.78f > nextGcd) throw new Exception("SIM attempted invalid weave");
                    if (modern && SongContinuity.IsSong(first.ActionId) &&
                        !SongContinuity.CanSend(first.ActionId, p.NextSongAction, p.SongSwitchIn))
                        throw new Exception($"SIM early/stale song: remaining={p.SongRemaining} cutIn={p.SongSwitchIn}");
                    if (modern && SongContinuity.IsSong(first.ActionId))
                        songTrace.Add($"{now:F3}s {song}->{WeavePlanner.Name(first.ActionId)} remaining={p.SongRemaining:F3}s cutIn={p.SongSwitchIn:F3}s");
                    if (first.ActionId == ActionCatalog.WanderersMinuet) wandererStarts.Add(now);
                    ExecuteOgcd(first.ActionId); slots++; lastAction = now;
                }
                else
                {
                    var wake = plan.First is { } future ? now + future.At : nextGcd;
                    wake = Math.Min(wake, nextSongTick);
                    wake = Math.Min(wake, pendingEmpyreal);
                    foreach (var cd in ready.Values) if (cd > now + 0.001f) wake = Math.Min(wake, cd);
                    Advance(Math.Min(nextGcd, Math.Max(now + 0.01f, wake)));
                }
            }
            if (now < End - 0.01f) throw new Exception("SIM did not complete");
            if (modern)
            {
                foreach (var id in new[] { ActionCatalog.EmpyrealArrow, ActionCatalog.HeartbreakShot, ActionCatalog.PitchPerfect, ActionCatalog.RagingStrikes, ActionCatalog.BattleVoice, ActionCatalog.RadiantFinale, ActionCatalog.Barrage, ActionCatalog.Sidewinder })
                    if (counts.GetValueOrDefault(id) == 0) throw new Exception($"SIM missing offense {id}; opener={string.Join(",",opener)}");
                if (!opener.Any(x => x.Contains("碎心箭"))) throw new Exception("SIM opener missing Heartbreak");
            }
            if (End == 300 && baseGcd == 2.49f && !downtime && modern && seed == 1)
                Console.WriteLine("OPENER " + string.Join(" | ", opener));
            if (End == 300 && baseGcd == 2.48f && !downtime && modern && seed == 1)
                Console.WriteLine("SONG_TRACE_248 " + string.Join(" | ", songTrace));
            return new(damage, dotDamage, counts.GetValueOrDefault(ActionCatalog.EmpyrealArrow), counts.GetValueOrDefault(ActionCatalog.HeartbreakShot), counts.GetValueOrDefault(ActionCatalog.PitchPerfect), overflow, wandererStarts.ToArray());
        }
        private WeavePlan Legacy(PlannerState p)
        {
            uint id = 0;
            if (song == BardSong.None || p.SongSwitchIn <= 0.65f)
                id = song == BardSong.Wanderer && repertoire > 0 ? ActionCatalog.PitchPerfect : p.NextSongReady == 0 ? p.NextSongAction : 0;
            if (id == 0 && song == BardSong.Wanderer && (repertoire >= 3 || repertoire >= 2 && p.EmpyrealReady < 2)) id = ActionCatalog.PitchPerfect;
            if (id == 0 && p.EmpyrealReady == 0) id = ActionCatalog.EmpyrealArrow;
            if (id == 0 && bank >= 15) id = ActionCatalog.HeartbreakShot;
            if (id == 0 && song != BardSong.None && p.RagingReady <= 2.2f)
            {
                if (coda.Count > 0 && p.FinaleReady == 0) id = ActionCatalog.RadiantFinale;
                else if (p.VoiceReady == 0) id = ActionCatalog.BattleVoice;
                else if (p.RagingReady == 0 && p.NextGcd <= p.Gcd * 0.52f) id = ActionCatalog.RagingStrikes;
            }
            if (id == 0 && p.BarrageReady == 0 && rageEnd > now && resonantEnd <= now) id = ActionCatalog.Barrage;
            if (id == 0 && p.SidewinderReady == 0 && (rageEnd > now || p.RagingReady > 30)) id = ActionCatalog.Sidewinder;
            return id == 0 ? WeavePlan.Empty("baseline wait") : new([new(id, 0, repertoire)], 0, "baseline");
        }
        private void Advance(float to)
        {
            to = Math.Min(End, to);
            while (Math.Min(Math.Min(Math.Min(nextSongTick, nextDotTick), pendingEmpyreal), pendingDot?.At ?? float.PositiveInfinity) <= to)
            {
                var eventTime = Math.Min(Math.Min(Math.Min(nextSongTick, nextDotTick), pendingEmpyreal), pendingDot?.At ?? float.PositiveInfinity);
                bank = Math.Min(45, bank + eventTime - now); now = eventTime;
                if (pendingDot is { } dot && dot.At <= now)
                {
                    if (TargetAt(now) && (dot.Id == ActionCatalog.CausticBite || dot.Id == ActionCatalog.IronJaws && causticEnd > now)) { causticEnd = now + 45; cMult = dot.Multiplier; }
                    if (TargetAt(now) && (dot.Id == ActionCatalog.Stormbite || dot.Id == ActionCatalog.IronJaws && stormEnd > now)) { stormEnd = now + 45; sMult = dot.Multiplier; }
                    pendingDot = null;
                }
                else if (pendingEmpyreal <= nextSongTick && pendingEmpyreal <= nextDotTick)
                {
                    if (TargetAt(now)) { damage += pendingEmpyrealDamage; if (songEnd > now) Proc(); }
                    pendingEmpyreal = float.PositiveInfinity;
                }
                else if (nextSongTick <= nextDotTick)
                {
                    if (songEnd - now >= 2.99f && random.NextDouble() < 0.8) Proc();
                    nextSongTick += 3;
                    if (songEnd - nextSongTick < 2.99f) nextSongTick = float.PositiveInfinity;
                }
                else
                {
                    if (TargetAt(now))
                    {
                        var dots = (causticEnd > now ? 20 * cMult : 0) + (stormEnd > now ? 25 * sMult : 0);
                        dotDamage += dots; damage += dots;
                    }
                    nextDotTick += 3;
                }
            }
            bank = Math.Min(45, bank + to - now); now = to;
            if (now >= songEnd) { song = BardSong.None; repertoire = 0; }
        }
        private void Proc()
        {
            if (song == BardSong.Wanderer) { if (repertoire == 3) overflow++; repertoire = Math.Min(3, repertoire + 1); }
            if (song == BardSong.Mage) bank = Math.Min(45, bank + 7.5f);
            if (song == BardSong.Army) army = Math.Min(4, army + 1);
            if (song != BardSong.None) soul = Math.Min(100, soul + 5);
        }
        private void Count(uint id)
        {
            counts[id] = counts.GetValueOrDefault(id) + 1;
            if (now < 25) opener.Add($"{now:F2} {WeavePlanner.Name(id)}");
        }
        private void ExecuteOgcd(uint id)
        {
            Count(id);
            switch (id)
            {
                case ActionCatalog.PitchPerfect: damage += WeavePlanner.PitchPotency(repertoire) * Multiplier; repertoire = 0; ready[id] = now + 1; break;
                case ActionCatalog.EmpyrealArrow: pendingEmpyrealDamage = 260 * Multiplier; ready[id] = now + 15; pendingEmpyreal = now + 0.65f; break;
                case ActionCatalog.HeartbreakShot: if (bank < 14.999f) throw new Exception("SIM spent missing charge"); bank -= 15; damage += 180 * Multiplier; break;
                case ActionCatalog.Sidewinder: damage += 400 * Multiplier; ready[id] = now + 60; break;
                case ActionCatalog.RagingStrikes: rageEnd = now + 20; ready[id] = now + 120; break;
                case ActionCatalog.BattleVoice: voiceEnd = now + 20; ready[id] = now + 120; break;
                case ActionCatalog.RadiantFinale: encoreCoda = coda.Count; finaleMult = 1 + encoreCoda * 0.02f; coda.Clear(); finaleEnd = now + 20; encoreEnd = now + 30; ready[id] = now + 110; break;
                case ActionCatalog.Barrage: barrageEnd = now + 10; resonantEnd = now + 30; ready[id] = now + 120; break;
                default:
                    if (song == BardSong.Army && army >= 4) museEnd = now + 10;
                    song = id == ActionCatalog.WanderersMinuet ? BardSong.Wanderer : id == ActionCatalog.MagesBallad ? BardSong.Mage : BardSong.Army;
                    coda.Add(id); ready[id] = now + 120; songStart = now; songEnd = now + 45; nextSongTick = now + 3; repertoire = 0; army = 0; break;
            }
        }
        private void ExecuteGcd(uint id)
        {
            Count(id);
            var potency = 0f;
            switch (id)
            {
                case ActionCatalog.Stormbite: potency = 100; pendingDot = (now + 0.65f, id, Multiplier); break;
                case ActionCatalog.CausticBite: potency = 150; pendingDot = (now + 0.65f, id, Multiplier); break;
                case ActionCatalog.IronJaws: potency = 100; pendingDot = (now + 0.65f, id, Multiplier); break;
                case ActionCatalog.RefulgentArrow: potency = barrageEnd > now ? 840 : 280; barrageEnd = 0; hawkEnd = 0; break;
                case ActionCatalog.ApexArrow: potency = 700 * soul / 100f; if (soul >= 80) blastEnd = now + 10; soul = 0; break;
                case ActionCatalog.BlastArrow: potency = 600; blastEnd = 0; break;
                case ActionCatalog.ResonantArrow: potency = 640; resonantEnd = 0; break;
                case ActionCatalog.RadiantEncore: potency = encoreCoda >= 3 ? 1100 : encoreCoda == 2 ? 800 : 700; encoreEnd = 0; break;
                default: potency = 220; break;
            }
            damage += potency * Multiplier;
            if (id is ActionCatalog.BurstShot or ActionCatalog.CausticBite or ActionCatalog.Stormbite or ActionCatalog.IronJaws)
                if (random.NextDouble() < 0.35) hawkEnd = now + 30;
        }
    }
}
