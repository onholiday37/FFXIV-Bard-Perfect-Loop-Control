using BardPerfectLoop;

var checks = 0;

void Check(bool condition, string name)
{
    checks++;
    if (!condition)
        throw new InvalidOperationException($"FAILED: {name}");
}

Check((int)SongPlanMode.Standard3312 == 0 && (int)SongPlanMode.GuideAuto == 3, "song-plan config compatibility");
Check((int)StepCondition.RepertoireThree == 6 && (int)StepCondition.DotSnapshotDue == 7, "step-condition config compatibility");
Check((int)RotationScenario.CurrentStandard == 0 && (int)RotationScenario.Custom == 5, "scenario config compatibility");

var current = ScenarioRules.Resolve(RotationScenario.CurrentStandard);
Check(current.SongPlan == SongPlanMode.Standard3312 && current.UsesModernBurstOrder, "current standard scenario");
var standard249 = ScenarioRules.Resolve(RotationScenario.Standard249);
Check(standard249.RecommendedGcd == 2.49f && standard249.SongPlan == SongPlanMode.Standard3312, "2.49 remains standard");
var advanced = ScenarioRules.Resolve(RotationScenario.Advanced369);
Check(advanced.RecommendedGcd == 2.50f && advanced.SongPlan == SongPlanMode.Advanced369, "2.50 advanced scenario");
var recovery = ScenarioRules.Resolve(RotationScenario.DowntimeRecovery);
Check(recovery.ReplansDowntime && recovery.SongPlan == SongPlanMode.Standard3312, "downtime recovery scenario");
var legacy = ScenarioRules.Resolve(RotationScenario.LegacyNga);
Check(!legacy.UsesModernBurstOrder && legacy.UsesLegacyDotSnapshot, "legacy NGA isolation");

var p247 = GuideAxisRules.ResolveSongPlan(SongPlanMode.GuideAuto, 2.47f, 9f, 9f, 9f);
Check(p247.WandererCutRemaining == 2f && p247.MageCutRemaining == 2f && p247.ArmyCutRemaining == 11f, "2.47 -> 3-3-12 cut points");
Check(p247.SingingSecondsFor(ActionCatalog.WanderersMinuet) == 43f, "3-3-12 Wanderer duration");
Check(p247.SingingSecondsFor(ActionCatalog.MagesBallad) == 43f, "3-3-12 Mage duration");
Check(p247.SingingSecondsFor(ActionCatalog.ArmysPaeon) == 34f, "3-3-12 Army duration");

var p248 = GuideAxisRules.ResolveSongPlan(SongPlanMode.GuideAuto, 2.48f, 0f, 0f, 0f);
Check(p248.ArmyCutRemaining == 11f, "2.48 -> 3-3-12");

var p249 = GuideAxisRules.ResolveSongPlan(SongPlanMode.GuideAuto, 2.49f, 0f, 0f, 0f);
Check(p249.WandererCutRemaining == 2f && p249.MageCutRemaining == 5f && p249.ArmyCutRemaining == 8f, "2.49 -> 3-6-9 cut points");
Check(p249.SingingSecondsFor(ActionCatalog.WanderersMinuet) == 43f, "3-6-9 Wanderer duration");
Check(p249.SingingSecondsFor(ActionCatalog.MagesBallad) == 40f, "3-6-9 Mage duration");
Check(p249.SingingSecondsFor(ActionCatalog.ArmysPaeon) == 37f, "3-6-9 Army duration");

var p250 = GuideAxisRules.ResolveSongPlan(SongPlanMode.GuideAuto, 2.50f, 0f, 0f, 0f);
Check(p250.MageCutRemaining == 5f, "2.50 -> 3-6-9");

var custom = GuideAxisRules.ResolveSongPlan(SongPlanMode.Custom, 2.47f, 3f, 4f, 5f);
Check(custom.WandererCutRemaining == 3f && custom.MageCutRemaining == 4f && custom.ArmyCutRemaining == 5f, "custom plan remains editable");

Check(GuideAxisRules.ShouldSnapshotDots(31f, 34f, 4.9f), "Raging tail DoT snapshot");
Check(!GuideAxisRules.ShouldSnapshotDots(36f, 34f, 4.9f), "DoT snapshot does not overwrite too early");
Check(GuideAxisRules.ShouldUseApex(80, true, 0f), "80 Soul Voice inside buffs");
Check(GuideAxisRules.ShouldUseApex(85, false, 55f), "one-minute Apex dump");
Check(!GuideAxisRules.ShouldUseApex(75, true, 55f), "Apex requires 80 Soul Voice");
Check(GuideAxisRules.ShouldUseCurrentApex(80, true, false, 35f), "current burst Apex");
Check(GuideAxisRules.ShouldUseCurrentApex(80, false, true, 20f), "current Mage deadline Apex");
Check(GuideAxisRules.ShouldUseCurrentApex(100, false, true, 35f), "current Mage overcap Apex");
Check(!GuideAxisRules.ShouldUseCurrentApex(100, false, false, 20f), "current Army holds Apex");

Check(GuideAxisRules.IsLateWeave(ActionCatalog.BattleVoice), "Battle Voice delayed weave");
Check(GuideAxisRules.IsLateWeave(ActionCatalog.RadiantFinale), "Radiant Finale delayed weave");
Check(GuideAxisRules.IsLateWeave(ActionCatalog.RagingStrikes), "Raging Strikes delayed weave");
Check(!GuideAxisRules.IsLateWeave(ActionCatalog.EmpyrealArrow), "Empyreal Arrow remains ASAP");
Check(
    GuideAxisRules.SelectImmediateOgcd(true, true, true, ActionCatalog.HeartbreakShot) == ActionCatalog.PitchPerfect,
    "three-stack Pitch Perfect prevents repertoire overcap first");
Check(
    GuideAxisRules.SelectImmediateOgcd(false, true, true, ActionCatalog.HeartbreakShot) == ActionCatalog.EmpyrealArrow,
    "Empyreal Arrow is immediate before charge shot");
Check(
    GuideAxisRules.SelectImmediateOgcd(false, false, true, ActionCatalog.HeartbreakShot) == ActionCatalog.HeartbreakShot,
    "Heartbreak Shot is immediate when a charge is available");
Check(!GuideAxisRules.IsLateWeave(ActionCatalog.RadiantFinale, true), "modern Finale can start double weave");
Check(!GuideAxisRules.IsLateWeave(ActionCatalog.BattleVoice, true), "modern Battle Voice can finish double weave");
Check(GuideAxisRules.IsLateWeave(ActionCatalog.RagingStrikes, true), "modern Raging remains late weave");
Check(GuideAxisRules.IsLateWeave(ActionCatalog.RadiantFinale, false), "legacy Finale remains late weave");
Check(ExecutionRules.MaxOgcdPerGcd == 2, "hard two-weave ceiling");
Check(ExecutionRules.MinimumActionIntervalSeconds == 0.70, "0.70 second minimum interval");
Check(!ExecutionRules.CanAttemptOgcd(1, 1.5f, 0.8, 0f, false, 1), "Army full-stack single-weave ceiling");

Check(ActionCatalog.Find(ActionCatalog.HeartbreakShot)?.Name == "碎心箭", "level 92 action catalog uses Heartbreak Shot");
var level100Ogcd = LevelSyncRules.SelectOgcd(
    100,
    new HashSet<uint> { ActionCatalog.HeartbreakShot, ActionCatalog.Bloodletter });
Check(level100Ogcd?.ActionId == ActionCatalog.HeartbreakShot && level100Ogcd.Name == "碎心箭", "level 100 selects Heartbreak Shot");
var level50Ogcd = LevelSyncRules.SelectOgcd(50, new HashSet<uint> { ActionCatalog.Bloodletter });
Check(level50Ogcd?.ActionId == ActionCatalog.Bloodletter && level50Ogcd.Name == "失血箭", "level 50 keeps Bloodletter");

Check(RotationMath.EstimateDotTicks(5.8f, 3f) == 2, "DoT remaining tick estimate");
Check(RotationMath.RemainingDotPotency(2, 25) == 50, "DoT remaining potency");

PlannerTests.Run(Check);
EncounterTests.Run(Check);
if (args.Contains("--simulate")) Simulation.Run();
Console.WriteLine($"PASS: {checks} checks");
