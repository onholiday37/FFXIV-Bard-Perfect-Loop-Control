using System;

namespace BardPerfectLoop;

public enum ConsumableKind { Food, DexterityPotion }
public sealed record ConsumableChoice(uint ItemId, bool Hq, string Name, ConsumableKind Kind,
    uint EffectId, float Duration, int Count, int Level, int Percent, int Cap)
{
    public uint GameId => ItemId + (Hq ? 1_000_000u : 0);
    public uint StatusParam => EffectId + (Hq ? 10_000u : 0);
    public string Label => $"{Name}{(Hq ? " HQ" : " NQ")} ×{Count}";
}
public readonly record struct ConsumableDecision(bool Use, string Reason);

// One bounded priority opportunity per pull. A submitted item is never retried
// by this gate while its confirmation is outstanding (the controller owns timeout).
public sealed class OpeningPotionGate
{
    private double deadline;
    public bool Finished { get; private set; } = true;
    public void Begin(double now) { deadline = now + 3; Finished = false; }
    public bool ShouldBlock(double now, bool eligible, bool pending, bool buffActive)
    {
        if (Finished) return false;
        if (pending) return true;
        if (buffActive || !eligible || now >= deadline) { Finished = true; return false; }
        return true;
    }
}

public static class ConsumableRules
{
    public static bool BlocksActions(bool pending, bool observed, bool requireBuff) => pending && (!observed || requireBuff);
    public static bool Confirmed(bool matchingBuffIncreased, bool inventoryReady, bool quantityDecreased, bool requireBuff) =>
        matchingBuffIncreased || !requireBuff && inventoryReady && quantityDecreased;

    public static ConsumableDecision Food(bool enabled, bool armed, bool shadow, bool inCombat,
        bool selectedAvailable, float currentDuration, uint currentParam, uint selectedParam, float refresh)
    {
        if (!enabled || !armed || shadow) return new(false, "食物自动使用未启用或为影子模式");
        if (!selectedAvailable) return new(false, "请先选择背包中的食物；缺货时不会换吃其他品种");
        if (inCombat) return new(false, "战斗中不吃食物，等待脱战续餐");
        if (currentDuration > 0 && currentParam != selectedParam) return new(false, "当前是另一种食物效果，保留玩家手动选择");
        return currentDuration <= refresh ? new(true, "所选食物到续餐时间") : new(false, "食物剩余时间充足");
    }

    public static ConsumableDecision Potion(bool enabled, bool armed, bool shadow, bool inCombat,
        bool selectedAvailable, double elapsed, int uses, double lastUseElapsed, float secondAt,
        float actualCooldown, float buffLeft, bool targetAvailable, bool holdBurst, float uptime, float minimumWindow)
    {
        if (!enabled || !armed || shadow || !inCombat) return new(false, "尚未进入自动用药战斗状态");
        if (!selectedAvailable) return new(false, "请先选择背包中的巧力/灵巧药；缺货时不会替换");
        if (buffLeft > 0) return new(false, "药物增益仍在，避免覆盖");
        if (!targetAvailable || holdBurst || uptime < minimumWindow) return new(false, "当前爆发窗口不足，药顺延至实际可攻击窗口");
        var due = uses == 0 ? 0 : uses == 1 ? secondAt : lastUseElapsed + 270;
        if (elapsed < due) return new(false, $"下一次计划用药 {Math.Floor(due / 60):F0}分{due % 60:00}秒");
        if (!float.IsFinite(actualCooldown) || actualCooldown > 0) return new(false, $"计划点已到，等待药品真实CD {actualCooldown:F1}s");
        return new(true, uses == 0 ? "起手药：进入第一个安全插入窗口" : "续爆发药：计划点已到且真实CD可用");
    }

    // Uses the item's actual action/data semantics, not translated names or category alone.
    public static ConsumableKind? Classify(uint action, ushort status, uint primaryParameter, float duration) =>
        action == 844 && status == 48 && duration >= 60 ? ConsumableKind.Food :
        action == 846 && status == 49 && primaryParameter == 2 && duration is > 0 and <= 60 ? ConsumableKind.DexterityPotion : null;
}
