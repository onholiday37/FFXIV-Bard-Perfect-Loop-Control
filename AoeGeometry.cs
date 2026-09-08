using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BardPerfectLoop;

public enum AoeShape { Circle5, Circle8, Cone12, Line25 }
public readonly record struct EnemyPoint(ulong Id, Vector2 Position, float Radius, bool Engaged, bool Protected);
public readonly record struct AoeCoverage(int Circle5, int Circle8, int Cone12, int Line25)
{
    public static AoeCoverage Single => new(1, 1, 1, 1);
    public int Count(AoeShape shape) => shape switch { AoeShape.Circle5 => Circle5, AoeShape.Circle8 => Circle8, AoeShape.Cone12 => Cone12, _ => Line25 };
    public float PitchMultiplier => Circle5 > 0 ? 1 + 0.5f * (Circle5 - 1) : 0;
    public static AoeShape? Shape(uint action) => action switch
    {
        ActionCatalog.QuickNock or ActionCatalog.Ladonsbite => AoeShape.Cone12,
        ActionCatalog.RainOfDeath => AoeShape.Circle8,
        ActionCatalog.PitchPerfect or ActionCatalog.Shadowbite or ActionCatalog.ResonantArrow or ActionCatalog.RadiantEncore => AoeShape.Circle5,
        ActionCatalog.ApexArrow or ActionCatalog.BlastArrow => AoeShape.Line25,
        _ => null,
    };
    public bool Allows(uint action) => Shape(action) is not { } shape || Count(shape) > 0;
}

/// <summary>Conservative planar hit estimation around the player's selected target.
/// Count with an inner margin; protect sensitive/unengaged enemies with an outer margin.
/// Never count the whole arena as though one attack could hit everything.</summary>
public static class AoeGeometry
{
    public static AoeCoverage Measure(Vector2 player, EnemyPoint primary, IReadOnlyList<EnemyPoint> enemies)
    {
        int Count(AoeShape shape)
        {
            if (!Hits(shape, player, primary.Position, primary, -0.25f)) return 0;
            if (enemies.Any(e => e.Id != primary.Id && (e.Protected || !e.Engaged) && Hits(shape, player, primary.Position, e, 0.5f))) return 0;
            return enemies.Count(e => (e.Engaged || e.Id == primary.Id) && (e.Id == primary.Id || !e.Protected) && Hits(shape, player, primary.Position, e, -0.25f));
        }
        return new(Count(AoeShape.Circle5), Count(AoeShape.Circle8), Count(AoeShape.Cone12), Count(AoeShape.Line25));
    }
    public static bool Hits(AoeShape shape, Vector2 player, Vector2 aim, EnemyPoint enemy, float margin = 0)
    {
        var radius = Math.Max(0, enemy.Radius + margin);
        if (shape is AoeShape.Circle5 or AoeShape.Circle8)
            return Vector2.Distance(aim, enemy.Position) <= (shape == AoeShape.Circle5 ? 5 : 8) + radius;
        var direction = aim - player;
        if (direction.LengthSquared() < 0.001f) return false;
        direction = Vector2.Normalize(direction);
        var delta = enemy.Position - player;
        var forward = Vector2.Dot(delta, direction);
        var side = Math.Abs(delta.X * direction.Y - delta.Y * direction.X);
        if (shape == AoeShape.Line25) return forward >= -radius && forward <= 25 + radius && side <= 2 + radius;
        // 90 degree cone. Native client still validates range/facing before execution.
        return delta.Length() <= 12 + radius && forward >= -radius && side - forward <= radius * MathF.Sqrt(2);
    }
}
