using UnityEngine;

// Native mouse aiming mutates shared contextual targets before a command setter
// runs. Keep confirmed interaction targets and ladder state while sending input.
internal sealed class GuestLadderSnapshot
{
    private sealed class Target
    {
        internal MiCharacterTarget value;
        private Vector3 point,skill,alternative,dataAlternative,offset;
        private Entity entity,additional;
        private MiTransformable transform;
        private MiCharacterTargetData data;
        private bool valid;
        private MiCharacterTargetData.Type type;
        internal Target(MiCharacterTarget t)
        {
            value=t;if(t==null)return;
            point=t.m_v3TargetPos;skill=t.m_v3SkillTargetPos;alternative=t.m_v3AlternativeTarget;
            offset=t.m_v3TargetOffset;entity=t.m_entityTarget;transform=t.m_transTarget;
            data=t.m_charTargetData;
            if(data!=null){valid=data.m_bValue;type=data.m_eType;dataAlternative=data.m_v3AlternativeTarget;additional=data.m_additionalHighlightableOrJumpTriggerFrom;}
        }
        internal void Restore()
        {
            if(value==null)return;
            value.m_v3TargetPos=point;value.m_v3SkillTargetPos=skill;value.m_v3AlternativeTarget=alternative;
            value.m_v3TargetOffset=offset;value.m_entityTarget=entity;value.m_transTarget=transform;
            value.m_charTargetData=data;
            if(data!=null){data.m_bValue=valid;data.m_eType=type;data.m_v3AlternativeTarget=dataAlternative;data.m_additionalHighlightableOrJumpTriggerFrom=additional;}
        }
    }
    private MiCharacter actor;
    private SkillCommand input;
    private Target inputTarget,processTarget,activeTarget,parallelTarget;
    private ParallelStateUseLadder parallel;
    private Vector3 idle;
    private ParallelStateUseLadder.State state;
    internal static GuestLadderSnapshot Capture(MiCharacter actor,bool edge)
    {
        bool climbing=InteractionSync.Climbing(actor);
        bool Context(SkillCommand cmd)=>cmd?.m_skill!=null&&InteractionSync.Supported(cmd.m_skill.eSkillType);
        if(!edge||!climbing&&!Context(actor?.controllerPlayer?.skillCommand)&&!Context(actor?.controller?.skillCommandProcess)&&!Context(actor?.controller?.skillCommandActive))return null;
        var s=new GuestLadderSnapshot{actor=actor,input=actor.controllerPlayer?.skillCommand};
        s.inputTarget=new Target(s.input?.m_target);
        s.processTarget=new Target(actor.controller?.skillCommandProcess?.m_target);
        s.activeTarget=new Target(actor.controller?.skillCommandActive?.m_target);
        s.parallel=climbing?actor.controller?.getSkill(Skill.SkillType.UseClimbMove)?.TryCast<SkillUseLadderMove>()?.m_parallelStateUseLadder:null;
        if(s.parallel!=null)
        {s.parallelTarget=new Target(s.parallel.m_targetCurrent);s.idle=s.parallel.m_v3TargetIdle;s.state=s.parallel.m_eStateCurrent;}
        return s;
    }
    internal void Restore()
    {
        // No native command/target allocation or ladder re-entry here.
        inputTarget.Restore();processTarget.Restore();activeTarget.Restore();parallelTarget?.Restore();
        if(parallel!=null){parallel.m_targetCurrent=parallelTarget.value;parallel.m_v3TargetIdle=idle;parallel.m_eStateCurrent=state;}
        if(actor.controllerPlayer?.skillCommand?.Pointer!=input?.Pointer)
        {
            bool old=NativeInput.Restoring;NativeInput.Restoring=true;
            try{actor.controllerPlayer.skillCommand=input;}finally{NativeInput.Restoring=old;}
        }
    }
}
