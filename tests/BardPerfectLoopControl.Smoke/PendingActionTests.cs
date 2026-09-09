using BardPerfectLoop;

internal static class PendingActionTests
{
    public static void Run(Action<bool, string> check)
    {
        var watch = new PendingActionWatch();
        PendingActionResult Poll(double t, bool queued = false, ushort seq = 10, float gcd = 0, float animation = 0, bool casting = false, bool failed = false)
            => watch.Poll(t, queued, seq, gcd, animation, casting, failed);
        check(Poll(0) == PendingActionResult.None, "no accepted request requires no reconciliation");
        watch.Begin(ActionCatalog.BurstShot, 0, true, 10, .4f);
        check(Poll(.1, true, gcd: .3f) == PendingActionResult.Waiting, "do not resend a queued GCD");
        check(Poll(.5) == PendingActionResult.Waiting, "empty queue needs stable observation before cancellation");
        check(Poll(.7) == PendingActionResult.Cancelled, "cancelled queue with unchanged sequence and expired old GCD can recover");
        watch.Clear();
        var timeline = new ActionTimeline(); timeline.RecordExecuted(true, -2.1);
        timeline.ReserveRemainingWeaves();
        check(!watch.Active && timeline.GcdCount == 1 && timeline.Ogcds == 2, "cancellation does not invent a GCD or give free weave slots");
        timeline.RecordExecuted(true, 10);
        check(timeline.GcdCount == 2 && timeline.Ogcds == 0, "observed resumed GCD restores normal budget");
        watch.Begin(ActionCatalog.BurstShot, 0, true, 10, .4f);
        check(Poll(3, true) == PendingActionResult.Waiting, "still queued during downtime waits beyond old two-second timeout");
        check(Poll(5) == PendingActionResult.Waiting && Poll(5.2) == PendingActionResult.Cancelled, "long downtime queue disappearance can recover");
        watch.Begin(ActionCatalog.BurstShot, 0, false, 10, 0);
        check(Poll(3) == PendingActionResult.Uncertain, "accepted but never seen queued cannot be assumed cancelled");
        watch.Begin(ActionCatalog.BurstShot, 0, true, 10, .4f);
        check(Poll(.5, seq: 11, gcd: 2.4f) == PendingActionResult.Waiting && Poll(3, seq: 11) == PendingActionResult.Uncertain,
            "unobserved sequence advance retains fail-closed timeout");
        watch.Begin(ActionCatalog.BurstShot, 0, true, 10, .4f);
        Poll(.5, gcd: 2.4f);
        check(Poll(3) == PendingActionResult.Uncertain, "GCD restart evidence remains sticky after cooldown expires");
        watch.Begin(ActionCatalog.BurstShot, 0, true, ushort.MaxValue, .4f);
        check(Poll(3, seq: 0) == PendingActionResult.Uncertain, "sequence wrap is execution evidence, not cancellation");
        watch.Begin(ActionCatalog.BurstShot, 0, true, 10, .4f);
        check(Poll(.5, failed: true) == PendingActionResult.Uncertain, "observer failure cannot be masked by target loss");
        check(Poll(.5, gcd: float.NaN) == PendingActionResult.Uncertain, "invalid native timing never authorizes a retry");
        watch.Begin(ActionCatalog.BurstShot, 0, true, 10, .4f);
        Poll(.5); Poll(.6, true); Poll(.7);
        check(Poll(.8) == PendingActionResult.Waiting && Poll(.9) == PendingActionResult.Cancelled, "queue reappearance resets stable-empty interval");
        watch.Begin(ActionCatalog.BurstShot, 0, true, 10, .4f);
        check(Poll(3, animation: .6f) == PendingActionResult.Waiting && Poll(4, casting: true) == PendingActionResult.Waiting,
            "casting or native action lock blocks cancellation inference");
        watch.Clear();
        check(!watch.Active && Poll(5) == PendingActionResult.None, "execution, death or manual stop clears pending watch");
        foreach (var initial in new[] { .05f, .20f, .45f, .5f })
        foreach (var downtime in new[] { .7, 3, 30, 120 })
        {
            watch.Begin(ActionCatalog.BurstShot, 0, true, 10, initial);
            check(Poll(downtime, true) == PendingActionResult.Waiting, "long target absence alone never stops a known queued request");
            check(Poll(downtime + .1) == PendingActionResult.Waiting, "queue disappearance starts stable-empty observation");
            check(Poll(downtime + .3) == PendingActionResult.Cancelled, "cancelled queued GCD recovers for every supported queue window");
        }
        Console.WriteLine("Queued GCD cancellation and uncertain-execution boundary tests passed.");
    }
}
