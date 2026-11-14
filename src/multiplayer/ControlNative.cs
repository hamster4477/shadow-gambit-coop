using System;

// Native cleanup restores previous/current controllers, UI and effects.
// A replayed release must not repeat the host's kill/knockout outcome.
internal static class ControlNative
{
    internal static bool Enter(SkillControl skill,MiCharacter target)
    {
        if(skill==null||target==null||target.health==null||
            target.health.eHealthState!=MiCharacterHealth.HealthState.Alive)return false;
        if(skill.bInMindControl&&skill.m_charControlled==target&&skill.m_controllerCurrent!=null)return true;
        // Let a real local cast settle; do not replace its controller halfway.
        if(skill.m_bInPreExecute)return false;
        if(skill.m_controllerCurrent!=null||skill.m_charControlled!=null||skill.bInMindControl)
            if(!Release(skill))return false;
        // Never steal an original crew member or a different caster's proxy.
        if(target.bControllerPlayer())return false;
        var caster=skill.character;
        var occupation=caster?.occupation;
        if(caster?.health?.eHealthState!=MiCharacterHealth.HealthState.Alive||occupation==null||
            !occupation.bIsFree(EntityOccupationComponent.OccupationReason.SkillTarget))return false;
        var priorTarget=skill.m_targetCharacter;
        bool occupied=false;
        try
        {
            occupation.occupy(EntityOccupationComponent.OccupationReason.SkillTarget,caster);
            occupied=true;
            skill.m_targetCharacter=target;
            // startControl uses native virtual NPC/player implementations and
            // stores both controllers. It skips execute's resource/effect path.
            // The death-during-preExecute callback is removed by native execute;
            // a settled-state repair must not register that temporary callback.
            skill.m_bCanceledByFrozen=false;
            skill.startControl();
            bool entered=skill.m_charControlled==target&&skill.m_controllerCurrent!=null;
            if(entered){skill.m_bInMindControl=true;skill.m_bInPreExecute=false;}
            else Release(skill);
            return entered;
        }
        catch(Exception ex)
        {
            Release(skill);
            MoveProbePlugin.ProbeLog.LogWarning("Control enter recovery: "+ex.GetType().Name);
            return false;
        }
        finally
        {
            if(!skill.bInMindControl&&skill.m_controllerCurrent==null&&skill.m_charControlled==null)
            {
                if(occupied)occupation.release(EntityOccupationComponent.OccupationReason.SkillTarget);
                skill.m_targetCharacter=priorTarget;
            }
        }
    }
    internal static bool Release(SkillControl skill)
    {
        if(skill==null)return false;
        if(!skill.bInMindControl&&!skill.m_bInPreExecute&&skill.m_controllerCurrent==null&&skill.m_charControlled==null)return true;
        try
        {
            if(skill.m_bInPreExecute&&skill.m_controllerCurrent==null&&skill.m_charControlled==null)
            {
                if(skill.m_targetCharacter==null)return false;
                skill.onCancelPreExecution();
                return !skill.m_bInPreExecute&&!skill.bInMindControl;
            }
            skill.stopControlNPC(new Il2CppSystem.Nullable<SkillControl.HealthImpact>(SkillControl.HealthImpact.None),true,true);
            // skipStopAnimation also skips the coroutine which invokes this
            // callback. Overtake's callback restores the caster's visibility
            // and occupation; switching the NPC controller alone is incomplete.
            bool released=!skill.bInMindControl&&skill.m_controllerCurrent==null&&skill.m_charControlled==null;
            if(released)
            {
                skill.onStopControlFinished();
                MoveProbePlugin.ProbeLog.LogInfo("Control release completed, including caster recovery: "+skill.eSkillType);
            }
            return released;
        }
        catch(Exception ex)
        {
            MoveProbePlugin.ProbeLog.LogWarning("Control release recovery: "+ex.GetType().Name);
            return false;
        }
    }
}
