using BardPerfectLoop;

internal static class RecoveryIntentTests
{
    private static RecoveryLifecycle Waiting()
    {
        var life = new RecoveryLifecycle();
        life.Update(true, false, false, 0, false);
        life.Update(false, false, false, 0, false);
        return life;
    }

    public static void Run(Action<bool, string> check)
    {
        var delayed = Waiting();
        check(delayed.Update(false, false, false, 7, true) == RecoveryMode.Waiting, "selection waits for combat readiness");
        check(delayed.Update(false, true, true, 7, true) == RecoveryMode.Resumed,
            "regression: Tab intent survives combat flags arriving a frame later");

        var range = Waiting();
        check(range.Update(false, true, false, 8, false) == RecoveryMode.Waiting, "out of range selection waits");
        check(range.Update(false, true, true, 8, true) == RecoveryMode.Resumed,
            "regression: selected target becoming usable resumes without another Tab");

        foreach (var waitFrames in new[] { 1, 2, 10, 300 })
        foreach (var ownCombat in new[] { false, true })
        {
            var life = Waiting();
            life.Update(false, false, false, 11, false);
            var stayedWaiting = true;
            for (var i = 0; i < waitFrames; i++)
                stayedWaiting &= life.Update(false, false, false, 11, false) == RecoveryMode.Waiting;
            check(stayedWaiting, "delayed readiness never sends while unavailable");
            check(life.Update(false, ownCombat, !ownCombat, 11, true) == RecoveryMode.Resumed,
                "either own or selected enemy combat flag can release stored intent");
            check(life.Update(false, ownCombat, !ownCombat, 11, true) == RecoveryMode.Active,
                "stored intent resumes only once");
        }

        var cancelled = Waiting();
        cancelled.Update(false, false, false, 12, false);
        cancelled.Update(false, true, true, 0, false);
        check(cancelled.PendingSelection == 0, "deselect actually clears remembered intent");
        check(cancelled.Update(false, true, true, 0, false) == RecoveryMode.Waiting,
            "deselect cancels pending recovery even when original boss becomes ready");

        var switched = Waiting();
        switched.Update(false, false, false, 13, false);
        check(switched.Update(false, true, true, 14, false) == RecoveryMode.Waiting,
            "another selected target replaces old intent and cannot attack old boss");
        check(switched.PendingSelection == 14, "old target intent is replaced rather than queued");
        check(switched.Update(false, true, true, 14, true) == RecoveryMode.Resumed,
            "only the currently selected replacement can resume");

        var diedAgain = Waiting();
        diedAgain.Update(false, false, false, 15, false);
        diedAgain.Update(true, true, true, 15, true);
        check(diedAgain.PendingSelection == 0, "death clears intent immediately");
        diedAgain.Update(false, true, true, 15, true);
        check(diedAgain.Update(false, true, true, 15, true) == RecoveryMode.Waiting,
            "second death cancels intent; retained target cannot break resurrection protection");

        var retained = new RecoveryLifecycle();
        retained.Update(true, true, true, 16, true);
        retained.Update(false, true, true, 16, true);
        check(retained.Update(false, true, true, 16, true) == RecoveryMode.Waiting,
            "ready retained target still cannot auto resume");
        retained.Update(false, true, false, 0, false);
        check(retained.Update(false, true, true, 16, true) == RecoveryMode.Resumed,
            "explicit deselect and reselect releases retained target");

        var firstFrame = new RecoveryLifecycle();
        firstFrame.Update(true, true, true, 0, false);
        firstFrame.Update(false, true, true, 17, true);
        check(firstFrame.Update(false, true, true, 17, true) == RecoveryMode.Waiting,
            "first revived frame remains a conservative baseline, not implicit intent");

        var stopped = Waiting();
        stopped.Update(false, false, false, 18, false);
        stopped.Reset();
        check(stopped.PendingSelection == 0, "duty stop clears pending selection immediately");
        check(!stopped.Waiting && !stopped.HasDied, "stop or duty reset clears revival session");
        stopped.Update(true, true, true, 18, true);
        stopped.Update(false, true, true, 18, true);
        check(stopped.Update(false, true, true, 18, true) == RecoveryMode.Waiting,
            "previous duty intent cannot survive reset and next death");
        Console.WriteLine("Delayed Tab recovery intent and cancellation boundary checks passed.");
    }
}
