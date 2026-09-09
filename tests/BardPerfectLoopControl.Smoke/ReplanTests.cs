using BardPerfectLoop;

internal static class ReplanTests
{
    public static void Run(Action<bool, string> check)
    {
        var state = new ExecutionSession();
        state.Reset(0);
        for (var i = 0; i < 40; i++) state.Timeline.RecordExecuted(true, i * 2.48);
        state.Timeline.RecordExecuted(false, 97.42);
        state.Pending.Begin(ActionCatalog.BurstShot, 99, true, 42, .4f);
        var time = state.Timeline.LastExecutionAt;
        for (var i = 0; i < 100; i++)
        {
            state.Replan();
            check(state.Timeline.GcdCount == 40 && state.Timeline.ActiveCycle && state.Timeline.Ogcds == 1,
                "replan cannot restart opener or create weave budget");
            check(state.Pending.Action == ActionCatalog.BurstShot && state.StartedAt == 0 && state.Timeline.LastExecutionAt == time,
                "replan retains accepted request, observation cutoff and action lock anchor");
        }
        check(state.Pending.Poll(99.1, true, 42, .3f, 0, false, false) == PendingActionResult.Waiting, "replanned queue remains tracked");
        state.Pending.Poll(99.6, false, 42, 0, 0, false, false);
        check(state.Pending.Poll(99.8, false, 42, 0, 0, false, false) == PendingActionResult.Cancelled, "queue cancellation still reconciles after replan");
        state.Pending.Begin(ActionCatalog.EmpyrealArrow, 100, false, 42, 1);
        state.Replan();
        check(state.Pending.Poll(103, false, 43, 0, 0, false, false) == PendingActionResult.Uncertain, "replan cannot hide uncertain accepted execution");
        var recovery = new RecoveryLifecycle();
        recovery.Update(true, true, true, 1, true); state.SuspendForDeath(104);
        state.Replan();
        check(state.Timeline.GcdCount == 40 && !state.Timeline.ActiveCycle && !state.Pending.Active, "death clears pending but preserves pull count through replan");
        check(recovery.Update(false, true, true, 1, true) == RecoveryMode.Waiting, "replan does not release revival hold");
        recovery.Update(false, true, false, 0, false);
        check(recovery.Update(false, true, true, 1, true) == RecoveryMode.Resumed, "manual reselection resumes after death and replan");
        state.Timeline.RecordExecuted(true, 110); state.Replan();
        check(state.Timeline.GcdCount == 41 && state.Timeline.Ogcds == 0, "first revived manual GCD is not wiped by replan");
        state.Reset(120);
        check(state.Timeline.GcdCount == 0 && state.Timeline.Ogcds == 2 && state.StartedAt == 120, "explicit new start still resets session");

        RotationStep[] steps = [
            new() { ActionId = ActionCatalog.IronJaws, Kind = ShadowActionKind.Gcd, Priority = 100 },
            new() { ActionId = ActionCatalog.BurstShot, Kind = ShadowActionKind.Gcd, Priority = 10 },
            new() { ActionId = ActionCatalog.EmpyrealArrow, Kind = ShadowActionKind.Ogcd, Priority = 90 },
            new() { ActionId = ActionCatalog.HeartbreakShot, Kind = ShadowActionKind.Ogcd, Priority = 80 }];
        HashSet<uint> rejected = [ActionCatalog.IronJaws];
        RotationStep? Choose(ShadowActionKind kind, Func<uint, bool>? ready = null, Func<uint, bool>? allowed = null,
            Func<uint, bool>? learned = null, Func<RotationStep, bool>? condition = null) =>
            RotationSelector.Select(steps, kind, rejected, allowed ?? (_ => true), learned ?? (_ => true),
                ready ?? (_ => true), condition ?? (_ => true));
        check(Choose(ShadowActionKind.Gcd)?.ActionId == ActionCatalog.BurstShot, "rejected high priority custom GCD falls through");
        check(Choose(ShadowActionKind.Gcd, _ => false)?.ActionId == ActionCatalog.BurstShot, "GCD queue still allowed while shared recast active");
        rejected.Add(ActionCatalog.BurstShot);
        check(Choose(ShadowActionKind.Gcd) is null, "all custom GCDs rejected waits without inventing disabled fallback");
        rejected.Clear();
        check(Choose(ShadowActionKind.Gcd)?.ActionId == ActionCatalog.IronJaws, "candidate returns after rejection expires");
        check(Choose(ShadowActionKind.Ogcd, id => id != ActionCatalog.EmpyrealArrow)?.ActionId == ActionCatalog.HeartbreakShot, "custom ability still requires ready cooldown");
        check(Choose(ShadowActionKind.Gcd, allowed: id => id != ActionCatalog.IronJaws)?.ActionId == ActionCatalog.BurstShot, "coverage protection precedes priority");
        check(Choose(ShadowActionKind.Gcd, learned: id => id != ActionCatalog.IronJaws)?.ActionId == ActionCatalog.BurstShot, "level eligibility precedes priority");
        check(Choose(ShadowActionKind.Gcd, condition: s => s.ActionId != ActionCatalog.IronJaws)?.ActionId == ActionCatalog.BurstShot, "unmet condition falls through");
        steps[0].Enabled = false;
        check(Choose(ShadowActionKind.Gcd)?.ActionId == ActionCatalog.BurstShot, "disabled skill cannot win");

        var song = new PlannerState { Song = BardSong.Wanderer, SongRemaining = 3.4f, SongSwitchIn = 1.4f,
            NextSongAction = ActionCatalog.MagesBallad, NextSongReady = 0, Repertoire = 2,
            Slots = 1, FutureSlots = 2, NextGcd = 2.48f, Gcd = 2.48f };
        var plan = SongContinuity.Plan(song);
        check(plan?.First?.ActionId == ActionCatalog.MagesBallad && plan.First.Value.At >= 1.4f,
            "last legal slot is reserved for configured song cut instead of postponing to retain PP");
        check(plan?.Actions.Length == 1, "song reservation never invents double weave");
        check(SongContinuity.Plan(song with { SongSwitchIn = 2.3f }) is null, "song reservation never clips GCD");
        Console.WriteLine("Replan, custom candidate fallback and song-slot regression checks passed.");
    }
}
