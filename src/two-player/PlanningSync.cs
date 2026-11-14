using System;
using System.IO;
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
        enabled=false;requestActor=executedActor=null;world=barrier=executed=seenExecution=request=lastGuestRequest=applied=null;revision=guestRevision=0;guestClient=null;client=Guid.NewGuid().ToString("N");next=0;
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
    {if(Pending)HostLoadingView.Draw("影子模式：正在确认双方指令，随后一起执行…\nShadow Mode: synchronizing queued orders before execution…");}
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
                string path=PathFor("guest-planning.json");
                if(File.Exists(path)&&new FileInfo(path).Length<=30000&&DateTime.UtcNow-File.GetLastWriteTimeUtc(path)<TimeSpan.FromSeconds(3))
                {
                    using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                    using var reader=new StreamReader(stream);
                    var incoming=Read(reader.ReadToEnd());
                    if(incoming!=null&&incoming.world==world&&incoming.executed==executed&&(incoming.client!=guestClient||incoming.revision>guestRevision))
                    {
                        string fingerprint=JsonSerializer.Serialize(incoming.queues);
                        bool owned=Array.TrueForAll(incoming.queues,q=>NativeInput.Probe.PlanningGuestOwns(MoveProbe.CrewById(q.actor)));
                        bool accepted=owned&&(fingerprint==applied||PlanningNative.Replace(mode,incoming.queues,a=>NativeInput.Probe.PlanningGuestOwns(a),true));
                        if(accepted)
                        {
                            if(applied!=fingerprint)MoveProbePlugin.ProbeLog.LogInfo("Planning host accepted queues="+incoming.queues.Length+" revision="+incoming.revision);
                            applied=fingerprint;guestClient=incoming.client;guestRevision=incoming.revision;
                            if(incoming.request!=null&&incoming.request!=lastGuestRequest)
                            {if(incoming.requestActor!=null&&!NativeInput.Probe.PlanningGuestOwns(MoveProbe.CrewById(incoming.requestActor)))throw new InvalidDataException("Cannot execute another player queue");lastGuestRequest=incoming.request;if(barrier==null){requestActor=incoming.requestActor;barrier=Guid.NewGuid().ToString("N");}}
                            if(barrier!=null&&incoming.barrier==barrier)
                            {
                                string commit=barrier;
                                MoveProbePlugin.ProbeLog.LogInfo("Planning native execute before="+JsonSerializer.Serialize(Capture(mode,true)));
                                if(!PlanningWire.Valid(Capture(mode,true)))throw new InvalidDataException("Combined planning queue exceeds synchronization bounds");
                                Executing=true;
                                try{if(requestActor==null)mode.execute();else {var actor=MoveProbe.CrewById(requestActor);if(actor==null)throw new InvalidDataException("Planned actor disappeared");mode.execute(actor);}}finally{Executing=false;}
                                MoveProbePlugin.ProbeLog.LogInfo("Planning native execute after="+JsonSerializer.Serialize(Capture(mode,true)));
                                executedActor=requestActor;requestActor=null;executed=commit;barrier=null;applied=null;
                                MoveProbePlugin.ProbeLog.LogInfo("Planning queues committed "+commit);
                            }
                        }
                    }
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
    private static void Warn(float now,string message)
    {if(now>=nextError){nextError=now+5;MoveProbePlugin.ProbeLog.LogWarning(message);}}
}
