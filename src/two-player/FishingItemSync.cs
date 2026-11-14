using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using HarmonyLib;

// IFishable and possessable pulls have an onFish/wait/onPostFish lifecycle,
// not the character MiFlyingProcess path. Let those native callbacks run once.
internal static class FishingItemSync
{
    private sealed class Record{internal SkillFishing Skill;internal SkillCommand Command;internal string[] Row;internal float Retry;internal bool Local,Done,Started,CleanupAttempted;internal Entity Target;internal Action Restore;}
    private static readonly Dictionary<string,Record> host=new(StringComparer.Ordinal),guest=new(StringComparer.Ordinal);
    private static readonly Dictionary<IntPtr,Record> targetOwners=new();
    private static readonly Dictionary<string,int> latestGuestSerial=new(StringComparer.Ordinal);
    private static readonly Queue<string> hostHistory=new(),guestHistory=new();
    private static int cursor;
    internal static void Clear(){host.Clear();guest.Clear();targetOwners.Clear();latestGuestSerial.Clear();hostHistory.Clear();guestHistory.Clear();cursor=0;}
    private static void PrepareRestore(Record r)
    {
        var entity=r.Command?.m_target?.m_entityTarget;if(entity==null)return;
        r.Target=entity;targetOwners[entity.Pointer]=r;
        var item=entity.TryCast<MiPossessableSkillItem>();
        if(item!=null)
        {
            bool wasFished=item.m_bHasBeenFished;
            r.Restore=()=>{if(item!=null)item.m_bHasBeenFished=wasFished;};
        }
        var distraction=entity.TryCast<MiPossessableDistractionEx>();
        var tanuki=entity.TryCast<MiPossessableBaitTanuki>();
        if(distraction!=null||tanuki!=null)
        {
            // Both concrete onFish overrides set MiUsable.m_bAutoUse. This
            // must not survive a cancelled pull, nor be reset after pickup.
            var usable=entity.TryCast<MiUsable>();bool autoUse=usable.m_bAutoUse;
            r.Restore+=()=>{if(usable!=null)usable.m_bAutoUse=autoUse;};
        }
        if(tanuki!=null)
        {
            var noise=tanuki.m_noiseEmitterTanuki;
            bool emitting=noise?.bIsEmitting==true;
            var range=tanuki.m_rangeGatherer;
            bool hadRange=range!=null&&range.bExists&&range.rangeGatherer!=null;
            float radius=noise?.m_fRadiusCurrent??0,height=noise?.m_fHeightCurrent??0;
            var settings=tanuki.m_rangeGathererSettings;
            r.Restore+=()=>
            {
                if(tanuki==null)return;
                // onFish stops noise and frees the pooled gatherer. Recreate
                // just those effects, never onFlyingHit (which runs scripts).
                if(emitting&&noise!=null&&!noise.bIsEmitting)noise.emitStart();
                var current=tanuki.m_rangeGatherer;
                if(hadRange&&current!=null&&(!current.bExists||current.rangeGatherer==null))
                {
                    var restored=current.ensure(RangeGatherer.RangeGathererType.Noise);
                    tanuki.m_rangeGatherer=current;
                    if(restored!=null)
                    {
                        restored.init(settings,radius,height,true);
                        restored.updateVisualisation(true);
                        restored.transform.SetParent(tanuki.transform,false);
                        restored.transform.localPosition=Vector3.zero;
                    }
                }
            };
        }
        var marker=entity.TryCast<TeleportBackMarkerFishableTarget>()?.m_markerAttachedTo;
        if(marker!=null&&!marker.m_bDisabledInCannonOrFlying)
        {
            // Unlike MiPossessable.onPostFish, this concrete native callback
            // restores marker visibility/ranges without executing item scripts
            // or moving/picking up the marker. It pairs with marker.onFish.
            r.Restore=()=>{if(marker!=null&&marker.m_bDisabledInCannonOrFlying)marker.onPostFish(r.Skill.charOrigin);};
        }
    }
    private static void ReleaseTarget(Record r,bool restore)
    {
        if(r.Target==null||!targetOwners.TryGetValue(r.Target.Pointer,out var owner)||owner!=r)return;
        targetOwners.Remove(r.Target.Pointer);
        if(restore)try{r.Restore?.Invoke();}catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Item pull target restore: "+ex.GetType().Name);}
        r.Restore=null;
    }
    private static string[] NativeRow(SkillFishing skill,SkillCommand command,string variant)
    {
        int serial=InteractionSync.CommandSerial(command);var entity=command?.m_target?.m_entityTarget;
        if(serial<=0||skill?.charOrigin==null||entity==null)return null;
        var point=entity.transform.position;
        var row=new[]{InteractionSync.EntityIdentity(skill.charOrigin),serial.ToString(CultureInfo.InvariantCulture),InteractionSync.EntityIdentity(entity),"A",
            point.x.ToString("R",CultureInfo.InvariantCulture),point.y.ToString("R",CultureInfo.InvariantCulture),point.z.ToString("R",CultureInfo.InvariantCulture),variant};
        return FishingItemProtocol.Row(row)?row:null;
    }
    private static void Trim(Dictionary<string,Record> records,Queue<string> history)
    {
        int attempts=history.Count;
        while(records.Count>128&&attempts-->0){var key=history.Dequeue();if(records.TryGetValue(key,out var r)&&r.Local&&!r.Done){history.Enqueue(key);continue;}records.Remove(key);}
    }
    internal static bool Before(SkillFishing skill,SkillCommand command,int stage,string variant)
    {
        if(NativeInput.Probe?.NetworkSessionActive!=true)return true;
        try{return BeforeCore(skill,command,stage,variant);}
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Item pull observation: "+ex.GetType().Name);return true;}
    }
    private static bool BeforeCore(SkillFishing skill,SkillCommand command,int stage,string variant)
    {
        bool remote=NativeInput.Probe?.NativeGuest==true;
        var records=remote?guest:host;var history=remote?guestHistory:hostHistory;
        if(command!=null)foreach(var existing in records.Values)
            if(existing.Command?.Pointer==command.Pointer)return !existing.Done&&(!remote||stage!=1||existing.Row[3]=="E");
        var row=NativeRow(skill,command,variant);if(row==null)return true;string key=FishingItemProtocol.Key(row);
        if(!records.TryGetValue(key,out var r)){records[key]=r=new(){Command=command,Row=row};history.Enqueue(key);Trim(records,history);}
        // Bind even a retained cancellation to the late native command so the
        // hook can terminate it before onFish acquires any locks or hides items.
        r.Command=command;r.Skill=skill;
        if(remote&&!r.Local&&latestGuestSerial.TryGetValue(row[0],out int latest)&&int.Parse(row[1],CultureInfo.InvariantCulture)<latest)
            r.Done=true;
        if(r.Done||r.Row[3]=="C")return false;
        if(!r.Local&&stage==0)PrepareRestore(r);
        r.Local=true;
        // The command/target identity is captured before callbacks can remove
        // an item. Completion is gated, never replaced by hand-written pickup.
        return !remote||stage!=1||r.Row[3]=="E";
    }
    internal static bool Waiting(SkillCommand command)
    {
        var records=NativeInput.Probe?.NativeGuest==true?guest:host;
        if(command!=null)foreach(var r in records.Values)
            if(r.Command?.Pointer==command.Pointer)return !r.Done&&r.Row[3]!="C";
        return true;
    }
    internal static void ReleaseUnstarted(SkillCommand command)
    {
        // execute acquires a target reference before constructing the iterator
        // (e.g.181E435B7). Skipping state0 must return that reference, but must
        // not call postReelIn: Fly occupation has not yet been acquired.
        try{command?.m_target?.free();}
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Unstarted fishing target release: "+ex.GetType().Name);}
    }
    internal static void After(SkillFishing skill,SkillCommand command,bool running,int stage=1)
    {
        if(command==null)return;
        var records=NativeInput.Probe?.NativeGuest==true?guest:host;
        foreach(var r in records.Values)if(r.Command?.Pointer==command.Pointer)
        {
            if(r.Done)continue;
            if(running){if(stage==1)r.Started=true;continue;}
            r.Done=true;r.Row[3]="E";
            ReleaseTarget(r,false);
        }
    }
    internal static SkillCommand CommandOf(MiCoroutine coroutine)
    {
        var enumerator=coroutine?.enumerator;
        return enumerator?.TryCast<SkillFishing._coroReelIn_d__115>()?._skillCommandReelIn??enumerator?.TryCast<SkillFishing._coroReelIn_d__116>()?._skillCommandReelIn??enumerator?.TryCast<SkillFishing._coroReelIn_d__113>()?._skillCommandReelIn;
    }
    internal static void OnStopped(SkillCommand command)
    {
        if(command==null||NativeInput.Probe?.NetworkSessionActive!=true)return;
        var records=NativeInput.Probe?.NativeGuest==true?guest:host;
        foreach(var r in records.Values)if(r.Command?.Pointer==command.Pointer&&!r.Done)
        {
            r.Done=true;r.Row[3]="C";
            ReleaseTarget(r,r.Started);
            // postReelIn releases occupation3 and decrements the retained target
            // reference. A successful first yield proves that setup ran.
            // Never run pickup/onPostFish or call this decrement twice.
            if(r.Started&&!r.CleanupAttempted)
            {
                r.CleanupAttempted=true;
                try{r.Skill.postReelIn(command);}catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Item pull cancellation cleanup: "+ex.GetType().Name);}
            }
        }
    }
    internal static List<string[]> Capture()
    {
        var records=new List<Record>(host.Values);var rows=new List<string[]>();
        if(records.Count==0)return rows;
        for(int i=0;i<records.Count&&rows.Count<FishingItemProtocol.MaxRows;i++)rows.Add((string[])records[(cursor+i)%records.Count].Row.Clone());
        cursor=(cursor+rows.Count)%records.Count;return rows;
    }
    internal static void Apply(string[][] rows)
    {
        if(rows==null||NativeInput.Probe?.NativeGuest!=true)return;
        // Pre-scan the whole page so ordering of retained endings cannot
        // replay an old item before a newer cast in the same page is seen.
        foreach(var row in rows)if(FishingItemProtocol.Row(row))
        {
            int serial=int.Parse(row[1],CultureInfo.InvariantCulture);
            if(!latestGuestSerial.TryGetValue(row[0],out int latest)||serial>latest)latestGuestSerial[row[0]]=serial;
        }
        foreach(var row in rows)
        {
            if(!FishingItemProtocol.Row(row))continue;string key=FishingItemProtocol.Key(row);
            if(!guest.TryGetValue(key,out var r)){guest[key]=r=new(){Row=(string[])row.Clone()};guestHistory.Enqueue(key);}
            else if(r.Row[7]!=row[7]||r.Done)continue;
            if(r.Row[3]=="A")r.Row=(string[])row.Clone();
            if(r.Row[3]=="C")
            {
                if(!r.Local){r.Done=true;continue;}
                var parent=r.Skill?.m_coroReelIn;
                if(CommandOf(parent)?.Pointer==r.Command?.Pointer)parent.stopInternal();
                OnStopped(r.Command);
                continue;
            }
            if(r.Local||Time.realtimeSinceStartup<r.Retry)continue;r.Retry=Time.realtimeSinceStartup+1;
            int replaySerial=int.Parse(row[1],CultureInfo.InvariantCulture);
            if(replaySerial<latestGuestSerial[row[0]]){r.Done=true;continue;}
            try
            {
                var actor=InteractionSync.Resolve(row[0])?.TryCast<MiCharacter>();if(actor?.controllerPlayer==null)continue;
                bool busy=false;foreach(var cmd in new[]{actor.controller.skillCommand,actor.controller.skillCommandActive,actor.controller.skillCommandProcess})
                {
                    if(cmd==null)continue;
                    int current=InteractionSync.CommandSerial(cmd);
                    if(current>replaySerial){latestGuestSerial[row[0]]=Math.Max(latestGuestSerial[row[0]],current);r.Done=true;busy=true;break;}
                    if(current==replaySerial||cmd.m_skill?.eSkillType!=Skill.SkillType.GoTo){busy=true;break;}
                }
                if(busy)continue;
                if(InteractionSync.Issue(actor,new[]{InteractionProtocol.Code("Fishing"),row[2],row[4],row[5],row[6],"0"},0,true,int.Parse(row[1],CultureInfo.InvariantCulture)))
                    InteractionSync.MarkRecovery(actor.controller.skillCommand);
            }
            catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Item pull recovery: "+ex.GetType().Name);}
        }
        Trim(guest,guestHistory);
    }
    [HarmonyPatch(typeof(SkillFishing._coroReelIn_d__115),"MoveNext")]
    internal static class ItemHook
    {
        internal static bool Prefix(SkillFishing._coroReelIn_d__115 __instance,ref bool __result)
        {if(Before(__instance.__4__this,__instance._skillCommandReelIn,__instance.__1__state,"I"))return true;__instance.__2__current=null;__result=Waiting(__instance._skillCommandReelIn);if(!__result){bool unstarted=__instance.__1__state==0;__instance.__1__state=-1;if(unstarted)ReleaseUnstarted(__instance._skillCommandReelIn);}return false;}
        private static void Postfix(SkillFishing._coroReelIn_d__115 __instance,bool __result)=>After(__instance.__4__this,__instance._skillCommandReelIn,__result,__instance.__1__state);
    }
    [HarmonyPatch(typeof(SkillFishing._coroReelIn_d__116),"MoveNext")]
    internal static class PossessableHook
    {
        internal static bool Prefix(SkillFishing._coroReelIn_d__116 __instance,ref bool __result)
        {if(Before(__instance.__4__this,__instance._skillCommandReelIn,__instance.__1__state,"P"))return true;__instance.__2__current=null;__result=Waiting(__instance._skillCommandReelIn);if(!__result){bool unstarted=__instance.__1__state==0;__instance.__1__state=-1;if(unstarted)ReleaseUnstarted(__instance._skillCommandReelIn);}return false;}
        private static void Postfix(SkillFishing._coroReelIn_d__116 __instance,bool __result)=>After(__instance.__4__this,__instance._skillCommandReelIn,__result,__instance.__1__state);
    }
    [HarmonyPatch(typeof(MiCoroutine),"stopInternal")]
    internal static class StopHook
    {
        private static void Prefix(MiCoroutine __instance,out SkillCommand __state)=>__state=CommandOf(__instance);
        private static void Postfix(SkillCommand __state){OnStopped(__state);FishingPullSync.OnStopped(__state);}
    }
}
