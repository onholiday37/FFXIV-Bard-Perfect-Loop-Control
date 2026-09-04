using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Numerics;
using Dalamud.Game.ClientState.JobGauge.Types;
using System.Linq;
using GaugeSong = Dalamud.Game.ClientState.JobGauge.Enums.Song;

namespace BardPerfectLoop;

internal readonly record struct ExecutedAction(ActionType Type, uint Id, ulong Target, double Time, int RepertoireBefore, int SoulBefore, int CodaBefore);

/// <summary>Observes local execution, including manual actions. Does not alter
/// arguments/results. Local execution is NOT proof of server-confirmed damage.</summary>
internal sealed unsafe class ActionObserver : IDisposable
{
    private readonly Hook<ActionManager.Delegates.UseActionLocation> hook;
    private readonly ConcurrentQueue<ExecutedAction> events = new();
    public bool Failed { get; private set; }
    public ActionObserver()
    {
        hook = Plugin.Interop.HookFromAddress<ActionManager.Delegates.UseActionLocation>(
            (nint)ActionManager.MemberFunctionPointers.UseActionLocation, Detour);
        hook.Enable();
    }
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public bool TryRead(out ExecutedAction action) => events.TryDequeue(out action);
    private bool Detour(ActionManager* manager, ActionType type, uint id, ulong target, Vector3* location, uint extra, byte a7)
    {
        var repertoire = -1;
        var soul = -1;
        var coda = -1;
        if (type == ActionType.Action && id is ActionCatalog.EmpyrealArrow or ActionCatalog.RadiantFinale)
        {
            try { var gauge = Plugin.JobGauges.Get<BRDGauge>(); repertoire = gauge.Repertoire; soul = gauge.SoulVoice; coda = gauge.Coda.Count(x => x != GaugeSong.None); }
            catch { /* Unknown pre-state: do not invent pending resources. */ }
        }
        var result = hook.Original(manager, type, id, target, location, extra, a7);
        if (result)
        {
            try
            {
                if (events.Count < 128) events.Enqueue(new(type, id, target, Now, repertoire, soul, coda));
                else Failed = true;
            }
            catch { Failed = true; }
        }
        return result;
    }
    public void Dispose() => hook.Dispose();
}
