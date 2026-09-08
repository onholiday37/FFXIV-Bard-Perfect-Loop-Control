using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace BardPerfectLoop;

internal sealed class LiveBattlefield(Plugin plugin)
{
    private double updatedAt = double.NegativeInfinity;
    private ulong selectedId;
    private readonly EncounterPlanner encounter = new();
    public AoeCoverage Coverage { get; private set; } = AoeCoverage.Single;
    public EncounterDecision Encounter => encounter.Decision;
    public int Enemies { get; private set; }
    public string Summary { get; private set; } = "等待目标";
    public void Reset() { encounter.Reset(); updatedAt = double.NegativeInfinity; selectedId = 0; }

    public unsafe void Update(bool force = false)
    {
        var now = ActionObserver.Now;
        var target = plugin.ResolveBattleTarget();
        if (!force && target?.GameObjectId == selectedId && now - updatedAt < 0.05) return;
        updatedAt = now; selectedId = target?.GameObjectId ?? 0;
        var actors = Plugin.ObjectTable.OfType<IBattleChara>().ToArray();
        encounter.Update(now, Plugin.ClientState.TerritoryType, plugin.Configuration.Scenario == RotationScenario.Uwu && (plugin.IsInCombat || plugin.Engine.PullHasStarted),
            actors.Select(a => new EncounterActor(a.GameObjectId, a.BaseId, a.IsTargetable && !a.IsDead,
                a.CurrentHp, a.MaxHp, a.IsCasting ? a.CastActionId : 0, a.CurrentCastTime)).ToArray(),
            new(plugin.Configuration.UwuMinimumBurstWindow, plugin.Configuration.UwuReturnDelay,
                plugin.Configuration.UwuTitanBurstDelay, plugin.Configuration.UwuUltimaBurstDelay, plugin.Configuration.UwuHoldBurstForAdds));
        var player = Plugin.ObjectTable.LocalPlayer;
        if (target is null || player is null) { Coverage = new(0, 0, 0, 0); Enemies = 0; Summary = "无可攻击目标"; return; }
        var protect = Plugin.ClientState.TerritoryType == EncounterPlanner.Territory && plugin.Configuration.ProtectUwuMechanics;
        var enemies = actors.Where(a => a.IsTargetable && !a.IsDead && a.Address != 0 &&
            Vector3.Distance(player.Position, a.Position) <= 55 &&
            ActionManager.CanUseActionOnTarget(ActionCatalog.HeavyShot, (NativeObject*)a.Address))
            .Select(a => new EnemyPoint(a.GameObjectId, new(a.Position.X, a.Position.Z), a.HitboxRadius,
                a.GameObjectId == selectedId || (a.StatusFlags & StatusFlags.InCombat) != 0,
                protect && EncounterPlanner.Sensitive(a.BaseId))).ToArray();
        var primary = enemies.FirstOrDefault(a => a.Id == selectedId);
        if (primary.Id == 0) { Coverage = new(0, 0, 0, 0); return; }
        Coverage = AoeGeometry.Measure(new(player.Position.X, player.Position.Z), primary, enemies);
        Enemies = enemies.Count(a => a.Engaged);
        // Disabling automatic AoE never disables collision protection on inherent AoE skills.
        if (!plugin.Configuration.AutoAoe) Coverage = new(Math.Min(1, Coverage.Circle5), Math.Min(1, Coverage.Circle8), Math.Min(1, Coverage.Cone12), Math.Min(1, Coverage.Line25));
        Summary = $"已接战 {Enemies}；命中估计：音调/影噬 {Coverage.Circle5}，箭雨 {Coverage.Circle8}，扇形 {Coverage.Cone12}，直线 {Coverage.Line25}（0=越界或会误伤保护目标）";
    }

    public bool Allows(uint action) => Coverage.Allows(action);
    public bool IsEncounterBoss => plugin.ResolveBattleTarget() is { } target && target.BaseId == EncounterPlanner.BossFor(Encounter.Phase);
    public float AddLifetimeEstimate => plugin.ResolveBattleTarget()?.BaseId switch
    {
        EncounterPlanner.Satin or EncounterPlanner.Razor or EncounterPlanner.Bit => 10,
        _ => 300,
    };
}
