using System.Collections.Generic;
using Dalamud.Configuration;

namespace BardPerfectLoop;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public bool Enabled = true;
    public bool ShowOverlay = true;
    public bool StopWhenCombatEnds = true;
    public float GcdSeconds = 2.47f;
    public SongPlanMode SongPlan = SongPlanMode.Standard3312;
    public float WandererCutRemaining = 3f;
    public float MageCutRemaining = 3f;
    public float ArmyCutRemaining = 12f;
    public float OgcdLookAheadSeconds = 0.65f;
    public float GcdQueueWindowSeconds = 0.45f;

    public bool DynamicDotRefreshWindow = true;
    public float DotRefreshLeadSeconds = 3.2f;
    public float DotTickSeconds = 3f;
    public int CausticTickPotency = 20;
    public int StormTickPotency = 25;

    public List<RotationStep> Steps = ActionCatalog.CreateDefaultSteps();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

    public void EnsureDefaults()
    {
        Steps ??= [];
        if (Steps.Count == 0)
            Steps = ActionCatalog.CreateDefaultSteps();

        foreach (var step in Steps)
        {
            if (ActionCatalog.Find(step.ActionId) is { } definition)
                step.Name = definition.Name;
        }
    }

    public void ApplyStandard3312()
    {
        SongPlan = SongPlanMode.Standard3312;
        WandererCutRemaining = 3f;
        MageCutRemaining = 3f;
        ArmyCutRemaining = 12f;
        Save();
    }

    public void ApplyAdvanced369()
    {
        SongPlan = SongPlanMode.Advanced369;
        WandererCutRemaining = 3f;
        MageCutRemaining = 6f;
        ArmyCutRemaining = 9f;
        Save();
    }
}
