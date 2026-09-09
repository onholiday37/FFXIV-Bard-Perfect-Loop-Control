using BardPerfectLoop;

internal static class AuditTests
{
    public static void Run(Action<bool, string> check)
    {
        var gap = new DotSnapshotLedger();
        gap.Observe(10, 40, 40, 0); gap.Record(10, ActionCatalog.IronJaws, 1.15f);
        gap.Stage(10, ActionCatalog.IronJaws, 1, 10);
        gap.Observe(10, 25, 25, 30);
        check(!gap.Read(10).Known, "target-switch gap must not keep old buff after unconfirmed refresh");
        gap.Observe(11, 40, 40, 0); gap.Record(11, ActionCatalog.IronJaws, 1.15f);
        gap.Observe(11, 10, 10, 30);
        check(gap.Read(11).Known, "ordinary countdown across target switch retains known snapshot");
        gap.Observe(12, 40, 40, 0); gap.Record(12, ActionCatalog.IronJaws, 1.15f);
        gap.Observe(12, 25, 10, 30);
        check(!gap.Read(12).Known && gap.Read(12).Caustic == 1 && gap.Read(12).Storm == 1.15f,
            "unobserved poison refresh only invalidates poison, not unchanged wind");
        gap.Observe(13, 45, 45, 0); gap.Record(13, ActionCatalog.IronJaws, 1.15f);
        gap.Stage(13, ActionCatalog.IronJaws, 1, 10); gap.Observe(13, 44.9f, 44.9f, 10.1);
        check(gap.Read(13).Known && gap.Read(13).Caustic == 1 && gap.Read(13).Storm == 1,
            "fresh application confirms against time-adjusted baseline after target gap");
        gap.Observe(14, 20, 20, 0); gap.Record(14, ActionCatalog.IronJaws, 1.15f);
        gap.Observe(14, 15, 15, 50);
        check(!gap.Read(14).Known, "expired and reapplied DoTs cannot inherit snapshot across unseen zero");
        gap.Observe(15, 30, 30, 10); gap.Record(15, ActionCatalog.IronJaws, 1.15f);
        gap.Observe(15, 30, 30, 9);
        check(!gap.Read(15).Known, "backward observation time invalidates confidence");
        gap.Observe(16, 30, 30, 0); gap.Record(16, ActionCatalog.IronJaws, 1.15f);
        gap.Observe(16, float.NaN, 29, 1);
        check(!gap.Read(16).Known, "invalid status data cannot retain trusted snapshot");
        for (var seconds = 1; seconds <= 39; seconds++)
        {
            var cases = new DotSnapshotLedger();
            cases.Observe(1, 40, 40, 0); cases.Record(1, ActionCatalog.IronJaws, 1.15f);
            cases.Observe(1, 40 - seconds, 40 - seconds, seconds);
            check(cases.Read(1).Known, "time-adjusted natural countdown property");
            cases.Observe(2, 40, 40, 0); cases.Record(2, ActionCatalog.IronJaws, 1.15f);
            cases.Observe(2, 41 - seconds, 40 - seconds, seconds);
            check(!cases.Read(2).Known && cases.Read(2).Storm == 1.15f, "time-adjusted hidden-refresh property");
        }
        var song = new PlannerState { Song = BardSong.Mage, SongRemaining = 2.1f, SongSwitchIn = .1f,
            NextSongAction = ActionCatalog.ArmysPaeon, NextSongReady = 0, Earliest = 0, NextGcd = 2.48f };
        var plan = WeavePlanner.Plan(song);
        check(plan.Actions.Where(a => SongContinuity.IsSong(a.ActionId)).All(a => a.At >= song.SongSwitchIn),
            "song must never be sent before configured cut, including the final 250ms");
        var ledger = new DotSnapshotLedger();
        ledger.Observe(1, 30, 30, 0);
        ledger.Record(1, ActionCatalog.IronJaws, 1.15f);
        ledger.Observe(1, 0, 0, 31);
        check(!ledger.Read(1).Known, "expired DoTs must discard old snapshot confidence");
        ledger.Observe(2, 20, 20, 0);
        ledger.Record(2, ActionCatalog.IronJaws, 1.15f);
        ledger.Observe(2, 45, 45, 1);
        check(!ledger.Read(2).Known, "unattributed refresh must not inherit old buffs");
        ledger.Observe(3, 30, 30, 0);
        ledger.Record(3, ActionCatalog.IronJaws, 1.15f);
        ledger.Observe(3, 29, 29, 1);
        check(ledger.Read(3).Known, "ordinary timer decrease preserves snapshot");
        ledger.Observe(3, 0, 20, 10);
        check(!ledger.Read(3).Known && ledger.Read(3).Storm == 1.15f, "one expired DoT does not erase the other snapshot");
        ledger.Stage(3, ActionCatalog.CausticBite, 1, 10);
        ledger.Observe(3, 44.9f, 19.9f, 10.1);
        check(ledger.Read(3).Known && ledger.Read(3).Caustic == 1 && ledger.Read(3).Storm == 1.15f,
            "confirmed reapplication uses new buff rather than expired old buff");
        ledger.Stage(3, ActionCatalog.IronJaws, 1.3f, 11);
        ledger.Observe(3, 41, 16, 14);
        check(ledger.Read(3).Known && ledger.Read(3).Caustic == 1, "unapplied request does not invent stronger buffs");
        ledger.Observe(3, 45, 45, 15);
        check(!ledger.Read(3).Known, "refresh after confirmation timeout becomes unknown");
        ledger.Observe(4, 20, 20, 0); ledger.Record(4, ActionCatalog.IronJaws, 1.15f);
        ledger.Stage(4, ActionCatalog.CausticBite, 1, .1); ledger.Observe(4, 44.9f, 44.9f, .2);
        check(!ledger.Read(4).Known && ledger.Read(4).Caustic == 1, "confirming poison does not falsely confirm unrelated wind refresh");

        var locks = new ActionLockEstimate();
        check(locks.Value(0, .7f) == .7f, "initial action lock floor");
        locks.Observe(0, .05, 1.3f, true);
        check(locks.Value(.05, .7f) > 1.3f, "long observed normal lock is respected");
        for (var i = 0; i < 50; i++) locks.Observe(0, .05 + i * .001, 1.3f - i * .001f, true);
        check(locks.Value(.2, .7f) > 1.3f, "many frames are one action, not many recovery samples");
        for (var i = 1; i <= 15; i++) locks.Observe(i, i + .05, .55f, true);
        check(locks.Value(15.1, .7f) > 1.3f, "transient spike remains conservative during recovery");
        locks.Observe(16, 16.05, .55f, true);
        check(locks.Value(16.1, .7f) == .7f, "sixteen newer normal actions retire stale high lock");
        check(locks.Value(16.1, .9f) == .9f, "user configured safety floor is never lowered");
        locks.Observe(17, 17.05, 1.4f, false);
        check(locks.Value(17.1, .7f) == .7f, "item or unknown action cannot contaminate normal lock");
        locks.Observe(18, 18.1, 1.3f, true);
        check(locks.Value(49, .7f) == .7f, "old lock estimate expires during downtime");
        locks.Observe(50, 50.05, 1.3f, true); locks.Reset();
        check(locks.Value(50.1, .7f) == .7f, "death or new pull clears old lock estimate");
        locks.Observe(double.NaN, 51, .8f, true); locks.Observe(51, 51.1, float.NaN, true);
        check(float.IsFinite(locks.Value(51.2, .7f)), "invalid samples do not poison scheduler");

        check(!SongContinuity.CanSend(ActionCatalog.ArmysPaeon, ActionCatalog.ArmysPaeon, .001f), "send guard rejects song even 1ms early");
        check(SongContinuity.CanSend(ActionCatalog.ArmysPaeon, ActionCatalog.ArmysPaeon, 0), "send guard accepts due expected song");
        check(!SongContinuity.CanSend(ActionCatalog.MagesBallad, ActionCatalog.ArmysPaeon, 0), "send guard rejects stale wrong-song recommendation");
        check(!SongContinuity.CanSend(ActionCatalog.ArmysPaeon, ActionCatalog.ArmysPaeon, float.NaN), "invalid deadline cannot release song");
        check(SongContinuity.SwitchIn(3.4f, 2) > 1.39f, "reported 2.48 live input waits 1.4 seconds, not immediate");
        check(SongContinuity.SwitchIn(11.2f, 11) > .19f, "Army final quarter-second is not skipped");
        check(SongContinuity.SwitchIn(45, 0) == 45, "level 50 full-duration song policy stays intact");
        foreach (var mode in new[] { SongPlanMode.Standard3312, SongPlanMode.Advanced369, SongPlanMode.Custom })
        foreach (var gcd in new[] { 2.08f, 2.47f, 2.48f, 2.49f, 2.50f })
        foreach (var currentSong in new[] { BardSong.Wanderer, BardSong.Mage, BardSong.Army })
        foreach (var delta in new[] { -.1f, 0, .001f, .1f, .249f, 1.4f })
        {
            var preset = GuideAxisRules.ResolveSongPlan(mode, gcd, 1.5f, 4, 10);
            var cut = currentSong == BardSong.Wanderer ? preset.WandererCutRemaining : currentSong == BardSong.Mage ? preset.MageCutRemaining : preset.ArmyCutRemaining;
            var state = song with { Song = currentSong, SongRemaining = cut + delta, Gcd = gcd, NextGcd = gcd,
                Slots = currentSong == BardSong.Army ? 1 : 2, FutureSlots = currentSong == BardSong.Army ? 1 : 2,
                SongSwitchIn = SongContinuity.SwitchIn(cut + delta, cut),
                NextSongAction = currentSong == BardSong.Wanderer ? ActionCatalog.MagesBallad : currentSong == BardSong.Mage ? ActionCatalog.ArmysPaeon : ActionCatalog.WanderersMinuet };
            foreach (var stacks in new[] { 0, 3 })
            {
                var result = WeavePlanner.Plan(state with { Repertoire = currentSong == BardSong.Wanderer ? stacks : 0 });
                check(result.Actions.Where(a => SongContinuity.IsSong(a.ActionId)).All(a => a.At >= state.SongSwitchIn), "all presets and GCD speeds respect song deadline");
                check(result.Actions.Count(a => a.At < state.NextGcd) <= state.Slots, "song deadline never adds an extra weave");
                foreach (var a in result.Actions.Where(a => a.At < state.NextGcd))
                    check(CombatTiming.Fits(a.At, state.NextGcd, state.Lock, state.Margin), "song deadline never clips current GCD");
            }
        }
        Console.WriteLine("1.1.3 song deadline, action-lock recovery and DoT confidence regression checks passed.");
    }
}
