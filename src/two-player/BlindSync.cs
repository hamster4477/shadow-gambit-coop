using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Mimimi.Animation;
using UnityEngine;

// Persistent native Blind processes. Capture starts after the original impact
// delay; recovered effects must not wait for that impact a second time.
internal sealed class BlindSync
{
    private sealed class Stamp{internal IntPtr Pointer;internal int Handle,Generation;internal float Seen;}
    private sealed class Sample{internal MiCharacterProcessOptionsBlind Options;internal float Elapsed,At;}
    private sealed class Timer{internal float Elapsed,StopTotal;internal bool Pending;}
    private sealed class Replica{internal MiCharacter Actor;internal int Generation,Handle;internal IntPtr Options;}
    private readonly Dictionary<string,Stamp> stamps=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Replica> replicas=new(StringComparer.Ordinal);
    private readonly Dictionary<string,MiAnimAction> assets=new(StringComparer.Ordinal);
    private float nextCapture,nextAssets,nextError;
    private static readonly Dictionary<IntPtr,Sample> observed=new();
    private static readonly Dictionary<IntPtr,float> durations=new();
    private static readonly Dictionary<IntPtr,Timer> timers=new();
    private static readonly Dictionary<IntPtr,IntPtr> movementActors=new();
    private static bool authoritative;
    [ThreadStatic] private static bool applying;
    internal void Clear()
    {stamps.Clear();replicas.Clear();assets.Clear();observed.Clear();durations.Clear();timers.Clear();movementActors.Clear();authoritative=false;nextCapture=nextAssets=nextError=0;}
    internal static bool Protected(MiCharacter actor)=>NativeInput.Probe?.NativeGuest==true&&actor!=null&&movementActors.TryGetValue(actor.Pointer,out var pointer)&&RemainingStop(pointer)>0;
    private static string F(float value)=>value.ToString("G9",CultureInfo.InvariantCulture);
    private static float Parse(string value)=>float.Parse(value,CultureInfo.InvariantCulture);
    private static string AssetKey(MiAnimAction action)=>action==null?"-":Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(action.name??"")));
    private static float Duration(MiCharacterProcessOptionsBlind options)
    {
        // The native timer has already resolved nullable/default duration.
        // Prefer its actual total over re-reading the nullable interop field.
        if(durations.TryGetValue(options.Pointer,out var total))return float.IsPositiveInfinity(total)?-1:total;
        var value=options.m_fDuration;if(!value.HasValue)throw new InvalidOperationException("Blind timer has not been sampled");
        return float.IsPositiveInfinity(value.Value)?-1:value.Value;
    }
    private static int Flags(MiCharacterProcessOptionsBlind o)=>(o.m_bInvestigateAfterwards?1:0)|(o.m_bSkipAnimation?2:0)|(o.m_bStopLookAround?4:0)|(o.m_bActivateViewcone?8:0);
    private static float NativeDuration(string value){float span=Parse(value);return span==-1?float.PositiveInfinity:span;}
    private static string Span(float value)=>F(float.IsPositiveInfinity(value)?-1:value);
    private static float StopTotal(string[] row)=>((int.Parse(row[6],CultureInfo.InvariantCulture)&1)!=0)?Math.Max(NativeDuration(row[4]),NativeDuration(row[8])):NativeDuration(row[8]);
    private static float RemainingStop(IntPtr pointer)
    {
        if(!timers.TryGetValue(pointer,out var timer))return 0;
        float elapsed=timer.Pending?timer.Elapsed:observed.TryGetValue(pointer,out var sample)?sample.Elapsed:timer.Elapsed;
        return Math.Max(0,timer.StopTotal-elapsed);
    }
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
        if(list!=null)for(int i=0;i<list.Count;i++)if(list[i]?.m_options?.TryCast<MiCharacterProcessOptionsBlind>()!=null)result.Add(list[i]);
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
                if(executions.Count!=1)throw new InvalidOperationException("Ambiguous active Blind process");
                var execution=executions[0];var options=execution.m_options.TryCast<MiCharacterProcessOptionsBlind>();
                string target=InteractionSync.EntityIdentity(actor),origin=InteractionSync.EntityIdentity(options.m_charOrigin);
                if(!FreezeProtocol.Character(target)||(origin!="-"&&!FreezeProtocol.Character(origin)))throw new InvalidOperationException("Blind character identity unavailable");
                if(!stamps.TryGetValue(target,out var stamp))stamps[target]=stamp=new();
                if(stamp.Pointer!=options.Pointer||stamp.Handle!=execution.m_iHandleID)
                {stamp.Pointer=options.Pointer;stamp.Handle=execution.m_iHandleID;stamp.Generation++;stamp.Seen=now;}
                live.Add(options.Pointer);float elapsed=0;
                // Even indefinite blindness can have a finite movement-stop
                // window. Its elapsed timer must never be reset to zero.
                if(!observed.TryGetValue(options.Pointer,out var sample)||sample.At<stamp.Seen){waiting=true;continue;}
                float duration=Duration(options);
                elapsed=duration==-1?Math.Max(0,sample.Elapsed):Math.Clamp(sample.Elapsed,0,duration);
                var row=new[]{target,stamp.Generation.ToString(CultureInfo.InvariantCulture),origin,((int)options.m_eSkillTypeOrigin).ToString(CultureInfo.InvariantCulture),F(duration),F(elapsed),Flags(options).ToString(CultureInfo.InvariantCulture),F(options.m_fDurationWaitBeforeImpact),Span(options.m_fDurationStopMovement),Span(options.m_fDurationStruggle),AssetKey(options.m_actionImpact),AssetKey(options.m_actionStruggle),AssetKey(options.m_actionFall),NativeDetectionProfile.Capture(options.m_detectionData)};
                if(!BlindProtocol.Row(row))throw new InvalidOperationException("Invalid native blind sample");rows.Add(row);
            }
            if(waiting)return null;
            if(rows.Count>BlindProtocol.MaxEffects)throw new InvalidOperationException("Blind manifest exceeds bound");
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
    private static bool Matches(MiCharacterProcessOptionsBlind o,string[] row)=>
        InteractionSync.EntityIdentity(o.m_charOrigin)==row[2]&&(int)o.m_eSkillTypeOrigin==int.Parse(row[3],CultureInfo.InvariantCulture)&&
        Duration(o)==Parse(row[4])&&Flags(o)==int.Parse(row[6],CultureInfo.InvariantCulture)&&
        (o.m_fDurationWaitBeforeImpact==0||o.m_fDurationWaitBeforeImpact==Parse(row[7]))&&
        (o.m_fDurationStopMovement==NativeDuration(row[8])||o.m_fDurationStopMovement==0&&Parse(row[5])>=StopTotal(row))&&
        o.m_fDurationStruggle==NativeDuration(row[9])&&AssetKey(o.m_actionImpact)==row[10]&&AssetKey(o.m_actionStruggle)==row[11]&&AssetKey(o.m_actionFall)==row[12]&&
        NativeDetectionProfile.Capture(o.m_detectionData)==row[13];
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
        if(replica.Actor!=null)movementActors.Remove(replica.Actor.Pointer);replicas.Remove(key);
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
            movementActors.Remove(actor.Pointer);Forget(key);
        }catch(Exception ex){Error("end",ex);}
        foreach(var row in rows)try
        {
            var actor=InteractionSync.Resolve(row[0])?.TryCast<MiCharacter>();if(actor==null)continue;
            var origin=row[2]=="-"?null:InteractionSync.Resolve(row[2])?.TryCast<MiCharacter>();if(row[2]!="-"&&origin==null)continue;
            int generation=int.Parse(row[1],CultureInfo.InvariantCulture);float duration=Parse(row[4]),elapsed=Parse(row[5]);
            replicas.TryGetValue(row[0],out var previous);
            var list=Executions(actor);MiCharacterProcessHandler.MiCharacterProcessExecution existing=null;
            foreach(var execution in list)
            {
                if(existing==null&&(previous==null||previous.Generation==generation)&&
                    (previous!=null&&previous.Options==execution.m_options.Pointer||Matches(execution.m_options.TryCast<MiCharacterProcessOptionsBlind>(),row)))existing=execution;
                else Stop(actor,execution);
            }
            if(existing==null)
            {
                Forget(row[0]);
                movementActors.Remove(actor.Pointer);
                if(duration>0&&elapsed>=duration)continue; // Do not replay an already-finished effect.
                if(!ResolveAsset(row[10],out var impact)||!ResolveAsset(row[11],out var struggle)||!ResolveAsset(row[12],out var end))
                {Error("assets",new InvalidOperationException("Missing or ambiguous native animation asset"));continue;}
                int flags=int.Parse(row[6],CultureInfo.InvariantCulture);
                if(!NativeDetectionProfile.TryBuild(row[13],out var profile))continue;
                float stopTotal=StopTotal(row),rawStop=elapsed>=stopTotal?0:NativeDuration(row[8]);
                var options=new MiCharacterProcessOptionsBlind(origin,actor,(Skill.SkillType)int.Parse(row[3],CultureInfo.InvariantCulture),
                    (flags&1)!=0,(flags&2)!=0,(flags&4)!=0,(flags&8)!=0,NativeDuration(row[4]),0,rawStop,impact,struggle,end,profile);
                options.m_fDurationStruggle=NativeDuration(row[9]);
                durations[options.Pointer]=NativeDuration(row[4]);
                timers[options.Pointer]=new(){Elapsed=elapsed,StopTotal=stopTotal,Pending=true};
                bool started=false;applying=true;try{started=actor.processes.bStart(options,0);}finally{applying=false;}
                if(!started){timers.Remove(options.Pointer);continue;}
                foreach(var execution in Executions(actor))if(execution.m_options.Pointer==options.Pointer){existing=execution;break;}
                if(existing==null){timers.Remove(options.Pointer);continue;}
            }
            var pointer=existing.m_options.Pointer;
            if(previous!=null&&previous.Options!=pointer){timers.Remove(previous.Options);observed.Remove(previous.Options);}
            bool correct=!observed.TryGetValue(pointer,out var sample)||Math.Abs(sample.Elapsed-elapsed)>.15f;
            if(!timers.TryGetValue(pointer,out var timer))timers[pointer]=timer=new();
            timer.StopTotal=StopTotal(row);
            if(correct){timer.Elapsed=elapsed;timer.Pending=true;}
            replicas[row[0]]=new(){Actor=actor,Generation=generation,Handle=existing.m_iHandleID,Options=pointer};
            movementActors[actor.Pointer]=pointer;
        }
        catch(Exception ex){Error("apply",ex);}
    }
    private void Error(string stage,Exception ex)
    {float now=Time.realtimeSinceStartup;if(now<nextError)return;nextError=now+5;MoveProbePlugin.ProbeLog.LogWarning("Blind "+stage+": "+ex.GetType().Name+" "+ex.Message);}
    internal static void BeforeTimer(MiCharacterProcessOptions options,ref float elapsed)
    {
        if(NativeInput.Probe?.NativeGuest==true&&options?.TryCast<MiCharacterProcessOptionsBlind>()!=null&&timers.TryGetValue(options.Pointer,out var timer)&&timer.Pending)
        {elapsed=timer.Elapsed;timer.Pending=false;}
    }
    internal static void ObserveTimer(MiCharacterProcessOptions options,float elapsed,float? total=null)
    {
        var freeze=options?.TryCast<MiCharacterProcessOptionsBlind>();if(freeze==null||!float.IsFinite(elapsed))return;
        if(total.HasValue&&(float.IsFinite(total.Value)||float.IsPositiveInfinity(total.Value))&&total.Value>0)durations[options.Pointer]=total.Value;
        observed[options.Pointer]=new(){Options=freeze,Elapsed=elapsed,At=Time.realtimeSinceStartup};
    }
    internal static bool AllowStart(MiCharacterProcessOptions options)=>!authoritative||applying||NativeInput.Probe?.NativeGuest!=true||options?.TryCast<MiCharacterProcessOptionsBlind>()==null;
    [HarmonyPatch(typeof(MiCharacterProcessHandler),"bStart",new Type[]{typeof(MiCharacterProcessOptions),typeof(int)})]
    internal static class StartHook
    {private static bool Prefix(MiCharacterProcessOptions __0,ref bool __result){if(AllowStart(__0))return true;__result=false;return false;}}
    internal static bool TryStopDuration(MiCharacterProcessOptionsBlind options,out float value)
    {
        value=0;if(NativeInput.Probe?.NativeGuest!=true||options==null||!timers.ContainsKey(options.Pointer))return false;
        value=RemainingStop(options.Pointer);return true;
    }
    [HarmonyPatch(typeof(MiCharacterProcessOptionsBlind),"get_fDurationStopMovement")]
    internal static class StopDurationHook
    {private static bool Prefix(MiCharacterProcessOptionsBlind __instance,ref float __result){if(!TryStopDuration(__instance,out var remaining))return true;__result=remaining;return false;}}
}
