using BardPerfectLoop;
using System.Numerics;

internal static class EncounterTests
{
    public static void Run(Action<bool, string> check)
    {
        var primary = new EnemyPoint(1, new(0, 8), 1, true, false);
        var pack = new[] { primary, new EnemyPoint(2, new(1, 9), 1, true, false), new EnemyPoint(3, new(-1, 8), 1, true, false) };
        var coverage = AoeGeometry.Measure(Vector2.Zero, primary, pack);
        check(coverage.Circle5 == 3 && coverage.Circle8 == 3 && coverage.Cone12 == 3 && coverage.Line25 == 3, "cluster geometry counts actual hits");
        var protectedPack = pack.Append(new EnemyPoint(4, new(2, 8), 1, true, true)).ToArray();
        var blocked = AoeGeometry.Measure(Vector2.Zero, primary, protectedPack);
        check(blocked.Circle5 == 0 && blocked.Circle8 == 0 && blocked.Cone12 == 0, "sensitive target inside AOE blocks collateral damage");
        check(!blocked.Allows(ActionCatalog.PitchPerfect) && blocked.Allows(ActionCatalog.EmpyrealArrow), "inherent Pitch Perfect AOE is protected too");
        var pullRisk = pack.Append(new EnemyPoint(4, new(2, 8), 1, false, false)).ToArray();
        check(AoeGeometry.Measure(Vector2.Zero, primary, pullRisk).Circle8 == 0, "do not auto pull unengaged enemies");
        check(AoeGeometry.Measure(Vector2.Zero, primary, pack.Append(new EnemyPoint(5, new(30, 30), 1, true, false)).ToArray()).Circle8 == 3, "arena-wide enemy count is not hit count");
        check(!AoeGeometry.Hits(AoeShape.Cone12, Vector2.Zero, primary.Position, new(6, new(0, -8), 1, true, false)), "enemy behind player excluded from cone");
        check(!AoeGeometry.Hits(AoeShape.Line25, Vector2.Zero, primary.Position, new(7, new(8, 8), 1, true, false)), "off-axis enemy excluded from line");
        var distant = primary with { Position = new(0, 24) };
        check(AoeGeometry.Measure(Vector2.Zero, distant, pack.Skip(1).Append(distant).ToArray()).Cone12 == 0, "nearby adds cannot make an out-of-range primary a legal cone target");
        check(ActionCatalog.MinimumLevel(ActionCatalog.Shadowbite) == 72 && ActionCatalog.MinimumLevel(ActionCatalog.RainOfDeath) == 45, "70 sync excludes Shadowbite but includes Rain");
        var s = new GcdState { Level = 70, Caustic = 30, Storm = 30, Lifetime = 10, Aoe = coverage };
        check(GcdPlanner.Select(s).Action == ActionCatalog.QuickNock, "level 70 clustered adds use Quick Nock");
        check(GcdPlanner.Select(s with { Excluded = new HashSet<uint> { ActionCatalog.QuickNock } }).Action == ActionCatalog.HeavyShot, "rejected cone GCD falls back without stalling");
        check(GcdPlanner.Select(s with { HawksEyeLeft = 20 }).Action == ActionCatalog.QuickNock, "three-target Quick Nock beats unbuffed Refulgent");
        check(GcdPlanner.Select(s with { BarrageLeft = 8 }).Action == ActionCatalog.RefulgentArrow, "level 70 Barrage stays Refulgent even in AOE");
        check(GcdPlanner.Select(s with { Level = 100, HawksEyeLeft = 20 }).Action == ActionCatalog.Shadowbite, "level 100 proc uses Shadowbite when profitable");
        check(GcdPlanner.Select(s with { Level = 100 }).Action == ActionCatalog.Ladonsbite, "level 100 filler uses Ladonsbite");
        check(GcdPlanner.Select(s with { Caustic = 0, Storm = 0, Lifetime = 4 }).Action == ActionCatalog.QuickNock, "short-lived pack does not waste GCD on long DOTs");
        check(GcdPlanner.Select(s with { Aoe = AoeCoverage.Single, Caustic = 0, Storm = 0, Lifetime = 60 }).Action == ActionCatalog.Stormbite, "long-lived single target still receives DOTs");
        var p = new PlannerState { Level = 70, Aoe = coverage, Charges = new(2, 2, 0, 0), EmpyrealReady = 10, NextNaturalTick = float.PositiveInfinity };
        check(p.ChargeAction == ActionCatalog.RainOfDeath && p.ChargePotency == 300, "Rain replaces shared Bloodletter resource by total potency");
        check((p with { Excluded = new HashSet<uint> { ActionCatalog.RainOfDeath } }).ChargeAction == ActionCatalog.Bloodletter, "rejected Rain falls back to usable Bloodletter");
        check(WeavePlanner.Plan(p).Actions.Any(a => a.ActionId == ActionCatalog.RainOfDeath), "joint planner actually schedules Rain");
        var held = WeavePlanner.Plan(p with { RagingReady = 0, VoiceReady = 0, BarrageReady = 0, HoldBurstUntil = 30 });
        check(held.Actions.All(a => a.ActionId is not (ActionCatalog.RagingStrikes or ActionCatalog.BattleVoice or ActionCatalog.Barrage)), "hold gate excludes long CDs across lookahead");
        check(held.Actions.Any(a => a.ActionId == ActionCatalog.RainOfDeath), "hold gate does not starve short-CD AOE");

        var encounter = new EncounterPlanner();
        var options = new UwuOptions(18, 1, 30, 0, true);
        EncounterActor Boss(uint id, bool targetable = true, uint cast = 0, float castElapsed = 0) => new(id, id, targetable, 10000, 10000, cast, castElapsed);
        check(encounter.Update(500, 1, true, [Boss(EncounterPlanner.Titan)], options).Phase == UwuPhase.Unknown, "UWU detection restricted to territory 777");
        var phase = encounter.Update(500, 777, true, [Boss(EncounterPlanner.Titan)], options);
        check(phase.Phase == UwuPhase.Titan && phase.HoldBurst && phase.BurstIn >= 30, "Titan first landing holds burst for first jump");
        check(encounter.Update(519, 777, true, [Boss(EncounterPlanner.Titan)], options).HoldBurst, "late Titan initial window holds cooldowns");
        encounter.Update(523, 777, true, [Boss(EncounterPlanner.Titan, false)], options);
        phase = encounter.Update(530.5, 777, true, [Boss(EncounterPlanner.Titan)], options);
        check(phase.HoldBurst && phase.BurstIn <= 1.01, "actual landing supersedes old disappearance deadline");
        phase = encounter.Update(532, 777, true, [Boss(EncounterPlanner.Titan)], options);
        check(!phase.HoldBurst && phase.UptimeRemaining > 40, "Titan burst releases after return");
        encounter.Reset();
        encounter.Update(800, 777, true, [Boss(EncounterPlanner.Ultima)], options);
        phase = encounter.Update(820.5, 777, true, [Boss(EncounterPlanner.Ultima, true, 11126)], options);
        check(phase.HoldBurst && Math.Abs(phase.UptimeRemaining - 7.4f) < .1, "Predation cast anchors coming downtime");
        encounter.Update(828, 777, true, [Boss(EncounterPlanner.Ultima, false)], options);
        encounter.Update(848, 777, true, [Boss(EncounterPlanner.Ultima)], options);
        check(!encounter.Update(850, 777, true, [Boss(EncounterPlanner.Ultima)], options).HoldBurst, "Predation return resumes burst from actual target state");
        encounter.Update(900, 777, true, [Boss(EncounterPlanner.Ultima, true, 11596)], options);
        encounter.Update(908, 777, true, [Boss(EncounterPlanner.Ultima, false)], options);
        encounter.Update(912.3, 777, true, [Boss(EncounterPlanner.Ultima)], options);
        check(!encounter.Update(914, 777, true, [Boss(EncounterPlanner.Ultima)], options).HoldBurst, "Annihilation is not treated as a whole untargetable phase");
        check(encounter.Update(915, 777, true, [Boss(EncounterPlanner.Garuda)], options).Phase == UwuPhase.Ultima, "primal reappearance does not regress encounter phase");
        encounter.Reset();
        encounter.Update(0, 777, true, [Boss(EncounterPlanner.Titan)], options with { TitanDelay = 0 });
        check(!encounter.Update(26, 777, true, [Boss(EncounterPlanner.Titan)], options with { TitanDelay = 0 }).PredictedEnd, "missed forecast expires instead of stalling indefinitely");
        encounter.Reset();
        phase = encounter.Update(0, 777, true, [Boss(EncounterPlanner.Ifrit), Boss(EncounterPlanner.Nail)], options);
        check(phase.HoldBurst, "Ifrit nails gate long buffs by default");
        check(EncounterPlanner.Sensitive(EncounterPlanner.Spiny) && EncounterPlanner.Sensitive(EncounterPlanner.Nail), "mechanic-sensitive objects recognized by ID");

        check(ConsumableRules.Classify(846, 49, 2, 30) == ConsumableKind.DexterityPotion, "DEX pot classified from effect, not translated name");
        check(ConsumableRules.Classify(846, 49, 1, 30) is null, "STR pot excluded for Bard");
        check(ConsumableRules.Classify(20086, 49, 2, 30) is null, "unrelated item data 49 does not identify potion");
        check(ConsumableRules.Classify(844, 48, 27, 1800) == ConsumableKind.Food, "food action identified");
        check(!ConsumableRules.Food(true, true, false, true, true, 0, 0, 10001, 300).Use, "food never consumed in combat");
        check(!ConsumableRules.Food(true, true, true, false, true, 0, 0, 10001, 300).Use, "shadow mode never consumes food");
        check(!ConsumableRules.Food(true, true, false, false, true, 100, 10002, 10001, 300).Use, "do not overwrite different active food");
        check(ConsumableRules.Food(true, true, false, false, true, 100, 10001, 10001, 300).Use, "renew selected same food near expiry");
        ConsumableDecision Pot(double elapsed, float cd = 0, bool hold = false, bool shadow = false, bool available = true) =>
            ConsumableRules.Potion(true, true, shadow, true, available, elapsed, 1, 4, 270, cd, 0, true, hold, 60, 18);
        check(!Pot(269.9).Use && Pot(270).Use, "4:30 is the second potion planned boundary");
        check(!Pot(270, 4).Use && Pot(274).Use, "late first potion waits for real recast even after 4:30");
        check(!Pot(270, hold: true).Use && Pot(310).Use, "downtime delays due potion until viable return");
        check(!Pot(270, shadow: true).Use && !Pot(270, available: false).Use, "shadow/missing selection never consumes items");
        var opening = new OpeningPotionGate();
        check(!opening.ShouldBlock(0, true, false, false), "opening gate inactive before pull");
        opening.Begin(100);
        check(opening.ShouldBlock(100, true, false, false), "eligible pull potion takes priority before first GCD");
        check(opening.ShouldBlock(102.99, true, false, false), "opening waits briefly for existing native action lock");
        check(!opening.ShouldBlock(103, true, false, false), "unsubmitted opening request cannot stall indefinitely");
        check(!opening.ShouldBlock(104, true, false, false), "opening gate does not reopen later in same pull");
        opening.Begin(200);
        check(opening.ShouldBlock(201, true, true, false), "submitted opening potion blocks damage pending confirmation");
        check(opening.ShouldBlock(204, false, true, false), "outstanding item is not released by opening attempt deadline");
        check(!opening.ShouldBlock(204.1, false, false, true), "confirmed potion releases opening gate");
        foreach (var decision in new[] {
            ConsumableRules.Potion(false, true, false, true, true, 0, 0, 0, 270, 0, 0, true, false, 60, 18),
            ConsumableRules.Potion(true, true, true, true, true, 0, 0, 0, 270, 0, 0, true, false, 60, 18),
            ConsumableRules.Potion(true, true, false, true, false, 0, 0, 0, 270, 0, 0, true, false, 60, 18),
            ConsumableRules.Potion(true, true, false, true, true, 0, 0, 0, 270, 30, 0, true, false, 60, 18),
            ConsumableRules.Potion(true, true, false, true, true, 0, 0, 0, 270, 0, 0, true, true, 60, 18),
            ConsumableRules.Potion(true, true, false, true, true, 0, 0, 0, 270, 0, 0, false, false, 60, 18),
        })
        {
            opening.Begin(300);
            check(!opening.ShouldBlock(300, decision.Use, false, false), $"ineligible opening does not block: {decision.Reason}");
        }
        opening.Begin(400);
        check(!opening.ShouldBlock(400, true, false, true), "existing prepull medicine does not cause another potion");
        check(ConsumableRules.BlocksActions(true, true, true), "client execution alone does not release opening damage gate");
        check(!ConsumableRules.BlocksActions(true, true, false), "ordinary confirmed execution retains previous nonblocking behavior");
        check(!ConsumableRules.BlocksActions(false, false, true), "cleared pending item does not block actions");
        check(!ConsumableRules.Confirmed(false, true, true, true), "quantity decrease alone cannot release first burst before buff");
        check(ConsumableRules.Confirmed(true, false, false, true), "actual opening buff is sufficient confirmation");
        check(ConsumableRules.Confirmed(false, true, true, false), "ordinary items retain inventory confirmation");
        check(!ConsumableRules.Confirmed(false, false, true, false), "unloaded inventory never confirms an item");
        check(ConsumableRules.Potion(true, true, false, true, true, 0, 0, double.NegativeInfinity, 270, 0, 0, true, false, 60, 18).Use,
            "first potion is eligible immediately at combat entry");
        check(!ConsumableRules.Potion(true, true, false, false, true, 0, 0, double.NegativeInfinity, 270, 0, 0, true, false, 60, 18).Use,
            "arming outside combat does not consume medicine or pull boss");
        var timeline = new ActionTimeline(); timeline.RecordExecuted(true, 0); timeline.RecordExecuted(false, .7); timeline.ReserveRemainingWeaves();
        check(timeline.Ogcds == 2, "potion reserves remaining weave slots");
        timeline.RecordExecuted(true, 2.5);
        check(timeline.Ogcds == 0 && timeline.LastGcdAt == 2.5, "next GCD restores normal weave budget");
        check(Math.Abs(DamageFactors.MainStatRatio(70, 2000, 2200) - 916f / 831) < .00001, "70 main-stat potion factor uses integer damage tiers");
        check(DamageFactors.MainStatRatio(70, 0, 2200) == 1 && DamageFactors.MainStatRatio(71, 2000, 2200) == 1, "unknown starting stat/level does not invent potion snapshot");
        var random = new Random(7100);
        foreach (var level in new[] { 50, 70, 100 })
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var aoe = new AoeCoverage(random.Next(0, 6), random.Next(0, 6), random.Next(0, 6), random.Next(0, 6));
            var state = s with { Level = level, Aoe = aoe, BarrageLeft = random.Next(0, 10), HawksEyeLeft = random.Next(0, 30),
                EncoreLeft = random.Next(0, 30), BlastLeft = random.Next(0, 10), ResonantLeft = random.Next(0, 30), Soul = random.Next(0, 101),
                Caustic = random.Next(0, 46), Storm = random.Next(0, 46), Lifetime = random.Next(1, 90) };
            var action = GcdPlanner.Select(state).Action;
            check(action == 0 || ActionCatalog.MinimumLevel(action) <= level, "random sync fixture never emits an unlearned GCD");
            check(aoe.Allows(action), "random geometry fixture never selects prohibited GCD splash");
            var plan = WeavePlanner.Plan(p with { Level = level, Aoe = aoe, Repertoire = random.Next(0, 4), HoldBurstUntil = 30,
                RagingReady = 0, VoiceReady = 0, FinaleReady = 0, BarrageReady = 0 });
            check(plan.Actions.All(a => ActionCatalog.MinimumLevel(a.ActionId) <= level && aoe.Allows(a.ActionId)), "random weave fixture obeys level and collateral constraints");
            check(plan.Actions.All(a => a.ActionId is not (ActionCatalog.RagingStrikes or ActionCatalog.BattleVoice or ActionCatalog.RadiantFinale or ActionCatalog.Barrage)), "random delayed-burst fixture never bypasses hold gate");
        }
        Console.WriteLine("UWU/AOE/consumable boundary tests passed.");
    }
}
