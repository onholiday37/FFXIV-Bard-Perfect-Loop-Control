using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace BardPerfectLoop;

internal sealed class ConsumableController(Plugin plugin)
{
    private double scannedAt = double.NegativeInfinity, rejectedUntil;
    private ConsumableChoice? pending;
    private double sentAt;
    private float beforeBuff;
    private bool observed;
    private bool pendingRequiresBuff;
    private readonly OpeningPotionGate opening = new();
    private int uses;
    private double lastUseElapsed = double.NegativeInfinity;
    private bool disabledAfterUncertainUse;
    private int dexWithoutPotion, measuredLevel;
    public float PotionMultiplier { get; private set; } = 1;
    public float PotionRemaining { get; private set; }
    public IReadOnlyList<ConsumableChoice> Inventory { get; private set; } = [];
    public bool InventoryReady { get; private set; }
    public string FoodStatus { get; private set; } = "先从背包选择食物";
    public string PotionStatus { get; private set; } = "先从背包选择药品";
    public string LastFailure { get; private set; } = string.Empty;
    public bool Outstanding => pending is not null;
    public bool BlocksActions => ConsumableRules.BlocksActions(pending is not null, observed, pendingRequiresBuff);
    public double LastItemExecutedAt { get; private set; } = double.NegativeInfinity;
    public ConsumableChoice? SelectedPotion => Selected(ConsumableKind.DexterityPotion);
    public ConsumableChoice? SelectedFood => Selected(ConsumableKind.Food);
    private ConsumableChoice? Selected(ConsumableKind kind) => Inventory.FirstOrDefault(c => c.Kind == kind &&
        c.ItemId == (kind == ConsumableKind.Food ? plugin.Configuration.FoodItemId : plugin.Configuration.PotionItemId) &&
        c.Hq == (kind == ConsumableKind.Food ? plugin.Configuration.FoodHq : plugin.Configuration.PotionHq));

    public void BeginPull()
    {
        opening.Begin(ActionObserver.Now);
        var buff = Buff(49);
        var item = SelectedPotion;
        uses = item is not null && buff.Left > 0 && buff.Param == item.StatusParam ? 1 : 0;
        lastUseElapsed = uses > 0 ? -Math.Max(0, item!.Duration - buff.Left) : double.NegativeInfinity;
    }
    public void OnStart() { disabledAfterUncertainUse = false; }

    public unsafe void Refresh(bool force = false)
    {
        if (!force && ActionObserver.Now - scannedAt < 1) return;
        scannedAt = ActionObserver.Now;
        var manager = InventoryManager.Instance();
        if (manager is null) { Inventory = []; InventoryReady = false; return; }
        var rows = Plugin.DataManager.GetExcelSheet<Item>();
        var foods = Plugin.DataManager.GetExcelSheet<ItemFood>();
        var counts = new Dictionary<(uint Id, bool Hq), int>();
        var loadedBags = 0;
        for (var bag = 0; bag < 4; bag++)
        {
            var container = manager->GetInventoryContainer((InventoryType)bag);
            if (container is null || !container->IsLoaded) continue;
            loadedBags++;
            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item is null || item->ItemId == 0 || item->Quantity <= 0 || item->IsSymbolic) continue;
                var key = (item->ItemId, (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0);
                counts[key] = counts.GetValueOrDefault(key) + item->Quantity;
            }
        }
        InventoryReady = loadedBags == 4;
        if (!InventoryReady) { Inventory = []; return; }
        var candidates = new List<ConsumableChoice>();
        foreach (var (key, count) in counts)
        {
            if (!rows.TryGetRow(key.Id, out var row) || !row.ItemAction.IsValid) continue;
            var action = row.ItemAction.Value;
            var data = key.Hq ? action.DataHQ : action.Data;
            if (data.Count < 3 || !foods.TryGetRow(data[1], out var effect)) continue;
            var primary = effect.Params[0];
            if (ConsumableRules.Classify(action.Action.RowId, data[0], primary.BaseParam.RowId, data[2]) is not { } kind) continue;
            candidates.Add(new(key.Id, key.Hq, row.Name.ToString(), kind, data[1], data[2], count, action.CondLv,
                key.Hq ? primary.ValueHQ : primary.Value, key.Hq ? primary.MaxHQ : primary.Max));
        }
        Inventory = candidates.OrderBy(c => c.Kind).ThenByDescending(c => c.Hq).ThenByDescending(c => c.Cap).ThenBy(c => c.ItemId).ToArray();
    }

    private static (float Left, uint Param) Buff(uint id)
    {
        var status = Plugin.ObjectTable.LocalPlayer?.StatusList.FirstOrDefault(s => s.StatusId == id);
        return status is null ? (0, 0) : (Math.Max(0, status.RemainingTime), status.Param);
    }
    public void ObserveItem(uint id, double at)
    {
        LastItemExecutedAt = at;
        if (pending is not null && (id == pending.GameId || id == pending.ItemId)) { observed = true; return; }
        // Manual selected potions also move the schedule, without ever replacing the selected item.
        if (SelectedPotion is { } potion && (id == potion.GameId || id == potion.ItemId))
        { uses++; lastUseElapsed = plugin.Engine.PullElapsed; }
    }
    public unsafe void Poll()
    {
        Refresh();
        var nativePlayer = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (nativePlayer is not null && Plugin.PlayerState.IsLoaded)
        {
            if (measuredLevel != plugin.EffectiveLevel) { dexWithoutPotion = 0; measuredLevel = plugin.EffectiveLevel; }
            var dex = nativePlayer->GetAttributeByIndex(FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerAttribute.Dexterity);
            var medicated = Buff(49);
            if (medicated.Left <= 0) dexWithoutPotion = dex;
            PotionRemaining = medicated.Left;
            PotionMultiplier = medicated.Left > 0 ? DamageFactors.MainStatRatio(measuredLevel, dexWithoutPotion, dex) : 1;
        }
        else { PotionMultiplier = 1; PotionRemaining = 0; dexWithoutPotion = 0; }
        if (pending is null) return;
        var buff = Buff(pending.Kind == ConsumableKind.Food ? 48u : 49u);
        var count = Inventory.FirstOrDefault(c => c.ItemId == pending.ItemId && c.Hq == pending.Hq)?.Count ?? 0;
        if (ConsumableRules.Confirmed(buff.Param == pending.StatusParam && buff.Left > beforeBuff + 1,
            InventoryReady, count < pending.Count, pendingRequiresBuff))
        {
            if (!observed && plugin.Executor.Timeline.LastGcdAt <= sentAt)
            {
                plugin.Executor.Timeline.RecordExecuted(false, sentAt);
                plugin.Executor.Timeline.ReserveRemainingWeaves();
            }
            LastItemExecutedAt = sentAt;
            if (pending.Kind == ConsumableKind.DexterityPotion) { uses++; lastUseElapsed = plugin.Engine.PullElapsed - (ActionObserver.Now - sentAt); }
            if (pending.Kind == ConsumableKind.Food) FoodStatus = $"已确认使用 {pending.Label}";
            else PotionStatus = $"已确认使用 {pending.Label}";
            LastFailure = string.Empty;
            pending = null;
        }
        else if (ActionObserver.Now - sentAt > 4)
        {
            disabledAfterUncertainUse = true; pending = null;
            plugin.StopControl("药食请求未能确认，已停止；请核对背包/增益后重新启动，避免重复消费");
        }
    }

    public unsafe bool TryFood()
    {
        var buff = Buff(48);
        var item = SelectedFood;
        var choice = ConsumableRules.Food(plugin.Configuration.AutoFood, plugin.Engine.Armed, plugin.Configuration.ShadowOnly,
            plugin.IsInCombat, item is not null, buff.Left, buff.Param, item?.StatusParam ?? 0, plugin.Configuration.FoodRefreshSeconds);
        FoodStatus = choice.Reason;
        return choice.Use && item is not null && TryUse(item, buff.Left);
    }
    public unsafe bool TryOpeningPotion()
    {
        if (opening.Finished) return false;
        var choice = PotionDecision(out var item, out var buffLeft);
        // A manually inserted ability must not turn the opening potion into a third weave.
        var budgetAvailable = !plugin.Executor.Timeline.ActiveCycle || plugin.Executor.Timeline.Ogcds == 0;
        if (!opening.ShouldBlock(ActionObserver.Now, choice.Use && budgetAvailable, pending is not null, buffLeft > 0)) return false;
        PotionStatus = "开怪首药优先：等待动作锁解除、药效确认后开始自动输出";
        if (pending is null && item is not null) TryUse(item, buffLeft, requireBuff: true);
        return true;
    }
    public bool TryPotion()
    {
        var choice = PotionDecision(out var item, out var buffLeft);
        PotionStatus = choice.Reason;
        return choice.Use && item is not null && TryUse(item, buffLeft);
    }
    private unsafe ConsumableDecision PotionDecision(out ConsumableChoice? item, out float buffLeft)
    {
        item = SelectedPotion;
        var manager = ActionManager.Instance();
        var buff = Buff(49);
        buffLeft = buff.Left;
        var cooldown = manager is null || item is null ? float.PositiveInfinity :
            manager->IsRecastTimerActive(ActionType.Item, item.GameId) ? Math.Max(0, manager->GetRecastTime(ActionType.Item, item.GameId) - manager->GetRecastTimeElapsed(ActionType.Item, item.GameId)) : 0;
        var phase = plugin.Battlefield.Encounter;
        return ConsumableRules.Potion(plugin.Configuration.AutoPotion, plugin.Engine.Armed, plugin.Configuration.ShadowOnly,
            plugin.IsInCombat, item is not null, plugin.Engine.PullElapsed, uses, lastUseElapsed, plugin.Configuration.SecondPotionAt,
            cooldown, buff.Left, plugin.ResolveBattleTarget() is not null, phase.HoldBurst, phase.UptimeRemaining,
            plugin.Configuration.UwuMinimumBurstWindow);
    }

    private unsafe bool TryUse(ConsumableChoice choice, float buffBefore, bool requireBuff = false)
    {
        if (pending is not null || disabledAfterUncertainUse || ActionObserver.Now < rejectedUntil || plugin.Configuration.ShadowOnly || !plugin.Engine.Armed) return false;
        Refresh(true);
        var item = Selected(choice.Kind);
        var manager = ActionManager.Instance();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (item is null || item.GameId != choice.GameId || player is null || manager is null || player.IsCasting || player.IsDead || plugin.EffectiveLevel < item.Level ||
            manager->ActionQueued || manager->AnimationLock > 0 || plugin.Executor.Timeline.EarliestWeave(ActionObserver.Now, manager->AnimationLock) > 0) return false;
        var status = manager->GetActionStatus(ActionType.Item, item.GameId, player.GameObjectId, true, true, null);
        if (status != 0)
        { rejectedUntil = ActionObserver.Now + 1; LastFailure = $"{item.Label} 暂不可用，客户端状态码 {status}"; return false; }
        pending = item; sentAt = ActionObserver.Now; beforeBuff = buffBefore; observed = false; pendingRequiresBuff = requireBuff;
        if (!manager->UseAction(ActionType.Item, item.GameId, player.GameObjectId, 0xFFFF, ActionManager.UseActionMode.None, 0, null))
        { pending = null; rejectedUntil = ActionObserver.Now + 1; LastFailure = $"客户端拒绝了 {item.Label} 的使用请求"; return false; }
        return true;
    }
}
