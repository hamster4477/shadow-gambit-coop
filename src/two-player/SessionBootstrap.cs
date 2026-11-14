using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

// Startup handoff from a verified local save installation. Never downloads or
// extracts on Unity's thread, and never constructs incomplete native holders.
internal sealed class SessionBootstrap
{
    private static SessionBootstrap active;
    private static bool hooks;
    internal static LobbyProtocol.Frame PreparationFrame;
    internal static string PreparedWorld{get;private set;}
    private int preparationMission;
    internal static bool MatchPreparation(LobbyProtocol.Frame frame,string expectedWorld)=>
        frame!=null&&frame.world==expectedWorld&&SyncProtocol.Session(expectedWorld)&&
        frame.mission!=0&&frame.mission!=-379511569&&frame.nativeStage?.StartsWith("Init_",StringComparison.Ordinal)==true;

    private readonly System.Collections.Generic.HashSet<string> retired=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
    private SessionGuestBarrier barrier=new SessionGuestBarrier();
    private readonly SessionTaskContinue taskContinue=new SessionTaskContinue();
    private bool waitingBarrier,profileSwitchRequested;
    private bool entryCanContinue;
    private bool entryIndexStarted;
    internal static bool WaitingEntryContinue=>active?.request!=null&&EntryWait(active.request.RootElement)&&!active.entryCanContinue&&!active.failed;
    private float profileSwitchDeadline;
    private bool inspected, suspended, issued, completed, failed, failurePublished, failureCleaned;
    private float nextFailurePublish;
    internal static bool LoadingCheckpoint=>active!=null&&active.issued&&!active.completed&&!active.failed&&active.request!=null;
    private float deadline;
    private float nextInspect;
    private string lastProgress;
    private string lastStatus;
    private string nonce, expectedPath, room, requestFile, error;
    private JsonDocument request;
    private SessionQuickReload quickReload=new SessionQuickReload();
    internal bool Busy=>request!=null;
    internal string JoinedWorld{get;private set;}
    internal string JoinedRoom{get;private set;}
    internal void ForgetJoinedWorld(){JoinedWorld=JoinedRoom=null;}
    internal void Supersede()
    {
        if(nonce!=null)retired.Add(nonce);
        Cancel();inspected=false;nextInspect=0;
    }
    private static string DirectoryPath=>Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","session-next","runtime","control");
    internal void Cancel(bool explicitRequest=true)
    {
        PreparedWorld=null;preparationMission=0;GuestMissionPreparation.WaitingDirectContinue=false;
        MissionEntrySync.EntryRender.Cancel();
        // Steam can restart even after the first Tick read the handoff. Only
        // an explicit disconnect discards it; component shutdown preserves it
        // for the next process. A new launcher join retires it transactionally.
        barrier=barrier.Restart();
        waitingBarrier=false;entryCanContinue=false;
        EnemyRewind.Cancel();
        SessionQuickReload.Release();quickReload=new SessionQuickReload();
        taskContinue.Reset();
        inspected=true;failed=false;failurePublished=failureCleaned=false;nextFailurePublish=0;completed=false;issued=false;entryIndexStarted=false;request?.Dispose();request=null;
        if(active==this)active=null;
        string path=Path.Combine(DirectoryPath,"session-load.json");
        try
        {
            if(!explicitRequest||!File.Exists(path))return;
            File.Move(path,path+".cancelled",true);
        }
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Session cancellation file: "+ex.GetType().Name);}
    }
    internal bool Tick(Action suspend,Action<string> report,Func<bool> canJoin=null)
    {
        try
        {
            // Finish cleanup even if a newer generation is already waiting.
            // Cleanup publishes the old result but never consumes that file.
            if(failed)PublishFailure();
            // Accept a new checkpoint after a previous handoff finished, as
            // well as a corrected request after failure. Never replace an
            // in-flight native load or its host-ready barrier.
            if((failed || inspected && request==null) && !SaveProcessManager.bLoading && !SaveProcessManager.bProcessing && Time.realtimeSinceStartup>=nextInspect)
            {
                nextInspect=Time.realtimeSinceStartup+1f;
                string generation=PendingGeneration();
                if(generation!=null && generation!=nonce && !retired.Contains(generation))
                {
                    if(nonce!=null)retired.Add(nonce);
                    barrier=barrier.Restart();taskContinue.Reset();waitingBarrier=false;profileSwitchRequested=false;entryCanContinue=false;
                    request?.Dispose();request=null;inspected=failed=failurePublished=failureCleaned=issued=completed=suspended=false;nextFailurePublish=0;
                    expectedPath=error=null;lastProgress=lastStatus=null;nextInspect=0;active=null;entryIndexStarted=false;
                    quickReload=new SessionQuickReload();
                }
            }
            if(!inspected)
            {
                if(Time.realtimeSinceStartup<nextInspect)return false;
                nextInspect=Time.realtimeSinceStartup+1f;
                inspected=true;requestFile=Path.Combine(DirectoryPath,"session-load.json");
                if(!File.Exists(requestFile)){inspected=false;Progress("waiting-request: "+requestFile);return false;}
                Progress("reading-request: "+requestFile);
                if(new FileInfo(requestFile).Length>8192||DateTime.UtcNow-File.GetLastWriteTimeUtc(requestFile)>TimeSpan.FromMinutes(30))
                    throw new InvalidDataException("Expired or oversized session load request");
                request=JsonDocument.Parse(File.ReadAllText(requestFile));
                var r=request.RootElement;
                nonce=r.GetProperty("generation").GetString();room=r.GetProperty("room").GetString();
                if(r.GetProperty("schema").GetInt32()!=1||!Regex.IsMatch(nonce??"","^[0-9a-f]{32}$")||GuestMove.Room(room)==null)
                    throw new InvalidDataException("Invalid session load request");
                deadline=Time.realtimeSinceStartup+600f;
            }
            if(request==null&&!failed)return false;
            if(!suspended){suspend();suspended=true;}
            if(failed){PublishFailure();Status(report,"Session load failed: "+error+". F11 cancels.");return true;}
            if(Time.realtimeSinceStartup>deadline)throw new TimeoutException(issued?(completed?"Scene objects appeared but native entry did not finish; last stage: "+lastProgress:"Native mission load did not complete; last stage: "+lastProgress):"Game main menu did not become ready");
            bool hub=request.RootElement.TryGetProperty("hub",out var hubFlag)&&hubFlag.GetBoolean();
            bool directPreparation=hub&&request.RootElement.TryGetProperty("preparation",out var preparationFlag)&&preparationFlag.GetBoolean();
            if(directPreparation&&issued&&!completed&&!SaveProcessManager.bLoading&&!SaveProcessManager.bProcessing&&
               SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue==preparationMission&&
               SceneLookup.Find<MiSceneLoaderTactics>()?.m_goRootContinue?.activeInHierarchy==true){
                completed=true;Progress("native-preparation-at-continue");
            }
            if(hub&&!directPreparation&&issued&&!completed&&!SaveProcessManager.bLoading&&!SaveProcessManager.bProcessing&&
                SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue==-379511569&&
                (canJoin==null||canJoin())){
                var characters=SceneLookup.Find<Mimimi.HUB.HUBLogic>()?.characters;
                if(characters?.m_bRoutineSpawningProcessEnded==true){completed=true;Progress("native-ship-entry-completed");}
                else Progress("waiting-native-ship-spawn-completion");
            }
            if(completed&&!SaveProcessManager.bLoading)
            {
                EnemyRewind.ValidateCompletion();
                // Entry readiness is acknowledged while the native Continue
                // screen still owns the camera/timeline pause. Ordinary joins
                // retain their existing post-Continue handshake.
                if(EntryWait(request.RootElement)&&!entryCanContinue)
                {
                    if(!waitingBarrier){waitingBarrier=true;deadline=Time.realtimeSinceStartup+600f;}
                    if(!directPreparation&&canJoin!=null&&!canJoin())return true;
                    if(!barrier.Tick(request.RootElement,room,report))return true;
                    entryCanContinue=true;
                }
                if(!directPreparation&&!taskContinue.Tick(request.RootElement,report))return true;
                // A newer request may arrive while the engine is loading.
                // Finish the current native load without consuming that file
                // or briefly joining its now-obsolete room/checkpoint.
                string pendingGeneration=PendingGeneration();
                bool superseded=pendingGeneration!=null && pendingGeneration!=nonce && !retired.Contains(pendingGeneration);
                // Reconnect only after the exact native save reports success.
                bool join=!superseded && (!request.RootElement.TryGetProperty("joinAfterLoad",out var connect)||connect.GetBoolean());
                if(join&&request.RootElement.TryGetProperty("waitForHost",out var wait)&&wait.GetBoolean())
                {
                    if(!waitingBarrier){waitingBarrier=true;deadline=Time.realtimeSinceStartup+600f;}
                    if(!directPreparation&&canJoin!=null&&!canJoin())return true;
                    if(!barrier.Tick(request.RootElement,room,report))return true;
                }
                if(join)
                {
                    JoinedWorld=request.RootElement.TryGetProperty("hostWorld",out var hostWorld)&&hostWorld.ValueKind==JsonValueKind.String?hostWorld.GetString():null;
                    JoinedRoom=GuestMove.Room(room)?.AbsoluteUri;
                    if(directPreparation)PreparedWorld=JoinedWorld;
                    string pairing=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ShadowGambitCoop");
                    Directory.CreateDirectory(pairing);if(!request.RootElement.TryGetProperty("preparation",out var prep)||!prep.GetBoolean())File.WriteAllText(Path.Combine(pairing,"join-once.txt"),room);
                }
                if(pendingGeneration==nonce)File.Move(requestFile,requestFile+".completed",true);
                File.WriteAllText(Path.Combine(DirectoryPath,"session-load-result.json"),JsonSerializer.Serialize(new{generation=nonce,status=superseded?"superseded":"loaded",savePath=expectedPath}));
                retired.Add(nonce);request.Dispose();request=null;active=null;if(superseded)nextInspect=0;return superseded;
            }
            if(issued){Progress("native-load-requested; awaiting completion");Status(report,"Loading host checkpoint...");return true;}
            var manager=SaveGameManager.m_instance;
            if(manager==null||!manager.bLoadedAllSaveFiles){Progress("waiting-native-profile-index");return true;}
            if(SaveProcessManager.bProcessing){Progress("waiting-native-save-process");return true;}
            // Visible saves use the native menu; entry Hidden holders use the
            // save manager with fully initialized scene-event value structs.
            var menu=SceneLookup.Find<UIMainMenuGUI>();
            // Native onOpenAfterFadeIn enables the menu before setting this
            // flag. A visible button can exist earlier, while opening is still
            // in flight; never invoke Continue during that window.
            // If Steam restarted the process, the copied file is indexed on
            // startup; use normal Continue instead of requiring a lost scene.
            bool reload=request.RootElement.TryGetProperty("reloadInPlace",out var inPlace)&&inPlace.GetBoolean()&&SessionQuickReload.InMission;
            bool entering=request.RootElement.TryGetProperty("entering",out var entryFlag)&&entryFlag.GetBoolean();
            if(!reload&&(menu==null||!menu.gameObject.activeInHierarchy||!menu.m_bOnOpenAfterFadeInDone||(!entering&&!hub&&(menu.m_buttonContinue==null||!menu.m_buttonContinue.gameObject.activeInHierarchy))))
            {Progress("waiting-native-main-menu: found="+(menu!=null)+", active="+(menu?.gameObject.activeInHierarchy==true)+", continue="+(menu?.m_buttonContinue?.gameObject.activeInHierarchy==true)+", opened="+(menu?.m_bOnOpenAfterFadeInDone==true));return true;}
            var root=request.RootElement;int index=root.GetProperty("profile").GetInt32();
            if(index<0||index>=SaveProfileContainer.c_iMaxSaveProfileCount)throw new InvalidDataException("Invalid local profile slot");
            if(reload&&(!int.TryParse(root.GetProperty("mission").GetString(),out int reloadMission)||!SessionQuickReload.Ready(reloadMission)))
                throw new InvalidOperationException("Same-mission reload is no longer ready");
            if(reload&&!SessionQuickReload.HudReady)
            {Progress("waiting-native-memory-window");return true;}
            if(manager.saveProfile?.iProfileNumber!=index)
            {
                if(!profileSwitchRequested)
                {
                    // The container's trySetActiveProfileNoAdd only switches
                    // pointers and unloads the previous profile. Use the SAME
                    // manager flow as UISaveProfileButton.onButtonPressed.
                    // Require the target to exist so this never creates a slot.
                    var profiles=manager.saveProfileContainer.m_liProfiles;bool indexed=false;
                    if(profiles!=null)for(int i=0;i<profiles.Count;i++)
                        if(profiles[i]?.iProfileNumber==index){indexed=true;break;}
                    if(!indexed)throw new InvalidDataException("Installed session profile was not indexed by the game");
                    profileSwitchRequested=true;profileSwitchDeadline=Time.realtimeSinceStartup+60f;
                    Progress("switching-native-profile: "+index);
                    manager.createAndSetActiveSaveProfile(index);
                }
                if(Time.realtimeSinceStartup>profileSwitchDeadline)throw new TimeoutException("Native profile switch did not complete");
                return true;
            }
            var profile=manager.saveProfile;
            if(profile.saveGameUser==null)
            {Progress("waiting-native-profile-data: "+index);return true;}
            if(hub){
                if(root.GetProperty("mission").GetString()!="-379511569"||root.GetProperty("savePath").GetString()!="profileData.save")
                    throw new InvalidDataException("Invalid ship profile request");
                expectedPath=profile.saveGameUserHolder.sPath;
                using(var stream=File.OpenRead(expectedPath))using(var hash=SHA256.Create())
                    if(!string.Equals(Convert.ToHexString(hash.ComputeHash(stream)),root.GetProperty("fileSha256").GetString(),StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Installed ship progress checksum mismatch");
                if(directPreparation){
                    string expectedWorld=root.GetProperty("hostWorld").GetString();
                    var frame=PreparationFrame;
                    if(!MatchPreparation(frame,expectedWorld)){Progress("waiting-matching-host-preparation");return true;}
                    preparationMission=frame.mission;PreparedWorld=null;GuestMissionPreparation.WaitingDirectContinue=true;
                    active=this;issued=true;deadline=Time.realtimeSinceStartup+180f;
                    Progress("calling-native-preparation-direct mission="+preparationMission);
                    MiMissionHandler.loadMissionFromGuid(preparationMission,EmptySceneEvents());return true;
                }
                active=this;issued=true;deadline=Time.realtimeSinceStartup+180f;
                Progress("calling-native-menu-load-hub");
                // Use the original menu-owned transition and its callbacks,
                // rather than calling the lower-level scene loader with no-op events.
                menu.loadHUB();return true;
            }
            string relative=root.GetProperty("savePath").GetString();
            if(!Regex.IsMatch(relative??"",@"^(manualSave|quickSave)/[A-Za-z0-9_-]{1,180}\.save$"))throw new InvalidDataException("Invalid mission save name");
            var directory=Path.GetDirectoryName(profile.saveGameUserHolder.sPath);
            expectedPath=Path.GetFullPath(Path.Combine(directory,relative.Replace('/',Path.DirectorySeparatorChar)));
            SaveGameHolder holder;
            if(reload)
            {
                holder=quickReload.Index(expectedPath);
                if(holder==null){Progress("reading-reload-header");return true;}
            }
            else if(entering)
            {
                // Imported paths can be indexed in either container. Resolve
                // the exact verified path, never substitute the newest save.
                holder=profile.hiddenSaveContainer?.getHolder(expectedPath)??profile.quickSaveContainer?.getHolder(expectedPath);
                if(holder==null){
                    if(!entryIndexStarted){
                        var container=profile.hiddenSaveContainer;
                        if(container==null)throw new InvalidDataException("Native entry save container is unavailable");
                        entryIndexStarted=true;
                        NativeInput.Probe.StartCoroutine(container.coroLoadHeader(expectedPath));
                    }
                    Progress("indexing-exact-entry-checkpoint");return true;
                }
                if(holder.Header==null||!holder.m_bDataIsWrittenToStorage){Progress("waiting-entry-header-storage");return true;}
            }
            else if(!profile.getNewestHolder(out holder))throw new InvalidDataException("No native checkpoint indexed");
            if(holder==null||holder.bHeaderOrDataCorrupt||holder.bSaveThreadActive||
                !string.Equals(Path.GetFullPath(holder.sPath),expectedPath,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native checkpoint does not match the received save");
            if(holder.Header==null||holder.Header.m_iMissionGuid.ToString(System.Globalization.CultureInfo.InvariantCulture)!=root.GetProperty("mission").GetString()||
                holder.Header.strApplicationVersion!=root.GetProperty("gameVersion").GetString())throw new InvalidDataException("Native mission or build mismatch");
            using(var stream=File.OpenRead(expectedPath))
            using(var hash=SHA256.Create())
                if(!string.Equals(Convert.ToHexString(hash.ComputeHash(stream)),root.GetProperty("fileSha256").GetString(),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Installed checkpoint checksum mismatch");
            if(!hooks){new Harmony("local.shadowgambit.sessionload").PatchAll(typeof(Loaded));hooks=true;}
            MissionEntrySync.EntryRender.Cancel();
            active=this;issued=true;deadline=Time.realtimeSinceStartup+180f;
            Progress(reload?"calling-native-mission-memory-slot":entering?"calling-native-hidden-entry-checkpoint":"calling-native-menu-continue");
            if(EntryWait(root))barrier.PreHold();
            EnemyRewind.Prepare(root,expectedPath,reload);
            if(reload)quickReload.Load();
            else if(entering)
            {
                // This is scene entry, not an in-mission rewind. The rewind
                // executor runs its before-load/after-load effect graph even
                // across scenes. Use its underlying native storage loader,
                // retaining the ordinary native Continue screen. Never permit
                // corruption or fall back to starting a fresh mission.
                var loader=SaveProcessManager.instance;
                if(loader==null)throw new InvalidOperationException("Native hidden-save loader is unavailable");
                var result=loader.tryLoadSaveGame(holder,true,false,false,null,EmptySceneEvents());
                Progress("native-entry-load-result: "+result);
                if(result!=SaveProcessManager.LoadResult.Success&&result!=SaveProcessManager.LoadResult.SuccessLoadDifferentScene)
                    throw new InvalidOperationException("Native entry load rejected: "+result);
            }
            else {
                GuestLoadMenuGuard.Automatic=true;
                try{menu.onContinueButtonPressed();}finally{GuestLoadMenuGuard.Automatic=false;}
            }
            return true;
        }
        catch(Exception ex)
        {
            failed=true;error=ex.Message;
            MoveProbePlugin.ProbeLog.LogWarning("Session bootstrap: "+ex);
            PublishFailure();
            return true;
        }
    }
    private void PublishFailure()
    {
        // Native completion failures arrive asynchronously, outside Tick's
        // exception handler. Publish the same terminal result on the next
        // Unity tick so the scene follower can retry, and release our pause
        // exactly once instead of leaving an invisible failed handoff active.
        if(!failureCleaned){
            failureCleaned=true;
            MissionEntrySync.EntryRender.Cancel();
            SessionQuickReload.Release();EnemyRewind.Cancel();barrier.Cancel();
        }
        // A locked result file must not strand the scene follower. Retry the
        // publication at most once a second, without repeating native cleanup
        // or preventing a newer request from superseding this failed one.
        if(failurePublished||Time.realtimeSinceStartup<nextFailurePublish)return;
        nextFailurePublish=Time.realtimeSinceStartup+1f;
        try{
            Directory.CreateDirectory(DirectoryPath);
            string path=Path.Combine(DirectoryPath,"session-load-result.json");
            File.WriteAllText(path+".failed.new",JsonSerializer.Serialize(new{generation=nonce,status="failed",error}));
            File.Move(path+".failed.new",path,true);
            failurePublished=true;
        }catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private string PendingGeneration()
    {
        // A launcher writes atomically, but an incomplete diagnostic edit or
        // an inaccessible file must not turn a completed mission into failure.
        try
        {
            string path=requestFile??Path.Combine(DirectoryPath,"session-load.json");
            if(!File.Exists(path)||new FileInfo(path).Length>8192)return null;
            using var candidate=JsonDocument.Parse(File.ReadAllText(path));
            if(!candidate.RootElement.TryGetProperty("generation",out var value)||value.ValueKind!=JsonValueKind.String)return null;
            string generation=value.GetString();
            return Regex.IsMatch(generation??"","^[0-9a-f]{32}$")?generation:null;
        }
        catch(IOException){return null;}
        catch(UnauthorizedAccessException){return null;}
        catch(JsonException){return null;}
    }
    private static bool EntryWait(JsonElement root)=>
        root.TryGetProperty("entering",out var entry)&&entry.GetBoolean()&&
        root.TryGetProperty("waitForHost",out var wait)&&wait.GetBoolean();
    internal static void ReturnToShip(){NativeUiEvidence.Capture("return-hub");MiMissionHandler.loadHUB(EmptySceneEvents());}
    private static MiSceneLoadEvents EmptySceneEvents()=>new MiSceneLoadEvents {
        m_delOnFinishedLoading=EmptyBound<MiSceneLoadDelegate>(),
        m_delOnFinishSceneInit=EmptyBound<MiSceneLoadDelegate>(),
        m_delOnDeactivateLoader=EmptyBound<MiSceneLoaderDelegate>()
    };
    private static MiGameObjectBoundClass<T> EmptyBound<T>() where T:Il2CppSystem.Object=>
        new MiGameObjectBoundClass<T>{m_strSceneName=null,m_strName=null,m_goOwner=null,m_classRef=null};
    private void Progress(string phase)
    {
        if(lastProgress==phase)return;
        lastProgress=phase;
        MoveProbePlugin.ProbeLog.LogInfo("Session bootstrap: "+phase);
    }
    private void Status(Action<string> report,string message)
    {
        if(lastStatus==message)return;
        lastStatus=message;report(message);
    }
    [HarmonyPatch(typeof(SaveProcessManager),"invokeOnAfterLoadCallbacks")]
    internal static class Loaded
    {
        private static void Prefix(SaveGameHolder __0,SaveProcessManager.LoadProcessResult __1)
        {
            var c=active;if(c?.request==null||__1!=SaveProcessManager.LoadProcessResult.Success||__0==null)return;
            if(string.Equals(__0.sPath,c.expectedPath,StringComparison.OrdinalIgnoreCase)&&EntryWait(c.request.RootElement))c.barrier.PreHold();
        }

        private static void Postfix(SaveGameHolder __0,SaveProcessManager.LoadProcessResult __1)
        {
            var current=active;
            if(current==null||!current.issued||__0==null)return;
            try
            {
                if(!string.Equals(Path.GetFullPath(__0.sPath),current.expectedPath,StringComparison.OrdinalIgnoreCase))return;
                if(__1==SaveProcessManager.LoadProcessResult.Success)
                {
                    current.completed=true;
                    if(current.request.RootElement.TryGetProperty("entering",out var entry)&&entry.GetBoolean()&&__0.Header!=null)
                        MissionEntrySync.EntryRender.Arm(__0.Header.m_iMissionGuid);
                }
                else{current.failed=true;current.error="Native load result: "+__1;}
            }
            catch(Exception ex){current.failed=true;current.error=ex.Message;}
        }
    }
}

// Guard user-facing menu callbacks only. Native checkpoint loaders and the
// explicitly scoped automatic Continue remain available; Quit is untouched.
internal static class GuestLoadMenuGuard
{
    [ThreadStatic] internal static bool Automatic;
    internal static bool Allow()
    {
        if(Automatic||NativeInput.Probe?.GuestLoadPending!=true)return true;
        NativeInput.Probe.ReportGuestLoadBlocked();return false;
    }
    [HarmonyPatch(typeof(UIMainMenuGUI),"onContinueButtonPressed")]
    internal static class Continue {internal static bool Prefix()=>Allow();}
    [HarmonyPatch(typeof(UIMainMenuGUI),"onLoadButtonPressed")]
    internal static class Load {internal static bool Prefix()=>Allow();}
}
