using System.Collections.Generic;
using Dalamud.Configuration;

namespace BardPerfectLoop;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 6;

    public bool Enabled = true;
    public bool ShadowOnly;
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
    public float WeaveSafetyMargin = 0.08f;
    public float ActionLockSeconds = 0.70f;
    public float AssumedDirectHitRate = 0.20f;
    public float TargetLifetimeSeconds; // 0 = unknown, not an HP-derived invulnerability timer.

    public bool DynamicDotRefreshWindow;
    public float DotRefreshLeadSeconds = 5.5f;
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

        if (Version < 5)
        {
            if (!Steps.Exists(step => step.ActionId == ActionCatalog.HeartbreakShot) &&
                ActionCatalog.Find(ActionCatalog.HeartbreakShot) is { } heartbreak)
            {
                Steps.Add(new RotationStep
                {
                    ActionId = heartbreak.Id,
                    Name = heartbreak.Name,
                    Kind = heartbreak.Kind,
                    Condition = heartbreak.SuggestedCondition,
                    Priority = heartbreak.SuggestedPriority,
                    Enabled = true,
                });
            }

            Version = 5;
        }

        foreach (var step in Steps)
        {
            if (ActionCatalog.Find(step.ActionId) is { } definition)
                step.Name = definition.Name;
        }
        Version = 6;
        WeaveSafetyMargin = float.IsFinite(WeaveSafetyMargin) ? System.Math.Clamp(WeaveSafetyMargin, 0.02f, 0.30f) : 0.08f;
        ActionLockSeconds = float.IsFinite(ActionLockSeconds) ? System.Math.Clamp(ActionLockSeconds, 0.70f, 1.2f) : 0.70f;
        GcdSeconds = float.IsFinite(GcdSeconds) ? System.Math.Clamp(GcdSeconds, 1.5f, 3.5f) : 2.49f;
        AssumedDirectHitRate = float.IsFinite(AssumedDirectHitRate) ? System.Math.Clamp(AssumedDirectHitRate, 0f, 1f) : 0.20f;
        TargetLifetimeSeconds = float.IsFinite(TargetLifetimeSeconds) ? System.Math.Clamp(TargetLifetimeSeconds, 0f, 3600f) : 0;
        GcdQueueWindowSeconds = float.IsFinite(GcdQueueWindowSeconds) ? System.Math.Clamp(GcdQueueWindowSeconds, 0.05f, 0.50f) : 0.45f;
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
