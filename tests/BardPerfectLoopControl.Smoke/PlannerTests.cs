using BardPerfectLoop;
using System.Diagnostics;

internal static class PlannerTests
{
    public static void Run(Action<bool, string> check)
    {
        var isolated = new PlannerState
        {
            NextNaturalTick = float.PositiveInfinity, NextSongAction = 0,
            EmpyrealReady = 0, Repertoire = 2,
            Excluded = new HashSet<uint> { ActionCatalog.Sidewinder, ActionCatalog.RadiantFinale, ActionCatalog.BattleVoice, ActionCatalog.RagingStrikes, ActionCatalog.Barrage },
            Charges = new(0, 3, 14, 44),
        };
        void Expect(PlannerState s, uint first, string name)
        {
            var plan = WeavePlanner.Plan(s);
            Console.WriteLine($"{name}: {plan.Reason} score={plan.Score:F2}");
            check(plan.First?.ActionId == first, name);
        }
        Expect(isolated, ActionCatalog.EmpyrealArrow, "two stacks EA then PP, no intervening proc");
        check(WeavePlanner.Plan(isolated).Actions.ElementAtOrDefault(1).ActionId == ActionCatalog.PitchPerfect, "EA into third stack reserves second slot for PP");
        Expect(isolated with { Repertoire = 3 }, ActionCatalog.PitchPerfect, "three stacks PP before EA");
        Expect(isolated with { NextNaturalTick = 1.0f, NaturalProcProbability = 1 }, ActionCatalog.PitchPerfect, "two stacks clear before certain intervening natural proc");
        Expect(isolated with { NextNaturalTick = 1.0f, NaturalProcProbability = 0 }, ActionCatalog.EmpyrealArrow, "no natural proc does not force two-stack dump");
        Expect(isolated with { Repertoire = 0, EmpyrealReady = 10, Charges = new(3, 3, 0, 0) }, ActionCatalog.HeartbreakShot, "EA cooling does not starve Heartbreak");
        Expect(isolated with { Song = BardSong.Mage, Repertoire = 0, Charges = new(3, 3, 0, 0) }, ActionCatalog.HeartbreakShot, "Mage full charges spends before EA reduction");
        var wait = WeavePlanner.Plan(isolated with { Repertoire = 0, EmpyrealReady = 0.76f, Earliest = 0.70f, Charges = new(1, 3, 14, 29) });
        Console.WriteLine($"reserve: {wait.Reason}");
        check(wait.First?.ActionId == ActionCatalog.EmpyrealArrow && Math.Abs(wait.First.Value.At - 0.76f) < 0.002f, "reserve 0.06s for EA instead of filling early");
        check(WeavePlanner.Plan(isolated with { TargetAvailable = false }).Actions.Length == 0, "no target no action");
        check(WeavePlanner.Plan(isolated with { KnownTargetEnd = true, TargetLifetime = 0.1f }).Actions.Length == 0, "no actions after known target end");
        foreach (var state in new[] { isolated, isolated with { Repertoire = 3 }, isolated with { NextNaturalTick = 1.0f }, isolated with { Slots = 1, FutureSlots = 1, Gcd = 2.09f, NextGcd = 2.09f } })
        {
            var plan = WeavePlanner.Plan(state);
            var last = float.NegativeInfinity;
            var counts = new Dictionary<int, int>();
            foreach (var action in plan.Actions)
            {
                var cycle = action.At < state.NextGcd ? 0 : 1 + (int)((action.At - state.NextGcd) / state.Gcd);
                var end = state.NextGcd + cycle * state.Gcd;
                counts[cycle] = counts.GetValueOrDefault(cycle) + 1;
                check(counts[cycle] <= (cycle == 0 ? state.Slots : state.FutureSlots), "planned slot budget");
                check(CombatTiming.Fits(action.At, end, state.Lock, state.Margin), "planned no clip");
                check(action.At - last >= 0.699f, "planned minimum interval");
                last = action.At;
            }
        }
        var charge = ChargeState.FromNative(1, 3, true, 45, 20);
        check(charge.Available == 1 && charge.UntilNext == 10 && charge.Bank == 20, "native charges not full recast readiness");
        check(ChargeState.FromNative(2, 2, true, 45, 30).UntilNext == 0, "synced charge maximum authoritative");
        var timeline = new ActionTimeline();
        check(!timeline.ActiveCycle && timeline.Ogcds == 2, "joining mid cycle cannot assume free slots");
        timeline.RecordExecuted(true, 0); timeline.RecordExecuted(false, 0.7); timeline.RecordExecuted(false, 1.4);
        check(timeline.Ogcds == 2, "manual and automatic actions share budget");
        timeline.RecordExecuted(true, 2.49);
        check(timeline.Ogcds == 0 && timeline.GcdCount == 2, "manual GCD resets cycle");
        check(Math.Abs(DotEvaluator.ExpectedTicks(5.8f) - 5.8 / 3) < 0.0001, "unknown DoT phase is expectation not ceil");
        check(DotEvaluator.Refresh(2, 2, 2.49f, 0, 100, 1, 1, 1, 0, 241).Emergency, "DoT emergency before next GCD");
        check(!DotEvaluator.Refresh(30, 30, 2.49f, 0, 100, 1.15f, 1.15f, 1, 0, 241, snapshotsKnown: true).Refresh, "do not overwrite stronger snapshot early");
        check(!DotEvaluator.Refresh(2, 2, 2.49f, 0, 1, 1, 1, 1, 0, 241).Refresh, "dying target no futile refresh");
        var gcd = new GcdState { Caustic = 20, Storm = 20 };
        check(GcdPlanner.Select(gcd with { Level = 50, Caustic = 0, Storm = 0 }).Action == ActionCatalog.Windbite, "50 uses old DoT names");
        check(GcdPlanner.Select(gcd with { Caustic = 2, Storm = 2, BarrageLeft = 8 }).Action == ActionCatalog.IronJaws, "DoTs not starved behind burst procs");
        check(GcdPlanner.Select(gcd with { Caustic = 3.1f, Storm = 3.1f }).Action == ActionCatalog.IronJaws, "DoT refresh reserves effect application delay");
        check(GcdPlanner.Select(gcd with { Caustic = 0.3f, Storm = 15 }).Action == ActionCatalog.CausticBite, "too-late DoT reapplied rather than assuming Iron Jaws can save it");
        check(GcdPlanner.Select(gcd with { EncoreLeft = 1, RagingLeft = 0 }).Action == ActionCatalog.RadiantEncore, "Encore rescued outside buffs");
        check(GcdPlanner.Select(gcd with { BlastLeft = 1 }).Action == ActionCatalog.BlastArrow, "Blast rescued");
        check(GcdPlanner.Select(gcd with { ResonantLeft = 1 }).Action == ActionCatalog.ResonantArrow, "Resonant rescued");
        check(GcdPlanner.Select(gcd with { BarrageLeft = 1 }).Action == ActionCatalog.RefulgentArrow, "Barrage rescued");
        check(GcdPlanner.Select(gcd with { Level = 50, HawksEyeLeft = 5 }).Action == ActionCatalog.StraightShot, "50 proc uses Straight Shot");
        check(GcdPlanner.Select(gcd with { Soul = 100 }).Action == ActionCatalog.ApexArrow, "Soul cap considered outside Mage");
        var ledger = new DotSnapshotLedger(); ledger.Record(1, ActionCatalog.IronJaws, 1.15f);
        check(ledger.Read(1).Known && !ledger.Read(2).Known, "DoT snapshots isolated by target");
        ledger.Observe(2, 12, 14, 0); ledger.Stage(2, ActionCatalog.IronJaws, 1.15f, 0.1);
        check(!ledger.Read(2).Known, "accepted DoT does not claim server status application");
        ledger.Observe(2, 44.9f, 13.9f, 0.2); ledger.Observe(2, 44.8f, 44.9f, 0.3);
        check(ledger.Read(2).Known, "asynchronous dual-DoT updates confirm snapshot separately");
        ledger.Stage(3, ActionCatalog.IronJaws, 1.15f, 0); ledger.Observe(3, 0, 0, 3);
        check(!ledger.Read(3).Known, "unapplied DoT cannot acquire a fictitious buff snapshot");
        var burst = new PlannerState { Repertoire = 0, NextSongAction = 0, NextNaturalTick = float.PositiveInfinity, EmpyrealReady = 8,
            RagingReady = 0, VoiceReady = 0, FinaleReady = 0, BarrageReady = 0, Charges = new(3, 3, 0, 0) };
        var burstPlan = WeavePlanner.Plan(burst);
        check(burstPlan.Actions.Length >= 2 && burstPlan.Actions[0].ActionId == ActionCatalog.RadiantFinale && burstPlan.Actions[1].ActionId == ActionCatalog.BattleVoice, "burst buffs protected from full Heartbreak bank");
        Expect(burst with { Excluded = new HashSet<uint> { ActionCatalog.RadiantFinale } }, ActionCatalog.BattleVoice, "rejected Finale does not block all other buffs");
        Expect(burst with { Coda = 2, Song = BardSong.Army, SongSwitchIn = 0, NextSongReady = 0, NextSongAction = ActionCatalog.WanderersMinuet, NextSongGrantsNewCoda = true }, ActionCatalog.WanderersMinuet, "collect imminent third coda before Finale");
        var army0 = WeavePlanner.Plan(isolated with { Song = BardSong.Army, Repertoire = 0 });
        var army4 = WeavePlanner.Plan(isolated with { Song = BardSong.Army, Repertoire = 4 });
        check(Math.Abs(army0.Score - army4.Score) < 0.001, "Army haste stacks not valued as spendable PP");
        check(GcdPlanner.Select(gcd with { KnownEnd = true, Lifetime = 180, EncoreLeft = 29, RagingReady = 0 }).Action != ActionCatalog.RadiantEncore, "known long encounter still waits for imminent buff");

        var rng = new Random(104729);
        for (var i = 0; i < 200; i++)
        {
            var level = new[] { 50, 54, 68, 80, 84, 92, 100 }[i % 7];
            var length = 1.9f + (float)rng.NextDouble() * 0.7f;
            var state = new PlannerState { Level = level, Gcd = length, NextGcd = length, Earliest = 0.70f, Slots = i % 3,
                FutureSlots = i % 2 + 1, Repertoire = i % 4, Song = (BardSong)(i % 4), SongRemaining = 3 + i % 42,
                NextNaturalTick = 0.01f + (float)rng.NextDouble() * 3, EmpyrealReady = (float)rng.NextDouble() * 15,
                Charges = new(i % 3, level >= 84 ? 3 : 2, 1 + i % 14, 30), Coda = i % 4,
                RagingReady = i % 3 == 0 ? 0 : 60, VoiceReady = 0, FinaleReady = 0, BarrageReady = 0, SidewinderReady = 0 };
            var plan = WeavePlanner.Plan(state);
            var buckets = new Dictionary<int, int>();
            foreach (var action in plan.Actions)
            {
                var window = action.At < state.NextGcd ? 0 : 1 + (int)((action.At - state.NextGcd) / state.Gcd);
                buckets[window] = buckets.GetValueOrDefault(window) + 1;
                check(ActionCatalog.MinimumLevel(action.ActionId) <= level, "random plan never chooses unlearned level");
                check(buckets[window] <= (window == 0 ? state.Slots : state.FutureSlots), "random plan bounded weave count");
                check(CombatTiming.Fits(action.At, state.NextGcd + state.Gcd * window, state.Lock, state.Margin), "random plan never clips");
            }
            var gstate = gcd with { Level = level, Caustic = i % 46, Storm = (i * 7) % 46, Soul = i % 101, EncoreLeft = i % 31, BlastLeft = i % 11, ResonantLeft = i % 31, BarrageLeft = i % 11 };
            check(ActionCatalog.MinimumLevel(GcdPlanner.Select(gstate).Action) <= level, "random GCD level filtering");
        }
        var timings = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            var timer = Stopwatch.StartNew();
            WeavePlanner.Plan(new PlannerState { Repertoire = i % 4, EmpyrealReady = 0, RagingReady = 0, VoiceReady = 0, FinaleReady = 0, BarrageReady = 0, SidewinderReady = 0 });
            timings.Add(timer.Elapsed.TotalMilliseconds);
        }
        timings.Sort();
        Console.WriteLine($"PERF 30 plans median={timings[15]:F2}ms p95={timings[28]:F2}ms max={timings[^1]:F2}ms");
    }
}
