using System;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BardPerfectLoop;

internal static unsafe class LiveCombatReader
{
    public static float Cooldown(uint action, int level)
    {
        var manager = ActionManager.Instance();
        if (manager is null || level < ActionCatalog.MinimumLevel(action)) return float.PositiveInfinity;
        var adjusted = manager->GetAdjustedActionId(action);
        if (action is ActionCatalog.HeartbreakShot or ActionCatalog.Bloodletter or ActionCatalog.RainOfDeath)
        {
            var charges = Charges(action, level);
            return charges.Available > 0 ? 0 : charges.UntilNext;
        }
        if (!manager->IsRecastTimerActive(ActionType.Action, adjusted)) return 0;
        return Math.Max(0, manager->GetRecastTime(ActionType.Action, adjusted) - manager->GetRecastTimeElapsed(ActionType.Action, adjusted));
    }

    public static ChargeState Charges(uint action, int level)
    {
        var manager = ActionManager.Instance();
        if (manager is null || level < ActionCatalog.MinimumLevel(action)) return new(0, 1, 15, 15);
        var adjusted = manager->GetAdjustedActionId(action);
        var maximum = (int)ActionManager.GetMaxCharges(adjusted, (uint)level);
        return ChargeState.FromNative((int)manager->GetCurrentCharges(adjusted), maximum,
            manager->IsRecastTimerActive(ActionType.Action, adjusted),
            manager->GetRecastTime(ActionType.Action, adjusted), manager->GetRecastTimeElapsed(ActionType.Action, adjusted));
    }

    public static (float Remaining, float Total) Gcd(uint reference, float fallback)
    {
        var manager = ActionManager.Instance();
        if (manager is null) return (float.PositiveInfinity, fallback);
        var total = manager->GetRecastTime(ActionType.Action, reference);
        if (total < 1) total = ActionManager.GetAdjustedRecastTime(ActionType.Action, reference) / 1000f;
        if (total < 1 || total > 4) total = Math.Clamp(fallback, 1.5f, 3.5f);
        var remaining = manager->IsRecastTimerActive(ActionType.Action, reference)
            ? Math.Max(0, manager->GetRecastTime(ActionType.Action, reference) - manager->GetRecastTimeElapsed(ActionType.Action, reference)) : 0;
        return (remaining, total);
    }

    public static bool SelfTarget(uint action) => action is ActionCatalog.RagingStrikes or ActionCatalog.BattleVoice or
        ActionCatalog.RadiantFinale or ActionCatalog.Barrage or ActionCatalog.WanderersMinuet or ActionCatalog.MagesBallad or ActionCatalog.ArmysPaeon;

    public static ulong TargetFor(uint action, ulong enemy) => SelfTarget(action)
        ? Plugin.ObjectTable.LocalPlayer?.GameObjectId ?? 0xE0000000 : enemy;

    public static bool CanUseNow(uint action, int level, ulong target)
    {
        var manager = ActionManager.Instance();
        if (manager is null || level < ActionCatalog.MinimumLevel(action) || Cooldown(action, level) > 0f) return false;
        return manager->GetActionStatus(ActionType.Action, manager->GetAdjustedActionId(action), TargetFor(action, target), true, true, null) == 0;
    }
}
