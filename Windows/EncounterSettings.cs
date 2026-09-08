using System.Linq;
using Dalamud.Bindings.ImGui;

namespace BardPerfectLoop.Windows;

public sealed partial class ControlWindow
{
    private void DrawEncounterAndItems(Configuration config, ref bool changed)
    {
        changed |= ImGui.Checkbox("自动比较单体与群攻", ref config.AutoAoe);
        changed |= ImGui.Checkbox("绝神兵机制目标保护（刺羽/柱子/石牢/炸弹）", ref config.ProtectUwuMechanics);
        ImGui.TextWrapped("以你选中的目标瞄准，按圆形/扇形/直线范围估算命中；不会替你换锁定目标。保护目标需你手选，范围技能会避开旁边尚未指定的机制目标。");
        if (config.Scenario == RotationScenario.Uwu && ImGui.CollapsingHeader("绝神兵阶段爆发设置"))
        {
            ImGui.TextWrapped(plugin.Battlefield.Encounter.Reason);
            changed |= ImGui.SliderFloat("短于多少秒可攻击时间时保留爆发", ref config.UwuMinimumBurstWindow, 5, 30, "%.0fs");
            changed |= ImGui.SliderFloat("实际落地后的爆发准备时间", ref config.UwuReturnDelay, 0, 10, "%.1fs");
            changed |= ImGui.SliderFloat("土神首次可攻击后的最早爆发", ref config.UwuTitanBurstDelay, 0, 90, "%.0fs");
            changed |= ImGui.SliderFloat("神兵首次可攻击后的最早爆发", ref config.UwuUltimaBurstDelay, 0, 90, "%.0fs");
            changed |= ImGui.Checkbox("火神柱子阶段保留长CD爆发", ref config.UwuHoldBurstForAdds);
            ImGui.TextWrapped("默认土神30秒：避开首次上天，实际落地后才释放。究极歼灭只有短暂消失，复现后继续输出；压制按实际读条暂停长CD。中途启动、跳阶段或队伍速度差异会降低预测准确度。");
        }
        if (ImGui.CollapsingHeader("自动药食：先选一次背包物品"))
        {
            plugin.Consumables.Refresh();
            if (ImGui.SmallButton("刷新背包列表")) plugin.Consumables.Refresh(true);
            changed |= ImGui.Checkbox("自动使用所选巧力/灵巧药", ref config.AutoPotion);
            changed |= ImGui.Checkbox("自动续吃所选食物（仅脱战）", ref config.AutoFood);
            DrawItemSelection(config, ConsumableKind.DexterityPotion, ref changed);
            DrawItemSelection(config, ConsumableKind.Food, ref changed);
            changed |= ImGui.SliderFloat("第二次药计划时间（开怪秒数；270=4:30）", ref config.SecondPotionAt, 0, 900, "%.0fs");
            changed |= ImGui.SliderFloat("食物剩余多少秒续吃", ref config.FoodRefreshSeconds, 0, 900, "%.0fs");
            changed |= ImGui.SliderFloat("药品动作锁预留", ref config.PotionLockSeconds, 1.10f, 1.50f, "%.2fs");
            ImGui.TextWrapped("品质也是选择的一部分。缺货不替换、不买药；食物不覆盖另一种仍有效的食物。4:30须满足真实CD及可攻击窗口，NQ或晚起手会顺延。药单独占一个插入窗口。影子模式不会消费物品。");
            ImGui.TextWrapped($"药：{plugin.Consumables.PotionStatus}");
            ImGui.TextWrapped($"食物：{plugin.Consumables.FoodStatus}");
            if (plugin.Consumables.LastFailure.Length > 0) ImGui.TextWrapped($"最近未用成功原因：{plugin.Consumables.LastFailure}");
        }
    }

    private void DrawItemSelection(Configuration config, ConsumableKind kind, ref bool changed)
    {
        var selected = kind == ConsumableKind.Food ? plugin.Consumables.SelectedFood : plugin.Consumables.SelectedPotion;
        var id = kind == ConsumableKind.Food ? config.FoodItemId : config.PotionItemId;
        var preview = selected?.Label ?? (id == 0 ? "未选择（不会使用）" : $"已选物品 {id} 缺货或当前背包未加载");
        if (!ImGui.BeginCombo(kind == ConsumableKind.Food ? "食物" : "爆发药", preview)) return;
        if (ImGui.Selectable("不选择", id == 0))
        {
            if (kind == ConsumableKind.Food) config.FoodItemId = 0; else config.PotionItemId = 0;
            changed = true;
        }
        foreach (var item in plugin.Consumables.Inventory.Where(i => i.Kind == kind))
        {
            if (!ImGui.Selectable($"{item.Label}##{item.GameId}", selected?.GameId == item.GameId)) continue;
            if (kind == ConsumableKind.Food) { config.FoodItemId = item.ItemId; config.FoodHq = item.Hq; }
            else { config.PotionItemId = item.ItemId; config.PotionHq = item.Hq; }
            changed = true;
        }
        ImGui.EndCombo();
    }
}
