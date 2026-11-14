using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mimimi.CharacterSelection;
using UnityEngine;

// Runs even when the task's A/B objects do not exist. No gameplay inputs or
// direct profile mutation; selections use the original mission preparation API.
internal sealed class LobbyHost
{
    private readonly string room=Guid.NewGuid().ToString("N");
    private long revision;
    private float next,lastWrite;
    private string lastContent,lastRequest;
    private readonly Dictionary<string,string> peerPicks=new(StringComparer.Ordinal);
    private readonly HashSet<string> guestPicks=new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string,CharacterSelectionCharacter> characters=new Dictionary<string,CharacterSelectionCharacter>(StringComparer.Ordinal);
    private CharacterSelection selection;
    private bool published;
    private bool remoteSelection;
    private string lastNativePreparation;
    private SpawnPointHandler landingHandler;
    private string landingWorld,landingKey;
    private string departureWorld;private int departureMission;private string[] departurePlan=Array.Empty<string>();
    internal LobbyDeployment Departure{get;private set;}
    internal void ConsumeDeparture(LobbyDeployment departure){if(Departure==departure)Departure=null;}
    internal static string Definition(string name)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
    internal static string Identity(MiCharacterData data)=>Definition(data.m_characterDefinition.name);
    internal LobbySelectionHooks.Change BeforeSelection(CharacterSelection source,CharacterSelectionCharacter actor)
    {
        try
        {
            if(source==null||source!=selection||actor==null||actor.data==null)return null;
            return new LobbySelectionHooks.Change{host=this,selection=source,actor=actor,id=Identity(actor.data),picked=actor.bIsPicked,remote=remoteSelection};
        }
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Preparation selection observation: "+ex.GetType().Name);return null;}
    }
    internal LobbySelectionHooks.Change BeforeUnpick(CharacterSelection source,int slot)
    {
        try
        {
            if(source==null||source!=selection||slot<0)return null;
            foreach(var actor in source.m_arCharacters)
                if(actor!=null&&actor.bIsPicked&&source.iGetCharSlotIndex(actor)==slot)return BeforeSelection(source,actor);
        }
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Preparation unpick observation: "+ex.GetType().Name);}
        return null;
    }
    internal void AfterSelection(LobbySelectionHooks.Change change,bool success)
    {
        try
        {
            if(!success||change==null||change.host!=this||change.selection!=selection||change.actor==null)return;
            bool picked=change.actor.bIsPicked;
            if(!picked){guestPicks.Remove(change.id);peerPicks.Remove(change.id);}
            else if(!change.picked){if(change.remote){guestPicks.Add(change.id);peerPicks[change.id]=MultiPeerContext.Current;}else{guestPicks.Remove(change.id);peerPicks.Remove(change.id);}}
            next=0;
        }
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Preparation selection result: "+ex.GetType().Name);}
    }
    internal bool AllowUnpick(LobbySelectionHooks.Change change)
        =>change==null||change.host!=this||change.selection!=selection||change.remote||!guestPicks.Contains(change.id);
    internal void Completed(Il2CppSystem.Collections.Generic.List<MiCharacterDefinitionPlayer> definitions)
    {
        try
        {
            var mission=SceneLookup.Find<MiMissionHandler>()?.currentMissionData;
            if(mission==null||definitions==null)return;
            var members=new List<LobbyDeployment.Member>();
            foreach(var definition in definitions)
            {
                string id=Definition(definition.name);
                members.Add(new LobbyDeployment.Member{definition=id,owner=guestPicks.Contains(id)?'G':'H',peer=peerPicks.TryGetValue(id,out var chosen)?chosen:null});
            }
            Departure=new LobbyDeployment(mission.guid.iValue,members);
            departureWorld=SessionWorld.Current;departureMission=mission.guid.iValue;departurePlan=members.ConvertAll(m=>m.definition).ToArray();
            MoveProbePlugin.ProbeLog.LogInfo("Preparation departure captured: mission="+mission.guid.iValue+" crew="+members.Count);
        }
        catch(Exception ex){Departure=null;MoveProbePlugin.ProbeLog.LogWarning("Preparation departure unavailable: "+ex.GetType().Name);}
    }
    private static string Label(string text,int max)
    {
        if(text==null)return "";var label=new StringBuilder();
        foreach(char c in text){if(label.Length>=max)break;label.Append(char.IsControl(c)?' ':c);}
        return label.ToString();
    }
    private string FilePath(string name)=>Path.Combine(SessionHost.DirectoryPath,name);
    private void Write(string name,object value)
    {
        Directory.CreateDirectory(SessionHost.DirectoryPath);string path=FilePath(name);
        File.WriteAllText(path+".new",JsonSerializer.Serialize(value));
        if(File.Exists(path))File.Replace(path+".new",path,null);
        else File.Move(path+".new",path);
    }
    internal void Close()
    {
        Unpublish();
        if(LobbySelectionHooks.Active==this)LobbySelectionHooks.Active=null;
        Departure=null;departurePlan=Array.Empty<string>();departureMission=0;
        characters.Clear();guestPicks.Clear();peerPicks.Clear();selection=null;lastContent=null;next=0;
    }
    private void Unpublish(){if(published){try{File.Delete(FilePath("host-lobby.json"));}catch(IOException){}published=false;}}
    internal void Tick(bool hosting)
    {
        if(!hosting){Close();return;}
        LobbySelectionHooks.Active=this;
        if(Time.realtimeSinceStartup<next)return;next=Time.realtimeSinceStartup+.2f;
        try
        {
            var frame=Capture();Publish(frame);
            string path=FilePath("host-lobby-command.json");
            if(!File.Exists(path)||new FileInfo(path).Length>2048||DateTime.UtcNow-File.GetLastWriteTimeUtc(path)>TimeSpan.FromSeconds(3))return;
            var request=JsonSerializer.Deserialize<LobbyProtocol.Request>(File.ReadAllText(path));
            if(!LobbyProtocol.Valid(request)||request.request==lastRequest)return;
            lastRequest=request.request;
            using var peerScope=MultiPeerContext.Scope(request.peer??"guest");
            var candidate=LobbyProtocol.Allowed(frame,request);bool accepted=false;
            if(candidate!=null&&LobbyProtocol.ShipSelection(frame))accepted=ShipCrewSession.Choose(candidate.id,request.selected);
            else if(candidate!=null&&(!candidate.selected||peerPicks.TryGetValue(candidate.id,out var owner)&&owner==MultiPeerContext.Current)&&selection!=null&&characters.TryGetValue(candidate.id,out var actor))
            {
                if(candidate.selected==request.selected)accepted=true;
                else
                {
                    remoteSelection=true;
                    try
                    {
                        if(request.selected)accepted=selection.bTryPick(actor,true,false);
                        else {int slot=selection.iGetCharSlotIndex(actor);accepted=slot>=0&&slot<frame.limit&&selection.bTryUnPick(slot,true,false);}
                    }
                    finally{remoteSelection=false;}
                }
                if(accepted){if(request.selected&&!candidate.selected){guestPicks.Add(candidate.id);peerPicks[candidate.id]=MultiPeerContext.Current;}else if(!request.selected){guestPicks.Remove(candidate.id);peerPicks.Remove(candidate.id);}}
            }
            Write("host-lobby-reply.json",new LobbyProtocol.Reply{request=request.request,accepted=accepted,reason=accepted?"accepted":"changed-or-unavailable"});
            MoveProbePlugin.ProbeLog.LogInfo($"Host lobby selection actor={request.character} selected={request.selected} revision={request.revision} accepted={accepted}");
            if(accepted)Publish(Capture());
        }
        catch(Exception ex)
        {
            Unpublish();next=Time.realtimeSinceStartup+1;
            MoveProbePlugin.ProbeLog.LogWarning("Preparation state unavailable: "+ex.GetType().Name);
        }
    }
    private void Publish(LobbyProtocol.Frame frame)
    {
        frame.peerOwners=new Dictionary<string,string>(peerPicks,StringComparer.Ordinal);frame.room=room;frame.revision=1;frame.gameSpeed=GameSpeedSync.Capture();frame.slowEffect=GameSpeedSync.CaptureEffect();frame.alarmRemaining=GameSpeedSync.CaptureAlarm();
        string content=JsonSerializer.Serialize(frame);
        bool changed=content!=lastContent;
        if(changed){lastContent=content;revision++;MoveProbePlugin.ProbeLog.LogInfo("Preparation state: "+frame.phase+" mission="+frame.mission+" crew="+frame.characters.Length+" ready="+frame.ready+" revision="+revision);}
        frame.revision=revision;
        if(!LobbyProtocol.Valid(frame))throw new InvalidDataException("Invalid native preparation state");
        if(changed||!published||Time.realtimeSinceStartup-lastWrite>=1f)
        {Write("host-lobby.json",frame);published=true;lastWrite=Time.realtimeSinceStartup;}
    }
    private LobbyProtocol.Frame Capture()
    {
        characters.Clear();
        var frame=new LobbyProtocol.Frame{phase="loading",world=SessionWorld.Current};
        if(SaveProcessManager.bLoading)return frame;
        var missionHandler=SceneLookup.Find<MiMissionHandler>();
        var mission=missionHandler?.currentMissionData;
        frame.nativeStage=missionHandler?.eCurrentMissionState.ToString();
        // These UIs are absent/disabled during native gameplay. Searching for
        // them on every negative-cache expiry scans the entire scene. Keep
        // unknown and all transition stages on the original discovery path.
        bool preparation=mission==null||frame.nativeStage!="Playing_Objectives"&&frame.nativeStage!="Playing_Exit";
        var currentSelection=preparation?SceneLookup.Find<CharacterSelection>():null;
        if(currentSelection!=selection){guestPicks.Clear();peerPicks.Clear();selection=currentSelection;if(currentSelection!=null){Departure=null;departurePlan=Array.Empty<string>();departureMission=0;}}
        if(mission!=null&&departureWorld==frame.world&&departureMission==mission.guid.iValue)frame.departure=departurePlan;
        if(landingWorld!=frame.world){landingWorld=frame.world;landingHandler=null;landingKey=null;}
        var spawnHandler=preparation?SceneLookup.Find<SpawnPointHandler>():null;
        if(spawnHandler!=null)landingHandler=spawnHandler;
        // The native landing object is disabled as character selection opens.
        // Keep the scene-scoped wrapper and the confirmed choice for slower peers.
        if(landingHandler!=null&&landingHandler.bFinishedSpawnPointSelection)
            landingKey=GuestMissionPreparation.SpawnKey(landingHandler.selectedSpawnPoint)??landingKey;
        if(GuestMissionPreparation.ConfirmedLandingWorld==frame.world&&frame.nativeStage=="Init_CharacterSelection")
            landingKey=GuestMissionPreparation.ConfirmedLandingKey??landingKey;
        frame.spawn=landingKey;
        var mainMenu=mission==null?SceneLookup.Find<UIMainMenuGUI>():null;
        bool atMenu=mainMenu!=null&&mainMenu.gameObject.activeInHierarchy&&mainMenu.m_bOnOpenAfterFadeInDone;
        var spawn=preparation?SceneLookup.Find<UISpawnPointSelection>():null;
        string nativePreparation=$"mission={mission?.guid.iValue} state={missionHandler?.eCurrentMissionState} landing={spawn?.m_bActive} landingKey={frame.spawn} crew={selection?.bIsActive} menu={atMenu} cutscene={MiCutsceneHandler.bCutsceneRunning}";
        if(nativePreparation!=lastNativePreparation){lastNativePreparation=nativePreparation;MoveProbePlugin.ProbeLog.LogInfo("Native preparation: "+nativePreparation);}
        if(mission!=null){frame.mission=mission.guid.iValue;frame.title=Label(mission.m_lstrMissionName.strText,256);frame.description=Label(mission.m_lstrMissionDesc.strText,2048);}
        if(selection!=null&&selection.bIsActive)
        {
            frame.phase="crew";frame.ready=LobbySelectionHooks.Ready&&!selection.m_bBusy&&selection.m_bStartingFadeFinished;
            frame.limit=selection.m_params.m_iMaxCharacters;
            var rows=new List<LobbyProtocol.Character>();
            foreach(var actor in selection.m_arCharacters)
            {
                if(actor==null||actor.data==null)continue;
                string id=Identity(actor.data);
                if(!actor.bIsPicked){guestPicks.Remove(id);peerPicks.Remove(id);}
                characters.Add(id,actor);
                rows.Add(new LobbyProtocol.Character{id=id,label=Label(actor.uiData?.lstrName.strText,128),selected=actor.bIsPicked,
                    required=actor.bRequired,unavailable=actor.bUnpickable,slot=actor.bIsPicked?selection.iGetCharSlotIndex(actor):-1,
                    owner=actor.bIsPicked?(guestPicks.Contains(id)?"G":"H"):"U"});
            }
            frame.characters=rows.ToArray();return frame;
        }
        if(UIWorldMap.bOpenStatic&&frame.nativeStage?.StartsWith("Init_",StringComparison.Ordinal)!=true)
        {
            frame.phase="map";
            frame.mission=0;frame.title="房主正在浏览岛屿";frame.description="";
            var details=SceneLookup.Find<UIWorldMapMissionDetailsWindow>();
            if(details?.m_missionData!=null&&details.m_missionData.isRealMissionAndDisplayedOnWorldMap){frame.mission=details.m_missionData.guid.iValue;frame.title=Label(details.m_missionData.m_lstrMissionName.strText,256);frame.description=Label(details.m_missionData.m_lstrMissionDesc.strText,2048);}
        }
        else frame.phase=mission==null?(atMenu?"menu":"loading"):Time.timeScale<=0?"paused":"playing";
        // Scene unload temporarily removes the mission handler. Only an actual
        // opened main menu authorizes the guest to return to its own menu.
        if(frame.nativeStage?.StartsWith("Init_",StringComparison.Ordinal)==true&&frame.phase!="crew")frame.phase="loading";
        ShipCrewSession.Describe(frame);
        return frame;
    }
}
