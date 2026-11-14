using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;

internal static class PlanningNative
{
    private static float[] V(Vector3 v)=>new[]{v.x,v.y,v.z};
    private static Vector3 V(float[] p)=>new Vector3(p[0],p[1],p[2]);
    internal static float[] OptionalTarget(Func<Il2CppSystem.Nullable<Vector3>> read)
    {
        // Empty Nullable is boxed as null by IL2CPP. Its generated getter
        // throws while constructing the wrapper, before ?. can run.
        try{var value=read();return value?.HasValue==true?V(value.Value):null;}
        catch(NullReferenceException){return null;}
    }
    private static PlanningWire.Command Capture(SkillCommand c)
    {
        if(c?.m_skill==null)throw new InvalidOperationException("Planning command has no skill");
        var t=c.m_target;var d=t?.m_charTargetData;
        // Spawned crew, cannon objects and NPCs have different sibling indices
        // on each machine. Their entity identity is stable; a hierarchy hash is
        // only appropriate for non-entity scene transforms (e.g. ladder ends).
        string transformEntity=InteractionSync.EntityIdentity(t?.m_transTarget?.TryCast<Entity>());
        if(transformEntity=="-")transformEntity=null;
        return new PlanningWire.Command{skill=c.m_skill.eSkillType.ToString(),flags=(int)c.m_eExecutionModifier,target=t!=null,
            entity=t==null?null:InteractionSync.EntityIdentity(t.m_entityTarget),transformEntity=transformEntity,
            transform=transformEntity==null?StorySync.PlanningKey(t?.m_transTarget):null,
            point=t==null?null:V(t.m_v3TargetPos),skillPoint=t==null?null:V(t.m_v3SkillTargetPos),offset=t==null?null:V(t.m_v3TargetOffset),
            alternate=t==null?null:V(t.m_v3AlternativeTarget),movement=(int)(t?.m_eMovModifier??default),stopping=t?.m_fMovStoppingDistance??0,
            modifier=(int)(t?.m_eModifier??default),update=t?.m_bUpdateTargetPos==true,data=d!=null,value=d?.m_bValue==true,type=(int)(d?.m_eType??default),
            origin=d==null?null:InteractionSync.EntityIdentity(d.m_additionalHighlightableOrJumpTriggerFrom),dataPoint=d==null?null:V(d.m_v3AlternativeTarget)};
    }
    internal static PlanningWire.Queue[] Capture(PlanningMode mode,IEnumerable<MiCharacter> roster,Func<MiCharacter,bool> include)
    {
        var result=new List<PlanningWire.Queue>();
        foreach(var actor in roster)
        {
            if(actor==null||!include(actor))continue;
            var steps=new List<PlanningWire.Step>();
            foreach(var pair in mode.m_lCommands)
            {
                if(GuestCrewSelection.Source(pair.first)!=actor)continue;
                // Execution retains native queue records until callbacks finish.
                // They are no longer editable previews and must never recreate
                // a ghost (or be returned to the host as a new queued action).
                var native=pair.second;if(native==null||native.bExecute||mode.bInExecution(actor))continue;
                var chain=new List<PlanningWire.Command>();
                if(native.lSkillCommandChain!=null)foreach(var c in native.lSkillCommandChain)chain.Add(Capture(c));
                steps.Add(new PlanningWire.Step{command=Capture(native.skillCommand),chain=chain.ToArray(),crouch=native.bExecuteInCrouch,execute=native.bExecute,variant=native.m_iUISkillVisVariant,mouse=(int)native.m_eInputActionMouse,cancel=(int)native.m_eInputActionMouseCancel,controller=(int)native.m_eInputActionController,executionTarget=OptionalTarget(()=>native.v3ExecutionTarget)});
            }
            result.Add(new PlanningWire.Queue{actor=GuestMove.Identity(actor),steps=steps.ToArray()});
        }
        return result.ToArray();
    }
    private static SkillCommand Build(MiCharacter actor,PlanningWire.Command c)
    {
        if(!Enum.TryParse<Skill.SkillType>(c.skill,out var kind)||!Enum.IsDefined(typeof(Skill.SkillType),kind))throw new InvalidOperationException("Unknown planned skill");
        var skill=actor.controller?.getSkill(kind);if(skill==null)throw new InvalidOperationException("Planned skill unavailable on actor");
        Entity Find(string key){var value=key==null||key=="-"?null:InteractionSync.Resolve(key);if(key!=null&&key!="-"&&value==null)throw new InvalidOperationException("Planning entity unavailable");return value;}
        MiCharacterTarget target=null;
        if(c.target)
        {
            var entity=Find(c.entity);target=entity==null?new MiCharacterTarget(V(c.point)):new MiCharacterTarget(entity,null);
            if(c.transformEntity!=null)target.m_transTarget=Find(c.transformEntity);
            else if(c.transform!=null){target.m_transTarget=StorySync.PlanningFind<MiTransformable>(c.transform);if(target.m_transTarget==null)throw new InvalidOperationException("Planning transform unavailable");}
            target.m_v3TargetPos=V(c.point);target.m_v3SkillTargetPos=V(c.skillPoint);target.m_v3TargetOffset=V(c.offset);target.m_v3AlternativeTarget=V(c.alternate);
            target.m_eMovModifier=(MiMovement.MovementModifier)c.movement;target.m_fMovStoppingDistance=c.stopping;
            target.m_eModifier=(MiCharacterTarget.Modifier)c.modifier;target.m_bUpdateTargetPos=c.update;
            if(c.data)target.m_charTargetData=new MiCharacterTargetData{m_bValue=c.value,m_eType=(MiCharacterTargetData.Type)c.type,m_v3AlternativeTarget=V(c.dataPoint),m_additionalHighlightableOrJumpTriggerFrom=Find(c.origin)};
        }
        return new SkillCommand(skill,target){m_eExecutionModifier=(SkillCommand.ExecutionModifier)c.flags};
    }
    internal static bool Replace(PlanningMode mode,PlanningWire.Queue[] queues,Func<MiCharacter,bool> allowed,bool validate=false)
    {
        var prepared=new List<(MiCharacter actor,List<PlanningModeCommand> commands)>();
        foreach(var q in queues)
        {
            var actor=MoveProbe.CrewById(q.actor);if(actor==null||!allowed(actor))return false;
            if(mode.bInExecution(actor))continue; // Never reset a real host command in flight.
            var commands=new List<PlanningModeCommand>();
            foreach(var s in q.steps)
            {
                var chain=new Il2CppSystem.Collections.Generic.List<SkillCommandCallback>();
                foreach(var c in s.chain)chain.Add(new SkillCommandCallback(Build(actor,c),null,null));
                if(s.execute)continue;
                var built=Build(actor,s.command);
                // The native non-default constructor caches skill separately.
                // Omitting it makes both preview callbacks and removeCommands
                // throw, leaving the visual and its animation alive forever.
                var preparedCommand=new PlanningModeCommand{skillCommand=built,skill=built.m_skill,lSkillCommandChain=chain,bExecuteInCrouch=s.crouch,bExecute=false,m_iUISkillVisVariant=s.variant,m_eInputActionMouse=(MiInputActions)s.mouse,m_eInputActionMouseCancel=(MiInputActions)s.cancel,m_eInputActionController=(MiInputActions)s.controller};
                mode.onCommandPlanningSetOrigin(built,actor);
                mode.onCommandPlanningSetOrigin(chain,actor);
                if(s.executionTarget!=null)preparedCommand.v3ExecutionTarget=new Il2CppSystem.Nullable<Vector3>(V(s.executionTarget));
                commands.Add(preparedCommand);
            }
            if(validate)foreach(var c in commands)if(!mode.checkSkillCommandValidPlanning(c.skillCommand))return false;
            prepared.Add((actor,commands));
        }
        bool before=PlanningSync.Applying,restoring=NativeInput.Restoring;PlanningSync.Applying=true;NativeInput.Restoring=true;
        try
        {
            foreach(var item in prepared)
            {
                bool existing=false;
                if(mode.m_lCommands!=null)foreach(var pair in mode.m_lCommands)
                    if(GuestCrewSelection.Source(pair.first)==item.actor){existing=true;break;}
                if(!existing&&item.commands.Count==0)continue;
                if(existing)mode.removeCommands(item.actor);
                foreach(var c in item.commands)mode.onCommandPlanningInternal(item.actor,c);
            }
            mode.updateCommandCount();return true;
        }
        finally{PlanningSync.Applying=before;NativeInput.Restoring=restoring;}
    }
    internal static void CompletePreview(PlanningMode mode,string actorId,bool active)
    {
        bool before=PlanningSync.Applying,restoring=NativeInput.Restoring;
        PlanningSync.Applying=true;NativeInput.Restoring=true;
        try
        {
            // reset(false) also ends native visual playback and target-check
            // coroutines, while preserving real confirmed world commands.
            if(actorId==null)mode.reset(false);
            else {var actor=MoveProbe.CrewById(actorId);if(actor!=null)mode.reset(actor);}
            if(!active&&mode.bIsActive)mode.deactivate();
            var input=SceneLookup.Find<MiPlayerInput>();
            input?.m_planningModeInput?.checkActive(input,false);
            input?.m_planningModeInput?.checkLeaderChange(input);
        }
        finally{PlanningSync.Applying=before;NativeInput.Restoring=restoring;}
    }
}
