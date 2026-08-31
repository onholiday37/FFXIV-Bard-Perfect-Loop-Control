using System;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace BardPerfectLoop;

/// <summary>
/// Read-only lookup from an action ID to the key hint already maintained by the game hotbar.
/// This class never invokes a hotbar slot or writes a key binding.
/// </summary>
public sealed class HotbarKeyResolver
{
    private const int StandardHotbarCount = 10;
    private const int SlotsPerHotbar = 16;

    public unsafe HotbarKey Resolve(uint actionId)
    {
        var hotbarModule = RaptureHotbarModule.Instance();
        if (hotbarModule is null)
            return HotbarKey.NotFound;

        HotbarKey? unboundMatch = null;

        for (var hotbarIndex = 0; hotbarIndex < StandardHotbarCount; hotbarIndex++)
        {
            for (var slotIndex = 0; slotIndex < SlotsPerHotbar; slotIndex++)
            {
                var slot = hotbarModule->GetSlotById((uint)hotbarIndex, (uint)slotIndex);
                if (slot is null || slot->CommandType != RaptureHotbarModule.HotbarSlotType.Action)
                    continue;

                if (slot->CommandId != actionId &&
                    slot->ApparentActionId != actionId &&
                    slot->OriginalApparentActionId != actionId)
                    continue;

                var hint = CleanHint(slot->KeybindHintString);
                var match = new HotbarKey(hint, hotbarIndex + 1, slotIndex + 1, true);
                if (!string.IsNullOrWhiteSpace(hint))
                    return match;

                unboundMatch ??= match;
            }
        }

        return unboundMatch ?? HotbarKey.NotFound;
    }

    private static string CleanHint(string? hint) =>
        string.IsNullOrWhiteSpace(hint)
            ? string.Empty
            : hint.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
}

public readonly record struct HotbarKey(string KeyHint, int HotbarNumber, int SlotNumber, bool Found)
{
    public static HotbarKey NotFound => new(string.Empty, 0, 0, false);

    public string Format(string actionName)
    {
        if (!Found)
            return $"[热键栏未找到] {actionName}";
        if (string.IsNullOrWhiteSpace(KeyHint))
            return $"[热键栏{HotbarNumber}-{SlotNumber}未绑定] {actionName}";
        return $"[{KeyHint}] {actionName}";
    }
}
