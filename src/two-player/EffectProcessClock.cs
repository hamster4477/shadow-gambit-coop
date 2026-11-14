using System;
using HarmonyLib;
using UnityEngine;

// The installed Il2CppInterop bridge marshals ref float with ldind.i rather
// than ldind.r4. Never detour updateTimer: it also advances revive and other
// unrelated native processes. MoveNext has no by-ref arguments and gives us
// access to the same elapsed field before the original timer advances it.
internal static class EffectProcessClock
{
    private static float nextError;
    internal static void Before(MiCharacterProcess._coroRunTimer_d__6 state)
    {
        try
        {
            if(state==null||state.__1__state!=1)return;
            var options=state._options;
            if(!Tracked(options))return;
            float elapsed=state._fTimeCurrent_5__2;
            TimeFreezeSync.BeforeTimer(options,ref elapsed);
            BlindSync.BeforeTimer(options,ref elapsed);
            state._fTimeCurrent_5__2=elapsed;
        }
        catch(Exception ex){Error(ex);}
    }
    internal static void After(MiCharacterProcess._coroRunTimer_d__6 state)
    {
        try
        {
            if(state==null)return;
            var options=state._options;
            if(!Tracked(options))return;
            float elapsed=state._fTimeCurrent_5__2;
            TimeFreezeSync.ObserveTimer(options,elapsed);
            BlindSync.ObserveTimer(options,elapsed,state._fDuration_5__3);
        }
        catch(Exception ex){Error(ex);}
    }
    private static bool Tracked(MiCharacterProcessOptions options)=>options!=null&&
        (options.TryCast<MiCharacterProcessOptionsTimeFreeze>()!=null||options.TryCast<MiCharacterProcessOptionsBlind>()!=null);
    private static void Error(Exception ex)
    {
        float now=Time.realtimeSinceStartup;if(now<nextError)return;nextError=now+5;
        MoveProbePlugin.ProbeLog.LogWarning("Effect clock: "+ex.GetType().Name+" "+ex.Message);
    }
    // Native 0x1819757D0: state 0 initializes elapsed/duration; state 1 calls
    // the virtual updateTimer, then onUpdateTimer and the completion branch.
    // Leave every original transition and the character-specific delta intact.
    [HarmonyPatch(typeof(MiCharacterProcess._coroRunTimer_d__6),"MoveNext")]
    internal static class TimerHook
    {
        private static void Prefix(MiCharacterProcess._coroRunTimer_d__6 __instance)=>Before(__instance);
        private static void Postfix(MiCharacterProcess._coroRunTimer_d__6 __instance)=>After(__instance);
    }
}
