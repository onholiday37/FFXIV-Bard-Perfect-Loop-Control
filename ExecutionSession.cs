namespace BardPerfectLoop;

/// <summary>Actual execution state survives replanning; only a new session resets it.</summary>
public sealed class ExecutionSession
{
    public ActionTimeline Timeline { get; } = new();
    public PendingActionWatch Pending { get; } = new();
    public double StartedAt { get; private set; }
    public int PlanRevision { get; private set; }
    public void Reset(double now)
    {
        Timeline.Reset(); Pending.Clear(); StartedAt = now; PlanRevision++;
    }
    public void Replan() => PlanRevision++;
    public void SuspendForDeath(double now)
    {
        Timeline.ResetAfterDeath(); Pending.Clear(); StartedAt = now; PlanRevision++;
    }
}
