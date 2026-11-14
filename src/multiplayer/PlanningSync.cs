using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

internal static class PlanningSync
{
    internal static bool Applying,Executing;
    private static bool enabled,host;
    private static string world,barrier,executed,seenExecution,request,lastGuestRequest,applied;
    private static long revision,guestRevision;
    private static string requestActor,executedActor;
    private static string client=Guid.NewGuid().ToString("N"),guestClient;
    private static float next,nextError;
    private static Task<string> send;
    private static CancellationTokenSource cancel;
    private sealed class PeerPlan {internal string client,applied,request,barrier;internal long revision;internal readonly HashSet<string> actors=new(StringComparer.Ordinal);}
    private static readonly Dictionary<string,PeerPlan> peers=new();
    internal static bool Pending=>enabled&&(barrier!=null||request!=null);
    internal static bool InterceptExecute(MiCharacter actor=null)
    {
        if(!enabled||TrainingSession.Suspended||Executing||Applying)return false;
        if(Pending)return true;
        requestActor=actor==null?null:GuestMove.Identity(GuestCrewSelection.Source(actor));
        if(host){if(barrier==null)barrier=Guid.NewGuid().ToString("N");}
        else if(request==null)request=Guid.NewGuid().ToString("N");
        next=0;return true;
    }
    [HarmonyPatch(typeof(PlanningMode),"onCommandPlanningInternal")]
    internal static class Edit
    {private static bool Prefix()=>!Pending||Applying||Executing;}
    [HarmonyPatch(typeof(PlanningMode),"executeInternal",new[]{typeof(MiCharacter),typeof(PlanningModeCommand)})]
    internal static class ExecuteScope
    {
        private static void Prefix(out bool __state){__state=Executing;if(enabled&&host)Executing=true;}
        private static Exception Finalizer(Exception __exception,bool __state){Executing=__state;return __exception;}
    }
    [HarmonyPatch(typeof(PlanningMode),"removeLastCommand")]
    internal static class RemoveLast
    {private static bool Prefix()=>!Pending||Applying||Executing||SaveProcessManager.bLoading;}
    [HarmonyPatch(typeof(PlanningMode),"removeCommands")]
    internal static class RemoveAll
    {private static bool Prefix()=>!Pending||Applying||Executing||SaveProcessManager.bLoading;}
    [HarmonyPatch(typeof(PlanningMode),"execute",new[]{typeof(MiCharacter)})]
    internal static class ExecuteOne
    {private static bool Prefix(MiCharacter __0)=>!InterceptExecute(__0);}
    internal static void Clear()
    {
        peers.Clear();enabled=false;requestActor=executedActor=null;world=barrier=executed=seenExecution=request=lastGuestRequest=applied=null;revision=guestRevision=0;guestClient=null;client=Guid.NewGuid().ToString("N");next=0;
        cancel?.Cancel();cancel?.Dispose();cancel=null;
        if(send!=null)_=send.ContinueWith(t=>{_=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);send=null;
    }
    private static string PathFor(string name)=>Path.Combine(SessionHost.DirectoryPath,name);
    private static void Write(string name,PlanningWire.Message value)
    {
        string path=PathFor(name);Directory.CreateDirectory(SessionHost.DirectoryPath);
        File.WriteAllText(path+".new",JsonSerializer.Serialize(value));File.Move(path+".new",path,true);
    }
    private static PlanningWire.Message Read(string json)
    {if(json==null||json.Length>30000)return null;var m=JsonSerializer.Deserialize<PlanningWire.Message>(json);return PlanningWire.Valid(m)?m:null;}
    private static PlanningWire.Message Capture(PlanningMode mode,bool all)
    {
        var probe=NativeInput.Probe;
        return new PlanningWire.Message{client=client,world=world,revision=++revision,active=mode.bIsActive,requestActor=requestActor,executedActor=executedActor,request=request,barrier=barrier,executed=executed,
            queues=PlanningNative.Capture(mode,probe.PlanningRoster(),a=>all||probe.PlanningGuestOwns(a))};
    }
    internal static void Draw()
    {if(Pending)HostLoadingView.Draw("影子模式：正在确认所有玩家指令，随后一起执行…\nShadow Mode: synchronizing queued orders before execution…");}
    internal static void Tick(bool hosting,Uri room,string epoch,bool ready)
    {
        if(TrainingSession.Suspended||epoch==null||SaveProcessManager.bLoading){Clear();return;}
        if(world!=epoch||host!=hosting){Clear();world=epoch;host=hosting;cancel=new CancellationTokenSource();}
        // Ship selection rotates the gameplay pairing inside the same world.
        // Keep request/commit identities through that short gap. Clearing them
        // lets a guest execute locally and then mistakes an older commit for
        // confirmation of the new plan.
        if(!ready)return;
        enabled=true;
        var mode=SceneLookup.Find<PlanningMode>();if(mode==null)return;
        float now=Time.realtimeSinceStartup;
        try
        {
            if(!host&&send?.IsCompleted==true)
            {
                var incoming=Read(send.GetAwaiter().GetResult());send=null;
                if(incoming!=null&&incoming.world==world)
                {
                    barrier=incoming.barrier;
                    if(incoming.executed!=null&&incoming.executed!=seenExecution)
                    {
                        // The host alone executes real commands. Running them on
                        // the guest would forward a second attack/move order.
                        PlanningNative.CompletePreview(mode,incoming.executedActor,incoming.active);
                        executed=seenExecution=incoming.executed;executedActor=incoming.executedActor;requestActor=null;request=null;barrier=null;applied=null;
                    }
                    // Preview resolution must never prevent the execution
                    // acknowledgement or the guest's own queues from being sent.
                    try
                    {
                        var foreign=Array.FindAll(incoming.queues,q=>{var a=MoveProbe.CrewById(q.actor);return a!=null&&!NativeInput.Probe.PlanningGuestOwns(a);});
                        string fingerprint=JsonSerializer.Serialize(foreign);
                        if(fingerprint!=applied&&PlanningNative.Replace(mode,foreign,a=>!NativeInput.Probe.PlanningGuestOwns(a)))
                        {applied=fingerprint;MoveProbePlugin.ProbeLog.LogInfo("Planning guest preview queues="+foreign.Length+" revision="+incoming.revision);}
                    }
                    catch(Exception ex){Warn(now,"Planning preview deferred: "+ex.Message);}
                }
            }
            if(now<next)return;next=now+(mode.bIsActive||mode.bIsPlanned||Pending?.15f:1f);
            if(host)
            {
                var live=new HashSet<string>(MultiplayerPeers.Active(),StringComparer.Ordinal);
                foreach(string old in new List<string>(peers.Keys))if(!live.Contains(old))
                {
                    var stale=peers[old];
                    RetirePeerQueues(mode,old,stale.actors);
                    peers.Remove(old);
                }
                foreach(string peer in live)
                {
                    using var scope=MultiPeerContext.Scope(peer);
                    if(!peers.TryGetValue(peer,out var progress))peers[peer]=progress=new PeerPlan();
                    string path=MultiplayerPeers.PathFor(peer,"planning");
                    if(!MultiplayerPeers.Fresh(path,3,30000)){progress.barrier=null;continue;}
                    using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                    using var reader=new StreamReader(stream);
                    var incoming=Read(reader.ReadToEnd());
                    if(incoming==null||incoming.world!=world||incoming.executed!=executed){progress.barrier=null;continue;}
                    if(incoming.client==progress.client&&incoming.revision<=progress.revision)continue;
                    string fingerprint=JsonSerializer.Serialize(incoming.queues);
                    bool owned=Array.TrueForAll(incoming.queues,q=>NativeInput.Probe.PlanningGuestOwns(MoveProbe.CrewById(q.actor)));
                    bool accepted=owned&&(fingerprint==progress.applied||PlanningNative.Replace(mode,incoming.queues,a=>NativeInput.Probe.PlanningGuestOwns(a),true));
                    if(!accepted){progress.barrier=null;continue;}
                    progress.actors.Clear();foreach(var queue in incoming.queues)progress.actors.Add(queue.actor);
                    progress.applied=fingerprint;progress.client=incoming.client;progress.revision=incoming.revision;progress.barrier=incoming.barrier;
                    if(incoming.request!=null&&incoming.request!=progress.request)
                    {
                        if(incoming.requestActor!=null&&!NativeInput.Probe.PlanningGuestOwns(MoveProbe.CrewById(incoming.requestActor)))throw new InvalidDataException("Cannot execute another player queue");
                        progress.request=incoming.request;
                        if(barrier==null){requestActor=incoming.requestActor;barrier=Guid.NewGuid().ToString("N");}
                    }
                }
                bool allReady=barrier!=null;
                foreach(string peer in live)if(!peers.TryGetValue(peer,out var progress)||progress.barrier!=barrier){allReady=false;break;}
                if(allReady)
                {
                    string commit=barrier;
                    if(!PlanningWire.Valid(Capture(mode,true)))throw new InvalidDataException("Combined planning queue exceeds synchronization bounds");
                    Executing=true;
                    try{if(requestActor==null)mode.execute();else{var actor=MoveProbe.CrewById(requestActor);if(actor==null)throw new InvalidDataException("Planned actor disappeared");mode.execute(actor);}}
                    finally{Executing=false;}
                    executedActor=requestActor;requestActor=null;executed=commit;barrier=null;applied=null;
                    foreach(var progress in peers.Values){progress.applied=null;progress.barrier=null;}
                    MoveProbePlugin.ProbeLog.LogInfo("Planning queues committed "+commit+" peers="+live.Count);
                }
                var state=Capture(mode,true);if(!PlanningWire.Valid(state))throw new InvalidDataException("Planning queue exceeds synchronization bounds");
                Write("host-planning.json",state);
            }
            else if(room!=null&&send==null)
            {
                var state=Capture(mode,false);if(!PlanningWire.Valid(state))throw new InvalidDataException("Planning queue exceeds synchronization bounds");
                send=GuestMove.SelectLobby(room,JsonSerializer.Serialize(state),cancel.Token);
            }
        }
        catch(Exception ex)
        {
            if(send?.IsCompleted==true)send=null;
            Warn(now,"Planning sync waiting: "+ex);
        }
    }
    internal static void RetirePeerQueues(PlanningMode mode,string peer,IEnumerable<string> actorIds)
    {
        bool Retirable(MiCharacter actor)
        {if(actor==null)return false;string owner=NativeInput.Probe.PlanningOwner(actor);return owner==null||owner==peer;}
        var clear=new List<PlanningWire.Queue>();
        foreach(string id in actorIds)if(Retirable(MoveProbe.CrewById(id)))clear.Add(new PlanningWire.Queue{actor=id});
        // Replace processes only explicitly supplied actors. An empty manifest
        // does not remove previews. Never erase a new owner's queued orders.
        if(clear.Count>0)PlanningNative.Replace(mode,clear.ToArray(),Retirable,true);
    }
    private static void Warn(float now,string message)
    {if(now>=nextError){nextError=now+5;MoveProbePlugin.ProbeLog.LogWarning(message);}}
}
