using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

// A cannon load is an attachment/lock, not a second walk to the victim.
// Track explicit release records too, so a lost fire/empty cue cannot leave
// a character attached forever. Native load/empty own colliders, AI and UI.
internal sealed class CannonStateSync
{
    private sealed class Scope { internal MiCharacter Actor;internal string Owner;internal bool Tied,Stunned,Settled;internal Vector3 Position; }
    private static readonly Dictionary<IntPtr,SkillCannonShoot> shots=new();
    private static readonly HashSet<IntPtr> landingPending=new();
    internal static void ObserveShot(SkillCannonShoot skill,MiCharacter actor)
    {if(NativeInput.Probe?.NativeGuest==true&&skill!=null&&actor!=null){shots[actor.Pointer]=skill;landingPending.Add(actor.Pointer);wakeRetry.Remove(actor.Pointer);}}
    private static readonly Dictionary<IntPtr,float> wakeRetry=new();
    // Host trace: Knockout + Throw, then native ComeTo. health.comeTo()
    // alone changes health but leaves the knockout animation/control lifecycle.
    internal static bool ReconcileWake(MiCharacter actor,string expected)
    {
        if(expected!="Alive"||actor?.health==null||!scopes.TryGetValue(actor.Pointer,out var s))return false;
        var p=actor.processes;
        if(s.Settled&&!Held(actor)&&p!=null&&!p.bIsExecuting(MiCharacterProcess.Type.Throw)&&landingPending.Contains(actor.Pointer))
        {
            // The K1 position is the settled character position, whereas the
            // projectile endpoint can include a flight/attachment offset.
            actor.warp(s.Position,actor.transform.rotation);landingPending.Remove(actor.Pointer);
            MoveProbePlugin.ProbeLog.LogInfo("Cannon settled placement target="+InteractionSync.EntityIdentity(actor)+" point="+CrewSync.Position(s.Position));
        }
        bool knocked=actor.health.eHealthState==MiCharacterHealth.HealthState.Knockout || p?.bIsExecuting(MiCharacterProcess.Type.Knockout)==true;
        if(!knocked)return false;
        if(actor.health.eHealthState!=MiCharacterHealth.HealthState.Alive&&actor.health.eHealthState!=MiCharacterHealth.HealthState.Knockout)return false;
        if(!s.Settled||s.Tied||s.Stunned||Held(actor)||p==null||p.bIsExecuting(MiCharacterProcess.Type.Throw)||p.bIsExecuting(MiCharacterProcess.Type.ComeTo))return true;
        if(wakeRetry.TryGetValue(actor.Pointer,out float next)&&Time.realtimeSinceStartup<next)return true;
        wakeRetry[actor.Pointer]=Time.realtimeSinceStartup+.6f;
        if(!shots.TryGetValue(actor.Pointer,out var shot)||shot.m_actionWakeUp==null)return true;
        // Native second (successful) ComeTo supplies the cannon wake action.
        // The earlier skip-animation/null-action attempt is not that phase.
        var options=new MiCharacterProcessOptionsComeTo(shot.charOrigin,actor,Skill.SkillType.CannonShoot,true,false,shot.m_actionWakeUp,actor.animations.m_animHierarchyNode);
        bool accepted=p.bStart(options,0);
        MoveProbePlugin.ProbeLog.LogInfo("Cannon native ComeTo target="+InteractionSync.EntityIdentity(actor)+" accepted="+accepted+" health="+actor.health.eHealthState);
        return true;
    }
    private static readonly Dictionary<IntPtr,MiCharacter> observed=new();
    private static readonly Dictionary<IntPtr,Scope> scopes=new();
    private static readonly Dictionary<IntPtr,(MiCharacterProcessStun process,MiCharacterProcessOptionsStun options)> stunStates=new();
    [HarmonyPatch(typeof(MiCharacterProcessStun),"applyCharacterStunState")]
    internal static class StunHook
    {
        private static void Postfix(MiCharacterProcessStun __instance,MiCharacterProcessOptionsStun __0)
        {
            try {if(NativeInput.Probe?.NativeGuest==true&&__0?.charTarget!=null)
                stunStates[__0.charTarget.Pointer]=(__instance,__0);}
            catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Cannon stun observation: "+ex.GetType().Name);}
        }
    }
    [ThreadStatic] private static bool applying;
    private readonly Dictionary<string,MiCharacter> known=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Scope> replicas=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> reports=new(StringComparer.Ordinal);
    private float nextCapture,nextError;
    internal static bool Held(MiCharacter actor)=>NativeInput.Probe?.NativeGuest==true&&actor!=null&&(CannonFlightSync.Held(actor)||CannonLaunchSync.Deferred(actor)||scopes.TryGetValue(actor.Pointer,out var s)&&s.Owner!="-");
    private static SkillCannonLoad Cannon(MiCharacter owner)=>owner?.controller?.getSkill(Skill.SkillType.CannonLoad)?.TryCast<SkillCannonLoad>();
    private static SkillCannonLoadBase Source(MiCharacter actor)=>actor?.cannonInfo?.bInsideCannon==true?actor.cannonInfo.m_skillCannonLoad:null;
    internal static void Observe(MiCharacter target)
    {if(!applying&&NativeInput.Probe?.NativeGuest!=true&&target!=null)observed[target.Pointer]=target;}
    internal void Clear()
    {
        applying=true;
        try{foreach(var s in replicas.Values)try{Release(s.Actor,s.Tied);}catch(Exception ex){Error("clear",ex);}}
        finally{applying=false;replicas.Clear();known.Clear();scopes.Clear();observed.Clear();stunStates.Clear();wakeRetry.Clear();shots.Clear();landingPending.Clear();reports.Clear();CannonLaunchSync.Clear();CannonFlightSync.Clear();nextCapture=nextError=0;}
    }
    internal void Protect(HashSet<string> ids)
    {foreach(var pair in replicas)if((pair.Value.Owner!="-"||CannonLaunchSync.Deferred(pair.Value.Actor))&&pair.Key[0]=='e')ids.Add(pair.Key.Substring(1));}
    internal List<string[]> Capture(IEnumerable<MiCharacter> actors,IEnumerable<MiCharacter> guards)
    {
        SkillLifecycleTrace.Tick();
        float now=Time.realtimeSinceStartup;if(now<nextCapture)return null;nextCapture=now+.1f;
        try
        {
            void Remember(MiCharacter actor)
            {
                if(actor==null)return;
                string key=InteractionSync.EntityIdentity(actor);
                if(!FreezeProtocol.Character(key))throw new InvalidOperationException("Cannon target has no stable identity");
                known[key]=actor;
            }
            foreach(var actor in observed.Values)Remember(actor);
            if(actors!=null)foreach(var actor in actors)if(Source(actor)!=null)Remember(actor);
            if(guards!=null)foreach(var actor in guards)if(Source(actor)!=null)Remember(actor);
            var rows=new List<string[]>();
            foreach(var pair in known)
            {
                var actor=pair.Value;if(actor==null)continue;
                var source=Source(actor);string owner=source==null?"-":InteractionSync.EntityIdentity(source.charOrigin);
                string skill=source==null?"-":source.eSkillType.ToString();
                int conditions=actor.health.m_iHealthConditionFlag;
                int flags=((conditions&64)!=0?1:0)|((conditions&(16|256))!=0?2:0);
                var p=GuestGroundTarget.NavigationPosition(actor);
                var row=new[]{pair.Key,owner,skill,flags.ToString(CultureInfo.InvariantCulture),p.x.ToString("G9",CultureInfo.InvariantCulture),p.y.ToString("G9",CultureInfo.InvariantCulture),p.z.ToString("G9",CultureInfo.InvariantCulture),
                    actor.processes?.bIsExecuting(MiCharacterProcess.Type.Throw)==true?"1":"0"};
                if(!CannonProtocol.Row(row))throw new InvalidOperationException("Unsupported native cannon attachment");
                rows.Add(row);
            }
            if(rows.Count>CannonProtocol.MaxActors)throw new InvalidOperationException("Cannon history exceeds bound");return rows;
        }catch(Exception ex){Error("capture",ex);return null;}
    }
    private static void Release(MiCharacter target,bool tied)
    {
        var source=Source(target);if(source==null)return;
        var cannon=Cannon(source.charOrigin);
        if(cannon==null||cannon.charLoaded==null||cannon.charLoaded.Pointer!=target.Pointer)
            throw new InvalidOperationException("Inconsistent native cannon owner");
        var node=target.animations.m_animHierarchyNode;
        // Wakeup here is the native removal of the cannon's temporary TieUp.
        // Preserve a victim who is still tied on the host; do not invent KO.
        cannon.notifyEmpty(tied?SkillCannonLoad.EmptyCannonOptions.None(node):SkillCannonLoad.EmptyCannonOptions.Wakeup(node));
    }
    internal static bool RestoreShot(MiCharacter owner,MiCharacter target,string variant)
    {
        var canonical=Cannon(owner);
        var skill=owner?.controller?.getSkill(variant=="CannonAttack"?Skill.SkillType.CannonAttack:Skill.SkillType.CannonLoad)?.TryCast<SkillCannonLoadBase>();
        if(canonical==null||skill==null||target?.animations?.m_animHierarchyNode==null)return false;
        if(canonical.charLoaded?.Pointer==target.Pointer&&Source(target)?.charOrigin?.Pointer==owner.Pointer)return true;
        bool previous=applying;applying=true;
        try
        {
            if(canonical.charLoaded!=null)Release(canonical.charLoaded,(canonical.charLoaded.health.m_iHealthConditionFlag&64)!=0);
            if(Source(target)!=null)Release(target,(target.health.m_iHealthConditionFlag&64)!=0);
            SkillCannonLoad.s_applyLoadedState(owner,target,skill.m_animationLoaded,target.animations.m_animHierarchyNode,skill);
            canonical.notifyLoad(target);return canonical.charLoaded?.Pointer==target.Pointer;
        }
        finally{applying=previous;}
    }
    internal void Apply(string[][] rows)
    {
        // An absent target is unknown, not an empty cannon.
        if(rows==null)return;
        var resolved=new List<string[]>();
        foreach(var row in rows)
        {
            var actor=InteractionSync.Resolve(row[0])?.TryCast<MiCharacter>();if(actor?.cannonInfo==null)continue;
            if(replicas.TryGetValue(row[0],out var old)&&old.Actor.Pointer!=actor.Pointer)scopes.Remove(old.Actor.Pointer);
            int flags=int.Parse(row[3],CultureInfo.InvariantCulture);
            var scope=new Scope{Actor=actor,Owner=row[1],Tied=(flags&1)!=0,Stunned=(flags&2)!=0,Settled=row.Length==8&&row[1]=="-"&&row[7]=="0",Position=row.Length>=7?CrewSync.ReadPosition(row,4):default};replicas[row[0]]=scope;scopes[actor.Pointer]=scope;
            resolved.Add(row);
        }
        foreach(var row in resolved)try
        {
            if(!replicas.TryGetValue(row[0],out var scope))continue;
            var target=scope.Actor;
            var owner=row[1]=="-"?null:InteractionSync.Resolve(row[1])?.TryCast<MiCharacter>();
            var canonical=Cannon(owner);
            var skill=owner?.controller?.getSkill(row[2]=="CannonAttack"?Skill.SkillType.CannonAttack:Skill.SkillType.CannonLoad)?.TryCast<SkillCannonLoadBase>();
            if(row[1]!="-"&&(canonical==null||skill==null||target.animations?.m_animHierarchyNode==null))continue;
            applying=true;
            try
            {
                var current=Source(target);
                // K1 may already be empty while the matching native shot is
                // queued. Let executeThrow consume its own victim once.
                bool settled=row.Length==8&&row[1]=="-"&&row[7]=="0";
                bool completedFlight=settled&&CannonFlightSync.Settle(target);
                string status=row[1]+"/"+row[3]+"/"+(row.Length==8?row[7]:"?")+" local="+target.health?.m_iHealthConditionFlag+" throw="+(target.processes?.bIsExecuting(MiCharacterProcess.Type.Throw)==true)+" flight="+CannonFlightSync.Held(target)+" deferred="+CannonLaunchSync.Deferred(target)+" stunOptions="+stunStates.ContainsKey(target.Pointer);
                if(!reports.TryGetValue(row[0],out var previousStatus)||previousStatus!=status)
                {reports[row[0]]=status;MoveProbePlugin.ProbeLog.LogInfo("Cannon lifecycle "+row[0]+" "+status);}
                if(CannonFlightSync.Held(target)||row[1]=="-"&&CannonLaunchSync.Deferred(target))continue;
                if(current!=null&&(row[1]=="-"||current.charOrigin?.Pointer!=owner.Pointer))Release(target,scope.Tied);
                if(row[1]=="-")
                {
                    // Cannon impact applies Stun independently of TieUp and
                    // of Alive/Knockout. Finish that native process when the
                    // host has recovered; health-state equality is insufficient.
                    bool recovering=completedFlight;
                    // Throw owns landing and the carry-stop cleanup after
                    // impact. Stopping it here strands those animation locks.
                    bool landing=target.processes?.bIsExecuting(MiCharacterProcess.Type.Throw)==true;
                    if(settled&&!landing&&!scope.Stunned&&stunStates.TryGetValue(target.Pointer,out var nativeStun))
                    {
                        target.processes?.stop(MiCharacterProcess.Type.Stun,null);
                        // The options own a separate animation coroutine. It
                        // can survive removal of the process execution entry.
                        nativeStun.process.onProcessFinish(nativeStun.options);
                        stunStates.Remove(target.Pointer);recovering=true;
                        MoveProbePlugin.ProbeLog.LogInfo("Cannon recovery finished target="+row[0]+" conditions="+target.health.m_iHealthConditionFlag);
                    }
                    if(!landing&&!scope.Stunned&&target.health!=null&&(target.health.m_iHealthConditionFlag&(16|256))!=0)
                    {
                        // stop(Type) drops an already-ended coroutine without
                        // running its cleanup. Retain its native stun handler
                        // and finish any remaining visual/input locks explicitly.
                        MiCharacterProcessStun stun=null;
                        var executions=target.processes?.m_lCharacterProcessExecutions;
                        if(executions!=null)foreach(var execution in executions)
                        {var candidate=execution.m_process?.TryCast<MiCharacterProcessStun>();if(candidate!=null){stun=candidate;break;}}
                        target.processes?.stop(MiCharacterProcess.Type.Stun,null);
                        if((target.health.m_iHealthConditionFlag&(16|256))!=0&&stun!=null)stun.onStop(target);
                        recovering=true;
                    }
                    if(settled&&recovering)
                        target.warp(new Vector3(float.Parse(row[4],CultureInfo.InvariantCulture),float.Parse(row[5],CultureInfo.InvariantCulture),float.Parse(row[6],CultureInfo.InvariantCulture)),target.transform.rotation);
                    // Native impact can detach the cannon before removing its
                    // temporary tie. A later empty row must reconcile that
                    // condition even when there is no attachment left to empty.
                    if(target.health!=null&&((target.health.m_iHealthConditionFlag&64)!=0)!=scope.Tied&&
                        !target.health.hasCondition(MiCharacterHealth.HealthCondition.InProcessOfBeingTiedUp))
                        target.processes?.bStart(new MiCharacterProcessOptionsTieUp(null,target,true,true,true),0);
                    continue;
                }
                current=Source(target);
                if(current!=null&&canonical.charLoaded?.Pointer==target.Pointer)continue;
                // A late snapshot may replace one loaded victim with another.
                // Release through native lifecycle before attaching the new one.
                if(canonical.charLoaded!=null&&canonical.charLoaded.Pointer!=target.Pointer)
                {
                    var previous=canonical.charLoaded;
                    bool tied=scopes.TryGetValue(previous.Pointer,out var old)?old.Tied:(previous.health.m_iHealthConditionFlag&64)!=0;
                    Release(previous,tied);
                }
                SkillCannonLoad.s_applyLoadedState(owner,target,skill.m_animationLoaded,target.animations.m_animHierarchyNode,skill);
                // Attack.notifyLoad uses its cached target field; recovery uses
                // the canonical load notification with the explicit actor.
                canonical.notifyLoad(target);
            }
            finally{applying=false;}
        }catch(Exception ex){Error("apply",ex);}
    }
    private void Error(string stage,Exception ex)
    {float now=Time.realtimeSinceStartup;if(now<nextError)return;nextError=now+5;MoveProbePlugin.ProbeLog.LogWarning("Cannon state "+stage+": "+ex.GetType().Name+" "+ex.Message);}
    internal static bool AllowLoad(MiCharacter owner,MiCharacter target)
    {
        if(applying||NativeInput.Probe?.NativeGuest!=true||target==null)return true;
        string id=InteractionSync.EntityIdentity(owner);
        if(scopes.TryGetValue(target.Pointer,out var scope)&&(scope.Owner=="-"||scope.Owner!=id))return false;
        foreach(var s in scopes.Values)if(s.Owner==id&&s.Actor.Pointer!=target.Pointer)return false;
        return true;
    }
    internal static bool AllowEmpty(SkillCannonLoad cannon)=>applying||NativeInput.Probe?.NativeGuest!=true||CannonLaunchSync.Consume(cannon)||cannon?.charLoaded==null||!scopes.TryGetValue(cannon.charLoaded.Pointer,out var scope)||scope.Owner=="-";
    [HarmonyPatch(typeof(SkillCannonLoad),"s_applyLoadedState")]
    internal static class StateHook
    {
        private static bool Prefix(MiCharacter __0,MiCharacter __1)=>AllowLoad(__0,__1);
        private static void Postfix(MiCharacter __1)=>Observe(__1);
    }
    [HarmonyPatch(typeof(SkillCannonLoad),"notifyLoad")]
    internal static class LoadHook
    {private static bool Prefix(SkillCannonLoad __instance,MiCharacter __0)=>AllowLoad(__instance.charOrigin,__0);private static void Postfix(MiCharacter __0)=>Observe(__0);}
    [HarmonyPatch(typeof(SkillCannonLoad),"notifyEmpty")]
    internal static class EmptyHook{private static bool Prefix(SkillCannonLoad __instance)=>AllowEmpty(__instance);}
}
