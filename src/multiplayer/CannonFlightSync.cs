using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

// Only original cannon-created MiFlyingObjects are enrolled. Every unrelated
// projectile retains its native clock and hit behavior.
internal static class CannonFlightSync
{
    private sealed class Flight
    {
        internal MiFlyingObject Object;
        internal MiCharacter Target;
        internal SkillCommand Command;
        internal MiFlying._fly_d__44 State;
        internal string Owner,Victim,Key;
        internal int Serial;
        internal SkillCannonShoot Skill;
        internal int LandingFlags;
        internal string[] Sample;
        internal bool Finished,Adopted,Cancelled,SkillHit;
    }
    private sealed class Confirmation{internal string[] Row;internal Vector3[] Points;internal bool Pending;}
    private static readonly Dictionary<IntPtr,Flight> flights=new();
    private static readonly Dictionary<string,Confirmation> confirmations=new(StringComparer.Ordinal);
    private static readonly Queue<string> history=new();
    private static readonly Queue<Flight> retired=new();
    private static float nextCapture,nextError;
    private static int cursor,terminalCursor;
    private static readonly FlightCastRecovery recovery=new();
    private static string F(float f)=>f.ToString("G9",CultureInfo.InvariantCulture);
    private static float N(string[] r,int n)=>float.Parse(r[n],CultureInfo.InvariantCulture);
    private static int I(string[] r,int n)=>int.Parse(r[n],CultureInfo.InvariantCulture);
    private static Vector3 P(string[] r,int n)=>new(N(r,n),N(r,n+1),N(r,n+2));
    private static string Key(string owner,int serial)=>owner+":"+serial.ToString(CultureInfo.InvariantCulture);
    private static bool Bind(Flight f)
    {if(f.Key!=null)return true;int serial=InteractionSync.CommandSerial(f.Command);if(serial<=0)return false;f.Serial=serial;f.Key=Key(f.Owner,serial);return true;}
    internal static void Clear(){recovery.Clear();flights.Clear();confirmations.Clear();history.Clear();retired.Clear();cursor=terminalCursor=0;nextCapture=nextError=0;}
    internal static bool Terminal(string owner,int serial)=>NativeInput.Probe?.NativeGuest==true&&serial>0&&
        confirmations.TryGetValue(Key(owner,serial),out var c)&&c.Row[3]!="A";
    private static void Finish(Flight f,bool cancelled)
    {
        if(f.Finished)return;f.Finished=true;f.Cancelled=cancelled;retired.Enqueue(f);
        while(retired.Count>128){var old=retired.Dequeue();if(flights.TryGetValue(old.Object.Pointer,out var current)&&current==old)flights.Remove(old.Object.Pointer);}
    }
    internal static void Observe(SkillCannonShoot skill,MiCharacter victim,MiFlying flying)
    {
        try
        {
            var obj=flying?.TryCast<MiFlyingObject>();if(obj==null||victim==null||skill?.m_skillCommandThrow==null)return;
            string owner=InteractionSync.EntityIdentity(skill.charOrigin),target=InteractionSync.EntityIdentity(victim);
            if(!FreezeProtocol.Character(owner)||!FreezeProtocol.Character(target))return;
            // A prefab component may be reused; its next shot receives a new
            // record, and the command identity prevents adopting an old page.
            var f=new Flight{Object=obj,Target=victim,Command=skill.m_skillCommandThrow,Owner=owner,Victim=target,Skill=skill,
                LandingFlags=(skill.m_bCurrentShootDisposeWater?2:0)|(skill.m_bIsMindControlledAndDropOutOnHit?4:0)|(skill.m_bTargetAlreadyLying?8:0)};
            flights[obj.Pointer]=f;
            CannonStateSync.ObserveShot(skill,victim);
            if(NativeInput.Probe?.NativeGuest==true)Bind(f);
        }
        catch(Exception ex){Error("observe",ex);}
    }
    internal static bool Held(MiCharacter actor)
    {
        if(NativeInput.Probe?.NativeGuest!=true||actor==null)return false;
        foreach(var f in flights.Values)if(!f.Finished&&f.Target.Pointer==actor.Pointer)return true;
        return false;
    }
    internal static bool Settle(MiCharacter actor)
    {
        bool completed=false;
        if(NativeInput.Probe?.NativeGuest!=true||actor==null)return false;
        foreach(var f in new List<Flight>(flights.Values))
        {
            if(f.Finished||f.Target.Pointer!=actor.Pointer||f.State==null||!Bind(f)||
                !confirmations.TryGetValue(f.Key,out var c)||c.Row[2]!=f.Victim||c.Row[3]!="E")continue;
            Correct(f,c,true);f.Object.hitExternal();completed|=f.Finished;
        }
        return completed;
    }
    internal static void Protect(HashSet<string> ids)
    {foreach(var f in flights.Values)if(!f.Finished&&f.Victim.StartsWith("e",StringComparison.Ordinal))ids.Add(f.Victim.Substring(1));}
    private static string[] Sample(Flight f)
    {
        var s=f.State;var o=f.Object;if(s==null)return null;
        bool path=o.m_arAllPositions!=null&&o.m_arAllPositions.Length>=2;
        var points=new List<FishingPullProtocol.Point>();
        if(path)foreach(var p in o.m_arAllPositions)points.Add(new(p.x,p.y,p.z));
        var a=o.m_v3CurrentPos;var b=o.m_v3TargetPos;var start=o.m_v3EntityStartPosition;
        int current=path?Math.Clamp(s._iIdxCurrent_5__6,0,points.Count-1):0,target=path?Math.Clamp(s._iIdxTarget_5__7,0,points.Count-1):0;
        float inverse=s._fTotalFlyDistInv_5__4;
        if(o.m_bInstantHit&&!float.IsFinite(inverse))inverse=0;
        return new[]{f.Owner,InteractionSync.CommandSerial(f.Command).ToString(CultureInfo.InvariantCulture),f.Victim,f.Finished?"E":"A",
            path?"P":"S",o.m_bInverse?"1":"0",F(s._fFlySpeed),F(s._fFlyTime_5__5),F(s._fLerp_5__3),F(s._fLerpTotal_5__2),F(inverse),
            current.ToString(CultureInfo.InvariantCulture),target.ToString(CultureInfo.InvariantCulture),F(s._fDistanceCurr_5__8),F(o.m_fTotalDistanceMultiplier),F(o.m_fTotalFlyDistance),F(o.m_fFlyTime),
            s._acFlySpeedMultiplier_5__9!=null&&s._acFlySpeedMultiplier_5__9.Pointer==o.m_acFlySpeedMultiplierHigh?.Pointer?"H":"L",
            F(a.x),F(a.y),F(a.z),F(b.x),F(b.y),F(b.z),path?FishingPullProtocol.Encode(points):"-",F(start.x),F(start.y),F(start.z),(f.LandingFlags|(o.m_bInstantHit?1:0)).ToString(CultureInfo.InvariantCulture)};
    }
    internal static List<string[]> Capture()
    {
        float now=Time.realtimeSinceStartup;if(now<nextCapture)return null;nextCapture=now+.1f;
        try
        {
            var active=new List<string[]>();var endings=new List<string[]>();
            // Finished records belong to casts, not reusable prefab pointers.
            // Keep them even when Observe replaces the live object mapping.
            var records=new List<Flight>(retired);
            foreach(var f in flights.Values)if(!f.Finished)records.Add(f);
            foreach(var f in records)
            {
                if(!f.Finished&&f.State!=null){f.Sample=Sample(f);if(!f.Object.bIsActive())Finish(f,true);}
                if(f.Sample==null||!Bind(f))continue;
                var row=(string[])f.Sample.Clone();row[1]=f.Serial.ToString(CultureInfo.InvariantCulture);row[3]=f.Cancelled?"C":f.Finished?"E":"A";
                if(!CannonFlightProtocol.Row(row))throw new InvalidOperationException("Invalid native cannon flight");if(f.Finished)endings.Add(row);else active.Add(row);
            }
            var result=new List<string[]>();int bytes=0;
            // Current trajectories must not queue behind retained impact/cancel
            // history. Rotate each class separately to retain fair delivery.
            void Pack(List<string[]> rows,ref int start)
            {
                int taken=0;
                for(int n=0;n<rows.Count&&result.Count<CannonFlightProtocol.MaxFlights;n++)
                {
                    var row=rows[(start+n)%rows.Count];int size=string.Join(" ",row).Length+1;
                    if(size>10000)throw new InvalidOperationException("Cannon trajectory exceeds page budget");
                    if(bytes+size>10000)break;
                    result.Add(row);bytes+=size;taken++;
                }
                if(rows.Count>0)start=(start+taken)%rows.Count;
            }
            Pack(active,ref cursor);Pack(endings,ref terminalCursor);
            return result;
        }
        catch(Exception ex){Error("capture",ex);return null;}
    }
    internal static void Apply(string[][] rows)
    {
        if(rows==null)return;
        var accepted=new List<string[]>();
        foreach(var row in rows)
        {
            if(!CannonFlightProtocol.Row(row))continue;
            string key=Key(row[0],I(row,1));
            if(!confirmations.TryGetValue(key,out var c)){confirmations[key]=c=new();history.Enqueue(key);}
            else if(c.Row[3]!="A"||c.Row[2]!=row[2]||c.Row[4]!=row[4]||
                row[3]=="A"&&N(row,row[4]=="P"?7:8)<N(c.Row,row[4]=="P"?7:8))continue;
            c.Row=(string[])row.Clone();c.Pending=true;c.Points=null;
            accepted.Add(c.Row);
            if(row[4]=="P"){FishingPullProtocol.Points(row[24],out var points);c.Points=Array.ConvertAll(points,p=>new Vector3(p.x,p.y,p.z));}
        }
        int attempts=history.Count;
        while(confirmations.Count>128&&attempts-->0)
        {
            string key=history.Dequeue();bool pinned=false;
            foreach(var f in flights.Values)if(!f.Finished&&f.Key==key){pinned=true;break;}
            if(pinned)history.Enqueue(key);else confirmations.Remove(key);
        }
        // hitExternal can stop the native coroutine before its hit hook.
        // Once the host confirms that hit, finish through that same native
        // entry; a missing coroutine must not strand the target indefinitely.
        foreach(var f in new List<Flight>(flights.Values))try
        {
            if(f.Finished||f.State==null||!Bind(f)||!confirmations.TryGetValue(f.Key,out var c)||c.Row[2]!=f.Victim)continue;
            if(c.Row[3]=="C"){Cancel(f);continue;}
            if(c.Row[3]!="E"||f.Object.bIsActive())continue;
            Correct(f,c,true);f.Object.hitExternal();
        }
        catch(Exception ex){Error("external completion",ex);}
        recovery.Apply(accepted.ToArray(),true,row=>
        {
            string key=Key(row[0],I(row,1));
            foreach(var f in flights.Values)if(Bind(f)&&f.Key==key)return true;
            foreach(var f in retired)if(f.Key==key)return true;
            return false;
        });
    }
    private static void Cancel(Flight f)
    {
        var coroutine=f.Object.m_coroFly;coroutine?.stop(ref coroutine);f.Object.m_coroFly=coroutine;
        f.State.__1__state=-1;Finish(f,true);
    }
    private static void Correct(Flight f,Confirmation c,bool force=false)
    {
        if(!c.Pending&&!force)return;c.Pending=false;
        var s=f.State;var o=f.Object;var r=c.Row;
        bool init=!f.Adopted||s.__1__state==0,ended=r[3]=="E",path=r[4]=="P";
        if(init)
        {
            o.m_arAllPositions=path?new Il2CppStructArray<Vector3>(c.Points):null;o.m_bInverse=r[5]=="1";
            o.m_fTotalDistanceMultiplier=N(r,14);o.m_fTotalFlyDistance=N(r,15);o.m_fFlyTime=N(r,16);o.m_v3EntityStartPosition=P(r,25);o.m_bInstantHit=(I(r,28)&1)!=0;
        }
        bool behind=path?s._fFlyTime_5__5+.08f<N(r,7):s._fLerp_5__3+.08f<N(r,8);
        if(init||behind||ended||force)
        {
            s._fFlySpeed=N(r,6);s._fFlyTime_5__5=N(r,7);s._fLerp_5__3=N(r,8);s._fLerpTotal_5__2=N(r,9);s._fTotalFlyDistInv_5__4=N(r,10);
            s._iIdxCurrent_5__6=I(r,11);s._iIdxTarget_5__7=I(r,12);s._fDistanceCurr_5__8=N(r,13);
            s._acFlySpeedMultiplier_5__9=r[17]=="H"?o.m_acFlySpeedMultiplierHigh:o.m_acFlySpeedMultiplier;
            o.m_v3CurrentPos=P(r,18);o.m_v3TargetPos=P(r,21);s.__1__state=path?1:2;
            if(ended)
            {
                // The host samples just before hit; the last segment can be
                // short of the endpoint. Complete placement before native
                // impact starts the victim's landing/stun animation.
                var end=path?c.Points[r[5]=="1"?0:c.Points.Length-1]:P(r,21);
                o.m_v3CurrentPos=end;o.m_v3TargetPos=end;
            }
            // These original methods own projectile transform and the victim's
            // blend from its starting position. Do not replace with a teleport.
            o.flyLerp(ended||o.m_bInstantHit?1:s._fLerp_5__3);
            o.flyLerpTotal(ended||o.m_bInstantHit?1:path?Math.Clamp(s._fLerpTotal_5__2*s._fTotalFlyDistInv_5__4,0,1):s._fLerp_5__3);
        }
        if(ended)
        {
            // State2 with lerp1 takes the original common virtual hit branch
            // without advancing position again, including an external impact.
            s.__1__state=2;s._fLerp_5__3=1;
        }
        f.Adopted=true;
    }
    internal static bool Before(MiFlying._fly_d__44 state)
    {
        if(state?.__4__this==null||!flights.TryGetValue(state.__4__this.Pointer,out var f)||f.Finished)return true;
        if(f.State!=null&&f.State.Pointer!=state.Pointer)return true;
        f.State=state;
        if(NativeInput.Probe?.NativeGuest!=true)return true;
        try
        {
            if(!Bind(f)||!confirmations.TryGetValue(f.Key,out var c)||c.Row[2]!=f.Victim){state.__2__current=null;return false;}
            if(c.Row[3]=="C"){Cancel(f);return true;}
            Correct(f,c);
            if(c.Row[3]=="E")return true;
            bool path=c.Row[4]=="P";
            bool wait=state.__4__this.m_bInstantHit||WouldFinish(state,path)||
                (path?state._fFlyTime_5__5+Time.deltaTime>N(c.Row,7)+.08f:state._fLerp_5__3+Time.deltaTime*state._fFlySpeed>N(c.Row,8)+.08f);
            if(wait){state.__2__current=null;return false;}return true;
        }
        catch(Exception ex){Error("clock",ex);state.__2__current=null;return false;}
    }
    private static bool WouldFinish(MiFlying._fly_d__44 s,bool path)
    {
        if(!path)return s._fLerp_5__3>=1;
        var o=s.__4__this;if(o.m_arAllPositions==null||o.m_arAllPositions.Length<2)return true;
        var end=o.m_arAllPositions[o.m_bInverse?0:o.m_arAllPositions.Length-1];
        return Vector3.Distance(o.m_v3CurrentPos,end)<.0001f;
    }
    internal static void After(MiFlying._fly_d__44 state,bool result)
    {
        if(state?.__4__this==null||!flights.TryGetValue(state.__4__this.Pointer,out var f)||f.State?.Pointer!=state.Pointer||f.Finished)return;
        if(!result)
        {
            if(NativeInput.Probe?.NativeGuest!=true)try{f.Sample=Sample(f);}catch(Exception ex){Error("sample",ex);}
            Finish(f,true); // An admitted hit has already finished in HitHook.
        }
    }
    internal static bool BeforeHit(MiFlyingObject obj)
    {
        if(obj==null||!flights.TryGetValue(obj.Pointer,out var f))return true;
        if(f.Finished)return false;
        if(NativeInput.Probe?.NativeGuest==true)
        {
            if(!Bind(f)||!confirmations.TryGetValue(f.Key,out var c)||c.Row[2]!=f.Victim||c.Row[3]!="E")return false;
            // onHit selects wakeup/disposal from these per-shot facts. They
            // cannot be inferred from the guest's temporary carried pose.
            int flags=I(c.Row,28);
            f.Skill.m_bCurrentShootDisposeWater=(flags&2)!=0;
            f.Skill.m_bIsMindControlledAndDropOutOnHit=(flags&4)!=0;
            f.Skill.m_bTargetAlreadyLying=(flags&8)!=0;
            return true;
        }
        try
        {
            f.LandingFlags=(f.Skill.m_bCurrentShootDisposeWater?2:0)|(f.Skill.m_bIsMindControlledAndDropOutOnHit?4:0)|(f.Skill.m_bTargetAlreadyLying?8:0);
            f.Sample=Sample(f);
        }catch(Exception ex){Error("hit sample",ex);}return true;
    }
    internal static void AfterHit(MiFlyingObject obj,bool admitted)
    {
        if(!admitted||obj==null||!flights.TryGetValue(obj.Pointer,out var f))return;
        if(NativeInput.Probe?.NativeGuest==true&&!f.SkillHit&&f.Command?.m_target!=null)
        {
            // A native projectile can finish after its skill callback has
            // been detached. The confirmed impact still owes the original
            // cannon callback (including wakeup), exactly once.
            try{f.Skill.onHit(obj,f.Command.m_target);MoveProbePlugin.ProbeLog.LogInfo("Cannon impact callback recovered target="+f.Victim);}
            catch(Exception ex){Error("impact callback",ex);}
        }
        Finish(f,false);
    }
    internal static bool BeforeSkillHit(MiFlying obj)
    {
        if(NativeInput.Probe?.NativeGuest!=true||obj==null||!flights.TryGetValue(obj.Pointer,out var f))return true;
        if(f.SkillHit||!Bind(f)||!confirmations.TryGetValue(f.Key,out var c)||c.Row[2]!=f.Victim||c.Row[3]!="E")return false;
        f.SkillHit=true;
        MoveProbePlugin.ProbeLog.LogInfo("Cannon native impact target="+f.Victim+" flags="+c.Row[28]);
        return true;
    }
    private static void Error(string where,Exception ex)
    {float now=Time.realtimeSinceStartup;if(now<nextError)return;nextError=now+5;MoveProbePlugin.ProbeLog.LogWarning("Cannon flight "+where+": "+ex.GetType().Name+" "+ex.Message);}
    [HarmonyPatch(typeof(SkillCannonShoot),"executeGetFlying")]
    internal static class CreateHook{private static void Postfix(SkillCannonShoot __instance,MiCharacter __0,MiFlying __result)=>Observe(__instance,__0,__result);}
    [HarmonyPatch(typeof(MiFlying._fly_d__44),"MoveNext")]
    internal static class ClockHook
    {
        private static bool Prefix(MiFlying._fly_d__44 __instance,ref bool __result){if(Before(__instance))return true;__result=true;return false;}
        private static void Postfix(MiFlying._fly_d__44 __instance,bool __result)=>After(__instance,__result);
    }
    [HarmonyPatch(typeof(MiFlyingObject),"hit")]
    internal static class HitHook
    {
        private static bool Prefix(MiFlyingObject __instance,out bool __state){__state=BeforeHit(__instance);return __state;}
        private static void Postfix(MiFlyingObject __instance,bool __state)=>AfterHit(__instance,__state);
    }
    [HarmonyPatch(typeof(SkillCannonShoot),"onHit")]
    internal static class SkillHitHook
    {private static bool Prefix(MiFlying __0)=>BeforeSkillHit(__0);}
}
