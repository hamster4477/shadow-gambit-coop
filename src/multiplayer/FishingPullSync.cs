using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

// Native character fishing uses MiFlyingProcess, not MiFlying. Adopt the
// flight yielded by the original fishing coroutine; never replay its locks,
// landing, hiding or tie-up callbacks merely to correct its position.
internal sealed class FishingPullSync
{
    private sealed class Flight
    {
        internal SkillFishing Skill;
        internal SkillCommand Command;
        internal MiFlyingProcess._fly_d__0 State;
        internal SkillFishing._coroReelIn_d__113 Parent;
        internal int Generation,Serial;
        internal string Owner,Target,HostKey;
        internal bool Finished,Sampled,Adopted,Cancelled,ChildDone,ParentDone;
        internal string[] FinalSample;
    }
    private sealed class Confirmation
    {
        internal string[] Row;
        internal Vector3[] Points;
        internal bool Pending;
    }
    private static readonly Dictionary<IntPtr,Flight> flights=new();
    private static readonly Dictionary<string,Confirmation> confirmations=new(StringComparer.Ordinal);
    private const int HistoryLimit=128;
    private static readonly HashSet<string> completed=new(StringComparer.Ordinal);
    private static readonly Queue<string> completedHistory=new(),confirmationHistory=new();
    private static readonly Queue<Flight> retired=new();
    private static int generation;
    private static float nextError;
    private float nextCapture;
    private readonly FlightCastRecovery recovery=new();
    private int cursor,terminalCursor;
    private static string F(float n)=>n.ToString("G9",CultureInfo.InvariantCulture);
    private static float N(string[] r,int at)=>float.Parse(r[at],CultureInfo.InvariantCulture);
    private static int I(string[] r,int at)=>int.Parse(r[at],CultureInfo.InvariantCulture);
    private static Vector3 P(string[] r,int at)=>new(N(r,at),N(r,at+1),N(r,at+2));
    private static string Key(string[] r)=>r[0]+":"+r[1];
    private static int Serial(Flight f)=>f.Serial>0?f.Serial:f.Serial=InteractionSync.CommandSerial(f.Command);
    internal void Clear(){recovery.Clear();flights.Clear();confirmations.Clear();completed.Clear();completedHistory.Clear();confirmationHistory.Clear();retired.Clear();generation=cursor=terminalCursor=0;nextCapture=nextError=0;}
    internal static bool Held(MiCharacter actor)
    {
        if(NativeInput.Probe?.NativeGuest!=true||actor==null)return false;
        foreach(var f in flights.Values)if(!f.Finished&&f.State._options?.entity?.Pointer==actor.Pointer)return true;
        return false;
    }
    internal void Protect(HashSet<string> ids)
    {foreach(var f in flights.Values)if(!f.Finished&&f.Target?.StartsWith("e",StringComparison.Ordinal)==true)ids.Add(f.Target.Substring(1));}
    internal static void Observe(SkillFishing skill,Il2CppSystem.Object yielded,SkillCommand command,SkillFishing._coroReelIn_d__113 parent=null)
    {
        try
        {
            var state=yielded?.TryCast<MiFlyingProcess._fly_d__0>();
            if(skill==null||state?._options?.entity==null||flights.TryGetValue(state.Pointer,out var existing)&&!existing.Finished)return;
            string owner=InteractionSync.EntityIdentity(skill.charOrigin),target=InteractionSync.EntityIdentity(state._options.entity);
            if(!FreezeProtocol.Character(owner)||!FreezeProtocol.Character(target))return;
            var f=new Flight{Skill=skill,Command=command,State=state,Parent=parent,Generation=++generation,Owner=owner,Target=target};
            flights[state.Pointer]=f;
            if(NativeInput.Probe?.NativeGuest==true){Bind(f);if(f.HostKey!=null&&confirmations[f.HostKey].Row[3]=="C")Cancel(f);}
        }
        catch(Exception ex){Error("observe",ex);}
    }
    private static void Bind(Flight flight)
    {
        if(flight.HostKey!=null)return;
        int serial=Serial(flight);if(serial<=0)return;
        string best=null;int number=int.MaxValue;
        foreach(var pair in confirmations)
        {
            var r=pair.Value.Row;if(r[0]!=flight.Owner||r[2]!=flight.Target||I(r,21)!=serial)continue;
            bool claimed=false;foreach(var f in flights.Values)if(f.HostKey==pair.Key){claimed=true;break;}
            int seq=I(r,1);if(completed.Contains(pair.Key))continue;
            if(!claimed&&seq<number){number=seq;best=pair.Key;}
        }
        if(best!=null){flight.HostKey=best;confirmations[best].Pending=true;}
    }
    internal void Apply(string[][] rows)
    {
        // These are bounded pages of active casts and retained endings. Omission is
        // unknown. An explicit E record lets the original landing run once.
        if(rows==null)return;
        var accepted=new List<string[]>();
        foreach(var row in rows)
        {
            if(!FishingPullProtocol.Row(row))continue;
            string key=Key(row);
            if(completed.Contains(key))continue;
            if(confirmations.TryGetValue(key,out var existing))
            {
                // A cast cannot change target/command or be reopened by a
                // delayed active packet after its terminal record arrived.
                if(existing.Row[2]!=row[2]||existing.Row[21]!=row[21]||existing.Row[3]!="A"||
                    row[3]=="A"&&N(row,6)<N(existing.Row,6))continue;
            }
            if(!confirmations.TryGetValue(key,out var c)){confirmations[key]=c=new();confirmationHistory.Enqueue(key);}
            c.Row=(string[])row.Clone();c.Pending=true;
            accepted.Add(c.Row);
            if(row[20]=="-")
            {c.Points=new Vector3[I(row,12)];Array.Fill(c.Points,P(row,13));}
            else
            {FishingPullProtocol.Points(row[20],out var points);c.Points=Array.ConvertAll(points,p=>new Vector3(p.x,p.y,p.z));}
            foreach(var f in flights.Values)if(!f.Finished&&f.HostKey==null&&f.Owner==row[0]&&f.Target==row[2]){Bind(f);break;}
            if(row[3]=="C")foreach(var f in new List<Flight>(flights.Values))if(!f.Finished&&f.HostKey==key)Cancel(f);
        }
        // Keep pages used by a live local coroutine. Evict only unbound or
        // completed history so another caster cannot strand an active pull.
        int attempts=confirmationHistory.Count;
        while(confirmations.Count>HistoryLimit&&attempts-->0)
        {
            string key=confirmationHistory.Dequeue();bool pinned=false;
            foreach(var f in flights.Values)if(!f.Finished&&f.HostKey==key){pinned=true;break;}
            if(pinned)confirmationHistory.Enqueue(key);else confirmations.Remove(key);
        }
        recovery.Apply(accepted.ToArray(),false,row=>
        {
            string key=Key(row);if(completed.Contains(key))return true;
            foreach(var f in flights.Values)if(f.HostKey==key)return true;
            return false;
        });
    }
    internal List<string[]> Capture()
    {
        float now=Time.realtimeSinceStartup;if(now<nextCapture)return null;nextCapture=now+.1f;
        try
        {
            var active=new List<string[]>();var endings=new List<string[]>();
            var records=new List<Flight>(retired);
            foreach(var f in flights.Values)if(!f.Finished)records.Add(f);
            foreach(var f in records)
            {
                if(!f.Sampled)continue;
                // The child has landed but the parent still owns locks and
                // outcome callbacks. Omission is unknown; do not publish E yet.
                if(f.ChildDone&&!f.Finished)continue;
                int serial=Serial(f);if(serial<=0){Error("identity",new InvalidOperationException("Fishing coroutine has no published command identity"));continue;}
                var row=f.FinalSample!=null?(string[])f.FinalSample.Clone():Sample(f);if(row==null)continue;
                row[21]=serial.ToString(CultureInfo.InvariantCulture);
                if(!FishingPullProtocol.Row(row))throw new InvalidOperationException("Invalid native fishing trajectory");
                if(f.Finished)
                {
                    var compact=(string[])row.Clone();compact[20]="-";
                    if(FishingPullProtocol.Row(compact))row=compact;
                    f.FinalSample=(string[])row.Clone();endings.Add(row);
                }
                else active.Add(row);
            }
            // Current movement takes priority over completed history. Compact
            // endings use the remaining budget; they cannot delay live motion.
            var result=new List<string[]>();int bytes=0;
            void Pack(List<string[]> rows,ref int start)
            {
                int taken=0;
                for(int n=0;n<rows.Count&&result.Count<FishingPullProtocol.MaxFlights;n++)
                {var row=rows[(start+n)%rows.Count];int size=string.Join(" ",row).Length+1;if(size>12000)throw new InvalidOperationException("Fishing trajectory exceeds page budget");if(bytes+size>12000)break;result.Add(row);bytes+=size;taken++;}
                if(rows.Count>0)start=(start+taken)%rows.Count;
            }
            Pack(active,ref cursor);Pack(endings,ref terminalCursor);
            return result;
        }catch(Exception ex){Error("capture",ex);return null;}
    }
    private static string[] Sample(Flight f)
    {
        var s=f.State;var o=s._options;if(o?.m_arPositions==null)return null;
        if(f.Cancelled)
        {
            var p=o.entity.transform.position;
            return new[]{f.Owner,f.Generation.ToString(CultureInfo.InvariantCulture),f.Target,"C","0","1","0","0","0","0","0","1","2",
                F(p.x),F(p.y),F(p.z),F(p.x),F(p.y),F(p.z),"C","-",Serial(f).ToString(CultureInfo.InvariantCulture)};
        }
        var points=new List<FishingPullProtocol.Point>();for(int i=0;i<o.m_iLerpPositionCount;i++){var p=o.m_arPositions[i];points.Add(new(p.x,p.y,p.z));}
        var a=s._v3CurrentPos_5__7;var b=s._v3TargetPos_5__8;
        return new[]{f.Owner,f.Generation.ToString(CultureInfo.InvariantCulture),f.Target,f.Finished?"E":"A",o.m_bInverse?"1":"0",
            F(o.m_fDuration),F(s._fCurrentDuration_5__4),F(s._fSectionsPerSecond_5__2),F(s._fSectionPercentage_5__3),
            s._iIdxCurrent_5__5.ToString(CultureInfo.InvariantCulture),s._iIdxTarget_5__6.ToString(CultureInfo.InvariantCulture),
            F(o.m_fCurveCompensationMultiplier),o.m_iLerpPositionCount.ToString(CultureInfo.InvariantCulture),
            F(a.x),F(a.y),F(a.z),F(b.x),F(b.y),F(b.z),"C",FishingPullProtocol.Encode(points),Serial(f).ToString(CultureInfo.InvariantCulture)};
    }
    private static void Correct(Flight f,Confirmation c)
    {
        var s=f.State;var o=s._options;var r=c.Row;
        if(!c.Pending)return;c.Pending=false;
        bool initialize=s.__1__state==0;
        // Correct route geometry once when adopting a host cast. Later samples
        // advance a lagging clock, but never rewind a flight already ahead.
        bool newRoute=initialize||o.m_arPositions.Length!=c.Points.Length;
        if(newRoute||!f.Adopted)
        {
            o.m_arPositions=new Il2CppStructArray<Vector3>(c.Points);o.m_bInverse=r[4]=="1";
            o.m_iLerpPositionCount=I(r,12);o.m_fDuration=N(r,5);o.m_fCurveCompensationMultiplier=N(r,11);
        }
        if(initialize||!f.Adopted||s._fCurrentDuration_5__4+.12f<N(r,6)||r[3]=="E")
        {
            float before=initialize?0:s._fCurrentDuration_5__4,after=N(r,6);
            s._fSectionsPerSecond_5__2=N(r,7);s._fSectionPercentage_5__3=N(r,8);s._fCurrentDuration_5__4=after;
            s._iIdxCurrent_5__5=I(r,9);s._iIdxTarget_5__6=I(r,10);
            s._v3CurrentPos_5__7=P(r,13);s._v3TargetPos_5__8=P(r,16);
            // State0 initializes only these scalar/path caches. Skill-specific
            // locks and animation were already applied by the parent coroutine.
            s.__1__state=1;
            if(after>before)o.onLerp?.Invoke(before,after,o);
            o.entity.setPosition(Vector3.Lerp(s._v3CurrentPos_5__7,s._v3TargetPos_5__8,s._fSectionPercentage_5__3));
        }
        f.Adopted=true;
    }
    internal static bool Before(MiFlyingProcess._fly_d__0 state)
    {
        if(NativeInput.Probe?.NativeGuest!=true||state==null||!flights.TryGetValue(state.Pointer,out var f)||f.Finished||f.ChildDone)return true;
        if(f.HostKey==null)Bind(f);
        try
        {
            if(f.HostKey==null||!confirmations.TryGetValue(f.HostKey,out var c))
            {
                // Even before the first trajectory page, do not let a fast
                // guest finish (or run far ahead of) an unconfirmed pull.
                if(WouldFinish(state)||state._fCurrentDuration_5__4+Time.deltaTime>.12f)
                {state.__2__current=null;return false;}
                return true;
            }
            Correct(f,c);
            if(c.Row[3]=="E")return true;
            if(state.__1__state!=1)return true;
            if(WouldFinish(state)||state._fCurrentDuration_5__4+Time.deltaTime>N(c.Row,6)+.12f)
            {state.__2__current=null;return false;}
            return true;
        }
        catch(Exception ex){Error("clock",ex);state.__2__current=null;return false;}
    }
    private static bool WouldFinish(MiFlyingProcess._fly_d__0 s)
    {
        var o=s._options;bool first=s.__1__state==0;float dt=Time.deltaTime;
        float speed=first?o.m_iLerpPositionCount/o.m_fDuration:s._fSectionsPerSecond_5__2;
        float elapsed=first?0:s._fCurrentDuration_5__4;
        float section=first?0:s._iIdxCurrent_5__5+s._fSectionPercentage_5__3;
        return section+dt*speed*o.m_fCurveCompensationMultiplier*o.getSpeedMultiplier(elapsed+dt)>=o.m_iLerpPositionCount-1;
    }
    internal static bool CancelledBeforeStart(SkillFishing skill,SkillCommand command,MiCharacter target)
    {
        if(NativeInput.Probe?.NativeGuest!=true||command==null)return false;
        int serial=InteractionSync.CommandSerial(command);
        string owner=InteractionSync.EntityIdentity(skill?.charOrigin),id=InteractionSync.EntityIdentity(target);
        // A late parent must not pull the target back after authoritative
        // completion either. Already-running parents finish via their normal
        // child/parent gates; this check runs only before native state zero.
        foreach(var c in confirmations.Values)if(c.Row[3]!="A"&&c.Row[0]==owner&&c.Row[2]==id&&I(c.Row,21)==serial)return true;
        return false;
    }
    internal static void OnStopped(SkillCommand command)
    {
        if(command==null||NativeInput.Probe?.NetworkSessionActive!=true)return;
        foreach(var f in new List<Flight>(flights.Values))if(!f.Finished&&f.Command?.Pointer==command.Pointer)Cancel(f);
    }
    private static void Cancel(Flight f)
    {
        if(f.Finished||f.Cancelled)return;
        f.Cancelled=true;f.FinalSample=null;
        // Never resume the native landing branch: it can hide or tie up the
        // target. Retire both exact iterators, leaving a newer cast untouched.
        f.State.__1__state=-1;f.State.__2__current=null;
        if(f.Parent!=null){f.Parent.__1__state=-1;f.Parent.__2__current=null;}
        After(f.State,false);
        void Restore(string step,Action action){try{action();}catch(Exception ex){Error("cancel "+step,ex);}}
        var target=f.State._options?.entity?.TryCast<MiCharacter>();
        if(target!=null)
        {
            // Character parent resume181E4AF78 clears precisely this native
            // highlighter reason before releasing navigation/movement locks.
            Restore("highlight",()=>
            {
                if(!target.bNoHLGroup)target.highlighterGroup?.disableHighlighterDisableReason(
                    Mimimi.HighlighterSystem.HighlighterDisableReason.AnimationEvent);
            });
            Restore("tag",()=>target.tags?.removeTag(new MiEnumValue<MiCharacterTagEnum>(219)));
            Restore("navigation",()=>target.navigation?.setNavMovementActive(true,MiCharNavigation.NavMeshMovementDisableReason.Skill,true));
            Restore("height",()=>target.visOffset?.setEnabled(MiCharacterVisOffset.VisOffsetDisableReason.Internal));
            Restore("movement",()=>target.movement?.setStopped(false,MiMovement.StopMovementReason.SkillTarget,false));
            Restore("posture",()=>
            {
                if((int)target.eCharacterType!=2)target.movementType?.setMovementModifier(MiCharacterMovementType.MovementModifier.None,
                    new MiCharacterMovementType.SetMovementTypeOptions{eLayer=MiCharacterMovementType.MovementTypeLayer.Default,bBlendIn=true,bUpdateLocomotion=true,bSendEvents=true});
            });
        }
        // The observed child exists only after the parent acquired Fly. This
        // decrements the skill's retained target reference; execute it once.
        Restore("occupation",()=>f.Skill.postReelIn(f.Command));
    }
    internal static void After(MiFlyingProcess._fly_d__0 state,bool result)
    {
        if(state==null||!flights.TryGetValue(state.Pointer,out var f)||f.Finished)return;
        f.Sampled=true;if(result)return;
        // Cache the child's endpoint while options are still valid, but retain
        // protection until the original parent finishes landing/health cleanup.
        if(!f.ChildDone&&!f.Cancelled)
        {
            f.ChildDone=true;
            if(NativeInput.Probe?.NativeGuest!=true)try{f.FinalSample=Sample(f);if(f.FinalSample!=null)f.FinalSample[3]="E";}catch(Exception ex){Error("terminal sample",ex);}
        }
        if(f.Parent!=null&&!f.ParentDone&&!f.Cancelled)return;
        f.Finished=true;
        if(f.Cancelled&&NativeInput.Probe?.NativeGuest!=true)try{f.FinalSample=Sample(f);}catch(Exception ex){Error("cancel sample",ex);}
        retired.Enqueue(f);
        while(retired.Count>HistoryLimit)
        {
            var old=retired.Dequeue();
            if(flights.TryGetValue(old.State.Pointer,out var current)&&current==old)flights.Remove(old.State.Pointer);
        }
        if(NativeInput.Probe?.NativeGuest==true&&f.HostKey!=null&&confirmations.TryGetValue(f.HostKey,out var c))
        {
            if(completed.Add(f.HostKey))completedHistory.Enqueue(f.HostKey);
            while(completedHistory.Count>HistoryLimit)completed.Remove(completedHistory.Dequeue());
        }
    }
    internal static void AfterParent(SkillFishing._coroReelIn_d__113 parent,bool running)
    {
        if(parent==null||running)return;
        foreach(var f in new List<Flight>(flights.Values))
        {
            if(f.Finished||f.Parent?.Pointer!=parent.Pointer)continue;
            f.ParentDone=true;
            After(f.State,false);
        }
    }
    private static void Error(string stage,Exception ex)
    {float now=Time.realtimeSinceStartup;if(now<nextError)return;nextError=now+5;MoveProbePlugin.ProbeLog.LogWarning("Fishing pull "+stage+": "+ex.GetType().Name+" "+ex.Message);}
    [HarmonyPatch(typeof(SkillFishing._coroReelIn_d__113),"MoveNext")]
    internal static class CharacterHook
    {
        private static bool Prefix(SkillFishing._coroReelIn_d__113 __instance,ref bool __result)
        {if(__instance.__1__state!=0||!CancelledBeforeStart(__instance.__4__this,__instance._skillCommandReelIn,__instance._character))return true;__instance.__1__state=-1;__instance.__2__current=null;FishingItemSync.ReleaseUnstarted(__instance._skillCommandReelIn);__result=false;return false;}
        private static void Postfix(SkillFishing._coroReelIn_d__113 __instance,bool __result){if(__result)Observe(__instance.__4__this,__instance.__2__current,__instance._skillCommandReelIn,__instance);else AfterParent(__instance,false);}
    }
    // Dedicated native MoveNext at0x181BA0260; shared coroutine stubs and
    // arbitrary non-fishing MiFlyingProcess users are left untouched.
    [HarmonyPatch(typeof(MiFlyingProcess._fly_d__0),"MoveNext")]
    internal static class ClockHook
    {
        private static bool Prefix(MiFlyingProcess._fly_d__0 __instance,ref bool __result)
        {if(Before(__instance))return true;__result=true;return false;}
        private static void Postfix(MiFlyingProcess._fly_d__0 __instance,bool __result)=>After(__instance,__result);
    }
}
