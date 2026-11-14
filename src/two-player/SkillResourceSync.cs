using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// Native Cooldown is a value type. fCooldown is a ratio, using MiGameTime's
// saved clock; use(seconds) extends only and resets the progress denominator.
// Restore the original duration/start pair instead, on fresh host samples.
internal sealed class SkillResourceSync
{
    [ThreadStatic] private static PlayerSkill submitting;
    internal static bool SubmitConfirmed(PlayerSkill skill,Func<bool> submit)
    {
        var previous=submitting;submitting=skill;
        try{return submit();}finally{submitting=previous;}
    }
    internal static bool ConfirmedResourceCommand(PlayerSkill skill)
    {
        if(NativeInput.Probe?.NativeGuest!=true||skill?.character?.controller==null)return false;
        if(submitting?.Pointer==skill.Pointer)return true;
        var c=skill.character.controller;
        return Matches(c.skillCommand,skill)||Matches(c.skillCommandActive,skill)||Matches(c.skillCommandProcess,skill);
    }
    private static bool Matches(SkillCommand command,PlayerSkill skill)=>command?.m_skill?.Pointer==skill.Pointer&&
        InteractionSync.CommandSerial(command)>0&&command.hasModifier(SkillCommand.ExecutionModifier.IgnoreAmmo);
    // Native bActivatable reads m_bHasSkillItem directly before the controller
    // considers IgnoreAmmo. Resource snapshots may already contain the spent
    // last arrow (including a mimic's copied skill). Scope only that resource
    // precondition to the host-confirmed action; keep all other native checks.
    [HarmonyPatch(typeof(PlayerSkill),"get_bActivatable")]
    internal static class ConfirmedItemActivation
    {
        private static void Prefix(PlayerSkill __instance,out bool __state)
        {__state=NativeInput.Probe?.NativeGuest==true&&!__instance.m_bHasSkillItem&&ConfirmedResourceCommand(__instance);if(__state)__instance.m_bHasSkillItem=true;}
        private static Exception Finalizer(Exception __exception,PlayerSkill __instance,bool __state)
        {if(__state)__instance.m_bHasSkillItem=false;return __exception;}
    }
    [HarmonyPatch(typeof(PlayerSkill),"get_bHasSkillItem")]
    internal static class ConfirmedItemAvailable
    {private static void Postfix(PlayerSkill __instance,ref bool __result)
        {if(!__result&&ConfirmedResourceCommand(__instance))__result=true;}}
    private string session;
    private long sequence=-1;
    private float nextError,nextCapture;
    private sealed class SkillCache
    {internal IntPtr Actor,Controller;internal float Refresh;internal List<(int Id,PlayerSkill Skill)> Skills;}
    private readonly Dictionary<string,SkillCache> cache=new(StringComparer.Ordinal);
    private static readonly Dictionary<string,HashSet<IntPtr>> confirmed=new(StringComparer.Ordinal);
    internal void Clear(){session=null;sequence=-1;nextError=nextCapture=0;confirmed.Clear();cache.Clear();}
    private List<(int Id,PlayerSkill Skill)> Skills(MiCharacter actor)
    {
        string source=GuestMove.Identity(actor);float now=Time.realtimeSinceStartup;
        if(cache.TryGetValue(source,out var cached)&&cached.Actor==actor.Pointer&&cached.Controller==actor.controller.Pointer&&now<cached.Refresh)return cached.Skills;
        var result=new List<(int,PlayerSkill)>();var seen=new HashSet<IntPtr>();
        foreach(int id in ResourceProtocol.SkillIds)
        {
            var skill=actor.controller.getSkill((Skill.SkillType)id)?.TryCast<PlayerSkill>();
            if(skill!=null&&seen.Add(skill.Pointer))result.Add((id,skill));
        }
        cache[source]=new(){Actor=actor.Pointer,Controller=actor.controller.Pointer,Refresh=now+1,Skills=result};return result;
    }
    internal List<ResourceProtocol.Group> Capture(IReadOnlyList<MiCharacter> crew)
    {
        var groups=new List<ResourceProtocol.Group>();
        // Never resend a cached remaining-time sample: omit the tail between
        // 10 Hz samples, so the guest's native saved clock keeps counting.
        float sampledAt=Time.realtimeSinceStartup;if(sampledAt<nextCapture)return groups;nextCapture=sampledAt+0.1f;
        foreach(var actor in crew)try
        {
            if(actor?.controller==null||actor.inventory==null)continue;
            string source=GuestMove.Identity(actor);if(!SyncProtocol.Identity(source))continue;
            var ammo=new int[4];for(int i=0;i<4;i++)
            {
                ammo[i]=actor.inventory.iCount((MiCharacterInventory.ItemType)i);
                if(ammo[i]<0||ammo[i]>ResourceProtocol.MaxAmmo)throw new InvalidOperationException("Invalid native ammunition count");
            }
            var skills=Skills(actor);if(skills.Count==0)continue;
            var entries=new List<ResourceProtocol.Entry>();float now=MiGameTime.timeSaved;
            foreach(var item in skills)
            {
                var cooldown=item.Skill.m_cooldown;float duration=cooldown.m_fCooldown;
                if(!float.IsFinite(duration)||duration<0||duration>ResourceProtocol.MaxDuration||!float.IsFinite(cooldown.m_fStartTime)||!float.IsFinite(now))
                    throw new InvalidOperationException("Invalid native cooldown");
                float remaining=Math.Clamp(cooldown.m_fStartTime+duration-now,0,duration);
                if(remaining>0)entries.Add(new(item.Id,duration,remaining));
            }
            groups.Add(new(source,ammo,entries.ToArray()));
        }
        catch(Exception ex){Error("capture",ex);}// Omit unknown, never publish a partial sample as ready.
        return groups;
    }
    internal void Apply(ResourceProtocol.Group[] groups,string frameSession,long frameSequence)
    {
        if(frameSession==session&&frameSequence<=sequence)return;
        if(frameSession!=session){confirmed.Clear();cache.Clear();}session=frameSession;sequence=frameSequence;
        if(groups==null)return; // omitted family is unknown, not empty inventory
        foreach(var group in groups)try
        {
            var actor=MoveProbe.CrewById(group.Source);
            if(actor?.controller==null||actor.inventory==null)continue;
            var skills=Skills(actor);var pointers=new HashSet<IntPtr>();foreach(var item in skills)pointers.Add(item.Skill.Pointer);
            confirmed[group.Source]=pointers;
            for(int i=0;i<4;i++)
            {
                var type=(MiCharacterInventory.ItemType)i;int delta=group.Ammo[i]-actor.inventory.iCount(type);
                if(delta>0)actor.inventory.insert(type,delta,true,true);
                else if(delta<0)actor.inventory.remove(type,-delta);
            }
            var entries=new Dictionary<int,ResourceProtocol.Entry>();foreach(var entry in group.Cooldowns)entries[entry.Skill]=entry;
            float now=MiGameTime.timeSaved;
            foreach(var item in skills)
            {
                var old=item.Skill.m_cooldown;
                float duration=0,remaining=0;
                if(entries.TryGetValue(item.Id,out var entry)){duration=entry.Duration;remaining=entry.Remaining;}
                float oldRemaining=Math.Clamp(old.m_fStartTime+old.m_fCooldown-now,0,Math.Max(0,old.m_fCooldown));
                // Keep the native local countdown between host frames. Small
                // packet jitter must not restart the circle on every sample.
                if(float.IsFinite(oldRemaining)&&Math.Abs(old.m_fCooldown-duration)<0.001f&&Math.Abs(oldRemaining-remaining)<0.10f)continue;
                old.m_fCooldown=duration;old.m_fStartTime=now-(duration-remaining);
                item.Skill.m_cooldown=old; // setter is required for the native struct copy.
            }
        }
        catch(Exception ex){Error("apply",ex);}
    }
    private void Error(string action,Exception ex)
    {
        float now=Time.realtimeSinceStartup;if(now<nextError)return;nextError=now+5;
        MoveProbePlugin.ProbeLog.LogWarning("Skill resources "+action+": "+ex.GetType().Name+" "+ex.Message);
    }
    // Host and single-player retain all original resource rules. Only skills
    // already covered by a guest's complete authoritative sample are scoped.
    internal static bool AllowNativeCooldown(PlayerSkill skill)
    {
        if(NativeInput.Probe?.NativeGuest!=true||skill==null)return true;
        try
        {
            string source=GuestMove.Identity(skill.character);
            return source==null||!confirmed.TryGetValue(source,out var pointers)||!pointers.Contains(skill.Pointer);
        }
        catch{return true;}// A native skill can be released during scene teardown.
    }
    [HarmonyPatch(typeof(PlayerSkill),"startCooldown",new Type[]{typeof(float)})]
    internal static class StartTimed
    {private static bool Prefix(PlayerSkill __instance)=>AllowNativeCooldown(__instance);}
    [HarmonyPatch(typeof(PlayerSkill),"startCooldown",new Type[]{})]
    internal static class StartDefault
    {private static bool Prefix(PlayerSkill __instance)=>AllowNativeCooldown(__instance);}
    // Overtake's native bActivatable reads iAmmoCount before honoring the
    // command's IgnoreAmmo execution modifier. A confirmed shot can arrive
    // after the authoritative resource sample has consumed the last round.
    internal static bool ConfirmedGun(SkillGunAttackOvertake skill)
    {
        if(NativeInput.Probe?.NativeGuest!=true||skill?.charOrigin?.controller==null)return false;
        var c=skill.charOrigin.controller;
        foreach(var command in new[]{c.skillCommand,c.skillCommandActive,c.skillCommandProcess})
            if(command?.m_skill?.Pointer==skill.Pointer&&InteractionSync.CommandSerial(command)>0&&
                command.hasModifier(SkillCommand.ExecutionModifier.IgnoreAmmo))return true;
        return false;
    }
    [HarmonyPatch(typeof(SkillGunAttackOvertake),"get_iAmmoCount")]
    internal static class ConfirmedGunAmmo
    {private static void Postfix(SkillGunAttackOvertake __instance,ref int __result)
        {if(__result==0&&ConfirmedGun(__instance))__result=1;}}
}
