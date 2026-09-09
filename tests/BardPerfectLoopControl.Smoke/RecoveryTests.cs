using BardPerfectLoop;

internal static class RecoveryTests
{
    public static void Run(Action<bool, string> check)
    {
        var life = new RecoveryLifecycle();
        check(life.Update(false, false, false, 1, true) == RecoveryMode.Active, "normal prepull remains normal");
        check(life.Update(true, true, true, 1, true) == RecoveryMode.NewDeath && life.Waiting, "death suspends rather than stops");
        check(life.Update(true, true, true, 1, true) == RecoveryMode.Dead, "death cleanup runs only once");
        check(life.Update(false, true, true, 1, true) == RecoveryMode.Waiting, "retained target on revival cannot resume");
        for (var i = 0; i < 300; i++)
            check(life.Update(false, i % 2 == 0, true, 1, true) == RecoveryMode.Waiting && life.Waiting,
                "combat and remembered target readiness alone never resume");
        check(life.Update(false, true, false, 0, false) == RecoveryMode.Waiting, "deselecting waits");
        check(life.Update(false, true, true, 1, true) == RecoveryMode.Resumed && !life.Waiting, "reselecting the same boss resumes");
        check(life.Update(false, true, true, 1, true) == RecoveryMode.Active, "resume cleanup is not repeated");
        check(life.Update(true, true, true, 1, true) == RecoveryMode.NewDeath, "second death waits again");
        check(life.Update(false, true, true, 2, true) == RecoveryMode.Waiting, "first alive frame target restoration is not intent");
        check(life.Update(false, true, true, 3, true) == RecoveryMode.Resumed, "tab to another usable enemy resumes");
        life.Update(true, false, false, 0, false);
        life.Update(false, false, false, 0, false);
        check(life.Update(false, false, false, 9, true) == RecoveryMode.Waiting, "no auto pull of disengaged enemy after a wipe");
        check(life.Update(false, true, true, 9, true) == RecoveryMode.Resumed, "later combat flag completes stored selection intent");
        life.Update(true, false, false, 0, false);
        life.Update(false, false, false, 0, false);
        check(life.Update(false, true, false, 10, false) == RecoveryMode.Waiting, "friendly or unusable selection cannot resume");
        check(life.Update(false, true, true, 10, true) == RecoveryMode.Resumed, "selected target becoming usable completes stored intent");
        life.Update(true, false, false, 0, false);
        life.Update(false, false, false, 0, false);
        check(life.Update(false, false, true, 9, true) == RecoveryMode.Resumed, "explicit engaged enemy selection works before own combat flag");
        life.Reset();
        check(!life.Waiting && !life.HasDied, "manual stop and new pull clear recovery state");
        var timeline = new ActionTimeline();
        timeline.RecordExecuted(true, 1); timeline.RecordExecuted(false, 1.7);
        timeline.ResetAfterDeath();
        check(timeline.GcdCount == 1 && !timeline.ActiveCycle && timeline.Ogcds == 2, "death drops stale weave budget without resetting pull GCD count");
        timeline.RecordExecuted(true, 10);
        check(timeline.GcdCount == 2 && timeline.Ogcds == 0, "first revived GCD restores legal weave budget");

        SongOption[] songs = [new(ActionCatalog.WanderersMinuet, 5), new(ActionCatalog.MagesBallad, 0)];
        check(SongContinuity.Choose(songs, 12, true).Action == ActionCatalog.WanderersMinuet, "Army can wait for WM within remaining duration");
        check(SongContinuity.Choose(songs, 2, true).Action == ActionCatalog.MagesBallad, "if WM misses song end use available fallback");
        check(SongContinuity.Choose(songs, 0, false).Action == ActionCatalog.MagesBallad, "no song does not wait five seconds for WM when Mage ready");
        check(SongContinuity.Choose([new(ActionCatalog.WanderersMinuet, 8), new(ActionCatalog.MagesBallad, 3)], 0, false).Ready == 3, "all songs cooling choose earliest return");
        check(SongContinuity.Choose([], 0, false).Action == 0, "unlearned songs cannot be invented");
        var state = new PlannerState { Song = BardSong.Army, SongRemaining = 11, SongSwitchIn = 0,
            NextSongAction = ActionCatalog.WanderersMinuet, NextSongReady = 0, NextNaturalTick = 2,
            Slots = 1, FutureSlots = 1, NextGcd = 2.08f, Gcd = 2.08f, RagingReady = 40, EmpyrealReady = 0 };
        check(WeavePlanner.Plan(state).First?.ActionId == ActionCatalog.WanderersMinuet, "due WM takes single Army slot even with EA ready and burst not ready");
        check(WeavePlanner.Plan(state).Actions.Length == 1, "song rescue never adds a second Army weave");
        check(SongContinuity.Plan(state with { Slots = 0 }) is null, "no song rescue after weave budget used");
        check(SongContinuity.Plan(state with { NextGcd = 1 }) is null, "song rescue respects lock and GCD gap");
        check(SongContinuity.Plan(state with { NextSongReady = 3 }) is null, "song rescue respects real cooldown");
        check(SongContinuity.Plan(state with { Excluded = new HashSet<uint> { ActionCatalog.WanderersMinuet } }) is null, "native rejected song is not forced");
        check(SongContinuity.Plan(state with { Level = 50 }) is null, "50 cannot use WM");
        check(SongContinuity.Plan(state with { Song = BardSong.None, SongSwitchIn = 20 })?.First?.ActionId == ActionCatalog.WanderersMinuet, "no-song recovery ignores obsolete cut timer");
        var wm = state with { Song = BardSong.Wanderer, SongRemaining = 2.2f, NextSongAction = ActionCatalog.MagesBallad,
            Slots = 2, NextGcd = 2.48f, Gcd = 2.48f, Repertoire = 3 };
        var clear = SongContinuity.Plan(wm);
        check(clear?.Actions.Length == 2 && clear.Actions[0].ActionId == ActionCatalog.PitchPerfect && clear.Actions[1].ActionId == ActionCatalog.MagesBallad, "clear PP then switch when both fit");
        check(SongContinuity.Plan(wm with { Slots = 1 })?.First?.ActionId == ActionCatalog.MagesBallad, "last weave before expiry keeps song even when PP cannot fit");
        check(SongContinuity.Plan(wm with { Slots = 1, SongRemaining = 10, SongSwitchIn = 8 }) is null, "future cut cannot discard PP or switch early");
        for (var slots = 0; slots <= 2; slots++)
        for (var i = 0; i < 40; i++)
        {
            var s = wm with { Slots = slots, NextGcd = .1f + i * .07f, NextSongReady = i % 3 * .4f };
            var plan = SongContinuity.Plan(s);
            if (plan is null) continue;
            check(plan.Actions.Length <= slots, "continuity property: budget");
            foreach (var a in plan.Actions)
                check(CombatTiming.Fits(a.At, s.NextGcd, s.Lock, s.Margin), "continuity property: no clip");
        }
        Console.WriteLine("Death recovery and song continuity checks passed.");
    }
}
