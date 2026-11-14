using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// Frozen is a staged capture/execution process, not TimeFreeze. Its native
// coroutine has its own timer and can dispatch damage when that timer ends.
// This clock only controls explicitly registered guest replicas. Registration
// belongs to the persistent effect adapter, never to arbitrary local casts.
internal static class FrozenProcessClock
{
    internal sealed class Sample
    {
        internal MiCharacterProcessOptionsFrozen Options;
        internal float Duration,Elapsed,ObservedAt;
        internal bool Holding;
        internal int Phase;
    }
    private sealed class Scope
    {
        internal MiCharacterProcessOptionsFrozen Options;
        internal float Duration,Elapsed;
        internal bool Pending;
        internal bool Hold=true;
    }
    private static readonly Dictionary<IntPtr,Sample> samples=new();
    private static readonly Dictionary<IntPtr,Scope> scopes=new();
    private static readonly HashSet<IntPtr> tracked=new();
    internal static void Clear(){samples.Clear();scopes.Clear();tracked.Clear();}
    internal static void Track(MiCharacterProcessOptionsFrozen options)
    {if(options!=null)tracked.Add(options.Pointer);}
    internal static void Forget(MiCharacterProcessOptionsFrozen options)
    {if(options!=null){samples.Remove(options.Pointer);scopes.Remove(options.Pointer);tracked.Remove(options.Pointer);}}
    internal static void Retain(ISet<IntPtr> active)
    {
        foreach(var key in new List<IntPtr>(tracked))if(!active.Contains(key)&&!scopes.ContainsKey(key)){tracked.Remove(key);samples.Remove(key);}
    }
    internal static bool Read(MiCharacterProcessOptionsFrozen options,out Sample sample)
    {sample=null;return options!=null&&samples.TryGetValue(options.Pointer,out sample);}
    // Native Frozen treats negative duration as indefinite (unlike the
    // TimeFreeze option, which normally uses positive infinity).
    internal static bool Valid(float duration,float elapsed)=>float.IsFinite(duration)&&
        (duration==-1||duration>0&&duration<=86400)&&float.IsFinite(elapsed)&&elapsed>=0&&
        (duration==-1||elapsed<=duration);
    internal static bool Confirm(MiCharacterProcessOptionsFrozen options,float duration,float elapsed,bool hold=true)
    {
        if(options==null||!Valid(duration,elapsed)||NativeInput.Probe?.NativeGuest!=true)return false;
        Track(options);
        if(!scopes.TryGetValue(options.Pointer,out var scope))scopes[options.Pointer]=scope=new(){Options=options};
        scope.Duration=duration;
        scope.Hold=hold;
        if(!samples.TryGetValue(options.Pointer,out var sample)||Math.Abs(sample.Elapsed-elapsed)>.15f)
        {scope.Elapsed=elapsed;scope.Pending=true;}
        return true;
    }
    internal static bool AllowFollowUp(MiCharacterProcessOptionsFrozen options)=>
        NativeInput.Probe?.NativeGuest!=true||options==null||!scopes.ContainsKey(options.Pointer);
    private static MiCharacterProcessOptionsFrozen Options(MiCharacterProcessFrozen._coroRunProcess_d__15 state)=>
        state?._options?.TryCast<MiCharacterProcessOptionsFrozen>();
    private static void Correct(MiCharacterProcessFrozen._coroRunProcess_d__15 state,MiCharacterProcessOptionsFrozen options)
    {
        // State 0 initializes actors, staging, VFX and the timer; don't skip or
        // mutate those native transitions. State 1 is the yielded hold loop.
        if(state.__1__state!=1||NativeInput.Probe?.NativeGuest!=true||options==null||!scopes.TryGetValue(options.Pointer,out var scope))return;
        if(!scope.Hold){state._freezeDuration_5__5=0;state._time_5__6=1;return;}
        state._freezeDuration_5__5=scope.Duration;
        if(scope.Pending){state._time_5__6=scope.Elapsed;scope.Pending=false;}
        // The host is still holding this victim. Keep the guest's visual timer
        // at its end until the host reports the next stage; never execute the
        // kill/knockout locally just because this computer reached time first.
        if(scope.Duration>0&&state._time_5__6>scope.Duration)state._time_5__6=scope.Duration;
    }
    internal static void Before(MiCharacterProcessFrozen._coroRunProcess_d__15 state)
    {if(state!=null)Correct(state,Options(state));}
    internal static void After(MiCharacterProcessFrozen._coroRunProcess_d__15 state)
    {
        var options=Options(state);if(options==null||!tracked.Contains(options.Pointer))return;
        Correct(state,options);
        float duration=state._freezeDuration_5__5,elapsed=state._time_5__6;
        if(!float.IsFinite(duration)||!float.IsFinite(elapsed)||elapsed<0)return;
        if(duration<0)duration=-1;
        samples[options.Pointer]=new(){Options=options,Duration=duration,Elapsed=elapsed,
            Holding=state.__1__state==1,Phase=state.__1__state,ObservedAt=Time.realtimeSinceStartup};
    }
    // Dedicated non-shared native MoveNext at 0x181984030. Frozen does not call
    // MiCharacterProcess.updateTimer, so hooking that base method misses it.
    [HarmonyPatch(typeof(MiCharacterProcessFrozen._coroRunProcess_d__15),"MoveNext")]
    internal static class TimerHook
    {
        private static void Prefix(MiCharacterProcessFrozen._coroRunProcess_d__15 __instance)=>Before(__instance);
        private static void Postfix(MiCharacterProcessFrozen._coroRunProcess_d__15 __instance)=>After(__instance);
    }
    [HarmonyPatch(typeof(MiCharacterProcessFrozen),"startFollowUpProcess")]
    internal static class FollowUpHook
    {private static bool Prefix(MiCharacterProcessOptionsFrozen __0)=>AllowFollowUp(__0);}
}
