$ErrorActionPreference='Stop'
$source=[IO.File]::ReadAllText((Join-Path (Split-Path $PSScriptRoot) 'SessionBootstrap.cs'))
$source=$source.Replace('Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ShadowGambitCoop")','BootstrapTests.Root').Replace('Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","session-next","runtime","control")','BootstrapTests.Root')
$fixture=@'
namespace UnityEngine {public static class Time{public static float realtimeSinceStartup;}public class Object{public static T FindObjectOfType<T>() where T:class=>SceneLookup.Find<T>();}}
public static class SceneLookup{public static UIMainMenuGUI Menu=new UIMainMenuGUI();public static MiMissionHandler Mission;public static MiSceneLoaderTactics Loader;public static Mimimi.HUB.HUBLogic Hub;public static T Find<T>() where T:class=>typeof(T)==typeof(UIMainMenuGUI)?Menu as T:typeof(T)==typeof(MiMissionHandler)?Mission as T:typeof(T)==typeof(MiSceneLoaderTactics)?Loader as T:Hub as T;}
public static class LobbyProtocol{public class Frame{public string world,nativeStage;public int mission;}}
public static class SyncProtocol{public static bool Session(string s)=>s!=null&&Regex.IsMatch(s,"^[0-9a-f]{32}$");}
public static class GuestMissionPreparation{public static bool WaitingDirectContinue;}
public static class MissionEntrySync{public static class EntryRender{public static int Armed;public static void Cancel(){Armed=0;}public static void Arm(int m){Armed=m;}}}
public static class EnemyRewind{public static void Cancel(){}public static void ValidateCompletion(){}public static void Prepare(JsonElement r,string p,bool b){}}
public static class NativeUiEvidence{public static void Capture(string x){}}
public static class NativeInput{public static ProbeFixture Probe=new ProbeFixture();}
public class ProbeFixture{public bool GuestLoadPending;public int blocked,coroutines;public void ReportGuestLoadBlocked(){blocked++;}public void StartCoroutine(object x){coroutines++;}}
namespace Il2CppSystem{public class Object{}}
public class MiSceneLoadDelegate:Il2CppSystem.Object{}
public class MiSceneLoaderDelegate:Il2CppSystem.Object{}
public class MiGameObjectBoundClass<T> where T:Il2CppSystem.Object{public string m_strSceneName,m_strName;public object m_goOwner;public T m_classRef;}
public struct MiSceneLoadEvents{public MiGameObjectBoundClass<MiSceneLoadDelegate> m_delOnFinishedLoading,m_delOnFinishSceneInit;public MiGameObjectBoundClass<MiSceneLoaderDelegate> m_delOnDeactivateLoader;}
public class MissionData{public MissionGuid guid=new MissionGuid();}
public class MissionGuid{public int iValue;}
public class MiMissionHandler{public MissionData currentMissionData;public static int requested;public static void loadMissionFromGuid(int m,MiSceneLoadEvents e){requested=m;}public static void loadHUB(MiSceneLoadEvents e){}}
public class MiSceneLoaderTactics{public SceneObject m_goRootContinue=new SceneObject();}
namespace Mimimi.HUB{public class HUBLogic{public Characters characters=new Characters();}public class Characters{public bool m_bRoutineSpawningProcessEnded;}}
namespace HarmonyLib {
 public class HarmonyPatch:Attribute{public HarmonyPatch(Type t,string name){}}
 public class Harmony{public Harmony(string id){}public void PatchAll(Type t){}}
}
public class SessionQuickReload {public static bool InMission=true,HudReady=true;public static int loads,releases;public static bool Ready(int n)=>true;public static void Release(){releases++;}public SaveGameHolder Index(string p)=>SaveGameManager.m_instance.saveProfile.newest;public void Load(){loads++;}}
public class SessionGuestBarrier {public static bool Ready=true;public static int preHolds,cancels;public void Cancel(){cancels++;}public SessionGuestBarrier Restart()=>new SessionGuestBarrier();public void PreHold(){preHolds++;}public bool Tick(JsonElement r,string a,Action<string> report)=>Ready;}
public class SessionTaskContinue {public static bool Ready=true;public static int calls;public void Reset(){}public bool Tick(JsonElement r,Action<string> report){calls++;return Ready;}}
public static class GuestMove{public static Uri Room(string s)=>Uri.TryCreate(s,UriKind.Absolute,out var u)&&u.Scheme=="http"?u:null;}
public class Logger{public void LogWarning(object x){}public void LogInfo(object x){}}
public static class MoveProbePlugin{public static Logger ProbeLog=new Logger();}
public class SaveGameHeader{public int m_iMissionGuid=42;public string strApplicationVersion="test";}
public class SaveGameHolder{public bool bHeaderOrDataCorrupt,bSaveThreadActive,m_bDataIsWrittenToStorage=true;public string sPath;public SaveGameHeader Header=new SaveGameHeader();}
public class SaveContainer{public SaveGameHolder holder;public SaveGameHolder getHolder(string path)=>holder?.sPath==path?holder:null;public object coroLoadHeader(string path)=>new object();}
public class SaveProfile{public object saveGameUser=new object();public int iProfileNumber=2;public SaveGameHolder saveGameUserHolder=new SaveGameHolder();public SaveGameHolder newest;public SaveContainer hiddenSaveContainer=new SaveContainer(),quickSaveContainer=new SaveContainer();public bool getNewestHolder(out SaveGameHolder h){h=newest;return h!=null;}}
public class SaveProfileContainer{public System.Collections.Generic.List<SaveProfile> m_liProfiles=new System.Collections.Generic.List<SaveProfile>();public const int c_iMaxSaveProfileCount=3;public bool trySetActiveProfileNoAdd(int n)=>false;}
public class SaveGameManager{public int switches,requestedIndex;public void createAndSetActiveSaveProfile(int i){switches++;requestedIndex=i;}public static SaveGameManager m_instance;public bool bLoadedAllSaveFiles=true;public SaveProfile saveProfile;public SaveProfileContainer saveProfileContainer=new SaveProfileContainer();}
public class SceneObject{public bool activeInHierarchy=true;}
public class FakeButton{public SceneObject gameObject=new SceneObject();}
public class UIMainMenuGUI{public static bool Opened=true;public static int hubLoads;public bool m_bOnOpenAfterFadeInDone=>Opened;public SceneObject gameObject=new SceneObject();public FakeButton m_buttonContinue=new FakeButton();public void loadHUB(){hubLoads++;}public void onContinueButtonPressed(){if(!GuestLoadMenuGuard.Continue.Prefix())throw new Exception("Automatic load was blocked by guest menu guard");SaveProcessManager.instance.loads++;}}
public class SaveProcessManager{
 public static bool bLoading,bProcessing,bInstanceExists=true;public static SaveProcessManager instance=new SaveProcessManager();
 public enum LoadResult{Success,SuccessLoadDifferentScene,FailureWrongSceneLoaded} public enum LoadProcessResult{Success,FailureStorageDataCorrupt}
 public int loads;public SaveGameHolder requested;public LoadResult result=LoadResult.Success;
 public LoadResult tryLoadSaveGame(SaveGameHolder h,bool different,bool corrupt,bool immediate,object callback,object events){
  if(!different||corrupt||immediate||events is not MiSceneLoadEvents e||e.m_delOnFinishedLoading==null||e.m_delOnFinishSceneInit==null||e.m_delOnDeactivateLoader==null)throw new Exception("Unsafe hidden-entry load flags");requested=h;loads++;return result;
 }
}
public static class BootstrapTests{
 public static string Root;static int checks,suspended;
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 static SessionBootstrap Make(string hash=null,string version="test"){
  Root=Path.Combine(Path.GetTempPath(),"coop-bootstrap-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(Root,"profile","quickSave"));
  string path=Path.Combine(Root,"profile","quickSave","mission.save");File.WriteAllText(path,"saved mission");
  using(var h=SHA256.Create())hash??=Convert.ToHexString(h.ComputeHash(File.ReadAllBytes(path)));
  SaveGameManager.m_instance=new SaveGameManager{saveProfile=new SaveProfile{newest=new SaveGameHolder{sPath=path},saveGameUserHolder=new SaveGameHolder{sPath=Path.Combine(Root,"profile","profileData.save")}}};
  SceneLookup.Menu=new UIMainMenuGUI();SceneLookup.Mission=null;SceneLookup.Loader=null;SceneLookup.Hub=null;NativeInput.Probe=new ProbeFixture();UIMainMenuGUI.hubLoads=0;MiMissionHandler.requested=0;SessionBootstrap.PreparationFrame=null;
  SessionGuestBarrier.Ready=true;SessionGuestBarrier.preHolds=SessionGuestBarrier.cancels=0;SessionQuickReload.releases=0;SessionQuickReload.InMission=SessionQuickReload.HudReady=true;
  UIMainMenuGUI.Opened=true;SaveProcessManager.instance=new SaveProcessManager();Time.realtimeSinceStartup=0;SaveProcessManager.bLoading=SaveProcessManager.bProcessing=false;suspended=0;SessionTaskContinue.Ready=true;SessionTaskContinue.calls=0;
  File.WriteAllText(Path.Combine(Root,"session-load.json"),JsonSerializer.Serialize(new{schema=1,generation=new string('a',32),room="http://127.0.0.1/"+new string('x',24),profile=2,savePath="quickSave/mission.save",fileSha256=hash,mission="42",gameVersion=version}));
  return new SessionBootstrap();
 }
 static bool Tick(SessionBootstrap b)=>b.Tick(()=>suspended++,s=>{});
 static void Flags(string suffix){string p=Path.Combine(Root,"session-load.json"),text=File.ReadAllText(p);File.WriteAllText(p,text.Substring(0,text.Length-1)+suffix+"}");}
 static void ShipRequest(bool preparation=false){
  string path=SaveGameManager.m_instance.saveProfile.saveGameUserHolder.sPath;File.WriteAllText(path,"ship progress");
  string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
  File.WriteAllText(Path.Combine(Root,"session-load.json"),JsonSerializer.Serialize(new{schema=1,generation=new string('a',32),room="http://127.0.0.1/"+new string('x',24),profile=2,savePath="profileData.save",fileSha256=hash,mission="-379511569",gameVersion="test",hub=true,preparation,hostWorld=new string('c',32)}));
 }
 static void Loaded(SaveGameHolder h,SaveProcessManager.LoadProcessResult r)=>typeof(SessionBootstrap.Loaded).GetMethod("Postfix",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).Invoke(null,new object[]{h,r});
 public static int Run(){
  var b=Make();Check(Tick(b)&&SaveProcessManager.instance.loads==1&&suspended==1,"verified task enters native loader once");
  Check(SessionTaskContinue.calls==0,"automatic Continue cannot run before exact native load success");
  for(int i=0;i<20;i++)Tick(b);Check(SaveProcessManager.instance.loads==1&&suspended==1,"polling never repeats native load or cleanup");
  Loaded(new SaveGameHolder{sPath=Path.Combine(Root,"other.save")},SaveProcessManager.LoadProcessResult.Success);
  Check(Tick(b)&&!File.Exists(Path.Combine(Root,"join-once.txt")),"unrelated load callback cannot release join");
  Loaded(SaveGameManager.m_instance.saveProfile.newest,SaveProcessManager.LoadProcessResult.Success);SaveProcessManager.bLoading=true;
  Check(Tick(b),"successful callback waits for loader to finish");SaveProcessManager.bLoading=false;
  SessionTaskContinue.Ready=false;Check(Tick(b)&&!File.Exists(Path.Combine(Root,"join-once.txt")),"guest join waits for native task confirmation readiness");SessionTaskContinue.Ready=true;
  Check(!Tick(b)&&File.Exists(Path.Combine(Root,"join-once.txt"))&&!File.Exists(Path.Combine(Root,"session-load.json")),"successful exact load hands off to automatic join");
  Check(!Tick(b)&&SaveProcessManager.instance.loads==1,"completed request cannot replay");
  b=Make();UIMainMenuGUI.Opened=false;SessionQuickReload.loads=0;
  var reloadFile=Path.Combine(Root,"session-load.json");var reloadJson=File.ReadAllText(reloadFile);File.WriteAllText(reloadFile,reloadJson.Substring(0,reloadJson.Length-1)+",\"reloadInPlace\":true}");
  SessionQuickReload.HudReady=false;Check(Tick(b)&&SessionQuickReload.loads==0&&!File.Exists(Path.Combine(Root,"session-load-result.json")),"missing HUD waits without issuing or failing load");SessionQuickReload.HudReady=true;
  Check(Tick(b)&&SessionQuickReload.loads==1&&SaveProcessManager.instance.loads==0,"same mission uses native quickload without a ready main menu");
  Loaded(SaveGameManager.m_instance.saveProfile.newest,SaveProcessManager.LoadProcessResult.Success);
  Check(!Tick(b)&&File.Exists(Path.Combine(Root,"join-once.txt")),"quickload still requires exact success before rejoining");
  b=Make(new string('0',64));Check(Tick(b)&&SaveProcessManager.instance.loads==0,"bad installed checksum rejected before native load");
  b=Make(new string('0',64));Tick(b);
  string retryPath=Path.Combine(Root,"session-load.json");string retry=File.ReadAllText(retryPath);
  using(var checksum=SHA256.Create())retry=retry.Replace(new string('0',64),Convert.ToHexString(checksum.ComputeHash(File.ReadAllBytes(SaveGameManager.m_instance.saveProfile.newest.sPath))));
  retry=retry.Replace(new string('a',32),new string('b',32));File.WriteAllText(retryPath,retry);Time.realtimeSinceStartup=2;
  Check(Tick(b)&&SaveProcessManager.instance.loads==1,"fresh corrected request retries failed validation without process restart");
  b=Make(version:"wrong");Check(Tick(b)&&SaveProcessManager.instance.loads==0,"wrong game version rejected");
  b=Make();SaveGameManager.m_instance.bLoadedAllSaveFiles=false;Check(Tick(b)&&SaveProcessManager.instance.loads==0,"wait for native profile index");
  Time.realtimeSinceStartup=601;Check(Tick(b)&&File.ReadAllText(Path.Combine(Root,"session-load-result.json")).Contains("failed"),"index timeout stays disconnected");
  b=Make();Tick(b);Loaded(SaveGameManager.m_instance.saveProfile.newest,SaveProcessManager.LoadProcessResult.FailureStorageDataCorrupt);
  Check(Tick(b)&&!File.Exists(Path.Combine(Root,"join-once.txt")),"failed native load never joins");
  string failureResult=Path.Combine(Root,"session-load-result.json");
  Check(File.Exists(failureResult)&&File.ReadAllText(failureResult).Contains("failed"),"asynchronous native failure publishes terminal result so scene follower can retry");
  Check(SessionGuestBarrier.cancels==1,"asynchronous native failure releases the guest pause/barrier");
  Tick(b);Check(SessionGuestBarrier.cancels==1,"failure cleanup is not repeated every frame");
  b.Cancel();Check(!Tick(b)&&File.Exists(Path.Combine(Root,"session-load.json.cancelled")),"explicit cancellation releases bootstrap");
  b=Make();Tick(b);Loaded(SaveGameManager.m_instance.saveProfile.newest,SaveProcessManager.LoadProcessResult.FailureStorageDataCorrupt);
  failureResult=Path.Combine(Root,"session-load-result.json");Directory.CreateDirectory(failureResult);
  Check(Tick(b)&&SessionGuestBarrier.cancels==1&&!File.Exists(failureResult),"real publication IO failure still releases native pause exactly once");
  Directory.Delete(failureResult);Time.realtimeSinceStartup=.5f;
  Check(Tick(b)&&!File.Exists(failureResult)&&SessionGuestBarrier.cancels==1,"failed publication is throttled instead of retried every frame");
  Time.realtimeSinceStartup=1.1f;
  Check(Tick(b)&&File.ReadAllText(failureResult).Contains("failed")&&SessionGuestBarrier.cancels==1,"publication retries after path recovers without repeating cleanup");
  b=Make();Tick(b);Loaded(SaveGameManager.m_instance.saveProfile.newest,SaveProcessManager.LoadProcessResult.FailureStorageDataCorrupt);
  failureResult=Path.Combine(Root,"session-load-result.json");Directory.CreateDirectory(failureResult);Tick(b);
  string nextRequest=Path.Combine(Root,"session-load.json");File.WriteAllText(nextRequest,File.ReadAllText(nextRequest).Replace(new string('a',32),new string('b',32)));Time.realtimeSinceStartup=2;
  Check(Tick(b)&&SaveProcessManager.instance.loads==2&&File.Exists(nextRequest),"blocked old failure result does not prevent a new generation from loading or consume its request");
  Directory.Delete(failureResult);Loaded(SaveGameManager.m_instance.saveProfile.newest,SaveProcessManager.LoadProcessResult.Success);
  Check(!Tick(b)&&File.ReadAllText(failureResult).Contains(new string('b',32)),"new successful generation supersedes failed publication after path recovery");
  b=Make();File.Delete(Path.Combine(Root,"session-load.json"));Check(!Tick(b)&&suspended==0&&SaveProcessManager.instance.loads==0,"ordinary launch changes nothing");
  b=Make();UIMainMenuGUI.Opened=false;Check(Tick(b)&&SaveProcessManager.instance.loads==0,"visible Continue waits until native menu finishes opening");
  UIMainMenuGUI.Opened=true;Check(Tick(b)&&SaveProcessManager.instance.loads==1,"native opened flag permits one Continue call");
  b=Make();var manager=SaveGameManager.m_instance;var target=manager.saveProfile;manager.saveProfile=new SaveProfile{iProfileNumber=0};manager.saveProfileContainer.m_liProfiles.Add(target);
  Check(Tick(b)&&manager.switches==1&&SaveProcessManager.instance.loads==0,"different slot uses complete native profile switch before continuing");
  Tick(b);Check(manager.switches==1&&SaveProcessManager.instance.loads==0,"asynchronous profile switch is never reissued per frame");
  manager.saveProfile=target;target.saveGameUser=null;Check(Tick(b)&&SaveProcessManager.instance.loads==0,"indexed headers alone cannot satisfy profile data readiness");
  target.saveGameUser=new object();Check(Tick(b)&&SaveProcessManager.instance.loads==1,"native data readiness permits Continue after profile switch");
  b=Make();manager=SaveGameManager.m_instance;manager.saveProfile.iProfileNumber=0;
  Check(Tick(b)&&manager.switches==0&&SaveProcessManager.instance.loads==0,"missing imported profile cannot create an empty replacement slot");
  b=Make();b.Cancel(false);Check(File.Exists(Path.Combine(Root,"session-load.json")),"component disabled before first tick preserves startup handoff for next process");
  b=Make();Tick(b);b.Cancel(false);Check(File.Exists(Path.Combine(Root,"session-load.json")),"Steam restart after first Tick preserves even an inspected startup handoff");
  b=Make();Tick(b);string newPath=Path.Combine(Root,"session-load.json");File.WriteAllText(newPath,File.ReadAllText(newPath).Replace(new string('a',32),new string('b',32)));
  b.Cancel(false);Check(File.Exists(newPath),"older component shutdown cannot discard a newer generation");
  b=Make();b.Cancel();Check(File.Exists(Path.Combine(Root,"session-load.json.cancelled")),"explicit disconnect can discard an uninspected request");
  b=Make();Tick(b);File.WriteAllText(Path.Combine(Root,"session-load.json"),"{");b.Cancel(false);
  Check(File.Exists(Path.Combine(Root,"session-load.json")),"damaged replacement request cannot interrupt component shutdown cleanup");
  // Completed handoffs rearm for later checkpoints without process restart.
  b=Make();string handoff=File.ReadAllText(Path.Combine(Root,"session-load.json"));Tick(b);
  Loaded(SaveGameManager.m_instance.saveProfile.newest,SaveProcessManager.LoadProcessResult.Success);Tick(b);
  File.Delete(Path.Combine(Root,"join-once.txt"));
  File.WriteAllText(Path.Combine(Root,"session-load.json"),handoff);Time.realtimeSinceStartup=2;
  Check(!Tick(b)&&SaveProcessManager.instance.loads==1,"completed generation cannot replay even if launcher file reappears");
  File.WriteAllText(Path.Combine(Root,"session-load.json"),"{");Time.realtimeSinceStartup=4;
  Check(!Tick(b)&&SaveProcessManager.instance.loads==1,"partial next request cannot break an already completed session");
  File.WriteAllText(Path.Combine(Root,"session-load.json"),handoff.Replace(new string('a',32),new string('b',32)));Time.realtimeSinceStartup=6;
  SaveProcessManager.bProcessing=true;Check(!Tick(b)&&SaveProcessManager.instance.loads==1,"new checkpoint waits for current native save processing");
  SaveProcessManager.bProcessing=false;Check(Tick(b)&&SaveProcessManager.instance.loads==2&&suspended==2,"new checkpoint generation reloads using same bootstrap instance");
  Loaded(SaveGameManager.m_instance.saveProfile.newest,SaveProcessManager.LoadProcessResult.Success);Tick(b);
  File.WriteAllText(Path.Combine(Root,"session-load.json"),handoff);Time.realtimeSinceStartup=8;
  Check(!Tick(b)&&SaveProcessManager.instance.loads==2,"older completed generation cannot roll back newer loaded mission");
  b=Make();handoff=File.ReadAllText(Path.Combine(Root,"session-load.json"));Tick(b);
  File.WriteAllText(Path.Combine(Root,"session-load.json"),handoff.Replace(new string('a',32),new string('b',32)));
  Check(Tick(b)&&SaveProcessManager.instance.loads==1,"new generation never replaces an in-flight native load");
  Loaded(SaveGameManager.m_instance.saveProfile.newest,SaveProcessManager.LoadProcessResult.Success);
  Check(Tick(b)&&File.Exists(Path.Combine(Root,"session-load.json"))&&!File.Exists(Path.Combine(Root,"join-once.txt")),"old completion preserves newer handoff and does not join obsolete checkpoint");
  Time.realtimeSinceStartup=2;Check(Tick(b)&&SaveProcessManager.instance.loads==2,"preserved replacement starts after old load has completed");
  b=Make();Tick(b);string replacement=Path.Combine(Root,"session-load.json");
  File.WriteAllText(replacement,File.ReadAllText(replacement).Replace(new string('a',32),new string('b',32)));
  Loaded(SaveGameManager.m_instance.saveProfile.newest,SaveProcessManager.LoadProcessResult.FailureStorageDataCorrupt);Time.realtimeSinceStartup=2;
  Check(Tick(b)&&SaveProcessManager.instance.loads==2,"new checkpoint retries asynchronous load failure without restarting game");
  Check(File.Exists(replacement)&&File.ReadAllText(replacement).Contains(new string('b',32)),"failed old completion does not consume or cancel replacement request");
  Check(SessionQuickReload.releases==1&&SessionGuestBarrier.cancels==1,"old failure releases its load holds before accepting replacement");
  Check(File.ReadAllText(Path.Combine(Root,"session-load-result.json")).Contains(new string('a',32)),"old failure is published under old identity while replacement loads");
  Loaded(SaveGameManager.m_instance.saveProfile.newest,SaveProcessManager.LoadProcessResult.Success);
  Check(!Tick(b)&&File.ReadAllText(Path.Combine(Root,"session-load-result.json")).Contains(new string('b',32)),"replacement success publishes its own identity");
  // Hidden/early entry must load the exact imported holder, never Continue's newest save.
  b=Make();Flags(",\"entering\":true");var profile=SaveGameManager.m_instance.saveProfile;var exact=profile.newest;
  profile.newest=new SaveGameHolder{sPath=Path.Combine(Root,"other.save")};profile.hiddenSaveContainer.holder=exact;
  SceneLookup.Menu.m_buttonContinue=null;NativeInput.Probe.GuestLoadPending=true;
  Check(Tick(b)&&ReferenceEquals(SaveProcessManager.instance.requested,exact),"hidden entry accepts no Continue button and passes exact holder with safe scene-loader flags");
  Loaded(exact,SaveProcessManager.LoadProcessResult.Success);Check(MissionEntrySync.EntryRender.Armed==42,"entry renderer cleanup armed only by matching successful load");
  Check(!Tick(b)&&File.Exists(Path.Combine(Root,"join-once.txt")),"exact hidden entry completes normally");
  b=Make();Flags(",\"entering\":true");profile=SaveGameManager.m_instance.saveProfile;
  Check(Tick(b)&&NativeInput.Probe.coroutines==1&&SaveProcessManager.instance.loads==0,"missing hidden holder starts asynchronous header index without loading another save");
  Tick(b);Check(NativeInput.Probe.coroutines==1,"header indexing is not restarted per frame");
  profile.quickSaveContainer.holder=profile.newest;profile.newest.m_bDataIsWrittenToStorage=false;
  Check(Tick(b)&&SaveProcessManager.instance.loads==0,"header indexed but storage not ready cannot load");
  profile.newest.m_bDataIsWrittenToStorage=true;
  Check(Tick(b)&&ReferenceEquals(SaveProcessManager.instance.requested,profile.newest),"same imported path may resolve from native quick-save container");
  b=Make();Flags(",\"entering\":true,\"waitForHost\":true");profile=SaveGameManager.m_instance.saveProfile;profile.hiddenSaveContainer.holder=profile.newest;
  SessionGuestBarrier.Ready=false;Tick(b);Check(SessionGuestBarrier.preHolds==1,"entry pause held before invoking loader");
  Loaded(profile.newest,SaveProcessManager.LoadProcessResult.Success);
  Check(Tick(b)&&SessionTaskContinue.calls==0&&!File.Exists(Path.Combine(Root,"join-once.txt")),"completed entry waits for host gate before clicking native Continue or joining");
  Check(SessionBootstrap.WaitingEntryContinue,"entry gate exposed while waiting");
  SessionGuestBarrier.Ready=true;Check(!Tick(b)&&!SessionBootstrap.WaitingEntryContinue,"host ready releases entry gate and then connects");
  b=Make();Flags(",\"entering\":true");profile=SaveGameManager.m_instance.saveProfile;profile.hiddenSaveContainer.holder=profile.newest;
  SaveProcessManager.instance.result=SaveProcessManager.LoadResult.FailureWrongSceneLoaded;
  Check(Tick(b)&&File.ReadAllText(Path.Combine(Root,"session-load-result.json")).Contains("failed")&&!File.Exists(Path.Combine(Root,"join-once.txt")),"rejected hidden load cannot fall back to new mission or connect");
  b=Make();NativeInput.Probe.GuestLoadPending=true;
  Check(!GuestLoadMenuGuard.Continue.Prefix()&&!GuestLoadMenuGuard.Load.Prefix()&&NativeInput.Probe.blocked==2,"pending guest blocks manual continue and load");
  Check(Tick(b)&&SaveProcessManager.instance.loads==1&&!GuestLoadMenuGuard.Automatic,"verified automatic Continue enters scoped exemption and resets it");
  b=Make();ShipRequest();
  Check(Tick(b)&&UIMainMenuGUI.hubLoads==1&&SaveProcessManager.instance.loads==0,"ship entry loads verified profile through menu-owned hub transition");
  Check(Tick(b)&&!File.Exists(Path.Combine(Root,"join-once.txt")),"ship entry cannot complete before native spawn");
  SceneLookup.Mission=new MiMissionHandler{currentMissionData=new MissionData{guid=new MissionGuid{iValue=-379511569}}};SceneLookup.Hub=new Mimimi.HUB.HUBLogic();
  Check(Tick(b)&&!File.Exists(Path.Combine(Root,"join-once.txt")),"ship identity alone cannot bypass pending crew spawn");
  SceneLookup.Hub.characters.m_bRoutineSpawningProcessEnded=true;
  Check(!Tick(b)&&File.Exists(Path.Combine(Root,"join-once.txt")),"ship joins after native crew spawning finishes");
  b=Make();ShipRequest();File.AppendAllText(SaveGameManager.m_instance.saveProfile.saveGameUserHolder.sPath,"changed");
  Check(Tick(b)&&UIMainMenuGUI.hubLoads==0&&File.ReadAllText(Path.Combine(Root,"session-load-result.json")).Contains("failed"),"ship profile checksum mismatch rejected before native entry");
  b=Make();ShipRequest(true);
  Check(Tick(b)&&MiMissionHandler.requested==0,"direct preparation waits for matching host world");
  SessionBootstrap.PreparationFrame=new LobbyProtocol.Frame{world=new string('d',32),mission=42,nativeStage="Init_Landing"};
  Check(Tick(b)&&MiMissionHandler.requested==0,"wrong world cannot choose guest mission");
  SessionBootstrap.PreparationFrame.world=new string('c',32);SessionBootstrap.PreparationFrame.nativeStage="Gameplay";
  Check(Tick(b)&&MiMissionHandler.requested==0,"ordinary gameplay frame cannot launch preparation");
  SessionBootstrap.PreparationFrame.nativeStage="Init_Landing";
  Check(Tick(b)&&MiMissionHandler.requested==42&&GuestMissionPreparation.WaitingDirectContinue,"matching native initialization launches the requested mission directly");
  SceneLookup.Mission=new MiMissionHandler{currentMissionData=new MissionData{guid=new MissionGuid{iValue=42}}};SceneLookup.Loader=new MiSceneLoaderTactics();
  Check(!Tick(b)&&SessionBootstrap.PreparedWorld==new string('c',32)&&!File.Exists(Path.Combine(Root,"join-once.txt")),"native preparation completes at Continue and uses preparation channel instead of ordinary join file");
  return checks;
 }
}
'@
Add-Type -TypeDefinition ($source+"`n"+$fixture)
"Production bootstrap checks passed: $([BootstrapTests]::Run()) (simulated native loader)."
