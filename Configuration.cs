using System.Collections.Generic;
using Dalamud.Configuration;

namespace BardPerfectLoop;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    public bool Enabled = true;
    public bool ShowOverlay = true;
    public bool StopWhenCombatEnds = true;
    public float GcdSeconds = 2.47f;
    public bool GuidePerfectAxis = true;
    public RotationScenario Scenario = RotationScenario.CurrentStandard;
    public SongPlanMode SongPlan = SongPlanMode.Standard3312;
    public float WandererCutRemaining = 2f;
    public float MageCutRemaining = 2f;
    public float ArmyCutRemaining = 11f;
    public float OgcdLookAheadSeconds = 0.65f;
    public float GcdQueueWindowSeconds = 0.45f;

    public bool DynamicDotRefreshWindow;
    public float DotRefreshLeadSeconds = 5.5f;
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

        if (Version < 4)
        {
            Scenario = SongPlan switch
            {
                SongPlanMode.Advanced369 => RotationScenario.Advanced369,
                SongPlanMode.Custom => RotationScenario.Custom,
                _ => RotationScenario.CurrentStandard,
            };
            if (SongPlan == SongPlanMode.GuideAuto)
                SongPlan = SongPlanMode.Standard3312;
            DynamicDotRefreshWindow = false;
            DotRefreshLeadSeconds = 5.5f;
            Version = 4;
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
        ApplyScenario(RotationScenario.CurrentStandard);
    }

    public void ApplyAdvanced369()
    {
        ApplyScenario(RotationScenario.Advanced369);
    }

    public void ApplyGuideAuto()
    {
        ApplyScenario(RotationScenario.LegacyNga);
    }

    public void ApplyScenario(RotationScenario scenario)
    {
        var profile = ScenarioRules.Resolve(scenario);
        Scenario = scenario;
        GuidePerfectAxis = true;
        SongPlan = profile.SongPlan;
        if (profile.RecommendedGcd is { } gcd)
            GcdSeconds = gcd;

        switch (profile.SongPlan)
        {
            case SongPlanMode.Standard3312:
                WandererCutRemaining = 2f;
                MageCutRemaining = 2f;
                ArmyCutRemaining = 11f;
                break;
            case SongPlanMode.Advanced369:
                WandererCutRemaining = 2f;
                MageCutRemaining = 5f;
                ArmyCutRemaining = 8f;
                break;
        }

        StopWhenCombatEnds = !profile.ReplansDowntime;
        if (scenario != RotationScenario.Custom)
        {
            DynamicDotRefreshWindow = scenario == RotationScenario.LegacyNga;
            DotRefreshLeadSeconds = profile.DotRefreshLead;
        }
        Save();
    }
}
