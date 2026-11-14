using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

// Reconcile the native parallel state, not a visual-only invisible flag.
// Both ordinary DigIn and the portal variant use native state type DigIn.
internal sealed class DigStateSync
{
    private sealed class Scope{internal MiCharacter Actor;internal ParallelStateDigInBase State;internal ParallelStateHandler Handler;internal bool Active,Hidden;}
    private static readonly Dictionary<IntPtr,Scope> handlers=new(),states=new();
    private readonly Dictionary<string,Scope> replicas=new(StringComparer.Ordinal);
    [ThreadStatic] private static bool applying;
    private float nextCapture,nextError;
    private const int InvisibleReason=16;
    private static string F(float v)=>v.ToString("G9",CultureInfo.InvariantCulture);
    private static Vector3 Point(string[] row,int at)=>new(float.Parse(row[at],CultureInfo.InvariantCulture),float.Parse(row[at+1],CultureInfo.InvariantCulture),float.Parse(row[at+2],CultureInfo.InvariantCulture));
    private static bool Hidden(MiCharacter actor)=>actor?.vis!=null&&(actor.vis.m_iRendererDisableReasons&InvisibleReason)!=0;
    private static bool Strike(MiCharacter actor)
    {
        bool Is(SkillCommand c)=>c?.m_skill?.eSkillType is Skill.SkillType.DigKill or Skill.SkillType.DigKnockout or Skill.SkillType.DigKnock or Skill.SkillType.DigKillHideCorpse;
        return Is(actor?.controllerPlayer?.skillCommand)||Is(actor?.controller?.skillCommandProcess)||Is(actor?.controller?.skillCommandActive);
    }
    private static ParallelStateDigInBase State(MiCharacter actor)=>actor?.parallelStateHandler?.getState(ParallelState.ParallelStateType.DigIn)?.TryCast<ParallelStateDigInBase>();
    internal static bool PrepareStrike(MiCharacter actor,Vector3 origin)
    {
        var state=State(actor)?.TryCast<ParallelStateDigIn>();
        if(state?.m_skillDigIn==null)return false;
        bool previous=applying;applying=true;
        try
        {
            if(!state.bActive)
            {
                actor.warp(origin,actor.transform.rotation);
                actor.parallelStateHandler.addState(ParallelState.ParallelStateType.DigIn,new MiCharacterTarget(origin));
            }
            state.m_skillDigIn.m_bDigInActive=true;
            if(!Hidden(actor))state.activateInvisibleState();
            return state.bActive;
        }
        finally{applying=previous;}
    }
    private static string Kind(ParallelStateDigInBase state)=>state?.TryCast<ParallelStateDigIn>()!=null?"D":state?.TryCast<ParallelStateDigInPortal>()!=null?"P":null;
    internal static bool Held(MiCharacter actor)=>NativeInput.Probe?.NativeGuest==true&&actor?.parallelStateHandler!=null&&handlers.TryGetValue(actor.parallelStateHandler.Pointer,out var scope)&&scope.Active;
    private static bool CarryMismatch(MiCharacter actor,bool hidden)=>actor?.carry?.bIsCarryingCharacter==true&&actor.carry.pickedUpCharacter!=null&&Hidden(actor.carry.pickedUpCharacter)!=hidden;
    private void Forget(string key)
    {
        if(!replicas.Remove(key,out var r))return;
        handlers.Remove(r.Handler.Pointer);states.Remove(r.State.Pointer);
    }
    internal void Clear()
    {
        applying=true;
        try{foreach(var r in replicas.Values)try
        {r.State.deactivateInvisibleState();if(r.State.bActive)r.Handler.removeState(ParallelState.ParallelStateType.DigIn);}
        catch(Exception ex){Error("clear",ex);}}
        finally{applying=false;replicas.Clear();handlers.Clear();states.Clear();nextCapture=nextError=0;}
    }
    private static List<MiCharacter> Actors(IEnumerable<MiCharacter> crew)
    {
        var result=new List<MiCharacter>();var seen=new HashSet<IntPtr>();
        void Add(MiCharacter a){if(a!=null&&seen.Add(a.Pointer))result.Add(a);}
        if(crew!=null)foreach(var a in crew)Add(a);
        foreach(var e in SkillObjectRegistry.Active.Entities)if(e!=null&&e.bAsCharacter(out var a))Add(a);return result;
    }
    internal List<string[]> Capture(IEnumerable<MiCharacter> crew)
    {
        float now=Time.realtimeSinceStartup;if(now<nextCapture)return null;nextCapture=now+.1f;
        try
        {
            var rows=new List<string[]>();foreach(var actor in Actors(crew))
            {
                var state=State(actor);if(state==null)continue;string kind=Kind(state);if(kind==null)continue;
                var position=GuestGroundTarget.NavigationPosition(actor);var marker=state.m_marker;
                var mp=marker==null?Vector3.zero:marker.transform.position;float yaw=marker==null?0:marker.transform.eulerAngles.y;
                var dig=state.TryCast<ParallelStateDigIn>()?.m_skillDigIn;
                if(kind=="D"&&dig==null)throw new InvalidOperationException("Dig skill not initialized");
                var row=new[]{InteractionSync.EntityIdentity(actor),kind,state.bActive?"1":"0",Hidden(actor)?"1":"0",kind=="P"?"-":dig.m_bDigInActive?"1":"0",F(position.x),F(position.y),F(position.z),marker==null?"0":"1",F(mp.x),F(mp.y),F(mp.z),F(yaw),F(actor.transform.eulerAngles.y)};
                if(!DigProtocol.Row(row))throw new InvalidOperationException("Invalid native dig state");rows.Add(row);
            }
            if(rows.Count>DigProtocol.MaxActors)throw new InvalidOperationException("Dig state exceeds bound");return rows;
        }catch(Exception ex){Error("capture",ex);return null;}
    }
    internal void Apply(string[][] rows)
    {
        // Rows are known actors, not a complete world inventory. Omission is
        // unknown; explicit inactive state ends a known actor's dig state.
        if(rows==null)return;
        foreach(var row in rows)try
        {
            string key=row[0];var actor=InteractionSync.Resolve(key)?.TryCast<MiCharacter>();var state=State(actor);
            if(state==null||Kind(state)!=row[1])continue;
            if(row[1]=="D"&&state.TryCast<ParallelStateDigIn>().m_skillDigIn==null)continue;
            // Replay owns the native emerge/teleport until its strike ends.
            // Applying the host's already-out snapshot here destroys the
            // prerequisite before that queued command reaches execute.
            if(Strike(actor))continue;
            var handler=actor.parallelStateHandler;
            if(replicas.TryGetValue(key,out var old)&&(old.Handler.Pointer!=handler.Pointer||old.State.Pointer!=state.Pointer))Forget(key);
            var scope=new Scope{Actor=actor,State=state,Handler=handler,Active=row[2]=="1",Hidden=row[3]=="1"};
            replicas[key]=scope;handlers[handler.Pointer]=scope;states[state.Pointer]=scope;
            applying=true;
            try
            {
                if(scope.Active)
                {
                    var point=Point(row,5);float yaw=float.Parse(row[13],CultureInfo.InvariantCulture);
                    bool rebuild=state.bActive&&row[8]=="1"&&state.m_marker==null;
                    if(rebuild)handler.removeState(ParallelState.ParallelStateType.DigIn);
                    // Native DigIn locks navigation. Correct its stationary
                    // authoritative anchor directly, never send another walk.
                    if(!state.bActive||Vector3.Distance(GuestGroundTarget.NavigationPosition(actor),point)>.03f)
                        actor.warp(point,Quaternion.Euler(0,yaw,0));
                    actor.transform.rotation=Quaternion.Euler(0,yaw,0);
                    if(!state.bActive)handler.addState(ParallelState.ParallelStateType.DigIn,new MiCharacterTarget(point));
                    if(row[8]=="1"&&state.m_marker!=null)
                    {state.m_marker.transform.position=Point(row,9);state.m_marker.transform.rotation=Quaternion.Euler(0,float.Parse(row[12],CultureInfo.InvariantCulture),0);}
                }
                else if(state.bActive)
                {
                    // The native base removal restores actor visibility but
                    // the carried character needs the explicit paired entry.
                    state.deactivateInvisibleState();handler.removeState(ParallelState.ParallelStateType.DigIn);
                }
                var dig=state.TryCast<ParallelStateDigIn>()?.m_skillDigIn;
                if(dig!=null)
                {
                    if(row[4]=="0"&&dig.m_bDigInActive)dig.onDigOut();
                    else if(row[4]=="1")dig.m_bDigInActive=true;
                }
                if(Hidden(actor)!=scope.Hidden||CarryMismatch(actor,scope.Hidden))
                {if(scope.Hidden)state.activateInvisibleState();else state.deactivateInvisibleState();}
            }
            finally{applying=false;}
        }catch(Exception ex){Error("apply",ex);}
    }
    private void Error(string stage,Exception ex)
    {float now=Time.realtimeSinceStartup;if(now<nextError)return;nextError=now+5;MoveProbePlugin.ProbeLog.LogWarning("Dig state "+stage+": "+ex.GetType().Name+" "+ex.Message);}
    internal static bool AllowChange(ParallelStateHandler handler,ParallelState.ParallelStateType type,bool active)=>
        applying||NativeInput.Probe?.NativeGuest!=true||type!=ParallelState.ParallelStateType.DigIn||handler==null||!handlers.TryGetValue(handler.Pointer,out var scope)||Strike(scope.Actor)||scope.Active==active;
    internal static bool AllowVisibility(ParallelStateDigInBase state,bool hidden)=>
        applying||NativeInput.Probe?.NativeGuest!=true||state==null||!states.TryGetValue(state.Pointer,out var scope)||Strike(scope.Actor)||scope.Hidden==hidden;
    [HarmonyPatch(typeof(ParallelStateHandler),"addState")]
    internal static class AddHook{private static bool Prefix(ParallelStateHandler __instance,ParallelState.ParallelStateType __0)=>AllowChange(__instance,__0,true);}
    [HarmonyPatch(typeof(ParallelStateHandler),"removeState")]
    internal static class RemoveHook{private static bool Prefix(ParallelStateHandler __instance,ParallelState.ParallelStateType __0)=>AllowChange(__instance,__0,false);}
    [HarmonyPatch(typeof(ParallelStateDigInBase),"activateInvisibleState")]
    internal static class HideHook{private static bool Prefix(ParallelStateDigInBase __instance)=>AllowVisibility(__instance,true);}
    [HarmonyPatch(typeof(ParallelStateDigInBase),"deactivateInvisibleState")]
    internal static class RevealHook{private static bool Prefix(ParallelStateDigInBase __instance)=>AllowVisibility(__instance,false);}
}
