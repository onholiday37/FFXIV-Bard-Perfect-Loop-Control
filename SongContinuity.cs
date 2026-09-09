using System;
using System.Collections.Generic;
using System.Linq;

namespace BardPerfectLoop;

public readonly record struct SongOption(uint Action, float Ready);
public readonly record struct SongChoice(uint Action, float Ready, bool Fallback);

public static class SongContinuity
{
    // Shared by live input, simulation and the last-moment send guard.
    // A weave budget can postpone a song, never advance its configured cut.
    public static float SwitchIn(float remaining, float cutRemaining) =>
        float.IsFinite(remaining) && float.IsFinite(cutRemaining)
            ? Math.Max(0, remaining - cutRemaining) : float.PositiveInfinity;

    public static float Earliest(float earliest, float ready, bool active, float switchIn) =>
        Math.Max(earliest, Math.Max(ready, active ? switchIn : 0));

    public static bool CanSend(uint action, uint expectedAction, float switchIn) =>
        IsSong(action) && action == expectedAction && float.IsFinite(switchIn) && switchIn <= 0;

    public static bool IsSong(uint action) => action is ActionCatalog.WanderersMinuet or ActionCatalog.MagesBallad or ActionCatalog.ArmysPaeon;
    public static SongChoice Choose(IReadOnlyList<SongOption> ordered, float remaining, bool active)
    {
        var valid = ordered.Where(s => float.IsFinite(s.Ready)).ToArray();
        if (valid.Length == 0) return default;
        var preferred = valid[0];
        if (active && preferred.Ready < remaining - 0.1f || !active && preferred.Ready <= 0.001f)
            return new(preferred.Action, preferred.Ready, false);
        var soonest = valid.OrderBy(s => s.Ready).First();
        return new(soonest.Action, soonest.Ready, soonest.Action != preferred.Action);
    }

    // Reserve an actual, legal weave for a due song rather than letting short-term
    // attack value continually postpone the switch. Never add a third weave.
    public static WeavePlan? Plan(PlannerState s)
    {
        var action = s.NextSongAction;
        if (action == 0 || !IsSong(action) || s.Slots <= 0 || s.Excluded.Contains(action) || s.Level < ActionCatalog.MinimumLevel(action)) return null;
        var at = Earliest(s.Earliest, s.NextSongReady, s.Song != BardSong.None, s.SongSwitchIn);
        if (!float.IsFinite(at) || !CombatTiming.Fits(at, s.NextGcd, s.Lock, s.Margin) || at >= s.TargetLifetime) return null;
        if (s.Song == BardSong.Wanderer && s.Repertoire > 0)
        {
            var pitchAt = Math.Max(s.Earliest, s.PitchReady);
            var songAt = Math.Max(at, pitchAt + Math.Max(0.7f, s.Lock));
            if (s.Slots >= 2 && !s.Excluded.Contains(ActionCatalog.PitchPerfect) && s.Aoe.Allows(ActionCatalog.PitchPerfect) &&
                CombatTiming.Fits(pitchAt, s.NextGcd, s.Lock, s.Margin) && CombatTiming.Fits(songAt, s.NextGcd, s.Lock, s.Margin) &&
                songAt < s.SongRemaining && songAt < s.TargetLifetime)
                return new([new(ActionCatalog.PitchPerfect, pitchAt, s.Repertoire), new(action, songAt, 0)], 0, "先清音调，再接下一首歌");
            // A legal configured cut is already in THIS window. Deferring it to
            // save repertoire can lose this slot and drift the whole song cycle.
            return new([new(action, at, 0)], 0, "切歌窗口无法同时清音调，按配置切歌；不额外插入");
        }
        return new([new(action, at, 0)], 0, s.Song == BardSong.None ? "当前没歌，优先补歌" : "已到切歌时间，先给下一首歌留插入位置");
    }
}
