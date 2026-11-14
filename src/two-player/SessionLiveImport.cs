using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// The worker may verify/download/stage bytes, but native containers and the
// slot swap belong to Unity's thread. Reset -> commit -> reload has no await.
internal sealed class SessionLiveImport
{
    private string operation,phase="idle",room,account,error,checkpoint,expectedWorld;
    private bool localOnly,menuOnly,resetStarted,cancelAfterIndex,returnToMenu,menuRequested;
    private bool cutsceneSkipRequested;
    private float leaveAfter;
    private bool? autoContinue;
    private int reloadMission;
    private float next,deadline;
    private IntPtr user,oldContainer;
    private SaveProfileContainer retainedContainer;
    private Task<GuestSavePrepare.PreparedCheckpoint> worker;
    private GuestSavePrepare.PreparedCheckpoint prepared;
    private CancellationTokenSource cancellation;
    private volatile string progress;
    private string shownProgress;
    private string lastWaitReason;
    private float nextWaitLog;
    internal bool Busy=>phase=="waiting-menu"||phase=="staging"||phase=="waiting-commit"||phase=="indexing";
    private bool Exclusive=>phase=="indexing"||phase=="waiting-menu"&&menuRequested||reloadMission!=0&&Busy;
    private static string Runtime=>Path.GetDirectoryName(SessionHost.DirectoryPath);
    private static string PathFor(string name)=>Path.Combine(SessionHost.DirectoryPath,name);
    private static string MenuWaitReason()
    {
        if(SaveProcessManager.bLoading)return "native-loading";
        if(SaveProcessManager.bProcessing)return "native-save-process";
        if(SaveProcessManager.bAsyncWriteOperationRunning)return "native-save-write";
        if(SceneLookup.Find<MiMissionHandler>()?.currentMissionData!=null)return "mission-still-present";
        var menu=SceneLookup.Find<UIMainMenuGUI>();
        if(menu==null)return "main-menu-not-found";
        if(!menu.gameObject.activeInHierarchy)return "main-menu-inactive";
        return menu.m_bOnOpenAfterFadeInDone?null:"main-menu-opening-or-startup-dialog";
    }
    private static bool MenuReady()=>MenuWaitReason()==null;
    private void Waiting(string reason)
    {
        if(reason==lastWaitReason&&Time.realtimeSinceStartup<nextWaitLog)return;
        lastWaitReason=reason;nextWaitLog=Time.realtimeSinceStartup+15f;
        MoveProbePlugin.ProbeLog.LogInfo("Live checkpoint wait reason="+reason+" operation="+operation+" reloadMission="+reloadMission);
    }
    private static string Account(SaveGameManager manager)
    {
        string path=manager?.saveProfile?.saveGameUserHolder?.sPath;
        if(!string.IsNullOrEmpty(path))return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path)));
        // A first-time player may have an indexed account but no active slot.
        // Use the account-owned native holder, never guess a Steam ID or select
        // an arbitrary directory. The received profile supplies mission progress.
        string independent=manager?.saveProfileContainer?.m_profileIndependentHolder?.sPath;
        if(string.IsNullOrEmpty(independent)||!string.Equals(Path.GetFileName(independent),"userData.save",StringComparison.OrdinalIgnoreCase))return null;
        return Path.GetDirectoryName(Path.GetFullPath(independent));
    }
    private void Result()
    {
        Directory.CreateDirectory(SessionHost.DirectoryPath);
        string path=PathFor("live-import-result.json");
        File.WriteAllText(path+".new",JsonSerializer.Serialize(new{schema=1,generation=operation,checkpoint,phase,error,localOnly,updatedUtc=DateTime.UtcNow.ToString("O")}));
        File.Move(path+".new",path,true);
        MoveProbePlugin.ProbeLog.LogInfo("Live checkpoint handoff: "+phase+" operation="+operation+" checkpoint="+checkpoint);
    }
    internal bool Tick(Action disconnect,Action<string> report,bool anotherTransition=false,bool nativeSceneReady=true)
    {
        if(Time.realtimeSinceStartup<next)return Exclusive;
        next=Time.realtimeSinceStartup+.1f;
        bool indexingAtEntry=phase=="indexing";
        try
        {
            if(!Busy)
            {
                string path=PathFor("live-import.json");
                if(anotherTransition||File.Exists(PathFor("session-load.json"))||!File.Exists(path))return false;
                var info=new FileInfo(path);var age=DateTime.UtcNow-info.LastWriteTimeUtc;
                if(info.Length>2048||age<TimeSpan.FromMinutes(-1)||age>TimeSpan.FromMinutes(5))throw new InvalidDataException("Expired or oversized live import request");
                using var request=JsonDocument.Parse(File.ReadAllText(path));var root=request.RootElement;
                string candidate=root.GetProperty("generation").GetString();
                if(root.GetProperty("schema").GetInt32()!=1||!Regex.IsMatch(candidate??"","\\A[0-9a-f]{32}\\z"))throw new InvalidDataException("Invalid live import generation");
                if(candidate==operation){File.Move(path,path+".duplicate",true);return false;}
                string mode=root.GetProperty("operation").GetString();
                if(mode!="join-room"&&mode!="verify-local-load"&&mode!="return-menu")throw new InvalidDataException("Unknown live import operation");
                menuOnly=mode=="return-menu";
                localOnly=mode=="verify-local-load";
                reloadMission=root.TryGetProperty("reloadMission",out var reload)?reload.GetInt32():0;
                if(reloadMission!=0&&menuOnly)throw new InvalidDataException("Invalid reload mode");
                expectedWorld=root.TryGetProperty("world",out var world)&&world.ValueKind==JsonValueKind.String?world.GetString():null;
                if(expectedWorld!=null&&!Regex.IsMatch(expectedWorld,"\\A[0-9a-f]{32}\\z"))throw new InvalidDataException("Invalid requested host scene");
                cutsceneSkipRequested=false;leaveAfter=0;
                returnToMenu=menuOnly||(root.TryGetProperty("returnToMenu",out var goMenu)&&goMenu.GetBoolean());menuRequested=false;
                autoContinue=root.TryGetProperty("autoContinue",out var enterTask)?enterTask.GetBoolean():null;
                room=localOnly?"http://127.0.0.1:38477/abcdefghijklmnopqrstuvwx":root.GetProperty("room").GetString();
                if(GuestMove.Room(room)==null)throw new InvalidDataException("Invalid live join room");
                File.Move(path,path+".accepted",true);
                operation=candidate;phase="waiting-menu";error=checkpoint=null;resetStarted=cancelAfterIndex=false;
                progress=shownProgress=lastWaitReason=null;nextWaitLog=0;deadline=Time.realtimeSinceStartup+300f;Result();
                report(menuOnly?"Host returned to the main menu. Leaving the previous task...":reloadMission!=0?"Receiving host reload checkpoint...":"Host checkpoint handoff pending. Return to the main menu to receive it.");
            }
            if(Time.realtimeSinceStartup>deadline)throw new TimeoutException("Live checkpoint handoff timed out in "+phase);
            var manager=SaveGameManager.m_instance;
            if(phase=="waiting-menu")
            {
                if(reloadMission!=0&&!anotherTransition&&!menuRequested&&SessionQuickReload.Ready(reloadMission))
                {disconnect();SessionQuickReload.Hold();menuRequested=true;}
                if(!anotherTransition&&nativeSceneReady&&returnToMenu&&!menuRequested&&SceneLookup.Find<MiMissionHandler>()?.currentMissionData!=null&&
                    !SaveProcessManager.bProcessing&&!SaveProcessManager.bAsyncWriteOperationRunning&&!SaveProcessManager.bLoading)
                {
                    if(MiCutsceneHandler.bCutsceneRunning){
                        // Never tear down live timeline UI. A confirmed remote
                        // departure may request the original skip once; wait
                        // for native sequence completion before unloading.
                        if(!localOnly&&(menuOnly||expectedWorld!=null)&&!cutsceneSkipRequested){
                            var cutscene=UnityEngine.Object.FindObjectOfType<MiCutsceneHandler>();
                            if(cutscene!=null&&cutscene.bSkippable){
                                cutsceneSkipRequested=true;leaveAfter=Time.realtimeSinceStartup+1f;
                                report("房主已离开，正在结束客机当前过场…");
                                cutscene.skipCutscene();
                            }
                        }
                        return false;
                    }
                    if(Time.realtimeSinceStartup<leaveAfter)return false;
                    disconnect();menuRequested=true;MiMissionHandler.goBackToMainMenu();
                    report(menuOnly?"Returning to the host's main menu...":"Following host scene. Returning to the main menu for checkpoint handoff...");return true;
                }
                if(anotherTransition){Waiting("another-coop-transition");return menuRequested;}
                if(reloadMission!=0){if(!menuRequested||!SessionQuickReload.Ready(reloadMission)){Waiting("same-mission-not-ready");return menuRequested;}}
                else {string reason=MenuWaitReason();if(reason!=null){Waiting(reason);return menuRequested;}}
                if(menuOnly){disconnect();phase="menu-ready";Result();report("Host returned to the main menu. Waiting for the next task...");return false;}
                if(manager==null||!manager.bLoadedAllSaveFiles||manager.m_gameUserOwningSaveFiles==null){Waiting("native-account-index");return menuRequested;}
                account=Account(manager);if(account==null){Waiting("native-account-path");return false;}
                user=manager.m_gameUserOwningSaveFiles.Pointer;
                // A newly created profile may have no mission saves. Old
                // checkpoint headers describe their writer, not this client.
                // Display excludes the platform suffix carried by Full.
                string nativeVersion=MiVersion.Version.Display;
                if(string.IsNullOrWhiteSpace(nativeVersion)||nativeVersion.Length>128)throw new InvalidDataException("Native game version is unavailable");
                MoveProbePlugin.ProbeLog.LogInfo("Live checkpoint client version: "+nativeVersion);
                cancellation=new CancellationTokenSource();var token=cancellation.Token;
                string stagedAccount=account,stagedRoom=room,runtime=Runtime,id=operation,worldToReceive=expectedWorld;bool diagnostic=localOnly;
                worker=Task.Run(()=>diagnostic?StageLocal(runtime,id,stagedAccount,stagedRoom,nativeVersion,token):
                    GuestSavePrepare.PrepareLive(stagedRoom,stagedAccount,runtime,nativeVersion,s=>progress=s,token,worldToReceive));
                phase="staging";deadline=Time.realtimeSinceStartup+240f;Result();return reloadMission!=0;
            }
            if(phase=="staging")
            {
                if(progress!=null&&shownProgress!=progress){shownProgress=progress;report(shownProgress);}
                if(!worker.IsCompleted)return reloadMission!=0;
                var finished=worker;worker=null;prepared=finished.GetAwaiter().GetResult();checkpoint=prepared.Generation;
                phase="waiting-commit";Result();
            }
            if(phase=="waiting-commit")
            {
                if(reloadMission!=0)
                {
                    if(!SessionQuickReload.Ready(reloadMission))throw new InvalidOperationException("Mission changed during reload transfer");
                    if(prepared.Mission!=reloadMission.ToString(System.Globalization.CultureInfo.InvariantCulture))throw new InvalidDataException("Reload checkpoint belongs to another mission");
                    prepared.CommitReload(()=>SessionQuickReload.Ready(reloadMission),cancellation.Token,!localOnly,autoContinue);
                    prepared.Dispose();prepared=null;cancellation.Dispose();cancellation=null;
                    phase="ready-to-load";Result();report("Host reload checkpoint verified. Loading in the current mission...");return false;
                }
                if(anotherTransition||!MenuReady()||manager==null||!manager.bLoadedAllSaveFiles)return false;
                if(manager.m_gameUserOwningSaveFiles==null||manager.m_gameUserOwningSaveFiles.Pointer!=user||
                    !string.Equals(account,Account(manager),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Native account changed during download");
                if(File.Exists(PathFor("session-load.json")))throw new InvalidOperationException("Another checkpoint handoff became pending");
                retainedContainer=manager.saveProfileContainer;
                if(retainedContainer==null)throw new InvalidOperationException("Native save index is unavailable");
                oldContainer=retainedContainer.Pointer;
                disconnect();
                prepared.CommitWhileSuspended(()=>MenuReady()&&manager.bLoadedAllSaveFiles,
                    ()=>{resetStarted=true;phase="indexing";manager.resetAllSaveFiles();},
                    ()=>manager.loadSaveFilesForPrimaryGameUser(),cancellation.Token,!localOnly,autoContinue);
                prepared.Dispose();prepared=null;cancellation.Dispose();cancellation=null;
                phase="indexing";deadline=Time.realtimeSinceStartup+60f;Result();
                report("Host checkpoint installed. Refreshing the game's save index...");return true;
            }
            if(phase=="indexing")
            {
                if(manager==null||!manager.bLoadedAllSaveFiles||SaveProcessManager.bProcessing||SaveProcessManager.bAsyncWriteOperationRunning)return true;
                if(!MenuReady()||manager.m_gameUserOwningSaveFiles==null||manager.m_gameUserOwningSaveFiles.Pointer!=user||
                    manager.saveProfileContainer==null||manager.saveProfileContainer.Pointer==oldContainer)
                    throw new InvalidOperationException("Native save index did not rebuild for the same user");
                retainedContainer=null;
                if(error!=null||cancelAfterIndex)
                {
                    RetireRequest();phase=cancelAfterIndex?"cancelled":"failed";Result();return false;
                }
                // Bootstrap now validates the indexed profile, exact mission,
                // version and file hash before calling native Continue.
                phase="ready-to-load";Result();report("Save index refreshed. Loading the host checkpoint...");return false;
            }
        }
        catch(Exception ex)
        {
            SessionQuickReload.Release();
            error=ex.GetBaseException().Message;
            MoveProbePlugin.ProbeLog.LogWarning("Live checkpoint handoff: "+ex);
            CleanupStaging();
            try{string path=PathFor("live-import.json");if(File.Exists(path))File.Move(path,path+".rejected",true);RetireRequest();}catch{}
            // A failed swap already rolled back before the native reload.
            // Keep bootstrap blocked until that reload finishes or times out.
            if(resetStarted&&!indexingAtEntry)
            {phase="indexing";deadline=Time.realtimeSinceStartup+60f;}
            else{phase="failed";retainedContainer=null;}
            try{Result();}catch{}
            report("Checkpoint handoff failed. Original backup retained; see diagnostic result.");
        }
        return Exclusive;
    }
    private static GuestSavePrepare.PreparedCheckpoint StageLocal(string runtime,string generation,string account,string room,string version,CancellationToken token)
    {
        string inbox=Path.Combine(runtime,"live-import-inbox",generation);
        string manifestPath=Path.Combine(inbox,"manifest.json");
        if(new FileInfo(manifestPath).Length>32768)throw new InvalidDataException("Local manifest too large");
        string manifest=File.ReadAllText(manifestPath);
        using var data=JsonDocument.Parse(manifest);var root=data.RootElement;
        string hash=root.GetProperty("sha256").GetString();
        if(!Regex.IsMatch(hash??"","\\A[0-9a-f]{64}\\z")||root.GetProperty("generation").GetString()!=generation||root.GetProperty("gameVersion").GetString()!=version)
            throw new InvalidDataException("Local checkpoint identity mismatch");
        string bundle=Path.Combine(inbox,hash+".zip");
        if(new FileInfo(bundle).Length>96*1024*1024+65536)throw new InvalidDataException("Local checkpoint too large");
        token.ThrowIfCancellationRequested();
        return GuestSavePrepare.Stage(File.ReadAllBytes(bundle),manifest,account,Path.Combine(runtime,"control"),room,token);
    }
    private void RetireRequest()
    {
        string path=PathFor("session-load.json");if(checkpoint==null||!File.Exists(path)||new FileInfo(path).Length>8192)return;
        using var request=JsonDocument.Parse(File.ReadAllText(path));
        if(request.RootElement.GetProperty("generation").GetString()==checkpoint)File.Move(path,path+".live-cancelled",true);
    }
    private void CleanupStaging()
    {
        cancellation?.Cancel();var tokenSource=cancellation;cancellation=null;
        var pending=worker;worker=null;var staged=prepared;prepared=null;
        string address=room;bool diagnostic=localOnly;string generation=checkpoint;
        // Cancellation and host lease release must never block Unity's loop.
        Task.Run(async()=>{
            try{
                if(pending!=null)try{staged=await pending.ConfigureAwait(false);}catch{}
                if(staged!=null){generation=staged.Generation;staged.Dispose();}
                if(!diagnostic&&generation!=null)GuestSavePrepare.CancelJoin(address,generation);
            }catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Live handoff cleanup: "+ex.GetType().Name);}
            finally{tokenSource?.Dispose();}
        });
    }
    internal void Cancel(bool explicitRequest=true)
    {
        SessionQuickReload.Release();
        // Steam may replace the first game process before its main menu is
        // ready. No download/lease exists yet in this phase. Preserve only our
        // still-fresh accepted request, never overwrite a newer user request.
        if(!explicitRequest&&phase=="waiting-menu")
        {
            string pending=PathFor("live-import.json"),accepted=pending+".accepted";
            try
            {
                if(!File.Exists(pending)&&File.Exists(accepted))
                {
                    var info=new FileInfo(accepted);var age=DateTime.UtcNow-info.LastWriteTimeUtc;
                    if(info.Length<=2048&&age>=TimeSpan.FromMinutes(-1)&&age<=TimeSpan.FromMinutes(5))
                    {
                        using var saved=JsonDocument.Parse(File.ReadAllText(accepted));
                        if(saved.RootElement.GetProperty("generation").GetString()==operation)
                            File.Move(accepted,pending); // Keeps original expiry.
                    }
                }
                phase="deferred";Result();operation=null;return;
            }
            catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Preserve pending handoff: "+ex.GetType().Name);}
        }
        // A Steam relaunch can interrupt the load after a successful commit.
        // The next process rebuilds its index and consumes the same request.
        if(!explicitRequest&&(phase=="indexing"||phase=="ready-to-load"))return;
        if(phase=="indexing"){cancelAfterIndex=true;RetireRequest();CleanupStaging();return;}
        CleanupStaging();
        if(Busy){phase="cancelled";Result();}
        string path=PathFor("live-import.json");if(explicitRequest&&File.Exists(path))File.Move(path,path+".cancelled",true);
    }
}
