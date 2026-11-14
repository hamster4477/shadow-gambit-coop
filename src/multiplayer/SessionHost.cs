using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

// A local gateway request captures a NEW native checkpoint. The simulation is
// held after serialization, before the native save lock is released. File IO
// and compression belong to the gateway, never Unity's update loop.
internal sealed class SessionHost
{
    private const int JoinPause = 1 << 29;
    private static SessionHost active;
    private static SessionHost host;
    private static bool hooks;
    private string generation, phase="idle", failure, lastGeneration;
    private string receiptGeneration;
    private readonly System.Collections.Generic.HashSet<string> framePeers=new(StringComparer.Ordinal);
    private readonly System.Collections.Generic.HashSet<string> requiredFramePeers=new(StringComparer.Ordinal);
    private SaveGameHolder captured;
    private SaveGameHolder lastLoadedCheckpoint;
    private string lastLoadedWorld;
    private bool issued, capturedOK, written, held;
    private bool directReload, reloadComplete;
    private bool entering,hub,entryProfilePending;
    private bool preparedCheckpoint,preparation;
    private float LeaseGrace=>preparedCheckpoint?600f:15f;
    private DateTime profileStamp;
    private EnemyRewind.Frame rewind;
    private float next, deadline, saveStarted, lastLeaseSeen;
    private float lastPeerSeen=float.NegativeInfinity;
    internal bool HasRecentPeer=>Time.realtimeSinceStartup-lastPeerSeen<=30f;
    private string profilePath, savePath, mission, gameVersion,world;
    internal string Generation=>generation;
    internal bool ServingCheckpoint=>phase=="ready"||phase=="attaching";
    internal string LoadingStage=>phase switch {
        "waiting"=>"正在准备客机接档…\nPreparing the guest's checkpoint…",
        "profile-saving" or "entry-saving" or "saving" or "reloading"=>"正在准备共享场景…\nPreparing the shared scene…",
        "ready"=>"客机正在接收或加载场景，请稍候…\nGuest is receiving or loading the scene. Please wait…",
        "attaching"=>"客机已加载，正在确认同步…\nGuest has loaded. Confirming synchronization…",
        _=>null
    };
    internal static bool WaitingEntryContinue=>active!=null&&active.entering&&
        (active.phase=="entry-saving"||active.phase=="reloading"||active.phase=="ready"||active.phase=="attaching");
    internal void ObserveHosting(bool hosting){if(hosting){EnsureHooks();host=this;}else if(host==this)host=null;}
    private static void EnsureHooks(){if(!hooks){new Harmony("local.shadowgambit.sessionhost").PatchAll(typeof(Capture));new Harmony("local.shadowgambit.sessionhost.write").PatchAll(typeof(Written));new Harmony("local.shadowgambit.sessionhost.unlock").PatchAll(typeof(Unlock));new Harmony("local.shadowgambit.sessionhost.load").PatchAll(typeof(Loading));new Harmony("local.shadowgambit.sessionhost.reload").PatchAll(typeof(ReloadAccepted));hooks=true;}}
    internal void PeerSawCheckpoint()
    {
        lastPeerSeen=Time.realtimeSinceStartup;
        if(phase!="attaching")return;
        if(receiptGeneration!=generation){receiptGeneration=generation;framePeers.Clear();}
        if(!preparation)
        {
            if(MultiPeerContext.Valid(MultiPeerContext.Current))framePeers.Add(MultiPeerContext.Current);
            if(!requiredFramePeers.IsSubsetOf(framePeers))return;
        }
        Release();phase="released";active=null;
        MoveProbePlugin.ProbeLog.LogInfo("Session host: all participants received checkpoint frame "+generation);
    }
    internal static string DirectoryPath=>Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","session-next","runtime","control");
    private static void Write(string name,object value)
    {
        Directory.CreateDirectory(DirectoryPath);
        string target=Path.Combine(DirectoryPath,name);
        File.WriteAllText(target+".new",JsonSerializer.Serialize(value));
        if(File.Exists(target))File.Replace(target+".new",target,null);
        else File.Move(target+".new",target);
    }
    private static bool TransientFileError(Exception error)
    {
        int code=error.HResult&0xffff;
        return (error is IOException||error is UnauthorizedAccessException)&&(code==2||code==5||code==32||code==33);
    }
    internal void Cancel(string reason="Cancelled by host")
    {
        if(generation!=null&&phase!="released"&&phase!="failed") {phase="failed";failure=reason;}
        Release();
        if(active==this)active=null;
        if(directReload)SessionWorld.CancelReservation();
    }
    private void Release(){if(held){MiTime.setPause(false,JoinPause);held=false;}}
    private void Hold(){MiTime.setPause(true,JoinPause);held=true;}
    private void Fail(string reason){Cancel(reason);MoveProbePlugin.ProbeLog.LogWarning("Session host: "+reason);}
    internal static void BeginNativeEntry()
    {
        if(TrainingSession.Suspended)return;
        var c=host;if(c==null||c.phase=="saving"||c.phase=="entry-saving"||c.phase=="reloading"||c.ServingCheckpoint)return;
        try {
            var mission=SceneLookup.Find<MiMissionHandler>()?.currentMissionData;
            string world=SessionWorld.Current;if(mission==null||world==null)return;
            c.preparation=false;c.preparedCheckpoint=false;c.Release();c.generation=c.lastGeneration=Guid.NewGuid().ToString("N");
            c.world=world;c.mission=mission.guid.iValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            c.profilePath=c.savePath=c.gameVersion=null;c.rewind=null;c.failure=null;
            c.entryProfilePending=false;c.hub=false;c.entering=c.directReload=true;c.reloadComplete=false;c.phase="entry-saving";c.issued=false;c.captured=null;
            c.saveStarted=c.lastLeaseSeen=Time.realtimeSinceStartup;c.deadline=c.saveStarted+600f;c.next=0;active=c;
            c.Publish(false);MoveProbePlugin.ProbeLog.LogInfo("Session host: capturing original entry checkpoint before intro "+c.generation);
        }catch(Exception ex){c.Fail(ex.Message);}
    }
    internal static void HoldNativeEntry()
    {
        var c=active;if(c==null||!c.entering||c.phase!="entry-saving"||c.entryProfilePending)return;
        try{c.Hold();}catch(Exception ex){c.Fail(ex.Message);}
    }
    internal static void BeginPreparedCheckpoint()
    {
        var c=host;if(c==null||c.phase=="saving"||c.phase=="entry-saving"||c.phase=="reloading"||c.ServingCheckpoint)return;
        var mission=SceneLookup.Find<MiMissionHandler>()?.currentMissionData;
        string epoch=SessionWorld.Current;if(mission==null||epoch==null)return;
        c.Release();c.generation=c.lastGeneration=Guid.NewGuid().ToString("N");
        c.world=epoch;c.mission=mission.guid.iValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        c.profilePath=c.savePath=c.gameVersion=c.failure=null;c.rewind=null;
        c.entering=c.hub=c.entryProfilePending=c.reloadComplete=false;
        c.preparation=false;c.directReload=c.preparedCheckpoint=c.issued=true;c.capturedOK=c.written=false;c.captured=null;
        c.phase="saving";c.saveStarted=c.lastLeaseSeen=Time.realtimeSinceStartup;c.deadline=c.saveStarted+600f;c.next=0;active=c;
        c.Publish(false);MoveProbePlugin.ProbeLog.LogInfo("Session host: capturing native post-selection checkpoint "+c.generation);
    }
    internal static void FailPreparedCheckpoint(string reason)
    {var c=active;if(c?.preparedCheckpoint==true&&c.phase=="saving")c.Fail(reason);}
    internal static void FailNativeEntry(string reason)
    {var c=active;if(c!=null&&c.entering&&c.phase=="entry-saving")c.Fail(reason);}
    internal static void NativeEntryWritten(SaveGameHolder holder)
    {
        var c=active;if(c==null||!c.entering||c.phase!="entry-saving"||c.entryProfilePending)return;
        try {
            if(holder?.Header==null||holder.Header.m_iMissionGuid.ToString(System.Globalization.CultureInfo.InvariantCulture)!=c.mission)return;
            if(holder.bHeaderOrDataCorrupt||!holder.m_bDataIsWrittenToStorage)throw new InvalidDataException("Native entry checkpoint did not persist");
            if(!c.held||SessionWorld.Current!=c.world)throw new InvalidOperationException("Entry checkpoint lost its scene barrier");
            c.profilePath=SaveGameManager.m_instance.saveProfile.saveGameUserHolder.sPath;
            c.savePath=holder.sPath;c.gameVersion=holder.Header.strApplicationVersion;
            c.profileStamp=File.GetLastWriteTimeUtc(c.profilePath);
            c.entryProfilePending=true;c.saveStarted=Time.realtimeSinceStartup;
            // Keep the native Continue gate closed, but allow the original
            // progress writer to finish before exporting this new mission.
            c.Release();SaveGameManager.m_instance.saveProfile.saveSaveGameUser();SaveProcessManager.instance.StartCoroutine(SaveGameManager.m_instance.saveProfile.coro_saveSaveGameUser());
            c.Publish(false);MoveProbePlugin.ProbeLog.LogInfo("Session host: entry checkpoint persisted; refreshing mission progress "+c.savePath);
        }catch(Exception ex){c.Fail(ex.Message);}
    }
    internal static bool CanAdmitJoin(bool loading, bool processing, bool hasWorld, bool hasMission,
        bool playable, bool ship, bool preparationStage) =>
        !loading && !processing && hasWorld && hasMission && (playable || ship || preparationStage);
    private void Publish(bool available)=>Write("host-session-status.json",new{schema=1,generation,phase,available,error=failure,held,
        profilePath,savePath,mission,gameVersion,world,directReload,entering,hub,preparation,rewind,updatedUtc=DateTime.UtcNow.ToString("O")});
    // This runs only after native validation has accepted an in-place load.
    // Opening/cancelling the old-memory confirmation does not create a transfer.
    private void BeginReload(SaveGameHolder holder)
    {
        if((phase!="released"&&phase!="idle"&&phase!="failed")||(!HasRecentPeer&&!MissionEntrySync.PeerPresent())||holder?.Header==null||holder.bHeaderOrDataCorrupt||!holder.m_bDataIsWrittenToStorage)return;
        var current=SceneLookup.Find<MiMissionHandler>()?.currentMissionData;
        bool sameMission=current!=null&&current.guid.iValue==holder.Header.m_iMissionGuid;
        string profile=SaveGameManager.m_instance.saveProfile.saveGameUserHolder.sPath;
        string relative=Path.GetRelativePath(Path.GetDirectoryName(profile),holder.sPath).Replace('\\','/');
        // Special hidden/restart saves keep the existing post-load capture path.
        if(!Regex.IsMatch(relative,@"\A(manualSave|quickSave)/[A-Za-z0-9_-]{1,180}\.save\z"))return;
        preparation=false;preparedCheckpoint=false;generation=lastGeneration=Guid.NewGuid().ToString("N");world=Guid.NewGuid().ToString("N");
        profilePath=profile;
        savePath=holder.sPath;mission=holder.Header.m_iMissionGuid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        gameVersion=holder.Header.strApplicationVersion;
        entering=!sameMission;rewind=sameMission?EnemyRewind.ForCheckpoint(holder):null;
        hub=false;directReload=true;reloadComplete=false;failure=null;phase="reloading";active=this;
        lastLeaseSeen=Time.realtimeSinceStartup;deadline=lastLeaseSeen+600f;next=0;
        SessionWorld.Reserve(world);
        Publish(false);
        MoveProbePlugin.ProbeLog.LogInfo("Session host: early reload checkpoint "+generation+" "+savePath);
    }
    internal static void NativeLoaded(SaveGameHolder holder,SaveProcessManager.LoadProcessResult result)
    {
        if(host!=null){host.lastLoadedCheckpoint=result==SaveProcessManager.LoadProcessResult.Success?holder:null;host.lastLoadedWorld=SessionWorld.Current;}
        var c=active;if(c==null||!c.directReload||c.phase!="reloading")return;
        try
        {
            if(result!=SaveProcessManager.LoadProcessResult.Success||holder==null||!string.Equals(holder.sPath,c.savePath,StringComparison.OrdinalIgnoreCase))
            {c.Fail("Host did not load the announced checkpoint");return;}
            c.reloadComplete=true;c.Hold();
        }
        catch(Exception ex){c.Fail(ex.Message);}
    }
    internal bool Tick(bool hostAvailable,Action<string> report)
    {
        // Do not defer holding/releasing to the coarse filesystem poll.
        bool busy=phase=="profile-saving"||phase=="entry-saving"||phase=="saving"||phase=="ready"||phase=="attaching"||phase=="waiting"||phase=="reloading";
        if(Time.realtimeSinceStartup<next)return busy;
        next=Time.realtimeSinceStartup+0.25f;
        try
        {
            string requestPath=Path.Combine(DirectoryPath,"host-session-request.json");
            if(!busy&&File.Exists(requestPath)&&new FileInfo(requestPath).Length<=2048&&
                DateTime.UtcNow-File.GetLastWriteTimeUtc(requestPath)<TimeSpan.FromSeconds(15))
            {
                using var request=JsonDocument.Parse(File.ReadAllText(requestPath));
                string candidate=request.RootElement.GetProperty("generation").GetString();
                if(Regex.IsMatch(candidate??"","^[0-9a-f]{32}$")&&candidate!=lastGeneration)
                {
                    File.Move(requestPath,requestPath+".accepted",true);
                    generation=lastGeneration=candidate;phase="waiting";failure=null;
                    preparedCheckpoint=false;issued=capturedOK=written=false;captured=null;
                    directReload=reloadComplete=entering=hub=preparation=false;rewind=null;
                    profilePath=savePath=mission=gameVersion=world=null;
                    lastLeaseSeen=Time.realtimeSinceStartup;
                    deadline=Time.realtimeSinceStartup+600f;busy=true;
                    MoveProbePlugin.ProbeLog.LogInfo("Session host: requested "+generation);
                }
            }
            if(busy)
            {
                string lease=Path.Combine(DirectoryPath,"host-session-lease.json");
                if((phase=="saving"||phase=="entry-saving"||phase=="profile-saving")&&Time.realtimeSinceStartup-saveStarted>45f)Fail("Native checkpoint save timed out");
                else if(Time.realtimeSinceStartup>deadline)Fail("Guest join timed out");
                else if(!File.Exists(lease))
                {
                    // Atomic replacement can briefly hide the name. Retry next tick,
                    // but keep the same bounded watchdog for a stopped gateway.
                    if(Time.realtimeSinceStartup-lastLeaseSeen>LeaseGrace)Fail("Join gateway stopped responding");
                    else if(!preparedCheckpoint)return true;
                }
                else
                {
                    using var stream=new FileStream(lease,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                    using var command=JsonDocument.Parse(stream);
                    var root=command.RootElement;
                    if(root.GetProperty("generation").GetString()!=generation)
                    {if(!directReload||Time.realtimeSinceStartup-lastLeaseSeen>LeaseGrace)Fail("Join generation changed");}
                    else if(root.TryGetProperty("cancel",out var cancel)&&cancel.GetBoolean())
                    {
                        string reason=root.TryGetProperty("error",out var why)&&why.ValueKind==JsonValueKind.String?why.GetString():null;
                        Fail(reason??"Guest cancelled join");
                    }
                    else if(DateTime.UtcNow-File.GetLastWriteTimeUtc(lease)>TimeSpan.FromSeconds(15))Fail("Join gateway stopped responding");
                    else
                    {
                        lastLeaseSeen=Time.realtimeSinceStartup;
                        requiredFramePeers.Clear();
                        if(root.TryGetProperty("readyPeers",out var members)&&members.ValueKind==JsonValueKind.Array)
                            foreach(var member in members.EnumerateArray())
                                if(member.ValueKind==JsonValueKind.String&&MultiPeerContext.Valid(member.GetString()))requiredFramePeers.Add(member.GetString());
                        if(phase=="ready"&&root.TryGetProperty("attach",out var attach)&&attach.GetBoolean())
                        {phase="attaching";if(preparation)PeerSawCheckpoint();else report("Guest checkpoint loaded. Waiting for first synchronized frame...");}
                        else if(phase=="attaching"&&receiptGeneration==generation&&requiredFramePeers.IsSubsetOf(framePeers))PeerSawCheckpoint();
                    }
                }
            }
            // At native Continue the loaded checkpoint is still exact. Reuse
            // it rather than waiting for gameplay and requesting a quicksave.
            if(phase=="waiting"&&!SaveProcessManager.bLoading&&!SaveProcessManager.bProcessing)
            {
                var loader=SceneLookup.Find<MiSceneLoaderTactics>();
                var checkpoint=lastLoadedCheckpoint;
                var current=SceneLookup.Find<MiMissionHandler>()?.currentMissionData;
                if(loader?.m_goRootContinue?.activeInHierarchy==true&&checkpoint?.Header!=null&&
                    !checkpoint.bHeaderOrDataCorrupt&&checkpoint.m_bDataIsWrittenToStorage&&
                    current?.guid.iValue==checkpoint.Header.m_iMissionGuid&&File.Exists(checkpoint.sPath)&&SessionWorld.Current!=null&&lastLoadedWorld==SessionWorld.Current)
                {
                    profilePath=SaveGameManager.m_instance.saveProfile.saveGameUserHolder.sPath;
                    savePath=checkpoint.sPath;world=SessionWorld.Current;
                    mission=checkpoint.Header.m_iMissionGuid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    gameVersion=checkpoint.Header.strApplicationVersion;
                    entering=directReload=true;hub=false;rewind=null;active=this;
                    Hold();phase="ready";
                    MoveProbePlugin.ProbeLog.LogInfo("Session host: joining at native Continue using loaded checkpoint "+generation);
                }
            }
            // Ship progress does not require a playable crew or game-time tick.
            // Native loading/processing must still finish before exporting it.
            var admissionMission=SceneLookup.Find<MiMissionHandler>();
            bool preparationJoin=admissionMission?.currentMissionData!=null&&admissionMission.currentMissionData.guid.iValue!=-379511569&&SessionWorld.Current!=null&&admissionMission.eCurrentMissionState.ToString().StartsWith("Init_",StringComparison.Ordinal);
            bool shipJoinReady=SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue==-379511569&&SessionWorld.Current!=null;
            if(phase=="waiting"&&!SaveProcessManager.bLoading&&!SaveProcessManager.bProcessing&&
                (preparationJoin||shipJoinReady||hostAvailable&&Time.timeScale>0f&&!MiCutsceneHandler.bCutsceneRunning))
            {
                var hud=UnityEngine.Object.FindObjectOfType<UIMouseHUD>();
                if(hud!=null||shipJoinReady||preparationJoin)
                {
                    EnsureHooks();
                    world=SessionWorld.Current;
                    if(world==null)throw new InvalidOperationException("Host scene is not ready for checkpoint capture");
                    active=this;issued=true;phase="saving";saveStarted=Time.realtimeSinceStartup;
                    report("Preparing host checkpoint for guest...");
                    var nativeMission=SceneLookup.Find<MiMissionHandler>();
                    preparation=preparationJoin;hub=preparation||nativeMission?.currentMissionData?.guid.iValue==-379511569;
                    if(hub){
                        var profile=SaveGameManager.m_instance.saveProfile;
                        profilePath=profile.saveGameUserHolder.sPath;savePath=profilePath;
                        mission="-379511569";gameVersion=MiVersion.Version.Display;
                        profileStamp=File.GetLastWriteTimeUtc(profilePath);
                        // The native progress writer may yield on game time.
                        // Hold only after it has written, as for task saves.
                        phase="profile-saving";profile.saveSaveGameUser();SaveProcessManager.instance.StartCoroutine(profile.coro_saveSaveGameUser());
                        report("Publishing host progress for the Red Marley...");
                    }else if(nativeMission?.bCanQuicksave==true)hud.quickSaveButtonPressed();
                    else Fail("This task has no current save entry; waiting for a native entry checkpoint is required");
                }
            }
            if(phase=="entry-saving"&&entryProfilePending&&!SaveProcessManager.bProcessing&&!SaveProcessManager.bAsyncWriteOperationRunning&&
                Time.realtimeSinceStartup-saveStarted>=1f&&File.GetLastWriteTimeUtc(profilePath)!=profileStamp){
                if(world!=SessionWorld.Current)throw new InvalidOperationException("Host left entry scene during progress export");
                entryProfilePending=false;Hold();phase="ready";
                MoveProbePlugin.ProbeLog.LogInfo("Session host: entry checkpoint and mission progress ready "+generation);
            }
            if(phase=="profile-saving"&&!SaveProcessManager.bProcessing&&!SaveProcessManager.bAsyncWriteOperationRunning&&
                (preparation||SceneLookup.Find<Mimimi.HUB.HUBLogic>()?.characters?.m_bRoutineSpawningProcessEnded==true)&&
                Time.realtimeSinceStartup-saveStarted>=1f&&
                File.GetLastWriteTimeUtc(profilePath)!=profileStamp){
                if(world!=SessionWorld.Current)throw new InvalidOperationException("Host left the ship during profile export");
                Hold();phase="ready";report("Host ship progress ready. Waiting for guest entry...");
                MoveProbePlugin.ProbeLog.LogInfo("Session host: ship profile ready "+generation);
            }
            if(phase=="saving"&&capturedOK&&written&&!SaveProcessManager.bProcessing&&!SaveProcessManager.bAsyncWriteOperationRunning)
            {
                if(!held||!MiTime.isPaused(JoinPause)||world!=SessionWorld.Current)throw new InvalidOperationException("Checkpoint pause or scene was lost");
                if(captured==null||captured.bHeaderOrDataCorrupt||!captured.m_bDataIsWrittenToStorage||captured.Header==null)
                    throw new InvalidDataException("Native checkpoint is not written");
                profilePath=SaveGameManager.m_instance.saveProfile.saveGameUserHolder.sPath;
                savePath=captured.sPath;mission=captured.Header.m_iMissionGuid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                gameVersion=captured.Header.strApplicationVersion;phase="ready";
                report("Waiting for guest to load host checkpoint. F11 cancels.");
                MoveProbePlugin.ProbeLog.LogInfo("Session host: checkpoint ready "+generation);
            }
            if(phase=="reloading"&&reloadComplete&&!SaveProcessManager.bProcessing&&!SaveProcessManager.bLoading)
            {
                if(world!=SessionWorld.Current)throw new InvalidOperationException("Host reload scene identity changed");
                Hold();phase="ready";report("Host checkpoint loaded. Waiting for guest reload...");
            }
            // The native loader may reset pause bits when publishing Continue.
            // The button gate remains closed; restore our bit in that same
            // entry scene rather than treating native initialization as drift.
            if(entering&&ServingCheckpoint&&!SaveProcessManager.bLoading&&world==SessionWorld.Current)Hold();
            if(ServingCheckpoint&&(!held||!MiTime.isPaused(JoinPause)||SaveProcessManager.bLoading||world!=SessionWorld.Current))Fail("Host checkpoint changed during join");
            Publish(CanAdmitJoin(SaveProcessManager.bLoading,SaveProcessManager.bProcessing,
                SessionWorld.Current!=null,admissionMission?.currentMissionData!=null,
                hostAvailable,shipJoinReady,preparationJoin));
        }
        catch(Exception ex) when(TransientFileError(ex))
        {
            // Never sleep on Unity's update thread or abandon a valid checkpoint
            // just because a gateway reader briefly holds a Windows file handle.
            if(busy&&Time.realtimeSinceStartup-lastLeaseSeen>15f)Fail("Join gateway stopped responding");
        }
        catch(Exception ex){Fail(ex.Message);try{Write("host-session-status.json",new{schema=1,generation,phase,error=failure,available=false,held=false});}catch{}}
        return phase=="profile-saving"||phase=="entry-saving"||phase=="waiting"||phase=="saving"||phase=="reloading"||ServingCheckpoint;
    }
    [HarmonyPatch(typeof(SaveLoadExecuteAndListener),"createSaveGameProcessResult")]
    internal static class Capture
    {
        internal static void Postfix(SaveGameHolder __0,SaveProcessManager.SaveProcessResult __1)
        {
            var c=active;if(c==null||!c.issued||c.phase!="saving")return;
            try
            {
                if(__1!=SaveProcessManager.SaveProcessResult.Success||__0==null){c.Fail("Native save capture failed: "+__1);return;}
                c.captured=__0;c.capturedOK=true;c.Hold();
            }
            catch(Exception ex){c.Fail(ex.Message);}
        }
    }
    [HarmonyPatch(typeof(SaveLoadExecuteAndListener),"createSaveGameProcessWriteResult")]
    internal static class Written
    {
        internal static void Postfix(SaveGameHolder __0,SaveProcessManager.SaveProcessWriteResult __1)
        {
            var c=active;if(c==null||c.phase!="saving"||__0==null)return;
            try
            {
                if(c.captured==null||!string.Equals(__0.sPath,c.captured.sPath,StringComparison.OrdinalIgnoreCase))return;
                if(__1!=SaveProcessManager.SaveProcessWriteResult.Success)c.Fail("Native save write failed: "+__1);
                else c.written=true;
            }
            catch(Exception ex){c.Fail(ex.Message);}
        }
    }
    [HarmonyPatch(typeof(SaveProcessManager),"afterProcessUnlockGame")]
    internal static class Unlock
    {
        private static void Prefix(){var c=active;if(c!=null&&(c.ServingCheckpoint||c.phase=="saving"&&c.issued||c.phase=="reloading"&&c.reloadComplete)){try{c.Hold();}catch(Exception ex){c.Fail(ex.Message);}}}
    }
    [HarmonyPatch(typeof(SaveProcessManager),"tryLoadSaveGame")]
    internal static class ReloadAccepted
    {
        private static void Postfix(SaveGameHolder __0,SaveProcessManager.LoadResult __result)
        {
            if(__result!=SaveProcessManager.LoadResult.Success||host==null)return;
            try{host.BeginReload(__0);}catch(Exception ex){host.Fail("Early reload unavailable: "+ex.Message);}
        }
    }
    [HarmonyPatch(typeof(SaveProcessManager),"beforeProcessLockGame")]
    internal static class Loading
    {
        private static void Prefix()
        {
            var c=active;
            // Saving temporarily locks the game too, but does not replace
            // the loaded world or invalidate the checkpoint sent to a guest.
            // A genuine load is also checked against world identity in Tick.
            if(c!=null&&c.ServingCheckpoint&&SaveProcessManager.bLoading)
                c.Fail("Host started another load");
        }
    }
}
