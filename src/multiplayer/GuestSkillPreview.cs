using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

// Presentation only: never cancel a skill, change its target, or emit noise.
// Native skill-owned range and target-owned noise/trajectory objects are
// separate owners; both have to be retired when the local intent ends.
internal static class GuestSkillPreview
{
    private sealed class Entry
    {
        internal PlayerSkill Skill;
        internal readonly Dictionary<IntPtr,MiCharacterTargetPlayer> Targets=new();
        internal bool Retired,SeenCommand,Pending;
        internal int Generation,ExecutedGeneration=-1,StartFrame=-1;
        internal float Until;
    }
    private static readonly Dictionary<IntPtr,Entry> entries=new();
    private static bool Guest(PlayerSkill skill)=>skill?.character!=null&&NativeInput.Probe?.MirroredCrew(skill.character)==true;
    private static Entry Get(PlayerSkill skill)
    {if(!entries.TryGetValue(skill.Pointer,out var e))entries[skill.Pointer]=e=new(){Skill=skill};return e;}
    internal static bool Aiming(PlayerSkill skill)
    {
        var aim=skill?.character?.controllerPlayer?.m_playerInput?.m_aiming;
        return aim?.bIsActive==true&&aim.m_playerSkill?.Pointer==skill.Pointer;
    }
    private static bool Matches(SkillCommand command,PlayerSkill skill)=>command?.m_skill?.Pointer==skill.Pointer;
    private static bool Commands(PlayerSkill skill)
    {
        var c=skill.character?.controller;
        return c!=null&&(Matches(c.skillCommand,skill)||Matches(c.skillCommandActive,skill)||Matches(c.skillCommandProcess,skill));
    }
    private static bool Context(PlayerSkill skill,ContextAction action)
    {
        var input=skill.character?.controllerPlayer?.m_playerInput;
        return action?.bActive==true&&action.bEnabled&&Matches(action.m_skillCommand,skill)&&
            action.entity!=null&&input?.m_raycastCache?.entity?.Pointer==action.entity.Pointer;
    }
    private static bool Preparing(Entry e)
    {
        var actor=e.Skill.character;
        if(actor?.health?.eHealthState!=MiCharacterHealth.HealthState.Alive)return false;
        bool command=Commands(e.Skill);if(command)e.SeenCommand=true;
        if(Aiming(e.Skill)||Time.frameCount==e.StartFrame)return true;
        if(e.Pending&&Time.realtimeSinceStartup<e.Until)return true;
        if(command)return true;
        if(e.SeenCommand)return false;
        var trigger=actor.controllerPlayer?.m_characterPlayerTrigger;
        return Context(e.Skill,trigger?.m_contextAction)||Context(e.Skill,trigger?.m_contextActionAdvanced);
    }
    internal static void ReleaseRange(PlayerSkill skill)
    {
        var range=skill?.m_rangeVisualisation;
        if(range?.bExists==true){range.free();skill.m_rangeVisualisation=range;}
    }
    private static void Hide(Entry e)
    {
        foreach(var target in e.Targets.Values)target?.disable();
        e.Targets.Clear();
        e.Skill.m_target?.disable();ReleaseRange(e.Skill);
    }
    private static void Retire(Entry e){e.Retired=true;e.Pending=false;Hide(e);}
    internal static void Started(PlayerSkill skill)
    {
        SkillLifecycleTrace.SkillEvent(skill,"aim-start");
        if(!Guest(skill))return;
        if(Melee.Contact(skill.eSkillType)){GuestPreview.AimingStarted(skill.character);return;}
        GuestPreview.Cancel(skill.character);
        var e=Get(skill);e.Generation++;e.Retired=false;e.Pending=false;e.SeenCommand=false;e.StartFrame=Time.frameCount;
    }
    internal static void Stopped(PlayerSkill skill)
    {
        SkillLifecycleTrace.SkillEvent(skill,"aim-stop");
        if(!Guest(skill)||Melee.Contact(skill.eSkillType))return;
        if(entries.TryGetValue(skill.Pointer,out var e)&&!e.Pending&&!Commands(skill))Retire(e);
    }
    internal static void Submitted(MiCharacter actor,SkillCommand command)
    {
        var skill=command?.m_skill?.TryCast<PlayerSkill>();
        if(!Guest(skill)||skill.character?.Pointer!=actor?.Pointer||Melee.Contact(skill.eSkillType))return;
        var e=Get(skill);e.Generation++;e.Retired=false;e.SeenCommand=false;e.Pending=true;e.Until=Time.realtimeSinceStartup+30;
    }
    internal static void Executed(MiCharacter actor,SkillCommand command)
    {
        var skill=command?.m_skill?.TryCast<PlayerSkill>();
        if(!Guest(skill)||skill.character?.Pointer!=actor?.Pointer||!entries.TryGetValue(skill.Pointer,out var e)||e.Retired)return;
        e.ExecutedGeneration=e.Generation;e.Pending=false;e.SeenCommand=true;
    }
    internal static void Completed(PlayerSkill skill)
    {
        SkillLifecycleTrace.SkillEvent(skill,"complete");
        if(!Guest(skill)||!entries.TryGetValue(skill.Pointer,out var e))return;
        // A prior cast can finish after the player prepares the same ability
        // again. Do not erase that newer aiming/queued intent.
        if(e.ExecutedGeneration<e.Generation&&(Aiming(skill)||e.Pending))return;
        Retire(e);
    }
    internal static bool Allow(MiCharacterTargetPlayer target)
    {
        var skill=target?.m_playerSkill;if(!Guest(skill))return true;
        var e=Get(skill);if(e.Retired)return false;e.Targets[target.Pointer]=target;
        return !e.Retired&&Preparing(e);
    }
    internal static bool AllowRange(PlayerSkill skill)
    {
        if(!Guest(skill))return true;
        bool allowed=Melee.Contact(skill.eSkillType)?GuestPreview.Preparing(skill):AllowSkill(skill);
        if(!allowed)ReleaseRange(skill);return allowed;
    }
    private static bool AllowSkill(PlayerSkill skill){var e=Get(skill);return !e.Retired&&Preparing(e);}
    internal static void Cancel(MiCharacter actor)
    {foreach(var e in entries.Values)if(e.Skill.character?.Pointer==actor?.Pointer)Retire(e);}
    internal static void Clear(MiCharacter actor=null)
    {
        var remove=new List<IntPtr>();
        foreach(var p in entries)if(actor==null||p.Value.Skill.character?.Pointer==actor.Pointer){Hide(p.Value);remove.Add(p.Key);}
        foreach(var key in remove)entries.Remove(key);
    }
    internal static void Tick()
    {
        foreach(var e in new List<Entry>(entries.Values))
        {
            if(!Guest(e.Skill)){Hide(e);entries.Remove(e.Skill.Pointer);continue;}
            if(e.Retired||!Preparing(e))Retire(e);
        }
    }
    [HarmonyPatch(typeof(PlayerSkill),"startAiming")]
    internal static class StartHook{private static void Prefix(PlayerSkill __instance)=>Started(__instance);}
    [HarmonyPatch(typeof(PlayerSkill),"stopAiming")]
    internal static class StopHook{private static void Postfix(PlayerSkill __instance)=>Stopped(__instance);}
    [HarmonyPatch(typeof(PlayerSkill),"updateAimingVisualizeRange")]
    internal static class RangeHook{private static bool Prefix(PlayerSkill __instance)=>AllowRange(__instance);}
}
