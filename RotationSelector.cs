using System;
using System.Collections.Generic;
using System.Linq;

namespace BardPerfectLoop;

public static class RotationSelector
{
    public static RotationStep? Select(IEnumerable<RotationStep> steps, ShadowActionKind kind,
        IReadOnlySet<uint> rejected, Func<uint, bool> allowed, Func<uint, bool> learned,
        Func<uint, bool> ready, Func<RotationStep, bool> condition) => steps
        .Where(step => step.Enabled && step.Kind == kind && !rejected.Contains(step.ActionId))
        .Where(step => allowed(step.ActionId) && learned(step.ActionId))
        // Shared GCD recast is intentionally allowed for normal queueing.
        .Where(step => kind != ShadowActionKind.Ogcd || ready(step.ActionId))
        .OrderByDescending(step => step.Priority)
        .FirstOrDefault(condition);
}
