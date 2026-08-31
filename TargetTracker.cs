using Dalamud.Game.ClientState.Objects.Types;

namespace BardPerfectLoop;

/// <summary>
/// Keeps the last manually selected battle target across a temporary untargetable phase.
/// It never selects an arbitrary replacement target.
/// </summary>
public sealed class TargetTracker
{
    private ulong lastTargetId;
    private bool lastTargetBecameUnavailable;

    public ulong LastTargetId => lastTargetId;
    public bool WaitingForReturn => lastTargetBecameUnavailable;

    public IBattleChara? Resolve()
    {
        if (Plugin.TargetManager.Target is IBattleChara selected && IsUsable(selected))
        {
            lastTargetId = selected.GameObjectId;
            lastTargetBecameUnavailable = false;
            return selected;
        }

        if (lastTargetId == 0)
            return null;

        var previous = Plugin.ObjectTable.SearchById(lastTargetId) as IBattleChara;
        if (previous is null || !IsUsable(previous))
        {
            lastTargetBecameUnavailable = true;
            return null;
        }

        return lastTargetBecameUnavailable ? previous : null;
    }

    public void Reset()
    {
        lastTargetId = 0;
        lastTargetBecameUnavailable = false;
    }

    private static bool IsUsable(IBattleChara target) => target.IsTargetable && !target.IsDead;
}
