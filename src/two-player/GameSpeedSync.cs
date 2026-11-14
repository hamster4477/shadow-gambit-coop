using System;
using HarmonyLib;
using UnityEngine;

// Override only the clock's speed calculation; native and network pause locks
// remain owned by their existing callers. No writes to Time.timeScale.
internal static class GameSpeedSync
{
    internal static bool BlockGuestFastForward=>!TrainingSession.Suspended&&
        (NativeInput.Probe?.NativeGuest==true||MoveProbePlugin.LastPair.Value.StartsWith("G ",StringComparison.Ordinal));
    [HarmonyPatch(typeof(FastForward),"get_bCanFastForward")]
    internal static class FastForwardAllowed
    {private static void Postfix(ref bool __result){if(BlockGuestFastForward)__result=false;}}
    [HarmonyPatch(typeof(FastForwardPlayerInput),"bFastForward")]
    internal static class FastForwardInput
    {private static bool Prefix(ref bool __result){if(!BlockGuestFastForward)return true;__result=false;return false;}}
    private static float? speed;
    private static float until;
    private const int Owner=1<<26;
    private static bool owned;
    private static float? alarmRemaining;
    internal static float? CaptureAlarm()
    {
        var alarm=SceneLookup.Find<AlarmSystem>();
        return alarm==null?null:Math.Max(0,alarm.getLargestRemainingLocalAlarmDuration());
    }
    [HarmonyPatch(typeof(AlarmSystem),"getLargestRemainingLocalAlarmDuration")]
    internal static class AlarmClockHook
    {
        private static void Postfix(ref float __result)
        {if(alarmRemaining.HasValue&&Time.realtimeSinceStartup<=until&&!SaveProcessManager.bLoading)__result=alarmRemaining.Value;}
    }
    private static PostEffectHandler effectOwner;
    // Native enableSlowMotion changes bit 0 only; bit 1 belongs to Shadow Mode.
    // Verified against the shipped native enableSlowMotion/enableShadowMode bodies.
    internal static bool? CaptureEffect()
    {
        var effect=SceneLookup.Find<PostEffectHandler>();
        return effect==null?null:(effect.m_iCurrentActivatedEffect&1)!=0;
    }
    private static void ApplyEffect(bool? active)
    {
        if(active==null){ClearEffect();return;}
        var effect=SceneLookup.Find<PostEffectHandler>();
        if(effect==null)return;
        if(effectOwner!=null&&effectOwner!=effect)ClearEffect();
        if(((effect.m_iCurrentActivatedEffect&1)!=0)!=active.Value)
            effect.enableSlowMotion(active.Value);
        effectOwner=effect;
    }
    private static void ClearEffect()
    {
        if(effectOwner!=null&&(effectOwner.m_iCurrentActivatedEffect&1)!=0)
            effectOwner.enableSlowMotion(false);
        effectOwner=null;
    }
    private static void SetNative(float value)
    {
        if(!MiGameTime.bInstanceExists)return;
        MiGameTime.instance.setTimeScale(value,Owner);owned=value!=1f;
    }
    internal static float? Capture()
    {
        if(!MiGameTime.bInstanceExists)return null;
        float value=MiGameTime.instance.getCalculatedTimeScale();
        return float.IsFinite(value)&&value>=.01f&&value<=8f?value:null;
    }
    internal static void Observe(LobbyProtocol.Frame frame,int localMission)
    {
        if(frame?.gameSpeed==null||frame.mission!=localMission||localMission==0||
            frame.phase=="loading"||SaveProcessManager.bLoading)
        {Clear();return;}
        float value=frame.gameSpeed.Value;
        if(!float.IsFinite(value)||value<.01f||value>8f){Clear();return;}
        bool changed=!speed.HasValue||Math.Abs(speed.Value-value)>.001f;
        // setTimeScale recalculates the native clock synchronously. Publish the
        // new value first, otherwise our calculation hook returns the old speed.
        speed=value;until=Time.realtimeSinceStartup+1f;
        if(changed)SetNative(value);
        ApplyEffect(frame.slowEffect);
        alarmRemaining=frame.alarmRemaining;
    }
    internal static void Clear(){speed=null;alarmRemaining=null;if(owned)SetNative(1f);ClearEffect();}
    [HarmonyPatch(typeof(MiGameTime),"getCalculatedTimeScale")]
    internal static class ClockHook
    {
        private static void Postfix(ref float __result)
        {
            if(__result>0f&&speed.HasValue&&Time.realtimeSinceStartup<=until&&!SaveProcessManager.bLoading)
                __result=speed.Value;
        }
    }
}
