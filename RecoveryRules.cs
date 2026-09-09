namespace BardPerfectLoop;

public enum RecoveryMode { Active, NewDeath, Dead, Waiting, Resumed }

public sealed class RecoveryLifecycle
{
    private bool dead;
    private ulong previousSelection;
    public ulong PendingSelection { get; private set; }
    public bool Waiting { get; private set; }
    public bool HasDied { get; private set; }
    public void Reset() { dead = Waiting = HasDied = false; previousSelection = PendingSelection = 0; }
    public RecoveryMode Update(bool isDead, bool inCombat, bool selectedTargetEngaged,
        ulong selectedId, bool selectedUsable)
    {
        if (isDead)
        {
            var first = !dead;
            dead = Waiting = HasDied = true;
            PendingSelection = 0;
            previousSelection = selectedId;
            return first ? RecoveryMode.NewDeath : RecoveryMode.Dead;
        }
        var firstAliveFrame = dead;
        dead = false;
        if (!Waiting) return RecoveryMode.Active;
        // Establish a fresh baseline on revival: retained/restored targets are not intent.
        var changed = !firstAliveFrame && selectedId != previousSelection;
        previousSelection = selectedId;
        if (firstAliveFrame || selectedId == 0) PendingSelection = 0;
        else if (changed) PendingSelection = selectedId;
        // Selection and combat/target readiness can arrive on different frames.
        // Keep intent only for the current selection; never resolve a remembered enemy.
        if (PendingSelection == 0 || PendingSelection != selectedId || !selectedUsable || (!inCombat && !selectedTargetEngaged))
            return RecoveryMode.Waiting;
        PendingSelection = 0;
        Waiting = false;
        return RecoveryMode.Resumed;
    }
}
