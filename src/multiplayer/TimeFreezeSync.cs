using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Mimimi.Animation;
using UnityEngine;

// Persistent native TimeFreeze processes, independent of short skill cues.
// Do not patch the empty base onUpdateTimer/onStop methods: IL2CPP shares their
// address with unrelated empty methods. EffectProcessClock observes the
// native coroutine; updateTimer itself must remain unpatched (ref float ABI).
internal sealed class TimeFreezeSync
{
    private sealed class Stamp{internal IntPtr Pointer;internal int Handle,Generation;internal float Seen;}
    private sealed class Sample{internal MiCharacterProcessOptionsTimeFreeze Options;internal float Elapsed,At;}
    private sealed class Timer{internal float Elapsed;internal bool Pending;}
    private sealed class Replica{internal MiCharacter Actor;internal int Generation,Handle;internal IntPtr Options;internal string Signature;}
    private readonly Dictionary<string,Stamp> stamps=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Replica> replicas=new(StringComparer.Ordinal);
    private readonly Dictionary<string,MiAnimAction> assets=new(StringComparer.Ordinal);
    private float nextCapture,nextAssets,nextError;
    private static readonly Dictionary<IntPtr,Sample> observed=new();
    private static readonly Dictionary<IntPtr,Timer> timers=new();
    private static readonly HashSet<IntPtr> frozenActors=new();
    private static bool authoritative;
    [ThreadStatic] private static bool applying;
    internal void Clear()
    {stamps.Clear();replicas.Clear();assets.Clear();observed.Clear();timers.Clear();frozenActors.Clear();authoritative=false;nextCapture=nextAssets=nextError=0;}
    internal static bool Protected(MiCharacter actor)=>NativeInput.Probe?.NativeGuest==true&&actor!=null&&frozenActors.Contains(actor.Pointer);
    private static string F(float value)=>value.ToString("G9",CultureInfo.InvariantCulture);
    private static float Parse(string value)=>float.Parse(value,CultureInfo.InvariantCulture);
    private static string AssetKey(MiAnimAction action)=>action==null?"-":Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(action.name??"")));
    private static float Duration(MiCharacterProcessOptionsTimeFreeze options)
    {
        // An unset IL2CPP Nullable may be boxed as null; its generated getter
        // can throw before the HasValue check. Zagan's crystal uses this form.
        try {
            var value=options.m_fDuration;if(value==null||!value.HasValue)return -2;
            return float.IsPositiveInfinity(value.Value)?-1:value.Value;
        }catch(NullReferenceException){return -2;}
    }
    private static string Signature(string[] row)
    {var copy=(string[])row.Clone();copy[5]="0";return string.Join(" ",copy);}
    private static int Flags(MiCharacterProcessOptionsTimeFreeze o)=>(o.m_bActivateViewcone?1:0)|(o.m_bPauseRoutine?2:0)|(o.m_bFreezeAnimations?4:0)|(o.m_bUseFrozenVFXController?8:0);
    private static List<MiCharacter> Actors(IEnumerable<MiCharacter> crew,IEnumerable<MiCharacter> guards)
    {
        var actors=new List<MiCharacter>();var seen=new HashSet<IntPtr>();
        void Add(MiCharacter actor){if(actor!=null&&seen.Add(actor.Pointer))actors.Add(actor);}
        if(crew!=null)foreach(var actor in crew)Add(actor);if(guards!=null)foreach(var actor in guards)Add(actor);
        foreach(var entity in SkillObjectRegistry.Active.Entities)if(entity!=null&&entity.bAsCharacter(out var actor))Add(actor);
        return actors;
    }
    private static List<MiCharacterProcessHandler.MiCharacterProcessExecution> Executions(MiCharacter actor)
    {
        var result=new List<MiCharacterProcessHandler.MiCharacterProcessExecution>();var list=actor?.processes?.m_lCharacterProcessExecutions;
        if(list!=null)for(int i=0;i<list.Count;i++)if(list[i]?.m_options?.TryCast<MiCharacterProcessOptionsTimeFreeze>()!=null)result.Add(list[i]);
        return result;
    }
    internal List<string[]> Capture(IEnumerable<MiCharacter> crew,IEnumerable<MiCharacter> guards)
    {
        float now=Time.realtimeSinceStartup;if(now<nextCapture)return null;nextCapture=now+.1f;
        try
        {
            var rows=new List<string[]>();var live=new HashSet<IntPtr>();bool waiting=false;
            foreach(var actor in Actors(crew,guards))
            {
                var executions=Executions(actor);if(executions.Count==0)continue;
                if(executions.Count!=1)throw new InvalidOperationException("Ambiguous active TimeFreeze process");
                var execution=executions[0];var options=execution.m_options.TryCast<MiCharacterProcessOptionsTimeFreeze>();
                string target=InteractionSync.EntityIdentity(actor),origin=InteractionSync.EntityIdentity(options.m_charOrigin);
                if(!FreezeProtocol.Character(target)||(origin!="-"&&!FreezeProtocol.Character(origin)))throw new InvalidOperationException("Freeze character identity unavailable");
                if(!stamps.TryGetValue(target,out var stamp))stamps[target]=stamp=new();
                if(stamp.Pointer!=options.Pointer||stamp.Handle!=execution.m_iHandleID)
                {stamp.Pointer=options.Pointer;stamp.Handle=execution.m_iHandleID;stamp.Generation++;stamp.Seen=now;}
                live.Add(options.Pointer);float duration=Duration(options),elapsed=0;
                if(duration>=0)
                {
                    // Prime every new process in this pass. Returning at the
                    // first unseen timer would delay an AoE by one sample per victim.
                    if(!observed.TryGetValue(options.Pointer,out var sample)||sample.At<stamp.Seen){waiting=true;continue;}
                    elapsed=Math.Clamp(sample.Elapsed,0,duration);
                }
                var row=new[]{target,stamp.Generation.ToString(CultureInfo.InvariantCulture),origin,((int)options.m_eSkillTypeOrigin).ToString(CultureInfo.InvariantCulture),F(duration),F(elapsed),Flags(options).ToString(CultureInfo.InvariantCulture),((int)options.m_eIconID).ToString(CultureInfo.InvariantCulture),AssetKey(options.m_actionImpact),AssetKey(options.m_actionStruggle),AssetKey(options.m_actionEnd)};
                if(!FreezeProtocol.Row(row))throw new InvalidOperationException("Invalid native freeze sample");rows.Add(row);
            }
            if(waiting)return null;
            if(rows.Count>FreezeProtocol.MaxEffects)throw new InvalidOperationException("Freeze manifest exceeds bound");
            foreach(var pointer in new List<IntPtr>(observed.Keys))if(!live.Contains(pointer))observed.Remove(pointer);
            return rows;
        }
        catch(Exception ex){Error("capture",ex);return null;}// Unknown never means ended.
    }
    private bool ResolveAsset(string key,out MiAnimAction action)
    {
        action=null;if(key=="-")return true;
        if(Time.realtimeSinceStartup>=nextAssets)
        {
            nextAssets=Time.realtimeSinceStartup+1;assets.Clear();
            foreach(var asset in Resources.FindObjectsOfTypeAll<MiAnimAction>())
            {
                string id=AssetKey(asset);
                if(!assets.TryGetValue(id,out var previous))assets[id]=asset;
                else if(previous==null||previous.Pointer!=asset.Pointer)assets[id]=null;
            }
        }
        return assets.TryGetValue(key,out action)&&action!=null;
    }
    private static bool Matches(MiCharacterProcessOptionsTimeFreeze o,string[] row)=>
        InteractionSync.EntityIdentity(o.m_charOrigin)==row[2]&&(int)o.m_eSkillTypeOrigin==int.Parse(row[3],CultureInfo.InvariantCulture)&&
        Duration(o)==Parse(row[4])&&Flags(o)==int.Parse(row[6],CultureInfo.InvariantCulture)&&(int)o.m_eIconID==int.Parse(row[7],CultureInfo.InvariantCulture)&&
        AssetKey(o.m_actionImpact)==row[8]&&AssetKey(o.m_actionStruggle)==row[9]&&AssetKey(o.m_actionEnd)==row[10];
    private static void Stop(MiCharacter actor,MiCharacterProcessHandler.MiCharacterProcessExecution execution)
    {
        var options=execution.m_options;if(options!=null){timers.Remove(options.Pointer);observed.Remove(options.Pointer);}
        // Native handler owns removal, stop sounds/VFX, viewcone and movement
        // locks. Never destroy the options or clear the list directly.
        actor.processes.stop(execution.m_iHandleID,null);
    }
    private void Forget(string key)
    {
        if(!replicas.TryGetValue(key,out var replica))return;
        timers.Remove(replica.Options);observed.Remove(replica.Options);
        if(replica.Actor!=null)frozenActors.Remove(replica.Actor.Pointer);replicas.Remove(key);
    }
    internal void Apply(string[][] rows,IEnumerable<MiCharacter> crew,IEnumerable<MiCharacter> guards)
    {
        if(rows==null)return;authoritative=true;
        var wanted=new Dictionary<string,string[]>(StringComparer.Ordinal);foreach(var row in rows)wanted[row[0]]=row;
        var actors=Actors(crew,guards);foreach(var replica in replicas.Values)if(replica.Actor!=null&&!actors.Contains(replica.Actor))actors.Add(replica.Actor);
        foreach(var key in new List<string>(replicas.Keys))if(!wanted.ContainsKey(key)||replicas[key].Actor==null)Forget(key);
        // A complete host result also cancels an early local native effect.
        foreach(var actor in actors)try
        {
            string key=InteractionSync.EntityIdentity(actor);if(key==null||wanted.ContainsKey(key))continue;
            foreach(var execution in Executions(actor))Stop(actor,execution);
            frozenActors.Remove(actor.Pointer);Forget(key);
        }catch(Exception ex){Error("end",ex);}
        foreach(var row in rows)try
        {
            var actor=InteractionSync.Resolve(row[0])?.TryCast<MiCharacter>();if(actor==null)continue;
            var origin=row[2]=="-"?null:InteractionSync.Resolve(row[2])?.TryCast<MiCharacter>();if(row[2]!="-"&&origin==null)continue;
            int generation=int.Parse(row[1],CultureInfo.InvariantCulture);float duration=Parse(row[4]),elapsed=Parse(row[5]);
            replicas.TryGetValue(row[0],out var previous);string signature=Signature(row);
            var list=Executions(actor);MiCharacterProcessHandler.MiCharacterProcessExecution existing=null;
            foreach(var execution in list)
            {
                bool tracked=previous!=null&&previous.Generation==generation&&previous.Options==execution.m_options.Pointer&&
                    previous.Handle==execution.m_iHandleID&&previous.Signature==signature;
                if(existing==null&&(tracked||previous==null&&Matches(execution.m_options.TryCast<MiCharacterProcessOptionsTimeFreeze>(),row)))existing=execution;
                else Stop(actor,execution);
            }
            if(existing==null)
            {
                Forget(row[0]);
                frozenActors.Remove(actor.Pointer);
                if(duration>0&&elapsed>=duration)continue; // Do not replay an already-finished effect.
                if(!ResolveAsset(row[8],out var impact)||!ResolveAsset(row[9],out var struggle)||!ResolveAsset(row[10],out var end))
                {Error("assets",new InvalidOperationException("Missing or ambiguous native animation asset"));continue;}
                int flags=int.Parse(row[6],CultureInfo.InvariantCulture);
                var options=new MiCharacterProcessOptionsTimeFreeze(origin,actor,(Skill.SkillType)int.Parse(row[3],CultureInfo.InvariantCulture),
                    (flags&1)!=0,(flags&2)!=0,(flags&4)!=0,(flags&8)!=0,duration==-1?float.PositiveInfinity:duration,impact,struggle,end,(UIWorldInfoBatchIconID)int.Parse(row[7],CultureInfo.InvariantCulture));
                if(duration==-2)options.m_fDuration=new Il2CppSystem.Nullable<float>();
                timers[options.Pointer]=new(){Elapsed=elapsed,Pending=true};
                bool started=false;applying=true;try{started=actor.processes.bStart(options,0);}finally{applying=false;}
                if(!started){timers.Remove(options.Pointer);continue;}
                foreach(var execution in Executions(actor))if(execution.m_options.Pointer==options.Pointer){existing=execution;break;}
                if(existing==null){timers.Remove(options.Pointer);continue;}
            }
            var pointer=existing.m_options.Pointer;
            if(previous!=null&&previous.Options!=pointer){timers.Remove(previous.Options);observed.Remove(previous.Options);}
            bool correct=!observed.TryGetValue(pointer,out var sample)||Math.Abs(sample.Elapsed-elapsed)>.15f;
            if(!timers.TryGetValue(pointer,out var timer))timers[pointer]=timer=new();
            if(correct){timer.Elapsed=elapsed;timer.Pending=true;}
            replicas[row[0]]=new(){Actor=actor,Generation=generation,Handle=existing.m_iHandleID,Options=pointer,Signature=signature};
            frozenActors.Add(actor.Pointer);
        }
        catch(Exception ex){Error("apply",ex);}
    }
    private void Error(string stage,Exception ex)
    {float now=Time.realtimeSinceStartup;if(now<nextError)return;nextError=now+5;MoveProbePlugin.ProbeLog.LogWarning("TimeFreeze "+stage+": "+ex.GetType().Name+" "+ex.Message);}
    internal static void BeforeTimer(MiCharacterProcessOptions options,ref float elapsed)
    {
        if(NativeInput.Probe?.NativeGuest==true&&options!=null&&timers.TryGetValue(options.Pointer,out var timer)&&timer.Pending)
        {elapsed=timer.Elapsed;timer.Pending=false;}
    }
    internal static void ObserveTimer(MiCharacterProcessOptions options,float elapsed)
    {
        var freeze=options?.TryCast<MiCharacterProcessOptionsTimeFreeze>();if(freeze==null||!float.IsFinite(elapsed))return;
        observed[options.Pointer]=new(){Options=freeze,Elapsed=elapsed,At=Time.realtimeSinceStartup};
    }
    internal static bool AllowStart(MiCharacterProcessOptions options)=>!authoritative||applying||NativeInput.Probe?.NativeGuest!=true||options?.TryCast<MiCharacterProcessOptionsTimeFreeze>()==null;
    [HarmonyPatch(typeof(MiCharacterProcessHandler),"bStart",new Type[]{typeof(MiCharacterProcessOptions),typeof(int)})]
    internal static class StartHook
    {private static bool Prefix(MiCharacterProcessOptions __0,ref bool __result){if(AllowStart(__0))return true;__result=false;return false;}}
}
