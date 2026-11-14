using System;
using System.Collections.Generic;
using UnityEngine;

// Native range objects remain visual only. Ownership and lifecycle are local;
// no preview can start damage, noise detection, or a network gameplay command.
internal static class GuestPreview
{
    private sealed class Entry { internal SkillMeleeBase skill; internal MiCharacter actor,target; internal float intentUntil; internal bool executed,seenCommand; }
    private sealed class Revoked { internal MiCharacter actor; internal IntPtr target; internal bool leftTarget, stoppedAiming; }
    private static readonly Dictionary<IntPtr,Revoked> revoked=new Dictionary<IntPtr,Revoked>();
    private static readonly Dictionary<IntPtr,Entry> melee=new Dictionary<IntPtr,Entry>();
    private static readonly List<IntPtr> remove=new List<IntPtr>();
    private static readonly Dictionary<IntPtr,(MiCharacter actor,MiCharacterTargetPlayer target)> aimingTargets=new();
    private static void ClearTargets(MiCharacter actor=null)
    {
        foreach(var pair in new List<KeyValuePair<IntPtr,(MiCharacter actor,MiCharacterTargetPlayer target)>>(aimingTargets))
            if(actor==null||pair.Value.actor==null||pair.Value.actor.Pointer==actor.Pointer)
            {pair.Value.target?.disable();aimingTargets.Remove(pair.Key);}
    }
    private sealed class Disposal {internal MiCharacter actor;internal MiCharacterTargetPlayer target;internal PlayerSkill skill;internal float until;}
    private static readonly Dictionary<IntPtr,Disposal> disposals=new Dictionary<IntPtr,Disposal>();
    private static bool Finished(MiCharacter c)=>c==null || c.health==null || c.health.eHealthState is MiCharacterHealth.HealthState.PreDying or MiCharacterHealth.HealthState.Dying or MiCharacterHealth.HealthState.Dead or MiCharacterHealth.HealthState.Knockout or MiCharacterHealth.HealthState.Exterminated;
    internal static void AimingStarted(MiCharacter actor){if(actor!=null)revoked.Remove(actor.Pointer);}
    internal static bool Preparing(PlayerSkill skill)=>skill?.character!=null&&!revoked.ContainsKey(skill.character.Pointer)&&
        !Finished(skill.character)&&(GuestSkillPreview.Aiming(skill)||melee.ContainsKey(skill.Pointer));
    internal static bool Creating(SkillMeleeBase skill,Entity entity)
    {
        if(NativeInput.Probe==null || !NativeInput.Probe.MirroredCrew(skill.character)) return true;
        if(revoked.ContainsKey(skill.character.Pointer)) { skill.contextNoiseRangeRemove();return false; }
        if(entity==null || !entity.bAsCharacter(out var target) || Finished(target)) { skill.contextNoiseRangeRemove();return false; }
        if(melee.TryGetValue(skill.Pointer,out var old) && old.target!=null && old.target.Pointer==target.Pointer &&
            (skill.m_noiseRangeContext?.bExists==true || skill.m_noiseRangeContextConnect?.bExists==true)) return false;
        float intent=old?.target!=null && old.target.Pointer==target.Pointer ? old.intentUntil : 0f;
        skill.contextNoiseRangeRemove();
        bool same=old?.target?.Pointer==target.Pointer;
        melee[skill.Pointer]=new Entry{skill=skill,actor=skill.character,target=target,intentUntil=intent,executed=same&&old.executed,seenCommand=same&&old.seenCommand};
        return true;
    }
    internal static void Attack(MiCharacter actor,Skill.SkillType kind,MiCharacter target)
    {
        Clear(actor);
        if(actor!=null) revoked.Remove(actor.Pointer);
        var skill=actor?.controller?.getSkill(kind)?.TryCast<SkillMeleeBase>();
        if(skill==null || Finished(target)) return;
        skill.contextNoiseRangeCreate(target);
        if(melee.TryGetValue(skill.Pointer,out var entry)) entry.intentUntil=Time.realtimeSinceStartup+30f;
    }
    private static bool ContextTargets(ContextAction context,Entry entry)=>context?.bActive==true && context.bEnabled &&
        context.entity!=null && context.entity.Pointer==entry.target.Pointer &&
        entry.actor?.controllerPlayer?.m_playerInput?.m_raycastCache?.charNPC?.Pointer==entry.target.Pointer;
    private static bool CommandTargets(SkillCommand command,Entry entry)=>command?.m_skill!=null && Melee.Contact(command.m_skill.eSkillType) &&
        command.m_target?.m_entityTarget!=null && command.m_target.m_entityTarget.Pointer==entry.target.Pointer;
    private static void HideAiming(MiCharacter actor)
    {
        ClearTargets(actor);
        if(actor?.controller==null) return;
        foreach(var kind in new[]{Skill.SkillType.Kill,Skill.SkillType.Knockout})
        {
            var skill=actor.controller.getSkill(kind);
            skill?.TryCast<SkillMeleeBase>()?.contextNoiseRangeRemove();
            skill?.TryCast<PlayerSkill>()?.m_target?.disable();
            GuestSkillPreview.ReleaseRange(skill?.TryCast<PlayerSkill>());
        }
    }
    internal static void Cancel(MiCharacter actor)
    {
        if(actor==null) { Clear();return; }
        if(NativeInput.Probe?.MirroredCrew(actor)!=true)return;
        IntPtr target=IntPtr.Zero;
        foreach(var entry in melee.Values) if(entry.actor?.Pointer==actor.Pointer && entry.target!=null) { target=entry.target.Pointer;break; }
        var input=actor.controllerPlayer?.m_playerInput;
        if(target==IntPtr.Zero) target=input?.m_raycastCache?.charNPC?.Pointer??IntPtr.Zero;
        Clear(actor);
        revoked[actor.Pointer]=new Revoked{actor=actor,target=target,stoppedAiming=input?.m_aiming?.bIsActive!=true};
        HideAiming(actor);
    }
    internal static void Tick()
    {
        remove.Clear();
        foreach(var pair in revoked)
        {
            var r=pair.Value;var actor=r.actor;
            if(Finished(actor) || NativeInput.Probe?.MirroredCrew(actor)!=true) { remove.Add(pair.Key);continue; }
            var input=actor.controllerPlayer?.m_playerInput;
            var hover=input?.m_raycastCache?.charNPC;
            bool aiming=input?.m_aiming?.bIsActive==true;
            if(!aiming) r.stoppedAiming=true;
            if(hover==null) r.leftTarget=true;
            // A new hover after leaving the cancelled target, a different
            // target, or a new explicit aiming session is a new preparation.
            bool freshHover=hover!=null && !Finished(hover) && (hover.Pointer!=r.target || r.leftTarget);
            if(freshHover || aiming && r.stoppedAiming) { remove.Add(pair.Key);continue; }
            HideAiming(actor);
        }
        foreach(var key in remove) revoked.Remove(key);

        remove.Clear();
        var ended=new List<MiCharacter>();
        foreach(var pair in melee)
        {
            var e=pair.Value;
            var controller=e.actor?.controllerPlayer;
            var trigger=controller?.m_characterPlayerTrigger;
            bool command=controller!=null&&(CommandTargets(controller.skillCommand,e)||CommandTargets(e.actor.controller.skillCommandProcess,e)||CommandTargets(e.actor.controller.skillCommandActive,e));
            if(command)e.seenCommand=true;
            bool lostExecuted=(e.executed||e.seenCommand) && controller!=null && !command;
            bool keep=!lostExecuted && controller!=null && !Finished(e.actor) && !Finished(e.target) && NativeInput.Probe?.MirroredCrew(e.actor)==true &&
                (Time.realtimeSinceStartup<e.intentUntil || ContextTargets(trigger?.m_contextActionKill,e) || ContextTargets(trigger?.m_contextAction,e) ||
                 CommandTargets(controller.skillCommand,e) || CommandTargets(e.actor.controller.skillCommandProcess,e));
            if(!keep) { e.skill.contextNoiseRangeRemove();e.skill.m_target?.disable();GuestSkillPreview.ReleaseRange(e.skill);remove.Add(pair.Key); if((lostExecuted||Finished(e.target)) && e.actor!=null)ended.Add(e.actor); }
        }
        foreach(var key in remove) melee.Remove(key);
        foreach(var actor in ended)Cancel(actor);
        remove.Clear();
        foreach(var pair in disposals)
        {
            var d=pair.Value;
            if(Finished(d.actor) || NativeInput.Probe?.MirroredCrew(d.actor)!=true || Time.realtimeSinceStartup>=d.until)
            {d.target.disable();remove.Add(pair.Key);}
            else d.target.updateAll(d.skill,true);
        }
        foreach(var key in remove)disposals.Remove(key);
        GuestSkillPreview.Tick();
    }
    internal static void ShowDisposal(MiCharacter actor,SkillCommand command)
    {
        if(actor==null || command?.m_skill==null || !GuestDisposal.IsDisposal(command.m_skill.eSkillType) || command.m_target==null) return;
        Clear(actor);
        var skill=command.m_skill.TryCast<PlayerSkill>();
        if(skill==null) return;
        // Use the game's range/trajectory settings and its complete native target.
        var disposal=new MiCharacterTargetPlayer(skill);
        disposals[actor.Pointer]=new Disposal{actor=actor,skill=skill,target=disposal,until=Time.realtimeSinceStartup+30f};
        disposal.update(command.m_target,true);
        disposal.updateAll(skill,true);
    }
    // Native aiming targets can be refreshed after our LateUpdate cleanup.
    // Suppress the retired melee intent at the native update entry as well.
    internal static bool AllowAiming(MiCharacterTargetPlayer target)
    {
        var skill=target?.m_playerSkill;var actor=skill?.character;
        if(actor==null)return true;
        if(disposals.TryGetValue(actor.Pointer,out var disposal)&&disposal.target.Pointer==target.Pointer)
            return !Finished(actor)&&Time.realtimeSinceStartup<disposal.until&&NativeInput.Probe?.MirroredCrew(actor)==true;
        if(Melee.Contact(skill.eSkillType))
        {
            if(revoked.ContainsKey(actor.Pointer)){target.disable();return false;}
            aimingTargets[target.Pointer]=(actor,target);return true;
        }
        return GuestSkillPreview.Allow(target);
    }
    internal static void Completed(PlayerSkill skill)
    {
        if(skill?.character==null || NativeInput.Probe?.MirroredCrew(skill.character)!=true)return;
        GuestSkillPreview.Completed(skill);
        if(Melee.Contact(skill.eSkillType))
        {
            // A queued attack can already be preparing a different target
            // when the previous native animation finishes.
            foreach(var entry in melee.Values)
                if(entry.actor?.Pointer==skill.character.Pointer && !entry.executed)return;
            Cancel(skill.character);
        }
        else if(GuestDisposal.IsDisposal(skill.eSkillType)) ClearDisposal(skill.character);
    }
    internal static void Executed(MiCharacter actor,SkillCommand command)
    {
        GuestSkillPreview.Executed(actor,command);
        if(actor!=null && command?.m_skill!=null && Melee.Contact(command.m_skill.eSkillType))
            foreach(var entry in melee.Values) if(entry.actor?.Pointer==actor.Pointer && CommandTargets(command,entry)) entry.executed=true;
        if(actor!=null && command?.m_skill!=null && GuestDisposal.IsDisposal(command.m_skill.eSkillType)) ClearDisposal(actor);
    }
    private static void ClearDisposal(MiCharacter actor=null)
    {
        if(actor!=null)
        {
            if(disposals.TryGetValue(actor.Pointer,out var old)){disposals.Remove(actor.Pointer);old.target.disable();}
            return;
        }
        foreach(var d in disposals.Values)d.target.disable();
        disposals.Clear();
    }
    internal static void Clear(MiCharacter actor=null)
    {
        ClearTargets(actor);
        if(actor==null)GuestSkillPreview.Clear();else GuestSkillPreview.Cancel(actor);
        if(actor==null) revoked.Clear();
        remove.Clear();
        foreach(var pair in melee) if(actor==null || pair.Value.actor==null || pair.Value.actor.Pointer==actor.Pointer)
        { pair.Value.skill.contextNoiseRangeRemove();pair.Value.skill.m_target?.disable();GuestSkillPreview.ReleaseRange(pair.Value.skill);remove.Add(pair.Key); }
        foreach(var key in remove) melee.Remove(key);
        ClearDisposal(actor);
    }
}
