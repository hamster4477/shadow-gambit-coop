using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Mimimi.Animation;
using UnityEngine;

internal sealed class FrozenSync
{
    private sealed class Stamp {internal IntPtr Pointer;internal int Handle,Generation;internal float Seen;}
    private sealed class Replica
    {internal MiCharacter Owner,Target,Origin;internal MiCharacterProcessOptionsFrozen Options;internal int Handle,Generation;internal string Signature;}
    private readonly Dictionary<string,Stamp> stamps=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Replica> replicas=new(StringComparer.Ordinal);
    private readonly HashSet<string> finished=new(StringComparer.Ordinal);
    internal bool ReadyForOutcome(string id)=>finished.Contains(id);
    internal void OutcomeApplied(string id)=>finished.Remove(id);
    private readonly Dictionary<string,MiAnimAction> animations=new(StringComparer.Ordinal);
    private readonly Dictionary<string,FreezerVFXController> pools=new(StringComparer.Ordinal);
    private static readonly HashSet<IntPtr> protectedActors=new();
    private static bool authoritative;
    [ThreadStatic] private static bool applying;
    private float nextCapture,nextAssets,nextError;
    private static string F(float value)=>value.ToString("G9",CultureInfo.InvariantCulture);
    private static float P(string value)=>float.Parse(value,CultureInfo.InvariantCulture);
    private static int I(string value)=>int.Parse(value,CultureInfo.InvariantCulture);
    private static string Key(UnityEngine.Object asset)=>asset==null?"-":Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(asset.name??"")));
    private static int Flags(MiCharacterProcessOptionsFrozen o)=>(o.m_bAllowFreezingAI?1:0)|(o.m_bTargetIsSuspicious?2:0)|(o.m_bTriggerAlarmOnEnd?4:0)|(o.m_bPlayVO?8:0)|(o.m_bPlayStartSFX?16:0)|(o.m_bPlayLoopSFX?32:0)|(o.m_bSkipOriginAnimations?64:0)|(o.m_bHideUIIcons?128:0)|(o.m_bNoVFXController?256:0)|(o.m_bRemainTargetability?512:0);
    private static string Signature(string[] row)
    {var copy=(string[])row.Clone();copy[5]="0";copy[16]="1";return string.Join(" ",copy);}
    internal static bool Protected(MiCharacter actor)=>NativeInput.Probe?.NativeGuest==true&&actor!=null&&protectedActors.Contains(actor.Pointer);
    internal void Clear()
    {
        // Cancel before dropping authority: a replica must never resume its
        // independent kill/knockout when the user disconnects or changes scenes.
        foreach(var replica in replicas.Values)try{replica.Owner?.processes?.cancel(replica.Handle,null);}catch(Exception ex){Error("clear",ex);}
        replicas.Clear();stamps.Clear();animations.Clear();pools.Clear();finished.Clear();protectedActors.Clear();FrozenProcessClock.Clear();
        authoritative=false;nextCapture=nextAssets=nextError=0;
    }
    private static List<MiCharacter> Actors(IEnumerable<MiCharacter> crew,IEnumerable<MiCharacter> guards)
    {
        var result=new List<MiCharacter>();var seen=new HashSet<IntPtr>();
        void Add(MiCharacter a){if(a!=null&&seen.Add(a.Pointer))result.Add(a);}
        if(crew!=null)foreach(var a in crew)Add(a);if(guards!=null)foreach(var a in guards)Add(a);
        foreach(var e in SkillObjectRegistry.Active.Entities)if(e!=null&&e.bAsCharacter(out var a))Add(a);return result;
    }
    private static List<MiCharacterProcessHandler.MiCharacterProcessExecution> Executions(MiCharacter actor)
    {
        var result=new List<MiCharacterProcessHandler.MiCharacterProcessExecution>();var list=actor?.processes?.m_lCharacterProcessExecutions;
        if(list!=null)for(int i=0;i<list.Count;i++)if(list[i]?.m_options?.TryCast<MiCharacterProcessOptionsFrozen>()!=null)result.Add(list[i]);return result;
    }
    // IL2CPP boxes an empty Nullable as a null object. The generated field
    // getter constructs a wrapper anyway and throws before ?. can inspect it.
    internal static float OptionalFloat(Func<Il2CppSystem.Nullable<float>> read,float fallback)
    {
        try{var value=read();return value?.HasValue==true?value.Value:fallback;}
        catch(NullReferenceException){return fallback;}
    }
    private static string[] Describe(MiCharacter owner,MiCharacterProcessHandler.MiCharacterProcessExecution execution,FrozenProcessClock.Sample sample,int generation)
    {
        var o=execution.m_options.TryCast<MiCharacterProcessOptionsFrozen>();var process=execution.m_process.TryCast<MiCharacterProcessFrozen>();
        float staging=OptionalFloat(()=>o.m_fStagingDuration,process.m_fStagingDuration?.Value??0f);
        float duration=sample.Duration,elapsed=duration<0?sample.Elapsed:Math.Clamp(sample.Elapsed,0,duration);
        return new[]{InteractionSync.EntityIdentity(o.m_charTarget),generation.ToString(CultureInfo.InvariantCulture),InteractionSync.EntityIdentity(o.m_charOrigin),((int)o.m_eSkillTypeOrigin).ToString(CultureInfo.InvariantCulture),F(duration),F(elapsed),Flags(o).ToString(CultureInfo.InvariantCulture),F(staging),F(o.m_fDurationVFXOffset),Key(o.m_actionStaging),Key(o.m_actionTargetLoop),Key(o.m_actionTargetFall),Key(o.m_vfxControllerPool),((int)o.m_eFollowUpProcessType).ToString(CultureInfo.InvariantCulture),((int)o.m_eSkillTypeOnDamage).ToString(CultureInfo.InvariantCulture),InteractionSync.EntityIdentity(owner),sample.Phase.ToString(CultureInfo.InvariantCulture)};
    }
    internal List<string[]> Capture(IEnumerable<MiCharacter> crew,IEnumerable<MiCharacter> guards)
    {
        float now=Time.realtimeSinceStartup;if(now<nextCapture)return null;nextCapture=now+.1f;
        try
        {
            var rows=new List<string[]>();var live=new HashSet<IntPtr>();var targets=new HashSet<string>();bool waiting=false;
            foreach(var owner in Actors(crew,guards))foreach(var execution in Executions(owner))
            {
                var o=execution.m_options?.TryCast<MiCharacterProcessOptionsFrozen>();
                // Native ship practice targets can publish the options before
                // the process object. Keep the preceding confirmed effect page
                // until initialization completes instead of throwing each poll.
                if(o==null||o.m_charTarget==null||execution.m_process?.TryCast<MiCharacterProcessFrozen>()==null){waiting=true;continue;}
                string key=InteractionSync.EntityIdentity(o.m_charTarget);
                if(!FreezeProtocol.Character(key)||!targets.Add(key))throw new InvalidOperationException("Ambiguous frozen target");
                if(!stamps.TryGetValue(key,out var stamp))stamps[key]=stamp=new();
                if(stamp.Pointer!=o.Pointer||stamp.Handle!=execution.m_iHandleID){stamp.Pointer=o.Pointer;stamp.Handle=execution.m_iHandleID;stamp.Generation++;stamp.Seen=now;}
                live.Add(o.Pointer);FrozenProcessClock.Track(o);
                if(!FrozenProcessClock.Read(o,out var sample)||sample.ObservedAt<stamp.Seen){waiting=true;continue;}
                if(sample.Phase<1||sample.Phase>3)continue;
                var row=Describe(owner,execution,sample,stamp.Generation);
                if(!FrozenProtocol.Row(row))throw new InvalidOperationException("Invalid native frozen descriptor");rows.Add(row);
            }
            FrozenProcessClock.Retain(live);if(waiting)return null;
            if(rows.Count>FrozenProtocol.MaxEffects)throw new InvalidOperationException("Frozen manifest exceeds bound");return rows;
        }catch(Exception ex){Error("capture",ex);return null;}
    }
    private void RefreshAssets()
    {
        if(Time.realtimeSinceStartup<nextAssets)return;nextAssets=Time.realtimeSinceStartup+1;
        animations.Clear();pools.Clear();
        foreach(var a in Resources.FindObjectsOfTypeAll<MiAnimAction>())Add(animations,a);
        foreach(var a in Resources.FindObjectsOfTypeAll<FreezerVFXController>())Add(pools,a);
    }
    private static void Add<T>(Dictionary<string,T> map,T value)where T:UnityEngine.Object
    {string key=Key(value);if(!map.TryGetValue(key,out var old))map[key]=value;else if(old==null||old.Pointer!=value.Pointer)map[key]=null;}
    private static bool Find<T>(Dictionary<string,T> map,string key,out T value)where T:class
    {value=null;return key=="-"||map.TryGetValue(key,out value)&&value!=null;}
    private static void Stop(MiCharacter owner,MiCharacterProcessHandler.MiCharacterProcessExecution execution)
    {
        // Cancellation performs native VFX, animation, controller and frozen
        // state cleanup, without entering the damage follow-up coroutine.
        var options=execution.m_options.TryCast<MiCharacterProcessOptionsFrozen>();
        owner.processes.cancel(execution.m_iHandleID,null);FrozenProcessClock.Forget(options);
    }
    private void Forget(string key)
    {if(replicas.Remove(key,out var previous))FrozenProcessClock.Forget(previous.Options);}
    private void Protect()
    {
        protectedActors.Clear();foreach(var r in replicas.Values)
        {if(r.Target!=null)protectedActors.Add(r.Target.Pointer);if(r.Origin!=null)protectedActors.Add(r.Origin.Pointer);}
    }
    internal void Apply(string[][] rows,IEnumerable<MiCharacter> crew,IEnumerable<MiCharacter> guards)
    {
        if(rows==null)return;authoritative=true;var wanted=new Dictionary<string,string[]>(StringComparer.Ordinal);
        foreach(var row in rows)wanted[row[0]]=row;
        var actors=Actors(crew,guards);foreach(var replica in replicas.Values)if(replica.Owner!=null&&!actors.Contains(replica.Owner))actors.Add(replica.Owner);
        foreach(var owner in actors)foreach(var execution in Executions(owner))try
        {
            var o=execution.m_options.TryCast<MiCharacterProcessOptionsFrozen>();string key=InteractionSync.EntityIdentity(o.m_charTarget);
            if(!wanted.TryGetValue(key,out var row)||row[15]!=InteractionSync.EntityIdentity(owner)){Stop(owner,execution);finished.Add(key);}
        }catch(Exception ex){Error("end",ex);}
        foreach(var key in new List<string>(replicas.Keys))if(!wanted.ContainsKey(key)){finished.Add(key);Forget(key);}
        foreach(var row in rows)try
        {
            string key=row[0],signature=Signature(row);var target=InteractionSync.Resolve(key)?.TryCast<MiCharacter>();
            if(row[16]=="1")finished.Remove(key);else finished.Add(key);
            var origin=InteractionSync.Resolve(row[2])?.TryCast<MiCharacter>();var owner=InteractionSync.Resolve(row[15])?.TryCast<MiCharacter>();
            if(target==null||origin==null||owner==null)continue;
            int generation=I(row[1]);float duration=P(row[4]),elapsed=P(row[5]);bool hold=row[16]=="1";
            replicas.TryGetValue(key,out var previous);MiCharacterProcessHandler.MiCharacterProcessExecution existing=null;
            foreach(var execution in Executions(owner))
            {
                var o=execution.m_options.TryCast<MiCharacterProcessOptionsFrozen>();if(InteractionSync.EntityIdentity(o.m_charTarget)!=key)continue;
                bool match=previous!=null&&previous.Options.Pointer==o.Pointer&&previous.Handle==execution.m_iHandleID&&previous.Signature==signature;
                if(previous==null&&FrozenProcessClock.Read(o,out var sample))match=Signature(Describe(owner,execution,sample,generation))==signature;
                // An ordinary confirmed cast can start just before the first
                // manifest. Match its full descriptor before adopting it.
                if(previous==null&&!FrozenProcessClock.Read(o,out _))
                {
                    float nativeDuration=OptionalFloat(()=>o.m_fDuration,duration);
                    if(nativeDuration<0)nativeDuration=-1;
                    match=nativeDuration==duration&&Signature(Describe(owner,execution,new FrozenProcessClock.Sample{Duration=duration,Elapsed=elapsed,Phase=I(row[16])},generation))==signature;
                }
                if(existing==null&&match)existing=execution;else Stop(owner,execution);
            }
            if(existing==null)
            {
                Forget(key);
                // A missed process already in its damage/end stage is restored
                // through authoritative health, not by performing a new attack.
                if(!hold)continue;
                RefreshAssets();if(!Find(animations,row[9],out var stage)||!Find(animations,row[10],out var loop)||!Find(animations,row[11],out var fall)||!Find(pools,row[12],out var pool))
                {Error("assets",new InvalidOperationException("Missing or ambiguous frozen asset"));continue;}
                int flags=I(row[6]);float stageRemaining=Math.Max(0,P(row[7])-elapsed);
                var options=new MiCharacterProcessOptionsFrozen(origin,target,new Il2CppSystem.Nullable<float>(duration),
                    (flags&1)!=0,(flags&2)!=0,(flags&4)!=0,(flags&8)!=0,false,(flags&32)!=0,(flags&64)!=0,
                    new Il2CppSystem.Nullable<float>(stageRemaining),P(row[8])+elapsed,stageRemaining>0?stage:null,loop,fall,pool,
                    (MiCharacterProcessFrozen.FollowUpProcessType)I(row[13]),(Skill.SkillType)I(row[3]),(Skill.SkillType)I(row[14]),
                    (flags&128)!=0,(flags&256)!=0,(flags&512)!=0);
                if(!FrozenProcessClock.Confirm(options,duration,elapsed))continue;
                bool started=false;applying=true;
                try{started=owner.processes.bStart(options,0);}
                catch{FrozenProcessClock.Forget(options);throw;}
                finally{applying=false;}
                if(!started){FrozenProcessClock.Forget(options);continue;}
                foreach(var execution in Executions(owner))if(execution.m_options.Pointer==options.Pointer){existing=execution;break;}
                if(existing==null){FrozenProcessClock.Forget(options);continue;}
            }
            var active=existing.m_options.TryCast<MiCharacterProcessOptionsFrozen>();
            if(previous!=null&&previous.Options.Pointer!=active.Pointer)FrozenProcessClock.Forget(previous.Options);
            FrozenProcessClock.Confirm(active,duration,elapsed,hold);
            replicas[key]=new(){Owner=owner,Target=target,Origin=origin,Options=active,Handle=existing.m_iHandleID,Generation=generation,Signature=signature};
        }catch(Exception ex){Error("apply",ex);}
        Protect();
    }
    private void Error(string stage,Exception ex)
    {float now=Time.realtimeSinceStartup;if(now<nextError)return;nextError=now+5;MoveProbePlugin.ProbeLog.LogWarning("Frozen "+stage+": "+ex.ToString());}
    internal static bool AllowStart(MiCharacterProcessOptions options)=>!authoritative||applying||NativeInput.Probe?.NativeGuest!=true||options?.TryCast<MiCharacterProcessOptionsFrozen>()==null;
    [HarmonyPatch(typeof(MiCharacterProcessHandler),"bStart",new Type[]{typeof(MiCharacterProcessOptions),typeof(int)})]
    internal static class StartHook
    {private static bool Prefix(MiCharacterProcessOptions __0,ref bool __result){if(AllowStart(__0))return true;__result=false;return false;}}
}
