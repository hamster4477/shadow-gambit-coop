using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

// The duel process has no countdown: native MoveNext holds while both actors
// are alive, then releases the target and plays the origin's ending animation.
internal sealed class FrozenDuelSync
{
    private sealed class Stamp{internal IntPtr Pointer;internal int Handle,Generation;internal float Seen;}
    private sealed class Sample{internal MiCharacterProcessOptionsFrozenDuel Options;internal int Phase;internal float At;}
    private sealed class Replica{internal MiCharacter Owner,Target,Origin;internal MiCharacterProcessOptionsFrozenDuel Options;internal int Handle,Generation;}
    private readonly Dictionary<string,Stamp> stamps=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Replica> replicas=new(StringComparer.Ordinal);
    private readonly HashSet<string> finished=new(StringComparer.Ordinal);
    private static readonly Dictionary<IntPtr,Sample> observed=new();
    private static readonly Dictionary<IntPtr,int> phases=new();
    private static readonly HashSet<IntPtr> tracked=new(),protectedActors=new();
    private static bool authoritative;
    [ThreadStatic] private static bool applying;
    private float nextCapture,nextError;
    internal bool ReadyForOutcome(string id)=>finished.Contains(id);
    internal void OutcomeApplied(string id)=>finished.Remove(id);
    internal static bool Protected(MiCharacter actor)=>NativeInput.Probe?.NativeGuest==true&&actor!=null&&protectedActors.Contains(actor.Pointer);
    internal void Clear()
    {
        foreach(var r in replicas.Values)try{r.Owner?.processes?.cancel(r.Handle,null);}catch(Exception ex){Error("clear",ex);}
        replicas.Clear();stamps.Clear();finished.Clear();observed.Clear();phases.Clear();tracked.Clear();protectedActors.Clear();authoritative=false;nextCapture=nextError=0;
    }
    private static List<MiCharacter> Actors(IEnumerable<MiCharacter> crew,IEnumerable<MiCharacter> guards)
    {
        var result=new List<MiCharacter>();var seen=new HashSet<IntPtr>();void Add(MiCharacter a){if(a!=null&&seen.Add(a.Pointer))result.Add(a);}
        if(crew!=null)foreach(var a in crew)Add(a);if(guards!=null)foreach(var a in guards)Add(a);
        foreach(var e in SkillObjectRegistry.Active.Entities)if(e!=null&&e.bAsCharacter(out var a))Add(a);return result;
    }
    private static List<MiCharacterProcessHandler.MiCharacterProcessExecution> Executions(MiCharacter actor)
    {
        var result=new List<MiCharacterProcessHandler.MiCharacterProcessExecution>();var list=actor?.processes?.m_lCharacterProcessExecutions;
        if(list!=null)for(int i=0;i<list.Count;i++)if(list[i]?.m_options?.TryCast<MiCharacterProcessOptionsFrozenDuel>()!=null)result.Add(list[i]);return result;
    }
    internal List<string[]> Capture(IEnumerable<MiCharacter> crew,IEnumerable<MiCharacter> guards)
    {
        float now=Time.realtimeSinceStartup;if(now<nextCapture)return null;nextCapture=now+.1f;
        try
        {
            var rows=new List<string[]>();var live=new HashSet<IntPtr>();var targets=new HashSet<string>();bool waiting=false;
            foreach(var owner in Actors(crew,guards))foreach(var execution in Executions(owner))
            {
                var o=execution.m_options.TryCast<MiCharacterProcessOptionsFrozenDuel>();string key=InteractionSync.EntityIdentity(o.m_charTarget);
                if(!targets.Add(key))throw new InvalidOperationException("Ambiguous duel target");
                if(!stamps.TryGetValue(key,out var stamp))stamps[key]=stamp=new();
                if(stamp.Pointer!=o.Pointer||stamp.Handle!=execution.m_iHandleID){stamp.Pointer=o.Pointer;stamp.Handle=execution.m_iHandleID;stamp.Generation++;stamp.Seen=now;}
                live.Add(o.Pointer);tracked.Add(o.Pointer);
                if(!observed.TryGetValue(o.Pointer,out var sample)||sample.At<stamp.Seen){waiting=true;continue;}
                if(sample.Phase!=1&&sample.Phase!=2)continue;
                var row=new[]{key,stamp.Generation.ToString(CultureInfo.InvariantCulture),InteractionSync.EntityIdentity(o.m_charOrigin),((int)o.m_eSkillTypeOrigin).ToString(CultureInfo.InvariantCulture),InteractionSync.EntityIdentity(owner),sample.Phase.ToString(CultureInfo.InvariantCulture)};
                if(!DuelProtocol.Row(row))throw new InvalidOperationException("Invalid native duel descriptor");rows.Add(row);
            }
            foreach(var pointer in new List<IntPtr>(tracked))if(!live.Contains(pointer)&&!phases.ContainsKey(pointer)){tracked.Remove(pointer);observed.Remove(pointer);}
            if(waiting)return null;if(rows.Count>DuelProtocol.MaxEffects)throw new InvalidOperationException("Duel manifest exceeds bound");return rows;
        }catch(Exception ex){Error("capture",ex);return null;}
    }
    private static void Release(MiCharacterProcessOptionsFrozenDuel options)
    {if(options!=null){phases.Remove(options.Pointer);tracked.Remove(options.Pointer);observed.Remove(options.Pointer);}}
    private static void Stop(MiCharacter owner,MiCharacterProcessHandler.MiCharacterProcessExecution execution)
    {owner.processes.cancel(execution.m_iHandleID,null);Release(execution.m_options.TryCast<MiCharacterProcessOptionsFrozenDuel>());}
    private void Forget(string key){if(replicas.Remove(key,out var r))Release(r.Options);}
    internal void Apply(string[][] rows,IEnumerable<MiCharacter> crew,IEnumerable<MiCharacter> guards)
    {
        if(rows==null)return;authoritative=true;var wanted=new Dictionary<string,string[]>(StringComparer.Ordinal);foreach(var row in rows)wanted[row[0]]=row;
        var actors=Actors(crew,guards);foreach(var r in replicas.Values)if(r.Owner!=null&&!actors.Contains(r.Owner))actors.Add(r.Owner);
        foreach(var owner in actors)foreach(var execution in Executions(owner))try
        {
            string key=InteractionSync.EntityIdentity(execution.m_options.TryCast<MiCharacterProcessOptionsFrozenDuel>().m_charTarget);
            if(!wanted.TryGetValue(key,out var row)||row[4]!=InteractionSync.EntityIdentity(owner)){Stop(owner,execution);finished.Add(key);}
        }catch(Exception ex){Error("end",ex);}
        foreach(var key in new List<string>(replicas.Keys))if(!wanted.ContainsKey(key)){finished.Add(key);Forget(key);}
        foreach(var row in rows)try
        {
            string key=row[0];if(row[5]=="2")finished.Add(key);else finished.Remove(key);
            var target=InteractionSync.Resolve(key)?.TryCast<MiCharacter>();var origin=InteractionSync.Resolve(row[2])?.TryCast<MiCharacter>();var owner=InteractionSync.Resolve(row[4])?.TryCast<MiCharacter>();
            if(target==null||origin==null||owner==null)continue;
            int generation=int.Parse(row[1],CultureInfo.InvariantCulture),skill=int.Parse(row[3],CultureInfo.InvariantCulture);replicas.TryGetValue(key,out var previous);
            MiCharacterProcessHandler.MiCharacterProcessExecution existing=null;
            foreach(var execution in Executions(owner))
            {
                var o=execution.m_options.TryCast<MiCharacterProcessOptionsFrozenDuel>();if(InteractionSync.EntityIdentity(o.m_charTarget)!=key)continue;
                bool match=(previous==null||previous.Generation==generation)&&InteractionSync.EntityIdentity(o.m_charOrigin)==row[2]&&(int)o.m_eSkillTypeOrigin==skill;
                if(existing==null&&match)existing=execution;else Stop(owner,execution);
            }
            if(existing==null)
            {
                Forget(key);if(row[5]!="1")continue; // Already-ended duel: never start a fresh confrontation.
                var options=new MiCharacterProcessOptionsFrozenDuel(origin,target,(Skill.SkillType)skill);phases[options.Pointer]=1;tracked.Add(options.Pointer);
                bool started=false;applying=true;
                try{started=owner.processes.bStart(options,0);}catch{Release(options);throw;}finally{applying=false;}
                if(!started){Release(options);continue;}
                foreach(var execution in Executions(owner))if(execution.m_options.Pointer==options.Pointer){existing=execution;break;}
                if(existing==null){Release(options);continue;}
            }
            var active=existing.m_options.TryCast<MiCharacterProcessOptionsFrozenDuel>();if(previous!=null&&previous.Options.Pointer!=active.Pointer)Release(previous.Options);
            phases[active.Pointer]=int.Parse(row[5],CultureInfo.InvariantCulture);tracked.Add(active.Pointer);
            replicas[key]=new(){Owner=owner,Target=target,Origin=origin,Options=active,Handle=existing.m_iHandleID,Generation=generation};
        }catch(Exception ex){Error("apply",ex);}
        protectedActors.Clear();foreach(var r in replicas.Values)if(phases.TryGetValue(r.Options.Pointer,out var phase)&&phase==1)
        {if(r.Target!=null)protectedActors.Add(r.Target.Pointer);if(r.Origin!=null)protectedActors.Add(r.Origin.Pointer);}
    }
    private void Error(string stage,Exception ex)
    {float now=Time.realtimeSinceStartup;if(now<nextError)return;nextError=now+5;MoveProbePlugin.ProbeLog.LogWarning("FrozenDuel "+stage+": "+ex.GetType().Name+" "+ex.Message);}
    internal static bool AllowStart(MiCharacterProcessOptions options)=>!authoritative||applying||NativeInput.Probe?.NativeGuest!=true||options?.TryCast<MiCharacterProcessOptionsFrozenDuel>()==null;
    internal static bool Before(MiCharacterProcessFrozenDuel._coroRunProcess_d__10 state,ref bool result)
    {
        var o=state?._options?.TryCast<MiCharacterProcessOptionsFrozenDuel>();
        if(NativeInput.Probe?.NativeGuest!=true||o==null||state.__1__state!=1||!phases.TryGetValue(o.Pointer,out int phase)||phase!=1)return true;
        // Native state1 only checks actor health and yields null. Preserve that
        // yield while the host still reports a duel; never skip native state0.
        state.__2__current=null;result=true;return false;
    }
    internal static void After(MiCharacterProcessFrozenDuel._coroRunProcess_d__10 state)
    {
        var o=state?._options?.TryCast<MiCharacterProcessOptionsFrozenDuel>();if(o==null||!tracked.Contains(o.Pointer))return;
        observed[o.Pointer]=new(){Options=o,Phase=state.__1__state,At=Time.realtimeSinceStartup};
    }
    [HarmonyPatch(typeof(MiCharacterProcessFrozenDuel._coroRunProcess_d__10),"MoveNext")]
    internal static class PhaseHook
    {private static bool Prefix(MiCharacterProcessFrozenDuel._coroRunProcess_d__10 __instance,ref bool __result)=>Before(__instance,ref __result);private static void Postfix(MiCharacterProcessFrozenDuel._coroRunProcess_d__10 __instance)=>After(__instance);}
    [HarmonyPatch(typeof(MiCharacterProcessHandler),"bStart",new Type[]{typeof(MiCharacterProcessOptions),typeof(int)})]
    internal static class StartHook
    {private static bool Prefix(MiCharacterProcessOptions __0,ref bool __result){if(AllowStart(__0))return true;__result=false;return false;}}
}
