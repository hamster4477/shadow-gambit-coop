public static class ShipCrewSession{public static bool Choose(string id,bool selected)=>false;internal static void Describe(LobbyProtocol.Frame f){}}
namespace UnityEngine{
 public static class Time{public static float realtimeSinceStartup;public static float timeScale=1;}
 public static class Object{public static Dictionary<Type,object> values=new Dictionary<Type,object>();public static Dictionary<Type,int> scans=new();public static T FindObjectOfType<T>() where T:class{scans[typeof(T)]=scans.TryGetValue(typeof(T),out int n)?n+1:1;return values.TryGetValue(typeof(T),out var o)?o as T:null;}}
}
public static class SyncProtocol{public static bool Session(string s)=>s!=null&&System.Text.RegularExpressions.Regex.IsMatch(s,"\\A[0-9a-f]{32}\\z");public static bool Identity(string s)=>s!=null&&System.Text.RegularExpressions.Regex.IsMatch(s,"\\A[0-9A-F]{64}\\z");}
public static class SessionHost{public static string DirectoryPath;}
public static class SessionWorld{public static string Current=>SaveProcessManager.bLoading?null:new string('c',32);}
public static class SaveProcessManager{public static bool bLoading;}
public static class MoveProbePlugin{public static class ProbeLog{public static int warnings;public static void LogWarning(string s){warnings++;}public static void LogInfo(string s){}}}
public class TextInfo{public string strText="测试任务";}
public class GuidInfo{public int iValue=42;}
public class MissionData{public GuidInfo guid=new GuidInfo();public TextInfo m_lstrMissionName=new TextInfo();public TextInfo m_lstrMissionDesc=new TextInfo();public bool isRealMissionAndDisplayedOnWorldMap=true;}
public class MiMissionHandler{public string eCurrentMissionState="Playing";public MissionData currentMissionData=new MissionData();}
public class UIWorldMap{public static bool bOpenStatic;}
public class UIWorldMapMissionDetailsWindow{public MissionData m_missionData=new MissionData();}
namespace HarmonyLib{public class HarmonyPatch:Attribute{public HarmonyPatch(Type t,string name,Type[] types){}}public class Harmony{public Harmony(string id){}public void PatchAll(Type t){}}}
namespace Il2CppSystem.Collections.Generic{public class List<T>:System.Collections.Generic.List<T>{}}
public class MiCharacterDefinitionPlayer{public string name;}
public class MiCharacterData{public MiCharacterDefinitionPlayer m_characterDefinition=new MiCharacterDefinitionPlayer();public string name{get=>m_characterDefinition.name;set=>m_characterDefinition.name=value;}}
namespace Mimimi.CharacterSelection{
 public class MiMissionCharacterSelectionHandler{}
 public class CharData:MiCharacterData{}
 public class UIData{public TextInfo lstrName=new TextInfo();}
 public class CharacterSelectionCharacter{public CharData data=new CharData();public UIData uiData=new UIData();public bool bIsPicked,bRequired,bUnpickable;}
 public class CharacterSelection{
  public bool bIsActive=true,m_bBusy,m_bStartingFadeFinished=true;public Params m_params=new Params();public CharacterSelectionCharacter[] m_arCharacters;
  public readonly List<CharacterSelectionCharacter> slots=new List<CharacterSelectionCharacter>();public int picks,unpickCalls;public bool throwNextPick;
  public class Params{public int m_iMaxCharacters=8;}
  public int iGetCharSlotIndex(CharacterSelectionCharacter c)=>slots.IndexOf(c);
  public bool bTryPick(CharacterSelectionCharacter c,bool user,bool random){
   bool blockedResult=false; if(!LobbySelectionHooks.Pick.Prefix(this,c,user,out var change,ref blockedResult))return blockedResult;
   if(!user||random)throw new Exception("wrong native flags");picks++;
   if(throwNextPick){throwNextPick=false;throw new InvalidOperationException("native pick failed");}
   bool result=!c.bUnpickable&&(c.bIsPicked||slots.Count<m_params.m_iMaxCharacters);
   if(result&&!c.bIsPicked){c.bIsPicked=true;slots.Add(c);}
   LobbySelectionHooks.Pick.Postfix(result,change);return result;
  }
  public bool bTryUnPick(int index,bool user,bool random){
   bool result=false;
   if(!LobbySelectionHooks.Unpick.Prefix(this,index,user,out var change,ref result)){LobbySelectionHooks.Unpick.Postfix(result,change);return result;}
   if(!user||random)throw new Exception("wrong native flags");unpickCalls++;
   result=index>=0&&index<slots.Count&&!slots[index].bRequired;
   if(result){slots[index].bIsPicked=false;slots.RemoveAt(index);}
   LobbySelectionHooks.Unpick.Postfix(result,change);return result;
  }
 }
}
public static class LobbyHostTests{
 static int checks;static void Check(bool b,string why){if(!b)throw new Exception(why);checks++;}
 public static int Run(string root){
  SessionHost.DirectoryPath=Path.Combine(root,"lobby-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(SessionHost.DirectoryPath);
  LobbySelectionHooks.Ready=true;
  var host=new LobbyHost();string state=Path.Combine(SessionHost.DirectoryPath,"host-lobby.json"),cmd=Path.Combine(SessionHost.DirectoryPath,"host-lobby-command.json");
  LobbyProtocol.Frame Tick(){Time.realtimeSinceStartup+=1.1f;host.Tick(true);return File.Exists(state)?LobbyProtocol.Read(File.ReadAllText(state)):null;}
  LobbyProtocol.Reply Send(LobbyProtocol.Frame f,string id,bool selected){File.WriteAllText(cmd,JsonSerializer.Serialize(new LobbyProtocol.Request{request=Guid.NewGuid().ToString("N"),room=f.room,revision=f.revision,character=id,selected=selected}));Tick();return JsonSerializer.Deserialize<LobbyProtocol.Reply>(File.ReadAllText(Path.Combine(SessionHost.DirectoryPath,"host-lobby-reply.json")));}
  try{
   host.Tick(false);Check(!File.Exists(state),"inactive host does not publish a lobby");
   var frame=Tick();string room=frame.room;Check(frame.phase=="loading"&&frame.characters.Length==0,"absent scene remains loading until real menu is visible");
   UnityEngine.Object.values[typeof(MiMissionHandler)]=new MiMissionHandler();UIWorldMap.bOpenStatic=true;
   var details=new UIWorldMapMissionDetailsWindow();details.m_missionData.guid.iValue=99;details.m_missionData.m_lstrMissionName.strText="另一个岛屿";details.m_missionData.m_lstrMissionDesc.strText="任务说明";UnityEngine.Object.values[typeof(UIWorldMapMissionDetailsWindow)]=details;
   frame=Tick();Check(frame.room==room&&frame.phase=="map"&&frame.mission==99&&frame.title=="另一个岛屿","map exposes currently highlighted mission through same room");
   var selection=new CharacterSelection{m_arCharacters=Enumerable.Range(0,11).Select(i=>new CharacterSelectionCharacter{data=new CharData{name="crew-"+i},uiData=new UIData{lstrName=new TextInfo{strText="船员"+i}}}).ToArray()};
   selection.m_arCharacters[0].bRequired=selection.m_arCharacters[0].bIsPicked=true;selection.slots.Add(selection.m_arCharacters[0]);UnityEngine.Object.values[typeof(CharacterSelection)]=selection;
   frame=Tick();Check(frame.characters.Length==11&&frame.limit==8&&frame.characters[0].owner=="H","eleven available crew and eight deployed slots remain distinct");
   Check(!Send(frame,frame.characters[0].id,false).accepted&&selection.unpickCalls==0,"guest cannot unpick host-owned mandatory crew");
   frame=Tick();var before=frame;string hidden=frame.characters[10].id;
   Check(Send(frame,hidden,true).accepted&&selection.picks==1,"guest pick reaches original native selection with user-action checks");
   frame=Tick();Check(frame.characters[10].owner=="G"&&frame.characters[10].selected&&frame.revision>before.revision,"only resulting native selection enables guest-owned selected state");
   Tick();Check(selection.picks==1,"same request file is not executed on every frame");
   Check(!Send(before,before.characters[1].id,true).accepted&&selection.picks==1,"stale preparation revision cannot mutate newer host selection");
   var native=selection.m_arCharacters[10];string name=native.data.name;native.data.name=null;Check(Tick()==null,"temporary native read failure removes stale published UI");native.data.name=name;
   frame=Tick();Check(frame.characters[10].owner=="G","temporary sampling error preserves guest's existing preparation ownership");
   Check(Send(frame,hidden,false).accepted&&selection.unpickCalls==1&&!native.bIsPicked,"guest can withdraw its own optional pick through original native API");
   selection.m_bBusy=true;frame=Tick();Check(!frame.ready&&!Send(frame,frame.characters[1].id,true).accepted,"native animation busy state prevents additional picks");selection.m_bBusy=false;
   selection.m_arCharacters[1].bUnpickable=true;frame=Tick();Check(!Send(frame,frame.characters[1].id,true).accepted,"native locked character cannot be remotely selected");
   frame=Tick();Check(Send(frame,hidden,true).accepted,"guest may pick hidden character again after withdrawing");
   selection.bTryPick(native,true,false);frame=Tick();Check(frame.characters[10].owner=="G","native re-selection with no change never steals guest preparation ownership");
   Check(!selection.bTryUnPick(selection.iGetCharSlotIndex(native),true,false),"host cannot remove guest-owned departure pick");selection.bTryPick(native,true,false);
   frame=Tick();Check(frame.characters[10].owner=="G"&&native.bIsPicked,"host removal and re-selection cannot steal guest ownership");
   Check(Send(frame,hidden,false).accepted,"guest can release protected departure pick");
   selection.bTryPick(native,true,false);frame=Tick();Check(frame.characters[10].owner=="H","host can select crew released by guest");
   selection.bTryUnPick(selection.iGetCharSlotIndex(native),true,false);frame=Tick();Send(frame,hidden,true);frame=Tick();
   native.bUnpickable=true;selection.bTryPick(native,true,false);native.bUnpickable=false;
   Check(Tick().characters[10].owner=="G","failed native pick cannot alter ownership");
   var definitions=new Il2CppSystem.Collections.Generic.List<MiCharacterDefinitionPlayer>();definitions.AddRange(selection.slots.Select(c=>c.data.m_characterDefinition));
   LobbySelectionHooks.Completed.Prefix(definitions);
   var binding=host.Departure.Bind(42,new[]{new LobbyDeployment.Live{definition=hidden,actor=new string('A',64)},new LobbyDeployment.Live{definition=frame.characters[0].id,actor=new string('B',64)}});
   Check(binding!=null&&binding.guest==new string('A',64)&&binding.host==new string('B',64),"native completion receipt maps guest hidden crew across reordered live instances");
   Check(host.Departure.Bind(99,new[]{new LobbyDeployment.Live{definition=hidden,actor=new string('A',64)}})==null,"departure does not bind a different mission or a partial roster");
   var receipt=host.Departure;host.ConsumeDeparture(null);Check(host.Departure==receipt,"unrelated setup cannot consume pending departure");host.ConsumeDeparture(receipt);Check(host.Departure==null,"successful setup explicitly consumes exact departure receipt");
   selection.throwNextPick=true;frame=Tick();File.WriteAllText(cmd,JsonSerializer.Serialize(new LobbyProtocol.Request{request=Guid.NewGuid().ToString("N"),room=frame.room,revision=frame.revision,character=frame.characters[2].id,selected=true}));
   Check(Tick()==null,"native selection exception removes stale ready frame");selection.bTryPick(selection.m_arCharacters[2],true,false);
   Check(Tick().characters[2].owner=="H","exception restores remote-origin scope before next host input");
   LobbySelectionHooks.Ready=false;Check(!Tick().ready,"unavailable observation hooks disable remote mutation instead of guessing ownership");LobbySelectionHooks.Ready=true;
   var clicks=new List<string>();GuestMissionPreparation.Active=true;GuestMissionPreparation.RequestPick=(id,pick)=>{clicks.Add(id+":"+pick);return true;};
   var guestActor=selection.m_arCharacters[2];
   Check(!selection.bTryUnPick(selection.iGetCharSlotIndex(guestActor),true,false)&&clicks.Count==1&&clicks[0].EndsWith(":False"),"guest native slot click forwards unpick without local mutation");
   Check(!selection.bTryPick(guestActor,true,false)&&clicks.Count==2&&clicks[1].EndsWith(":True"),"guest native character click forwards pick to host");
   GuestMissionPreparation.Applying=true;selection.bTryUnPick(selection.iGetCharSlotIndex(guestActor),true,false);
   Check(clicks.Count==2,"host selection replay never resends guest input");
   GuestMissionPreparation.Applying=false;GuestMissionPreparation.Active=false;GuestMissionPreparation.RequestPick=null;
   var landing=new SpawnPointHandler{selectedSpawnPoint=new SpawnPoint()};UnityEngine.Object.values[typeof(SpawnPointHandler)]=landing;
   Tick();landing.bFinishedSpawnPointSelection=true;UnityEngine.Object.values.Remove(typeof(SpawnPointHandler));
   Check(Tick().spawn==new string('A',64),"confirmed landing survives native object deactivation before next poll");
   SaveProcessManager.bLoading=true;frame=Tick();Check(frame.room==room&&frame.phase=="loading"&&frame.characters.Length==0,"scene loading keeps room continuity and clears obsolete choice list");SaveProcessManager.bLoading=false;
   UnityEngine.Object.values.Remove(typeof(MiMissionHandler));UnityEngine.Object.values.Remove(typeof(CharacterSelection));UIWorldMap.bOpenStatic=false;
   Check(Tick().phase=="loading","missing mission during unload is not a main menu");
   UnityEngine.Object.values[typeof(UIMainMenuGUI)]=new UIMainMenuGUI{m_bOnOpenAfterFadeInDone=true,gameObject=new UIMainMenuGUI.Go{activeInHierarchy=true}};
   Check(Tick().phase=="menu","only visible ready native menu publishes return-menu");
   host.Close();Check(!File.Exists(state),"disconnect removes live preparation heartbeat");
   UnityEngine.Object.values.Clear();var perfMission=new MiMissionHandler();UnityEngine.Object.values[typeof(MiMissionHandler)]=perfMission;UIWorldMap.bOpenStatic=false;SaveProcessManager.bLoading=false;
   var preparationTypes=new[]{typeof(CharacterSelection),typeof(SpawnPointHandler),typeof(UISpawnPointSelection)};
   foreach(string stage in new[]{"Playing_Objectives","Playing_Exit"})foreach(float scale in new[]{0f,1f,2f}){
    perfMission.eCurrentMissionState=stage;Time.timeScale=scale;UnityEngine.Object.scans.Clear();frame=Tick();
    Check(preparationTypes.All(t=>!UnityEngine.Object.scans.ContainsKey(t))&&!UnityEngine.Object.scans.ContainsKey(typeof(UIMainMenuGUI)),"stable gameplay and pause skip missing preparation/menu object searches");
    Check(frame.mission==42&&frame.phase==(scale==0?"paused":"playing"),"search pruning preserves native mission and pause frame");
   }
   Time.timeScale=1;
   foreach(string stage in new[]{"Init_CharacterSelection","Init_SpawnPointSelection","Init_InitialQuicksave","Unknown_FutureStage","Playing_Success"}){
    perfMission.eCurrentMissionState=stage;UnityEngine.Object.scans.Clear();Tick();
    Check(preparationTypes.All(t=>UnityEngine.Object.scans.ContainsKey(t)),"initialization/tutorial/reload/unknown stages retain original UI discovery");
   }
   UnityEngine.Object.values.Remove(typeof(MiMissionHandler));UnityEngine.Object.scans.Clear();Tick();
   Check(UnityEngine.Object.scans.ContainsKey(typeof(UIMainMenuGUI)),"missing mission retains native main-menu discovery");
   return checks;
  }finally{host.Close();foreach(string file in Directory.GetFiles(SessionHost.DirectoryPath))File.Delete(file);Directory.Delete(SessionHost.DirectoryPath);}
 }
}

public class UIMainMenuGUI{public class Go{public bool activeInHierarchy;}public Go gameObject=new();public bool m_bOnOpenAfterFadeInDone;}
public class UISpawnPointSelection{public bool m_bActive;}
public static class MiCutsceneHandler{public static bool bCutsceneRunning;}

public class SpawnPoint{}
public class SpawnPointHandler{public bool bFinishedSpawnPointSelection;public SpawnPoint selectedSpawnPoint;}
internal static class GuestMissionPreparation{internal static string ConfirmedLandingWorld=null,ConfirmedLandingKey=null;internal static bool Active=false,Applying=false;internal static Func<string,bool,bool> RequestPick=null;internal static string SpawnKey(SpawnPoint p)=>p==null?null:new string('A',64);}
