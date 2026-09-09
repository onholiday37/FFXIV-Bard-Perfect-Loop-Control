using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace BardPerfectLoop;

/// <summary>
/// Keeps the last manually selected battle target across a temporary untargetable phase.
/// It never selects an arbitrary replacement target.
/// </summary>
public sealed class TargetTracker
{
    private ulong lastTargetId;
    private bool lastTargetBecameUnavailable;
    public bool Recovering { get; private set; }
    public bool RememberedEngaged => Plugin.ObjectTable.SearchById(lastTargetId) is IBattleChara target &&
        !target.IsDead && (target.StatusFlags & StatusFlags.InCombat) != 0;
    public void BeginRecovery() { Reset(); Recovering = true; }
    public void CompleteRecovery() { Recovering = false; }
    public IBattleChara? SelectedUsable => Plugin.TargetManager.Target is IBattleChara selected && IsUsable(selected) ? selected : null;
    public bool SelectedEngaged => SelectedUsable is { } target && (target.StatusFlags & StatusFlags.InCombat) != 0;

    public ulong LastTargetId => lastTargetId;
    public bool WaitingForReturn => lastTargetBecameUnavailable;

    public IBattleChara? Resolve()
    {
        // No remembered-target fallback or implicit recaching during resurrection protection.
        if (Recovering) return null;
        if (Plugin.TargetManager.Target is IBattleChara selected && IsUsable(selected))
        {
            lastTargetId = selected.GameObjectId;
            lastTargetBecameUnavailable = false;
            return selected;
        }

        // An explicit selection of someone else (for example a party member)
        // takes precedence over automatic return to the remembered boss.
        if (Plugin.TargetManager.Target is { } other && other.GameObjectId != lastTargetId)
            return null;

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
        Recovering = false;
    }

    private static unsafe bool IsUsable(IBattleChara target)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        return player is not null && target.IsTargetable && !target.IsDead && target.Address != 0 &&
            System.Numerics.Vector3.Distance(player.Position, target.Position) <= 25 + player.HitboxRadius + target.HitboxRadius &&
            ActionManager.CanUseActionOnTarget(ActionCatalog.HeavyShot, (NativeObject*)target.Address);
    }
}
