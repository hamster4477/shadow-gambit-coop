using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Mimimi.CharacterSelection;
using UnityEngine;

// Join an ordinary mission through its native map action while the guest is
// still in the synchronized HUB. Saves remain the final world-state authority.
internal sealed class GuestMissionPreparation
{
    internal static bool Active{get;private set;}
    internal static bool WaitingDirectContinue;
    internal static string ConfirmedLandingWorld,ConfirmedLandingKey;
    // Preparation ownership ends before native scene teardown begins. Keep UI
    // retirement protection separately until the verified HUB has spawned.
    private static string retiringRoom;
    private static float retiringUntil;
    internal static bool ProtectSceneUi=>Active||(retiringRoom!=null&&Time.realtimeSinceStartup<retiringUntil);
    internal static void BeginShipUiReturn(Uri address)
    {
        retiringRoom=address.AbsoluteUri;retiringUntil=Time.realtimeSinceStartup+180;
        MoveProbePlugin.ProbeLog.LogInfo("Guest native HUB UI retirement protection started");
    }
    internal static void EndShipUiReturn()
    {
        if(retiringRoom!=null)MoveProbePlugin.ProbeLog.LogInfo("Guest native HUB UI retirement protection ended");
        retiringRoom=null;retiringUntil=0;
    }
    private static string returnRoom;
    private static int returnMission;
    internal static bool CanReturnToShip(Uri address,int localMission)=>address!=null&&address.AbsoluteUri==returnRoom&&localMission==returnMission&&localMission!=0;
    internal static void ClearShipReturn(){returnRoom=null;returnMission=0;}
    [ThreadStatic] internal static bool Applying;
    internal static Func<string,bool,bool> RequestPick;
    private string world,room,failedWorld;
    private string awaitingStageWorld;
    private float awaitingStageUntil;
    private int mission;
    private bool launched,completed,mapOpening,joiningViaShip,shipRequested;
    private UIWorldMap openingMap;
    private float deadline,next;
    private string lastStage;
    // Native HUD restoration and destruction must retain the original lifecycle.
    private System.Threading.Tasks.Task<string> entryReady;
    private bool entryReadyConfirmed;
    internal static string SpawnKey(SpawnPoint point)
    {
        if(point==null)return null;
        var parts=new List<string>();var t=point.transform;
        for(int i=0;t!=null&&i<64;i++,t=t.parent)parts.Add(t.parent==null?t.name:t.name+"["+t.GetSiblingIndex()+"]");
        parts.Reverse();return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("/",parts))));
    }
    internal void Close(){WaitingDirectContinue=false;if(entryReady!=null)_=entryReady.ContinueWith(t=>{_ = t.Exception;},System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);entryReady=null;entryReadyConfirmed=false;world=room=failedWorld=awaitingStageWorld=null;mission=0;launched=completed=Active=mapOpening=joiningViaShip=shipRequested=false;openingMap=null;next=0;RequestPick=null;}
    internal bool Tick(Uri target,LobbyProtocol.Frame frame,int localMission,bool synchronizedShip,bool otherTransition,Action<string> report)
    {
        if(retiringRoom!=null&&(target==null||target.AbsoluteUri!=retiringRoom||Time.realtimeSinceStartup>=retiringUntil))EndShipUiReturn();
        if(target==null){ClearShipReturn();Close();return false;}
        if(room!=null&&room!=target.AbsoluteUri)Close();
        if(world==null)
        {
            if(!otherTransition&&synchronizedShip&&localMission==ShipCrewSession.Mission&&
                frame?.world!=null&&frame.mission!=0&&frame.mission!=ShipCrewSession.Mission&&
                SessionBootstrap.PreparedWorld!=frame.world)return false; // Scene-follow imports fresh host progress first.
            // Native mission identity is published before Init_Briefing.
            // Do not let a transient paused/playing frame send the guest to
            // the menu, destroying the synchronized HUB needed for departure.
            bool possibleDeparture=!otherTransition&&(synchronizedShip&&localMission==ShipCrewSession.Mission)&&
                frame!=null&&frame.world!=null&&frame.mission!=0&&frame.mission!=ShipCrewSession.Mission;
            if(possibleDeparture&&frame.nativeStage?.StartsWith("Init_",StringComparison.Ordinal)!=true&&
                (frame.phase=="paused"||frame.phase=="playing"||frame.phase=="loading"))
            {
                if(awaitingStageWorld!=frame.world)
                {awaitingStageWorld=frame.world;awaitingStageUntil=Time.realtimeSinceStartup+15;report("等待房主确认原版进关阶段…");}
                if(Time.realtimeSinceStartup<awaitingStageUntil)return true;
            }
            bool directEntry=frame!=null&&SessionBootstrap.PreparedWorld==frame.world&&localMission==frame.mission;
            if(otherTransition||!(synchronizedShip&&localMission==ShipCrewSession.Mission||directEntry)||
                frame==null||frame.mission==0||frame.mission==ShipCrewSession.Mission||
                frame.world==null||frame.world==failedWorld||frame.nativeStage?.StartsWith("Init_",StringComparison.Ordinal)!=true)return false;
            returnRoom=target.AbsoluteUri;returnMission=frame.mission;
            joiningViaShip=localMission==0;shipRequested=false;
            entryReadyConfirmed=false;entryReady=null;room=target.AbsoluteUri;world=frame.world;mission=frame.mission;launched=directEntry;completed=mapOpening=false;openingMap=null;next=0;deadline=Time.realtimeSinceStartup+600;Active=true;
            report("正在跟随房主进入关卡准备场景…");
        }
        if(Time.realtimeSinceStartup>deadline){failedWorld=world;world=null;Active=false;report("关卡准备等待超时，恢复检查点接档。");return false;}
        if(frame==null)return true; // Missing heartbeat is not permission to start or choose.
        if(frame.world!=world||frame.mission!=mission)
        {if(frame.phase=="loading"||frame.phase=="map")return true;Close();return false;}
        if(Time.realtimeSinceStartup<next)return true;next=Time.realtimeSinceStartup+.2f;
        try
        {
            if(!launched)
            {
                if(joiningViaShip)
                {
                    if(SaveProcessManager.bLoading||SaveProcessManager.bProcessing)return true;
                    var hub=SceneLookup.Find<Mimimi.HUB.HUBLogic>();
                    if(hub?.characters?.m_bRoutineSpawningProcessEnded!=true)
                    {
                        if(!shipRequested)
                        {
                            var menu=SceneLookup.Find<UIMainMenuGUI>();
                            if(menu==null||!menu.gameObject.activeInHierarchy||!menu.m_bOnOpenAfterFadeInDone)return true;
                            shipRequested=true;menu.loadHUB();report("首次加入：正在通过本地红玛丽号进入房主的关卡准备流程…");
                        }
                        return true;
                    }
                    joiningViaShip=false;
                }
                var map=UIWorldMap.s_worldMapInstance;if(map==null)return true;
                // The native fade owns the HUD transition. A delayed open flag is
                // not permission to submit another open every 200 ms.
                if(openingMap!=map){openingMap=map;mapOpening=false;}
                if(mapOpening&&!map.m_bFadedIn)return true;
                if(!UIWorldMap.bOpenStatic){mapOpening=true;NativeUiEvidence.Capture("preparation-map-open");map.openOnGlobalStack();return true;}
                mapOpening=false;
                if(!map.m_bFadedIn)return true;
                var details=SceneLookup.Find<UIWorldMapMissionDetailsWindow>();
                if(details?.m_missionData?.guid.iValue!=mission){map.selectMissionExternal(mission);return true;}
                GuestWorldMap.AutomaticDeparture=true;
                try{NativeUiEvidence.Capture("mission-departure");details.playButtonPressed();launched=true;}
                finally{GuestWorldMap.AutomaticDeparture=false;}
                MoveProbePlugin.ProbeLog.LogInfo("Guest native preparation started mission="+mission);return true;
            }
            if(SaveProcessManager.bLoading||SaveProcessManager.bProcessing)return true;
            var handler=SceneLookup.Find<MiMissionHandler>();
            if(handler?.currentMissionData?.guid.iValue!=mission)return true;
            var loader=SceneLookup.Find<MiSceneLoaderTactics>();
            if(loader?.m_goRootContinue?.activeInHierarchy==true&&loader.m_buttonContinue?.interactable==true)
            {
                if(!entryReadyConfirmed){
                    if(entryReady==null){
                        entryReady=GuestMove.SelectLobby(target,System.Text.Json.JsonSerializer.Serialize(new{operation="entry-ready",world,mission}),System.Threading.CancellationToken.None);
                        return true;
                    }
                    if(!entryReady.IsCompleted)return true;
                    try{using var reply=System.Text.Json.JsonDocument.Parse(entryReady.GetAwaiter().GetResult());entryReadyConfirmed=reply.RootElement.GetProperty("accepted").GetBoolean();}
                    catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Guest entry readiness retry: "+ex.GetType().Name);}
                    entryReady=null;if(!entryReadyConfirmed){next=Time.realtimeSinceStartup+1;return true;}
                }
                WaitingDirectContinue=false;
                loader.onContinueButtonPressed();return true;
            }
            string stage=$"world={world} local={handler.eCurrentMissionState} remote={frame.nativeStage} cutscene={MiCutsceneHandler.bCutsceneRunning} landingKey={frame.spawn}";
            if(stage!=lastStage){lastStage=stage;MoveProbePlugin.ProbeLog.LogInfo("Guest preparation stage "+stage);}
            if(MiCutsceneHandler.bCutsceneRunning)return true;
            var spawn=SceneLookup.Find<SpawnPointHandler>();
            if(spawn?.bInSpawnPointSelection==true)
            {
                if(frame.spawn==null)return true;
                SpawnPoint match=null;
                for(int i=0;i<spawn.m_liSpawnPoints.Count;i++){var p=spawn.m_liSpawnPoints[i];if(SpawnKey(p)==frame.spawn){if(match!=null)throw new InvalidOperationException("Ambiguous landing point");match=p;}}
                if(match==null){report("等待两端登陆点匹配…");return true;}
                MoveProbePlugin.ProbeLog.LogInfo("Guest native landing matched "+frame.spawn);
                Applying=true;try{spawn.selectSpawnPoint(match,true,false);}finally{Applying=false;}
                return true;
            }
            var selection=SceneLookup.Find<CharacterSelection>();
            if(selection?.bIsActive==true)
            {
                if(selection.m_bBusy||!selection.m_bStartingFadeFinished)return true;
                var wanted=new List<string>();
                if(frame.phase=="crew"){
                    var picked=new List<LobbyProtocol.Character>();foreach(var row in frame.characters)if(row.selected)picked.Add(row);
                    picked.Sort((a,b)=>a.slot.CompareTo(b.slot));foreach(var row in picked)wanted.Add(row.id);
                }else wanted.AddRange(frame.departure);
                if(wanted.Count==0&&frame.phase!="crew")return true;
                var local=new Dictionary<string,CharacterSelectionCharacter>();
                foreach(var actor in selection.m_arCharacters)if(actor?.data!=null)local.Add(LobbyHost.Identity(actor.data),actor);
                foreach(var id in wanted)if(!local.ContainsKey(id)){report("客机缺少本次选中的船员，等待匹配的进度或内容。");return true;}
                Applying=true;
                try{
                    // One native operation per update: let its animation settle.
                    foreach(var pair in local)if(pair.Value.bIsPicked&&!wanted.Contains(pair.Key)){
                        if(pair.Value.bRequired)return true;
                        selection.bTryUnPick(selection.iGetCharSlotIndex(pair.Value),true,false);return true;
                    }
                    foreach(var id in wanted)if(!local[id].bIsPicked){selection.bTryPick(local[id],true,false);return true;}
                    // Membership alone is insufficient: slot order defines the
                    // deployed crew. Rebuild the optional suffix when needed.
                    for(int i=0;i<wanted.Count;i++)if(selection.iGetCharSlotIndex(local[wanted[i]])!=i){
                        CharacterSelectionCharacter last=null;int slot=-1;
                        foreach(var pair in local)if(pair.Value.bIsPicked&&!pair.Value.bRequired){int s=selection.iGetCharSlotIndex(pair.Value);if(s==i){last=pair.Value;slot=s;}}
                        if(last!=null)selection.bTryUnPick(slot,false,false);
                        return true;
                    }
                    if(frame.phase!="crew"&&!completed&&frame.departure.Length==wanted.Count){selection.completeSelection();completed=true;}
                }finally{Applying=false;}
                return true;
            }
            string state=handler.eCurrentMissionState.ToString();
            if(!state.StartsWith("Init_",StringComparison.Ordinal)&&state!="None"&&localMission==mission)
            {
                // SceneFollow now requests an in-mission checkpoint, without
                // returning to the menu. Do not equate native entry with sync.
                MoveProbePlugin.ProbeLog.LogInfo("Guest native preparation complete; awaiting authoritative checkpoint");
                ClearShipReturn();failedWorld=world;world=null;Active=false;return false;
            }
        }
        catch(Exception ex){failedWorld=world;world=null;Active=false;MoveProbePlugin.ProbeLog.LogWarning("Guest native preparation: "+ex);report("关卡准备未完成，恢复检查点接档。");return false;}
        return true;
    }
    [HarmonyPatch(typeof(UISpawnPointSelection),"onSelectSpawnPointButtonPressed")]
    internal static class Landing {
        private static bool Prefix(UISpawnPointSelection __instance){
            if(Active)return Applying;
            string key=SpawnKey(__instance.m_spawnPointOnScreen);
            if(key!=null){ConfirmedLandingWorld=SessionWorld.Current;ConfirmedLandingKey=key;MoveProbePlugin.ProbeLog.LogInfo("Host landing confirmation captured "+key);}
            return true;
        }
    }
    [HarmonyPatch(typeof(CharacterSelection),"onCompleteClicked")]
    internal static class Complete {private static bool Prefix()=>!Active||Applying;}
}

