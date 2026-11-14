using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

// Wheel UI is local; changing form is authoritative. Delay dispatch until
// the wheel has released its pause/input lock, never generate a local copy.
internal static class MimicChoice
{
    private sealed class Pending { internal MiCharacter Actor; internal int Form; internal float Until; }
    private static readonly Dictionary<IntPtr,Pending> pending=new();
    private static UITricksterMimicWheel activeWheel;
    private static SkillBeingATrickster activeSkill;
    internal static void Clear(){pending.Clear();activeWheel=null;activeSkill=null;}
    [HarmonyPatch(typeof(SkillBeingATrickster),"onTricksterUISceneLoaded")]
    internal static class WheelOpened
    {
        private static void Postfix(SkillBeingATrickster __instance,UITricksterMimicWheel __0)
        {
            if(NativeInput.Probe?.NativeGuest!=true)return;
            activeWheel=__0;activeSkill=__instance;
            MoveProbePlugin.ProbeLog.LogInfo("Mimic wheel opened caster="+GuestMove.Identity(__instance.character));
        }
    }
    internal static bool Choose(SkillBeingATrickster skill,int form)
    {
        var probe=NativeInput.Probe;var actor=skill?.charCurrentMimicry??skill?.m_charExecutedBy??skill?.character;
        if(probe?.NativeGuest!=true)return true;
        // Native cancel only resets execution state; disassembly confirms it
        // neither spawns nor changes selection or form.
        skill?.onMimicUICanceled();
        if(actor==null||!probe.GuestOrderOwner(actor)||form<0){MoveProbePlugin.ProbeLog.LogWarning("Mimic choice denied actor="+GuestMove.Identity(actor)+" form="+form);return false;}
        pending[actor.Pointer]=new(){Actor=actor,Form=form,Until=Time.realtimeSinceStartup+10f};
        MoveProbePlugin.ProbeLog.LogInfo("Mimic choice queued actor="+GuestMove.Identity(actor)+" form="+form);
        return false;
    }
    internal static void Tick()
    {
        var probe=NativeInput.Probe;
        if(probe?.NativeGuest!=true){Clear();return;}
        foreach(var pair in new List<KeyValuePair<IntPtr,Pending>>(pending))
        {
            var p=pair.Value;
            if(p.Actor==null||!probe.GuestOrderOwner(p.Actor)||Time.realtimeSinceStartup>=p.Until||
                p.Actor.health?.eHealthState!=MiCharacterHealth.HealthState.Alive)
            {pending.Remove(pair.Key);continue;}
            if(Time.timeScale<=0||MiCutsceneHandler.bCutsceneRunning||!probe.ListensTo(p.Actor))continue;
            if(probe.TryCapture(p.Actor,"MIMIC "+p.Form.ToString(CultureInfo.InvariantCulture)))
                pending.Remove(pair.Key);
        }
    }
    // Intercept the parameterless wheel confirmation, not its value-struct
    // callback. Original code still closes the wheel and releases UI locks.
    internal static void Confirm(UITricksterMimicWheel wheel)
    {
        if(NativeInput.Probe?.NativeGuest!=true||wheel==null)return;
        var callback=wheel.m_delCallback;
        var skill=wheel==activeWheel?activeSkill:null;
        skill??=callback?.Target?.TryCast<SkillBeingATrickster>();
        skill??=wheel.m_delCanceled?.Target?.TryCast<SkillBeingATrickster>();
        // Suppress local spawning even if the native callback cannot be resolved.
        wheel.m_delCallback=null;
        if(skill==null){MoveProbePlugin.ProbeLog.LogWarning("Mimic wheel confirmation missing caster");return;}
        var slots=wheel.m_liSlots;
        if(slots!=null)foreach(var slot in slots)
            if(slot!=null&&slot.bHovered&&slot.playerDef!=null)
            {Choose(skill,slot.playerDef.m_eCharacterName.m_iValue);return;}
        skill.onMimicUICanceled();
    }
    [HarmonyPatch(typeof(UITricksterMimicWheel),"_UILateUpdate_b__20_0")]
    internal static class ChoiceHook
    {
        private static void Prefix(UITricksterMimicWheel __instance)
        {
            try{Confirm(__instance);}
            catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Mimic wheel: "+ex.GetType().Name);}
        }
    }
}
