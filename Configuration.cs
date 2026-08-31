using System.Collections.Generic;
using Dalamud.Configuration;

namespace BardPerfectLoop;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    public bool Enabled = true;
    public bool ShowOverlay = true;
    public bool StopWhenCombatEnds = true;
    public float GcdSeconds = 2.47f;
    public bool GuidePerfectAxis = true;
    public SongPlanMode SongPlan = SongPlanMode.GuideAuto;
    public float WandererCutRemaining = 2f;
    public float MageCutRemaining = 2f;
    public float ArmyCutRemaining = 11f;
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
        if (Version < 3)
        {
            GuidePerfectAxis = true;
            SongPlan = SongPlanMode.GuideAuto;
            Version = 3;
        }

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
        WandererCutRemaining = 2f;
        MageCutRemaining = 2f;
        ArmyCutRemaining = 11f;
        Save();
    }

    public void ApplyAdvanced369()
    {
        SongPlan = SongPlanMode.Advanced369;
        WandererCutRemaining = 2f;
        MageCutRemaining = 5f;
        ArmyCutRemaining = 8f;
        Save();
    }

    public void ApplyGuideAuto()
    {
        SongPlan = SongPlanMode.GuideAuto;
        Save();
    }
}
