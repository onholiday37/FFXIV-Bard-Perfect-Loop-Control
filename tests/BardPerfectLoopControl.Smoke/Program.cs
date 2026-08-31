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

Check(GuideAxisRules.IsLateWeave(ActionCatalog.BattleVoice), "Battle Voice delayed weave");
Check(GuideAxisRules.IsLateWeave(ActionCatalog.RadiantFinale), "Radiant Finale delayed weave");
Check(GuideAxisRules.IsLateWeave(ActionCatalog.RagingStrikes), "Raging Strikes delayed weave");
Check(!GuideAxisRules.IsLateWeave(ActionCatalog.EmpyrealArrow), "Empyreal Arrow remains ASAP");
Check(ExecutionRules.MaxOgcdPerGcd == 2, "hard two-weave ceiling");
Check(ExecutionRules.MinimumActionIntervalSeconds == 0.70, "0.70 second minimum interval");

Check(RotationMath.EstimateDotTicks(5.8f, 3f) == 2, "DoT remaining tick estimate");
Check(RotationMath.RemainingDotPotency(2, 25) == 50, "DoT remaining potency");

Console.WriteLine($"PASS: {checks} guide-axis smoke checks");
