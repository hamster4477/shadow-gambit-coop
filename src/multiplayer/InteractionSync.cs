using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using HarmonyLib;

// Reconstruct the native contextual order rather than replacing traversal or
// disposal with a ground click. Host confirmations own the execution/replay.
internal sealed class InteractionSync
{
    private sealed class Cue
    {
        internal IntPtr pointer;
        internal int serial;
        internal string descriptor,phase;
        internal float until;
        internal bool ended;
    }
    private Cue first=new Cue();
    private Cue second=new Cue();
    internal void RebindFirst()=>first=new Cue();
    internal void RebindSecond()=>second=new Cue();
    internal void Forget(MiCharacter actor)
    {if(actor==null)return;int id=actor.GetInstanceID();played.Remove(id);protectedUntil.Remove(id);retryAt.Remove(id);onLadder.Remove(id);middleTargets.Remove(actor.Pointer);}
    private readonly Dictionary<int,int> played=new Dictionary<int,int>();
    private readonly Dictionary<int,float> protectedUntil=new Dictionary<int,float>();
    private readonly Dictionary<int,float> retryAt=new Dictionary<int,float>();
    private readonly HashSet<int> onLadder=new HashSet<int>();
    private static readonly Dictionary<string,Entity> entities=new Dictionary<string,Entity>();
    private static readonly Dictionary<IntPtr,string> entityKeys=new Dictionary<IntPtr,string>();
    private static float nextScan;
    private static float nextWarning;
    // Retain the wrapper with its pointer: delayed native skill coroutines
    // must identify the confirmed command, not whichever cast is newest now.
    private sealed class CommandIdentity {internal SkillCommand Command;internal int Serial;internal long Revision;internal bool Recovery;}
    private static readonly Dictionary<IntPtr,CommandIdentity> commandSerials=new();
    private static readonly Queue<(IntPtr Pointer,long Revision)> commandHistory=new();
    private static long commandRevision;
    private static int nextCommandSerial;
    private static int NextSerial()=>nextCommandSerial=nextCommandSerial==int.MaxValue?1:nextCommandSerial+1;
    private static int CastSerial(Cue cue,SkillCommand command)
    {
        // Changing the primary crew/ownership creates another cue recorder.
        // Preserve an already-running native order across that handoff.
        int shot=command?.m_skill?.eSkillType==Skill.SkillType.CannonShoot?CannonLaunchSync.CapturedSerial(command):0;
        if(shot>0)return shot;
        int existing=cue.serial==0||command?.m_skill?.eSkillType==Skill.SkillType.CannonShoot?CommandSerial(command):0;
        return existing>0?existing:NextSerial();
    }
    internal static int CommandSerial(SkillCommand command)=>command!=null&&commandSerials.TryGetValue(command.Pointer,out var entry)?entry.Serial:0;
    internal static void MarkRecovery(SkillCommand command)
    {if(command!=null&&commandSerials.TryGetValue(command.Pointer,out var entry))entry.Recovery=true;}
    private static bool RecoveryCommand(SkillCommand command)=>command!=null&&commandSerials.TryGetValue(command.Pointer,out var entry)&&entry.Recovery;
    private static void RememberCommand(SkillCommand command,int serial)
    {
        if(command==null||serial<=0||CommandSerial(command)==serial)return;
        var entry=new CommandIdentity{Command=command,Serial=serial,Revision=++commandRevision};
        commandSerials[command.Pointer]=entry;commandHistory.Enqueue((command.Pointer,entry.Revision));
        while(commandHistory.Count>1024)
        {var old=commandHistory.Dequeue();if(commandSerials.TryGetValue(old.Pointer,out var current)&&current.Revision==old.Revision)commandSerials.Remove(old.Pointer);}
    }
    private sealed class Middle {internal string[] wire;internal int offset;internal float at;}
    private static readonly Dictionary<IntPtr,Middle> middleTargets=new();
    // The native enter skill automatically constructs an endpoint exit in
    // postExecute. A late CM must replace that continuation in the same tick;
    // polling after the short mount animation can miss the mounted interval.
    [HarmonyPatch(typeof(SkillUseLadderEnter),"createLadderExitCommand")]
    internal static class LadderContinuation
    {
        private static void Postfix(SkillUseLadderEnter __instance,ref SkillCommand __result)
        {
            try{__result=ContinueMiddle(__instance.character,__result);}
            catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Ladder continuation: "+ex.GetType().Name);}
        }
    }
    internal static SkillCommand ContinueMiddle(MiCharacter actor,SkillCommand fallback)
    {
        if(actor==null||!middleTargets.TryGetValue(actor.Pointer,out var middle)||Time.realtimeSinceStartup-middle.at>2f)return fallback;
        var f=middle.wire;int i=middle.offset;var encoded=f[i+3].Split('~');
        if(encoded.Length!=13)return fallback;
        var ladder=Resolve(encoded[0])?.TryCast<MiUsableLadder>();
        var skill=actor.controller.getSkill(Skill.SkillType.UseClimbMove)?.TryCast<PlayerSkill>();
        if(ladder==null||skill==null||fallback?.m_target?.m_entityTarget!=ladder)return fallback;
        var point=CrewSync.ReadPosition(f,i+4);var goal=CrewSync.ReadPosition(encoded,4);
        var target=new MiCharacterTarget(ladder,null);
        target.m_v3TargetPos=point;target.m_v3SkillTargetPos=CrewSync.ReadPosition(encoded,7);target.m_v3AlternativeTarget=goal;
        target.m_charTargetData=new MiCharacterTargetData();target.m_charTargetData.m_bValue=true;
        target.m_charTargetData.m_eType=MiCharacterTargetData.Type.ClimbMove;target.m_charTargetData.m_v3AlternativeTarget=goal;
        var command=new SkillCommand(skill,target);command.setModifier(SkillCommand.ExecutionModifier.PlayerInduced,true);
        MoveProbePlugin.ProbeLog.LogInfo("Guest ladder continuation keeps confirmed middle height "+CrewSync.Position(goal));
        return command;
    }
    internal static bool Supported(Skill.SkillType skill)=>InteractionProtocol.Code(skill.ToString())!=null;
    internal static bool ExitToward(MiCharacter actor,Vector3 ground)
    {
        var state=actor?.controller?.getSkill(Skill.SkillType.UseClimbMove)?.TryCast<SkillUseLadderMove>()?.m_parallelStateUseLadder;
        var ladder=state?.m_usableLadder?.TryCast<MiUsableLadder>();
        if(state?.bActive!=true||ladder?.entryTop==null||ladder.entryBottom==null)return false;
        var end=Vector3.Distance(ground,ladder.entryTop.position)<Vector3.Distance(ground,ladder.entryBottom.position)?ladder.entryTop.position:ladder.entryBottom.position;
        return Issue(actor,("CX "+Key(ladder)+" "+CrewSync.Position(end)+" 0").Split(' '),0);
    }
    internal static bool LadderSkill(Skill.SkillType skill)=>skill is Skill.SkillType.UseClimbEnter or Skill.SkillType.UseClimbMove or Skill.SkillType.UseClimbExit;
    private static bool Ladder(SkillCommand command) => command?.m_skill?.eSkillType is
        Skill.SkillType.UseClimbEnter or Skill.SkillType.UseClimbMove or Skill.SkillType.UseClimbExit;
    private static bool EntryOrExit(SkillCommand command) => command?.m_skill?.eSkillType is
        Skill.SkillType.UseClimbEnter or Skill.SkillType.UseClimbExit;
    private static bool LadderTransition(MiCharacter actor) =>
        EntryOrExit(actor.controller.skillCommandProcess) || EntryOrExit(actor.controller.skillCommandActive);
    private static bool Mounted(MiCharacter actor) => actor?.controller?.getSkill(Skill.SkillType.UseClimbMove)?.TryCast<SkillUseLadderMove>()?.m_parallelStateUseLadder?.bActive==true;
    internal static bool Climbing(MiCharacter actor)
    {
        if(actor?.controller==null) return false;
        // The process slot can retain the completed exit command. Only an
        // active mount/dismount or a pending entry blocks the next ground move.
        if(EntryOrExit(actor.controller.skillCommandActive)||actor.controller.skillCommandProcess?.m_skill?.eSkillType==Skill.SkillType.UseClimbEnter)return true;
        var state=actor.controller.getSkill(Skill.SkillType.UseClimbMove)?.TryCast<SkillUseLadderMove>()?.m_parallelStateUseLadder;
        return state!=null && state.bActive;
    }
    internal static void ResetRegistry() { entities.Clear();entityKeys.Clear();middleTargets.Clear();commandSerials.Clear();commandHistory.Clear();commandRevision=0;nextCommandSerial=0;nextScan=0f; }
    internal static string EntityIdentity(Entity entity)=>Key(entity);
    private static string Key(Entity entity)
    {
        if(entity==null) return "-";
        var generated=SkillObjectRegistry.Active.Key(entity);if(generated!=null)return generated;
        if(entity.bAsCharacter(out var actor))
        {
            var proxy=ControlledCrew.Active.Find(actor);
            return proxy!=null?"p"+proxy.Id:actor.guid.isEmpty()?null:"e"+Melee.GuardId(actor);
        }
        if(entityKeys.TryGetValue(entity.Pointer,out var saved)) return saved;
        // Scene hierarchy is stable between identical mission saves. Never
        // use per-process instance IDs or nearest-object matching.
        string path=entity.gameObject.scene.name;
        for(var t=entity.transform;t!=null;t=t.parent) path+="/"+t.name+"#"+t.GetSiblingIndex();
        uint hash=2166136261;foreach(char ch in path) hash=unchecked((hash^ch)*16777619);
        return entityKeys[entity.Pointer]="u"+unchecked((int)hash).ToString(CultureInfo.InvariantCulture);
    }
    internal static Entity Resolve(string key)
    {
        if(key=="-") return null;
        if(key!=null&&key.Length>1&&key[0]=='o')return SkillObjectRegistry.Active.Resolve(key.Substring(1));
        if(key!=null&&key.Length>1&&key[0]=='p')
            return ControlledCrew.Active.Find(key.Substring(1))?.Actor;
        if(Time.realtimeSinceStartup>=nextScan)
        {
            nextScan=Time.realtimeSinceStartup+2f;entities.Clear();
            foreach(var e in UnityEngine.Object.FindObjectsOfType<Entity>())
            {
                string k=Key(e);if(k==null) continue;
                entities[k]=entities.ContainsKey(k)?null:e;
            }
        }
        return entities.TryGetValue(key,out var target)?target:null;
    }
    internal static string Describe(SkillCommand command)
    {
        if(command?.m_skill==null) return null;
        string code=InteractionProtocol.Code(command.m_skill.eSkillType.ToString());
        int flags=Modifiers(command);
        // Teleport consumes the caster's existing marker. Its local marker
        // target is not a new terrain click or a cross-machine entity ID.
        if(code=="TK"||code=="TKN"||code=="TMV"||code=="S21"||code=="S48")return code+" n 0 0 0 "+flags;
        if(command.m_target==null)
            return InteractionProtocol.NullTarget(code)?code+" n 0 0 0 "+flags:null;
        var targetEntity=command.m_target.m_entityTarget;
        // A thrown teleport mark can attach to a character; ordinary terrain
        // is only the aiming surface, not the mark owner.
        bool coordinate=InteractionProtocol.PointTarget(code) || InteractionProtocol.OptionalEntityPoint(code) &&
            (targetEntity==null || !targetEntity.bAsCharacter(out _));
        string key=coordinate?"-":Key(targetEntity);
        if(code==null || key==null) return null;
        var p=command.m_target.v3Target;
        if(InteractionProtocol.Geometry(code))
        {
            var t=command.m_target;var data=t.m_charTargetData;
            string origin=Key(data?.m_additionalHighlightableOrJumpTriggerFrom);
            if(origin==null) return null;
            string V(Vector3 v)=>string.Format(CultureInfo.InvariantCulture,"{0:0.##}~{1:0.##}~{2:0.##}",v.x,v.y,v.z);
            key+="~"+(data?.m_bValue==true?"1":"0")+"~"+(int)(data?.m_eType??default)+"~"+origin+"~"+V(data?.m_v3AlternativeTarget??default)+"~"+V(t.m_v3SkillTargetPos)+"~"+V(t.m_v3AlternativeTarget);
        }
        return string.Format(CultureInfo.InvariantCulture,"{0} {1} {2:0.##} {3:0.##} {4:0.##} {5}",code,key,p.x,p.y,p.z,flags);
    }
    private static int Modifiers(SkillCommand command)=>
        (command.hasModifier(SkillCommand.ExecutionModifier.Run)?1:0) |
        (command.hasModifier(SkillCommand.ExecutionModifier.WasCrawling)?2:0) |
        (command.hasModifier(SkillCommand.ExecutionModifier.AdvancedExecution)?4:0) |
        (command.hasModifier(SkillCommand.ExecutionModifier.AlternativeExecution)?8:0);
    private static void ApplyModifiers(SkillCommand command,int flags)
    {
        command.setModifier(SkillCommand.ExecutionModifier.Run,(flags&1)!=0);
        command.setModifier(SkillCommand.ExecutionModifier.WasCrawling,(flags&2)!=0);
        command.setModifier(SkillCommand.ExecutionModifier.AdvancedExecution,(flags&4)!=0);
        command.setModifier(SkillCommand.ExecutionModifier.AlternativeExecution,(flags&8)!=0);
        command.setModifier(SkillCommand.ExecutionModifier.PlayerInduced,true);
    }
    private static void ReplayResources(SkillCommand command,bool confirmedReplay)
    {
        if(!confirmedReplay)return;
        command.setModifier(SkillCommand.ExecutionModifier.IgnoreAmmo,true);
        command.setModifier(SkillCommand.ExecutionModifier.IgnoreCooldown,true);
    }
    internal static bool Issue(MiCharacter actor,string[] f,int start,bool confirmedReplay=false,int replaySerial=0)
    {
        if(!confirmedReplay)return IssueNative(actor,f,start,false,replaySerial);
        if(!InteractionProtocol.Descriptor(f,start)||actor?.controller==null)return false;
        var kind=(Skill.SkillType)Enum.Parse(typeof(Skill.SkillType),InteractionProtocol.Skill(f[start]));
        var skill=actor.controller.getSkill(kind)?.TryCast<PlayerSkill>();
        return SkillResourceSync.SubmitConfirmed(skill,()=>IssueNative(actor,f,start,true,replaySerial));
    }
    private static bool IssueNative(MiCharacter actor,string[] f,int start,bool confirmedReplay,int replaySerial)
    {
        if(!InteractionProtocol.Descriptor(f,start) || actor?.health?.eHealthState!=MiCharacterHealth.HealthState.Alive) return false;
        var kind=(Skill.SkillType)Enum.Parse(typeof(Skill.SkillType),InteractionProtocol.Skill(f[start]));
        if(kind==Skill.SkillType.GhostCallBack&&f[start+1]=="n"){
            var ghost=actor.controller.getSkill(Skill.SkillType.GhostPlace)?.TryCast<SkillPlacePossessableGhost>();
            if(ghost==null)return false;
            bool restoring=NativeInput.Restoring;NativeInput.Restoring=true;
            try{ghost.commandMoveGhostToPlayer();return true;}finally{NativeInput.Restoring=restoring;}
        }
        var skill=actor.controller.getSkill(kind)?.TryCast<PlayerSkill>();
        if(f[start+1]=="n")
        {
            // Preserve genuinely targetless native commands. Never fabricate
            // a click at world origin, or permit ranged attacks to bypass target validation.
            if(skill==null || !InteractionProtocol.NullTarget(f[start]) || !skill.bCommandBySelectMouse || !confirmedReplay&&!skill.bActivatable)return false;
            bool teleport=kind is Skill.SkillType.TeleportMove or Skill.SkillType.TeleportKill or Skill.SkillType.TeleportKnockout;
            bool release=kind is Skill.SkillType.ControlCancel or Skill.SkillType.OvertakeCancelPaused;
            // Native host trace: a marker teleport carries a self target,
            // although it needs no user-selected destination. Null crashes
            // the original controller setter inside executeByCall.
            var instant=new SkillCommand(skill,teleport||release?new MiCharacterTarget(actor,null):null);
            ApplyModifiers(instant,int.Parse(f[start+5],CultureInfo.InvariantCulture));
            ReplayResources(instant,confirmedReplay);
            if(confirmedReplay)RememberCommand(instant,replaySerial);
            if(!teleport&&!release)
            {actor.controllerPlayer.skillCommand=instant;return true;}
            // The recorded native teleport runs immediately, outside the
            // controller's movement queue. executeByCall owns validation,
            // onCommand, execution, resource consumption and post-execution.
            bool restored=NativeInput.Restoring;
            bool accepted;
            try { NativeInput.Restoring=restored||confirmedReplay; accepted=skill.executeByCall(instant); }
            finally { NativeInput.Restoring=restored; }
            if(accepted&&!confirmedReplay)NativeInput.Probe?.InteractionExecuted(actor,instant);
            MoveProbePlugin.ProbeLog.LogInfo("Native targetless skill issued "+f[start]+" accepted="+accepted);
            return accepted;
        }

        var encoded=f[start+1].Split('~');
        var entity=Resolve(encoded[0]);var point=CrewSync.ReadPosition(f,start+2);
        if(skill==null || encoded[0]!="-" && entity==null ||
            !InteractionProtocol.Ability(f[start]) && (!float.IsFinite(Vector3.Distance(actor.transform.position,point)) ||
            entity!=null && Vector3.Distance(entity.transform.position,point)>8f)) return false;
        var target=entity==null ? new MiCharacterTarget(point) : new MiCharacterTarget(entity,null);
        target.m_v3TargetPos=point;target.m_v3SkillTargetPos=point;
        int flags=int.Parse(f[start+5],CultureInfo.InvariantCulture);
        if(InteractionProtocol.Context(f[start]))
        {
            // Rebuild the native entry transform on this machine. The object
            // center/clicked floor is not its conversation approach position.
            var mode=PlayerSkill.MiValidSkillTarget.GetModifier.Command|PlayerSkill.MiValidSkillTarget.GetModifier.Apply|
                ((flags&4)!=0?PlayerSkill.MiValidSkillTarget.GetModifier.Advanced:PlayerSkill.MiValidSkillTarget.GetModifier.Default);
            var contextual=skill.getSkillForTarget(target,mode).m_playerSkill;
            if(contextual==null||!InteractionProtocol.Context(InteractionProtocol.Code(contextual.eSkillType.ToString())))return false;
            skill=contextual;
        }
        bool jump=kind==Skill.SkillType.UseJump || kind==Skill.SkillType.UseJumpAcross || kind==Skill.SkillType.UseJumpDown;
        bool traversal=jump || kind==Skill.SkillType.UseClimbEnter || kind==Skill.SkillType.UseClimbExit || kind==Skill.SkillType.UseClimbMove;
        if(InteractionProtocol.Geometry(f[start]) && encoded.Length==13)
        {
            var origin=Resolve(encoded[3]);
            if(encoded[3]!="-" && origin==null) return false;
            var landing=CrewSync.ReadPosition(encoded,4);
            // Preserve the native target chosen on the guest. Its click point
            // is often the take-off point, so recomputing from that alone loses
            // the landing/trigger pairing. Native validity still owns admission.
            var data=new MiCharacterTargetData();
            data.m_bValue=encoded[1]=="1";data.m_eType=(MiCharacterTargetData.Type)int.Parse(encoded[2],CultureInfo.InvariantCulture);
            data.m_additionalHighlightableOrJumpTriggerFrom=origin;data.m_v3AlternativeTarget=landing;
            target.m_charTargetData=data;
            target.m_v3SkillTargetPos=CrewSync.ReadPosition(encoded,7);
            target.m_v3AlternativeTarget=CrewSync.ReadPosition(encoded,10);
            // Mouse aiming updates charTargetData first. The native parallel
            // ladder state reads TARGET +0x50, not that data object. Its old
            // cached alternative was the previous rung in the guest logs.
            if(kind==Skill.SkillType.UseClimbMove)
            {
                if(!data.m_bValue||data.m_eType!=MiCharacterTargetData.Type.ClimbMove)return false;
                target.m_v3AlternativeTarget=landing;
            }
            // Native ladder entry selects an actual entry transform, not just
            // XYZ. The old reconstruction dropped it, so top entry fell back
            // to the bottom despite carrying the correct top coordinates.
            if(kind==Skill.SkillType.UseClimbEnter || kind==Skill.SkillType.UseClimbExit)
            {
                var ladder=entity?.TryCast<MiUsableLadder>();
                if(ladder!=null)
                {
                    var top=ladder.entryTop;var bottom=ladder.entryBottom;
                    if(top==null||bottom==null)return false;
                    float topGap=Vector3.Distance(point,top.position),bottomGap=Vector3.Distance(point,bottom.position);
                    if(Math.Min(topGap,bottomGap)>1f)return false;
                    target.m_transTarget=topGap<bottomGap?top:bottom;
                }
            }
        }
        else if(InteractionProtocol.Geometry(f[start]) && !InteractionProtocol.CastGeometry(f[start]) && !(kind==Skill.SkillType.UseClimbExit&&Mounted(actor)))
        {
            // isTargetValid only checks a target. It does not construct the
            // jump origin/landing or climb data normally set by mouse aiming.
            // Run the native contextual target builder with Apply first.
            var mode=PlayerSkill.MiValidSkillTarget.GetModifier.Command | PlayerSkill.MiValidSkillTarget.GetModifier.Apply |
                ((flags&4)!=0 ? PlayerSkill.MiValidSkillTarget.GetModifier.Advanced : PlayerSkill.MiValidSkillTarget.GetModifier.Default);
            var found=skill.getSkillForTarget(target,mode).m_playerSkill;
            if(found==null || found.eSkillType!=kind || jump && !target.m_charTargetData.m_bValue)
            {
                MoveProbePlugin.ProbeLog.LogInfo("Native interaction rejected "+f[start]+": contextual target data unavailable");
                return false;
            }
        }
        // Recovery entries have no mouse-produced geometry. Select a real
        // entry transform before validation, just like a normal encoded CE.
        if((kind==Skill.SkillType.UseClimbEnter||kind==Skill.SkillType.UseClimbExit) && encoded.Length!=13)
        {
            var ladder=entity?.TryCast<MiUsableLadder>();
            var top=ladder?.entryTop;var bottom=ladder?.entryBottom;
            if(top==null||bottom==null)return false;
            target.m_transTarget=Vector3.Distance(point,top.position)<Vector3.Distance(point,bottom.position)?top:bottom;
            target.m_v3TargetPos=target.m_transTarget.position;
            target.m_v3SkillTargetPos=target.m_transTarget.position;
        }
        if(confirmedReplay&&kind==Skill.SkillType.CannonShoot&&CannonLaunchSync.AlreadyQueued(actor,replaySerial))return true;
        if(confirmedReplay&&!CannonLaunchSync.Prepare(actor,kind,replaySerial))return false;
        if(confirmedReplay&&!BaitObjectSync.PrepareThrow(actor,kind))return false;
        bool confirmedDig=confirmedReplay&&(kind==Skill.SkillType.DigKill||kind==Skill.SkillType.DigKnockout||kind==Skill.SkillType.DigKnock||kind==Skill.SkillType.DigKillHideCorpse);
        if(confirmedDig&&!DigStateSync.PrepareStrike(actor,encoded.Length==13?target.m_v3AlternativeTarget:actor.transform.position))return false;
        // The host's result snapshot can already say "out of ground" before
        // this confirmed strike is replayed. Rechecking that transient local
        // prerequisite rejects the very action which ended the underground
        // state. Host-side admission still uses the original validity check.
        if(!confirmedDig&&!skill.isTargetValid(target))
        {
            MoveProbePlugin.ProbeLog.LogInfo("Native interaction rejected "+f[start]+": native validity target="+encoded[0]+" geometry="+(encoded.Length==13));
            return false;
        }
        var command=new SkillCommand(skill,target);
        ApplyModifiers(command,flags);
        ReplayResources(command,confirmedReplay);
        if(confirmedDig)command.setModifier(SkillCommand.ExecutionModifier.IgnoreRange,true);
        if((kind==Skill.SkillType.UseClimbMove || kind==Skill.SkillType.UseClimbExit) && !Mounted(actor))return false;
        if(confirmedReplay)RememberCommand(command,replaySerial);
        if(kind==Skill.SkillType.OvertakeGun)SkillLifecycleTrace.SkillEvent(skill,"queue-before",command);
        actor.controllerPlayer.skillCommand=command;
        MimicNative.ObserveAction(actor,command,confirmedReplay?"replay-queued":"host-queued");
        if(kind==Skill.SkillType.OvertakeGun)SkillLifecycleTrace.SkillEvent(skill,"queue-after",command);
        if((kind==Skill.SkillType.UseClimbMove||kind==Skill.SkillType.UseClimbExit)&&Mounted(actor))
        {
            var state=actor.controller.getSkill(Skill.SkillType.UseClimbMove).TryCast<SkillUseLadderMove>().m_parallelStateUseLadder;
            // Exit stays active while travelling to an endpoint. Update the
            // mounted state's destination now instead of queuing behind it.
            state.m_eStateCurrent=state.getStateUpdateTarget(command);
        }
        if(confirmedReplay)CannonLaunchSync.Arm(actor,command,replaySerial);
        // Native ladder updates read the process slot ahead of the input slot.
        // Retire only an old ladder destination, never an active mount/dismount
        // animation, after the replacement has passed native target validation.
        if((kind==Skill.SkillType.UseClimbMove || kind==Skill.SkillType.UseClimbExit) &&
            Mounted(actor) &&
            !EntryOrExit(actor.controller.skillCommandActive) &&
            actor.controller.skillCommandProcess?.m_skill?.eSkillType is Skill.SkillType.UseClimbMove or Skill.SkillType.UseClimbExit)
            actor.controller.skillCommandProcess=null;
        MoveProbePlugin.ProbeLog.LogInfo("Native interaction issued "+f[start]+" serial="+replaySerial+" target="+f[start+1]+(InteractionProtocol.Geometry(f[start])?" data="+target.m_charTargetData.m_eType+" alternative="+CrewSync.Position(target.m_charTargetData.m_v3AlternativeTarget):""));
        return true;
    }
    [HarmonyPatch(typeof(MiCharacterControllerPlayer),"onSkillExecute")]
    internal static class Executed
    {
        private static void Postfix(MiCharacterControllerPlayer __instance,SkillCommand __0)
        { SkillLifecycleTrace.SkillEvent(__0?.m_skill?.TryCast<PlayerSkill>(),"execute",__0); if(NativeInput.Probe!=null) NativeInput.Probe.InteractionExecuted(__instance.character,__0); }
    }
    internal void Mark(bool b,SkillCommand command)
    {
        string descriptor=Describe(command);if(descriptor==null) return;
        var cue=b?second:first;
        bool sameAbility=cue.pointer==command.Pointer && cue.descriptor!=null &&
            InteractionProtocol.Ability(descriptor.Split(' ')[0]) && cue.descriptor.Split(' ')[0]==descriptor.Split(' ')[0];
        if(cue.ended || cue.pointer!=command.Pointer || !sameAbility && cue.descriptor!=descriptor)
        { cue.serial=CastSerial(cue,command);cue.pointer=command.Pointer;cue.descriptor=descriptor; }
        cue.descriptor=descriptor;cue.ended=false;cue.phase="D";cue.until=Time.realtimeSinceStartup+8f;
        RememberCommand(command,cue.serial);
    }
    private static string LadderPosition(MiCharacter actor)
    {
        var skill=actor?.controller?.getSkill(Skill.SkillType.UseClimbMove)?.TryCast<SkillUseLadderMove>();
        var state=skill?.m_parallelStateUseLadder;
        if(state==null || !state.bActive)return null;
        // Sampling must never create native saveable commands/targets. Those
        // constructors register native objects, even if no order is issued.
        // Read backing fields once and encode a managed descriptor directly.
        var ladder=state.m_usableLadder;
        var target=state.m_targetCurrent;
        var phase=state.m_eStateCurrent;
        if(ladder==null || target==null || (phase!=ParallelStateUseLadder.State.Idle &&
            phase!=ParallelStateUseLadder.State.MoveUpToIdle &&
            phase!=ParallelStateUseLadder.State.MoveDownToIdle))return null;
        // The native idle goal survives exit/re-entry. It is meaningful only
        // while the current target still carries actual ClimbMove metadata.
        var data=target.m_charTargetData;
        if(data==null || !data.m_bValue || data.m_eType!=MiCharacterTargetData.Type.ClimbMove)return null;
        string key=Key(ladder);if(key==null)return null;
        var goal=state.m_v3TargetIdle;
        var point=target.m_v3TargetPos;
        string V(Vector3 v)=>string.Format(CultureInfo.InvariantCulture,"{0:0.##}~{1:0.##}~{2:0.##}",v.x,v.y,v.z);
        string descriptor=string.Format(CultureInfo.InvariantCulture,"CM {0}~1~5~-~{1}~{2}~{1} {3:0.##} {4:0.##} {5:0.##} 0",
            key,V(goal),V(target.m_v3SkillTargetPos),point.x,point.y,point.z);
        return InteractionProtocol.Descriptor(descriptor.Split(' '),0)?descriptor:null;
    }
    private static string Wire(Cue cue,MiCharacter actor)
    {
        // Mid-ladder motion/idle is a parallel state, and can outlive all
        // controller commands. Publish its native destination until exit.
        string ladder=LadderPosition(actor);
        if(ladder!=null)
        {
            if(cue.ended || cue.descriptor!=ladder)
                cue.serial=NextSerial();
            cue.pointer=IntPtr.Zero;cue.descriptor=ladder;cue.phase="R";cue.ended=false;cue.until=Time.realtimeSinceStartup+8f;
            return cue.serial+" R "+ladder;
        }
        var queued=actor?.controllerPlayer?.skillCommand;
        var process=actor?.controller?.skillCommandProcess;
        var active=actor?.controller?.skillCommandActive;
        // Native ladder enter can remain in the input slot while exit/move
        // runs. Publishing the input first alternated CE/CX every snapshot.
        var cmd=Supported(process?.m_skill?.eSkillType??Skill.SkillType.None) ? process :
            Supported(active?.m_skill?.eSkillType??Skill.SkillType.None) ? active : queued;
        string descriptor=Describe(cmd);
        if(descriptor!=null)
        {
            bool sameTraversal=cue.pointer==cmd.Pointer && cue.descriptor!=null &&
                (InteractionProtocol.Traversal(descriptor.Split(' ')[0]) || InteractionProtocol.Ability(descriptor.Split(' ')[0])) && cue.descriptor.Split(' ')[0]==descriptor.Split(' ')[0];
            if(cue.ended || cue.pointer!=cmd.Pointer || !sameTraversal && cue.descriptor!=descriptor)
            { cue.serial=CastSerial(cue,cmd);cue.pointer=cmd.Pointer;cue.descriptor=descriptor;cue.phase="R"; }
            if(InteractionProtocol.Ability(descriptor.Split(' ')[0]))cue.descriptor=descriptor;
            RememberCommand(cmd,cue.serial);
            cue.ended=false;
            cue.until=Time.realtimeSinceStartup+8f;
        }
        else
        {
            // A parallel ladder can finish between snapshots, without its
            // short exit command ever being observed. Retain an explicit
            // native exit so a replica still mounted at mid-height can leave.
            if(!Climbing(actor) && cue.descriptor?.StartsWith("CM ")==true)
            {
                var key=cue.descriptor.Split(' ')[1].Split('~')[0];
                var usable=Resolve(key)?.TryCast<MiUsableLadder>();
                if(usable?.entryTop!=null && usable.entryBottom!=null)
                {
                    var p=actor.transform.position;
                    var end=Vector3.Distance(p,usable.entryTop.position)<Vector3.Distance(p,usable.entryBottom.position)?usable.entryTop.position:usable.entryBottom.position;
                    cue.serial=NextSerial();cue.descriptor="CX "+key+" "+CrewSync.Position(end)+" 0";
                    cue.phase="D";cue.until=Time.realtimeSinceStartup+8f;
                }
            }
            cue.ended=true; if(cue.phase=="R") cue.phase="C";
        }
        return cue.descriptor==null || Time.realtimeSinceStartup>cue.until ? "0 - - - 0 0 0 0" :
            cue.serial+" "+cue.phase+" "+cue.descriptor;
    }
    internal string WriteActor(MiCharacter actor)=>Wire(first,actor);
    internal string Write(MiCharacter a,MiCharacter b)=>" I "+Wire(first,a)+" "+Wire(second,b);
    internal static string ProtectedTarget(MiCharacter actor)
    {
        if(actor?.controller==null)return null;
        // A queued skill can still be approaching. Protect only a target whose
        // native action is already processing/active; queued aim alone must
        // not disable authoritative patrols or health reconciliation.
        foreach(var command in new[]{actor.controller.skillCommandActive,actor.controller.skillCommandProcess})
        {
            if(command?.m_skill==null || !InteractionProtocol.Ability(InteractionProtocol.Code(command.m_skill.eSkillType.ToString())))continue;
            if(command.m_target?.m_entityTarget!=null && command.m_target.m_entityTarget.bAsCharacter(out var target) &&
                target!=null && target!=actor && !target.guid.isEmpty())return Melee.GuardId(target);
        }
        return null;
    }
    internal bool ProtectBodies(string[] wire,int offset,MiCharacter actor)
    {
        if(actor?.controller==null || actor.controllerPlayer==null) return false;
        if(wire==null || wire[offset]=="0" || wire[offset+1]=="C" ||
            (wire[offset+2]!="BT" && wire[offset+2]!="BD")) return false;
        int serial=int.Parse(wire[offset],CultureInfo.InvariantCulture);
        return !played.TryGetValue(actor.GetInstanceID(),out int old) || old!=serial ||
            Supported(actor.controller.skillCommandProcess?.m_skill?.eSkillType??Skill.SkillType.None) ||
            Supported(actor.controller.skillCommandActive?.m_skill?.eSkillType??Skill.SkillType.None) ||
            Supported(actor.controllerPlayer.skillCommand?.m_skill?.eSkillType??Skill.SkillType.None);
    }
    internal bool Apply(string[] wire,int offset,MiCharacter actor,Vector3 host,bool fresh)
    {
        if(actor?.controller==null || actor.controllerPlayer==null || actor.health?.eHealthState!=MiCharacterHealth.HealthState.Alive) return false;
        bool climbing=Climbing(actor);
        if(wire==null || !fresh) return climbing;
        int id=actor.GetInstanceID(),serial=int.Parse(wire[offset],CultureInfo.InvariantCulture);
        bool justFinished=!climbing && onLadder.Remove(id);
        if(climbing) onLadder.Add(id);
        if(serial==0) { middleTargets.Remove(actor.Pointer);protectedUntil.Remove(id);return climbing; }
        string phase=wire[offset+1],code=wire[offset+2];
        if(code=="CM"&&phase!="C")middleTargets[actor.Pointer]=new Middle{wire=wire,offset=offset,at=Time.realtimeSinceStartup};
        else middleTargets.Remove(actor.Pointer);
        bool ladderCue=code=="CE" || code=="CX" || code=="CM";
        if(!climbing && code=="CX" && Vector3.Distance(actor.transform.position,host)<0.75f &&
            Vector3.Distance(host,CrewSync.ReadPosition(wire,offset+4))<0.75f)
        { played[id]=serial; protectedUntil.Remove(id); return false; }
        if(phase=="C")
        {
            if(!climbing && played.TryGetValue(id,out int previous) && previous==serial &&
                !CharmingGhostNative.IsRecoveryCommand(actor.controllerPlayer.skillCommand) &&
                !RecoveryCommand(actor.controllerPlayer.skillCommand) &&
                actor.controllerPlayer.skillCommand?.m_skill?.eSkillType.ToString()==InteractionProtocol.Skill(code) && actor.controllerPlayer.bCanMove)
                actor.controllerPlayer.cancelSkill();
            played[id]=serial;protectedUntil.Remove(id);return climbing;
        }
        bool current=played.TryGetValue(id,out int old) && old==serial;
        if(current && code=="CM" && Mounted(actor))
        {
            var state=actor.controller.getSkill(Skill.SkillType.UseClimbMove)?.TryCast<SkillUseLadderMove>()?.m_parallelStateUseLadder;
            var encoded=wire[offset+3].Split('~');
            // A matching serial is not proof that mouse aiming or a native
            // continuation left the parallel state's goal unchanged.
            if(state!=null && encoded.Length==13 && Vector3.Distance(state.m_v3TargetIdle,CrewSync.ReadPosition(encoded,4))>0.12f)
                current=false;
        }

        // An exit describes leaving the ladder, not permission to mount it
        // again. A retained CX used to bounce a finished guest up and down.
        if(code=="CX" && !Mounted(actor) && !climbing)
        {played[id]=serial;protectedUntil.Remove(id);return false;}
        // A late snapshot may start with mid-climb/exit, after CE was missed.
        // Never execute an exit animation on the ground or ground-follow a
        // point halfway up a ladder. Re-enter through native target building.
        if(code=="CM" && !Mounted(actor))
        {
            if(climbing || Ladder(actor.controllerPlayer.skillCommand))return true;
            if(retryAt.TryGetValue(id,out float retry) && Time.realtimeSinceStartup<retry)return true;
            retryAt[id]=Time.realtimeSinceStartup+0.5f;
            played.Remove(id); // A new mount must receive its middle goal again.
            var key=wire[offset+3].Split('~')[0];
            var ladder=Resolve(key)?.TryCast<MiUsableLadder>();
            if(ladder?.entryTop==null||ladder.entryBottom==null)return true;
            var local=actor.transform.position;
            var point=Vector3.Distance(local,ladder.entryTop.position)<Vector3.Distance(local,ladder.entryBottom.position)?
                ladder.entryTop.position:ladder.entryBottom.position;
            var entry=("CE "+key+" "+CrewSync.Position(point)+" 0").Split(' ');
            try { Issue(actor,entry,0,true); }
            catch(Exception ex) { if(Time.realtimeSinceStartup>=nextWarning){nextWarning=Time.realtimeSinceStartup+2f;MoveProbePlugin.ProbeLog.LogWarning("Ladder entry recovery: "+ex);} }
            return true; // Keep destination unconsumed until mounted.
        }
        if(!current)
        {
            // Do not restart an entry/exit animation, but do admit one new
            // CM/CX destination to the active parallel ladder state. That
            // state needs the exit order to choose up/down and leave idle.
            if(climbing && code=="CE") { played[id]=serial; return true; }
            // An exit command can occupy process throughout the whole climb,
            // not just the dismount animation. Native active ladder movement
            // must still accept a new intermediate destination.
            bool middle=(code=="CM" || code=="CX") && Mounted(actor) &&
                actor.controller.skillCommandActive?.m_skill?.eSkillType!=Skill.SkillType.UseClimbEnter;
            if(climbing && !middle && (LadderTransition(actor) || code!="CX")) return true;
            // Dig melee relocates at execution. The host is already beside
            // the victim while the replica must still emerge from its marker.
            // Requiring proximity first converts this into an ordinary walk.
            bool digStrike=code=="S22"||code=="S23"||code=="S24"||code=="S27"||code=="TK"||code=="TKN"||code=="TMV";
            if(!climbing && !ladderCue && !digStrike && Vector3.Distance(actor.transform.position,host)>2f) return false;
            if(retryAt.TryGetValue(id,out float next) && Time.realtimeSinceStartup<next) return ladderCue;
            retryAt[id]=Time.realtimeSinceStartup+0.25f;
            try
            {
                if(!Issue(actor,wire,offset+2,true,serial)) return ladderCue;
                played[id]=serial;protectedUntil[id]=Time.realtimeSinceStartup+3f;current=true;
            }
            catch(Exception ex)
            {
                if(Time.realtimeSinceStartup>=nextWarning) { nextWarning=Time.realtimeSinceStartup+2f;MoveProbePlugin.ProbeLog.LogWarning("Interaction replay "+code+": "+ex); }
                return ladderCue;
            }
        }
        // Ground following must not cancel native ladder/jump/door movement.
        var executing=actor.controller.skillCommandProcess?.m_skill;
        var queued=actor.controllerPlayer.skillCommand?.m_skill;
        bool active=Supported(executing?.eSkillType??Skill.SkillType.None) || Supported(queued?.eSkillType??Skill.SkillType.None) ||
            Supported(actor.controller.skillCommandActive?.m_skill?.eSkillType??Skill.SkillType.None);
        return climbing || current && active && (phase=="R" || protectedUntil.TryGetValue(id,out float until) && Time.realtimeSinceStartup<until);
    }
}
