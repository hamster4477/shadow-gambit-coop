using System;
using UnityEngine;

// Preserve each owned actor's confirmed command while the native mouse code
// computes squad destinations. Some paths mutate a target object in place.
internal sealed class GuestMouseSnapshot
{
    internal MiCharacter Actor;
    private IntPtr command,target;
    private bool hadGoTo,run,crouch;
    private Vector3 point,storedPoint,storedSkillPoint;
    private GuestLadderSnapshot ladder;
    internal static GuestMouseSnapshot Capture(MiCharacter actor,bool edge,bool held=false)
    {
        var s=new GuestMouseSnapshot{Actor=actor};
        s.ladder=GuestLadderSnapshot.Capture(actor,edge||held);
        var cmd=actor.controllerPlayer?.skillCommand;
        s.command=cmd?.Pointer??IntPtr.Zero;
        if(edge && cmd?.m_skill?.eSkillType==Skill.SkillType.GoTo && cmd.m_target!=null)
        {
            s.hadGoTo=true;s.point=cmd.m_target.v3Target;
            if(cmd.m_target.m_entityTarget==null&&cmd.m_target.transTarget==null)
            {
                s.target=cmd.m_target.Pointer;s.storedPoint=cmd.m_target.m_v3TargetPos;s.storedSkillPoint=cmd.m_target.m_v3SkillTargetPos;
                s.run=cmd.hasModifier(SkillCommand.ExecutionModifier.Run);s.crouch=cmd.hasModifier(SkillCommand.ExecutionModifier.WasCrawling);
            }
        }
        return s;
    }
    internal void Restore(bool edge,Action<MiCharacter,SkillCommand> capture,Action<MiCharacter,SkillCommand> observe)
    {
        ladder?.Restore();
        var cmd=Actor?.controllerPlayer?.skillCommand;
        if(cmd==null)return;
        bool changed=edge&&cmd.m_skill?.eSkillType==Skill.SkillType.GoTo&&cmd.m_target!=null&&
            (!hadGoTo||(cmd.m_target.v3Target-point).sqrMagnitude>0.0001f);
        if(cmd.Pointer!=command||changed)capture(Actor,cmd);
        if(target==IntPtr.Zero||cmd.Pointer!=command||cmd.m_skill?.eSkillType!=Skill.SkillType.GoTo||
            cmd.m_target?.Pointer!=target||cmd.m_target.m_entityTarget!=null||cmd.m_target.transTarget!=null||!edge)return;
        if(changed)
        {
            observe(Actor,cmd);
            cmd.m_target.m_v3TargetPos=storedPoint;cmd.m_target.m_v3SkillTargetPos=storedSkillPoint;
        }
        if(cmd.hasModifier(SkillCommand.ExecutionModifier.Run)!=run||cmd.hasModifier(SkillCommand.ExecutionModifier.WasCrawling)!=crouch)
        {
            capture(Actor,cmd);
            cmd.setModifier(SkillCommand.ExecutionModifier.Run,run);cmd.setModifier(SkillCommand.ExecutionModifier.WasCrawling,crouch);
        }
    }
}
