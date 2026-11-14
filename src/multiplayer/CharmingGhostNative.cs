using System;
using System.Collections.Generic;

internal static class CharmingGhostNative
{
    private static readonly Dictionary<IntPtr,IntPtr> recoveryCommands=new();
    internal static void Clear()=>recoveryCommands.Clear();
    internal static bool IsRecoveryCommand(SkillCommand command)=>command?.m_skill!=null&&
        recoveryCommands.TryGetValue(command.m_skill.Pointer,out var pointer)&&pointer==command.Pointer;
    private static bool IsCast(SkillCommand command,SkillCharmingGhost skill)=>command?.m_skill?.Pointer==skill.Pointer;
    internal static void CancelPending(SkillCharmingGhost skill)
    {
        var controller=skill?.character?.controllerPlayer;if(controller==null)return;
        bool queued=IsCast(controller.skillCommand,skill);
        bool running=IsCast(controller.skillCommandProcess,skill)||IsCast(controller.skillCommandActive,skill);
        if(running)controller.cancelSkill();
        if(queued)controller.skillCommand=null;
        recoveryCommands.Remove(skill.Pointer);
    }
    internal static bool RequestDeployment(SkillCharmingGhost skill,UnityEngine.Vector3 point)
    {
        if(!float.IsFinite(point.x)||!float.IsFinite(point.y)||!float.IsFinite(point.z)||!EnsurePool(skill))return false;
        if(!skill.m_bGhostAtChar)return true;
        var actor=skill.character;var controller=actor?.controllerPlayer;
        if(controller==null||actor.health?.eHealthState!=MiCharacterHealth.HealthState.Alive||!actor.gameObject.activeInHierarchy)return false;
        if(IsCast(controller.skillCommand,skill)||IsCast(controller.skillCommandProcess,skill)||IsCast(controller.skillCommandActive,skill))return true;
        // Preserve an unrelated native action; a later authoritative result
        // retries after it finishes, rather than silently replacing that cast.
        if(controller.skillCommand!=null||controller.skillCommandProcess!=null||controller.skillCommandActive!=null)return false;
        var target=new MiCharacterTarget(point);
        target.m_v3TargetPos=point;target.m_v3SkillTargetPos=point;
        if(!skill.isTargetValid(target))return false;
        var command=new SkillCommand(skill,target);
        command.setModifier(SkillCommand.ExecutionModifier.IgnoreAmmo,true);
        command.setModifier(SkillCommand.ExecutionModifier.IgnoreCooldown,true);
        // Keep the normal command/process lifecycle: executePlayerSkill reads
        // its command and target, not merely the skill's deployment flag.
        controller.skillCommand=command;
        recoveryCommands[skill.Pointer]=command.Pointer;
        return true; // queued, not proof of completed deployment
    }
    internal static bool PoolReady(SkillCharmingGhost skill)
    {
        var ghost=skill?.m_charGhost;
        return ghost!=null&&ghost.controller!=null&&ghost.health!=null&&
            skill.m_skillCancel?.m_skillCharmingGhost?.Pointer==skill.Pointer;
    }
    // Native init tail-calls instantiate. It allocates unconditionally, assigns
    // m_charGhost before finishing, then binds the ghost's cancel skill back to
    // this caster. Repeating it on a partially initialized ghost leaks actors.
    internal static bool EnsurePool(SkillCharmingGhost skill)
    {
        if(skill==null)return false;
        if(skill.m_charGhost!=null)return PoolReady(skill);
        if(skill.character?.health?.eHealthState!=MiCharacterHealth.HealthState.Alive||
            skill.character.controller==null||!skill.character.gameObject.activeInHierarchy||skill.m_characterPrefab==null)return false;
        try
        {
            skill.instantiate();
            return PoolReady(skill);
        }
        catch(Exception ex)
        {
            MoveProbePlugin.ProbeLog.LogWarning("Charming ghost pool recovery: "+ex.GetType().Name);
            return false;
        }
    }
}
