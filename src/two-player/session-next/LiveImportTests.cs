using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Collections.Generic;

namespace UnityEngine {
 public static class Time{public static float realtimeSinceStartup;}
 public static class Object{
  public static UIMainMenuGUI Menu=new UIMainMenuGUI();public static MiMissionHandler Mission=new MiMissionHandler();
  public static T FindObjectOfType<T>()where T:class=>typeof(T)==typeof(UIMainMenuGUI)?Menu as T:typeof(T)==typeof(MiCutsceneHandler)?MiCutsceneHandler.instance as T:Mission as T;
 }
}
public class SceneObject{public bool activeInHierarchy=true;}
public class UIMainMenuGUI{public SceneObject gameObject=new SceneObject();public bool m_bOnOpenAfterFadeInDone=true;}
public class MiMissionHandler{public object currentMissionData;public static int menus;public static void goBackToMainMenu(){menus++;}}
public class MiCutsceneHandler{public static bool bCutsceneRunning;public static MiCutsceneHandler instance=new();public bool bSkippable=true;public int skips;public void skipCutscene(){skips++;}}
public static class MiVersion{public sealed class Build{public string Display;}public static Build Version=new Build();}
public static class SaveProcessManager{public static bool bProcessing,bAsyncWriteOperationRunning,bLoading;}
public class NativeUser{public IntPtr Pointer=new IntPtr(10);}
public class SaveGameHeader{public string strApplicationVersion;}
public class SaveGameHolder{public string sPath;public SaveGameHeader Header=new SaveGameHeader();}
public class SaveProfile{
 public int iProfileNumber=2;public object saveGameUser=new object();
 public SaveGameHolder saveGameUserHolder=new SaveGameHolder(),newest=new SaveGameHolder();
 public bool getNewestHolder(out SaveGameHolder result){result=newest;return result!=null;}
}
public class SaveProfileContainer{public IntPtr Pointer=new IntPtr(20);public SaveGameHolder m_profileIndependentHolder;}
public class SaveGameManager{
 public static SaveGameManager m_instance;public bool bLoadedAllSaveFiles=true;
 public SaveProfile saveProfile=new SaveProfile();public SaveProfileContainer saveProfileContainer=new SaveProfileContainer();
 public NativeUser m_gameUserOwningSaveFiles=new NativeUser();public int resets,loads;public bool throwReset,throwReload;
 public void resetAllSaveFiles(){resets++;bLoadedAllSaveFiles=false;if(throwReset)throw new InvalidOperationException("partial reset failure");}
 public void loadSaveFilesForPrimaryGameUser(){loads++;if(throwReload)throw new InvalidOperationException("reload failure");}
}
public static class GuestMove{public static Uri Room(string s)=>Uri.TryCreate(s,UriKind.Absolute,out var u)?u:null;}
public static class SessionHost{public static string DirectoryPath;}
public static class MoveProbePlugin{public static class ProbeLog{public static void LogWarning(object s){}public static void LogInfo(object s){}}}
public static class LiveImportTests{
 // Reproducible synthetic bytes: no private save or extracted game file is
 // needed for coordinator/installer tests. Native deserialization is stubbed.
 public static string CreateSyntheticShare(string dir){
  Directory.CreateDirectory(dir);
  var entries=new Dictionary<string,byte[]>{
   ["profileData.save"]=System.Text.Encoding.UTF8.GetBytes("synthetic profile fixture"),
   ["quickSave/fixture.save"]=System.Text.Encoding.UTF8.GetBytes("synthetic checkpoint fixture")};
  Func<byte[],string> hash=b=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();
  var rows=new List<object>();foreach(var e in entries)rows.Add(new{path=e.Key,size=e.Value.Length,sha256=hash(e.Value)});
  byte[] data;using(var output=new MemoryStream()){
   using(var zip=new System.IO.Compression.ZipArchive(output,System.IO.Compression.ZipArchiveMode.Create,true)){
    foreach(var e in entries){using var stream=zip.CreateEntry(e.Key).Open();stream.Write(e.Value);}
    using var manifest=zip.CreateEntry("manifest.json").Open();
    manifest.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{schema=1,files=rows})));
   }data=output.ToArray();
  }
  string digest=hash(data);File.WriteAllBytes(Path.Combine(dir,digest+".zip"),data);
  File.WriteAllText(Path.Combine(dir,"manifest.json"),JsonSerializer.Serialize(new{schema=1,generation=new string('a',32),sha256=digest,bytes=data.Length,mission="42",gameVersion="synthetic-fixture",savePath="quickSave/fixture.save"}));
  return dir;
 }
 static int checks,disconnects;static string account,generation,share,root;
 static SaveGameManager Manager=>SaveGameManager.m_instance;
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 static string Control(string name)=>Path.Combine(SessionHost.DirectoryPath,name);
 static string Phase(){using var json=JsonDocument.Parse(File.ReadAllText(Control("live-import-result.json")));return json.RootElement.GetProperty("phase").GetString();}
 static bool Tick(SessionLiveImport r,bool other=false){UnityEngine.Time.realtimeSinceStartup+=.11f;return r.Tick(()=>disconnects++,_=>{},other);}
 static SessionLiveImport Make(bool request=true){
  string dir=Path.Combine(root,Guid.NewGuid().ToString("N"));SessionHost.DirectoryPath=Path.Combine(dir,"runtime","control");Directory.CreateDirectory(SessionHost.DirectoryPath);
  account=Path.Combine(dir,"user_steam_00000000000000001");Directory.CreateDirectory(Path.Combine(account,"gameState_02"));
  File.WriteAllText(Path.Combine(account,"gameState_02","original.txt"),"original slot");
  SaveGameManager.m_instance=new SaveGameManager();Manager.saveProfile.saveGameUserHolder.sPath=Path.Combine(account,"gameState_02","profileData.save");
  var manifest=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(share,"manifest.json")));
  Manager.saveProfile.newest.Header.strApplicationVersion=manifest["gameVersion"].GetString();
  MiVersion.Version.Display=manifest["gameVersion"].GetString();
  generation=Guid.NewGuid().ToString("N");manifest["generation"]=JsonSerializer.SerializeToElement(generation);
  string inbox=Path.Combine(dir,"runtime","live-import-inbox",generation);Directory.CreateDirectory(inbox);
  File.WriteAllText(Path.Combine(inbox,"manifest.json"),JsonSerializer.Serialize(manifest));
  string hash=manifest["sha256"].GetString();File.Copy(Path.Combine(share,hash+".zip"),Path.Combine(inbox,hash+".zip"));
  if(request)Request();
  UnityEngine.Object.Menu=new UIMainMenuGUI();UnityEngine.Object.Mission=new MiMissionHandler();UnityEngine.Time.realtimeSinceStartup=0;disconnects=0;
  SaveProcessManager.bLoading=SaveProcessManager.bProcessing=SaveProcessManager.bAsyncWriteOperationRunning=MiCutsceneHandler.bCutsceneRunning=false;MiMissionHandler.menus=0;MiCutsceneHandler.instance=new();return new SessionLiveImport();
 }
 static void Request()=>File.WriteAllText(Control("live-import.json"),JsonSerializer.Serialize(new{schema=1,generation,operation="verify-local-load"}));
 static void Until(SessionLiveImport r,string phase){
  var end=DateTime.UtcNow.AddSeconds(8);
  while(DateTime.UtcNow<end){Tick(r);if(Phase()==phase)return;if(Phase()=="failed")throw new Exception(File.ReadAllText(Control("live-import-result.json")));Thread.Sleep(5);}
  throw new Exception("Coordinator stalled: "+Phase());
 }
 static void Indexed(){Manager.bLoadedAllSaveFiles=true;Manager.saveProfileContainer=new SaveProfileContainer{Pointer=new IntPtr(30),m_profileIndependentHolder=Manager.saveProfileContainer.m_profileIndependentHolder};}
 public static int Run(string fixtures,string nativeShare){
  root=fixtures;share=nativeShare;
  var r=Make(false);Check(!Tick(r)&&Manager.resets==0,"no request leaves native state untouched");
  r=Make();UnityEngine.Object.Mission.currentMissionData=new object();Check(!Tick(r)&&Phase()=="waiting-menu"&&Manager.resets==0,"mission never receives a live slot swap");
  r.Cancel();Check(Phase()=="cancelled"&&!File.Exists(Control("session-load.json")),"pending cancellation preserves original slot");
  r=Make();UnityEngine.Object.Menu.m_bOnOpenAfterFadeInDone=false;Tick(r);r.Cancel(false);
  Check(Phase()=="deferred"&&File.Exists(Control("live-import.json"))&&Manager.resets==0,"Steam replacement before menu preserves uncommitted request");
  UnityEngine.Object.Menu.m_bOnOpenAfterFadeInDone=true;Until(r,"indexing");Indexed();Tick(r);
  Check(Phase()=="ready-to-load","preserved pre-menu request can resume and load");
  r=Make();UnityEngine.Object.Menu.m_bOnOpenAfterFadeInDone=false;Tick(r);File.WriteAllText(Control("live-import.json"),"newer request");r.Cancel(false);
  Check(File.ReadAllText(Control("live-import.json"))=="newer request","shutdown cannot replace a newer pending request");
  r=Make();UnityEngine.Object.Menu.m_bOnOpenAfterFadeInDone=false;Tick(r);File.SetLastWriteTimeUtc(Control("live-import.json.accepted"),DateTime.UtcNow.AddMinutes(-6));r.Cancel(false);
  Check(!File.Exists(Control("live-import.json")),"restart never renews an expired request");
  r=Make();UnityEngine.Object.Menu.m_bOnOpenAfterFadeInDone=false;Tick(r);r.Cancel();
  Check(!File.Exists(Control("live-import.json"))&&Phase()=="cancelled","explicit disconnect never resurrects accepted request");
  r=Make();SaveProcessManager.bAsyncWriteOperationRunning=true;Tick(r);Check(Phase()=="waiting-menu"&&Manager.resets==0,"native writes block staging and index reset");
  SaveProcessManager.bAsyncWriteOperationRunning=false;Until(r,"indexing");
  Check(Manager.resets==1&&Manager.loads==1&&disconnects==1,"one native reset, swap, reload and disconnect");
  Check(File.Exists(Control("session-load.json"))&&!File.Exists(Path.Combine(account,"gameState_02","original.txt")),"verified slot published at commit");
  Check(Tick(r)&&Manager.resets==1,"bootstrap stays blocked while native index rebuilds");
  Indexed();Check(!Tick(r)&&Phase()=="ready-to-load","fresh index releases exact-load bootstrap");
  using(var data=JsonDocument.Parse(File.ReadAllText(Control("session-load.json"))))Check(!data.RootElement.GetProperty("joinAfterLoad").GetBoolean(),"local verification never joins a fake room");
  r.Cancel(false);Check(File.Exists(Control("session-load.json")),"Steam relaunch retains committed handoff");
  r=Make();Manager.saveProfile.newest=null;Until(r,"indexing");Indexed();Tick(r);
  Check(Phase()=="ready-to-load","fresh native profile without a mission checkpoint can receive host save");
  r=Make();Manager.saveProfile.newest.Header.strApplicationVersion="old-writer-version";Until(r,"indexing");Indexed();Tick(r);
  Check(Phase()=="ready-to-load","existing old save header cannot impersonate current client version");
  r=Make();MiVersion.Version.Display="wrong-current-build";Tick(r);
  var versionDeadline=DateTime.UtcNow.AddSeconds(8);while(Phase()!="failed"&&DateTime.UtcNow<versionDeadline){Tick(r);Thread.Sleep(5);}
  Check(Phase()=="failed"&&Manager.resets==0,"matching old header cannot bypass incompatible current game build");
  r=Make();Until(r,"indexing");r.Cancel();Indexed();Tick(r);Check(Phase()=="cancelled"&&!File.Exists(Control("session-load.json"))&&Manager.loads==1,"disconnect finishes reindex but cancels native load");
  r=Make();Until(r,"indexing");Manager.bLoadedAllSaveFiles=true;Tick(r);Check(Phase()=="failed"&&!File.Exists(Control("session-load.json")),"stale container never releases bootstrap");
  r=Make();Until(r,"indexing");Indexed();Manager.m_gameUserOwningSaveFiles.Pointer=new IntPtr(99);Tick(r);Check(Phase()=="failed"&&!File.Exists(Control("session-load.json")),"different user rejects post-swap load");
  r=Make();Tick(r);UnityEngine.Object.Mission.currentMissionData=new object();Until(r,"waiting-commit");Check(Manager.resets==0&&File.Exists(Path.Combine(account,"gameState_02","original.txt")),"entering mission during staging defers commit");
  UnityEngine.Object.Mission.currentMissionData=null;Manager.m_gameUserOwningSaveFiles.Pointer=new IntPtr(99);Tick(r);Check(Phase()=="failed"&&Manager.resets==0,"account switch during download rejects before reset");
  r=Make();Manager.throwReset=true;Until(r,"indexing");Check(Manager.loads==1&&File.Exists(Path.Combine(account,"gameState_02","original.txt")),"partial reset reloads unchanged original slot");
  Indexed();Tick(r);Check(Phase()=="failed"&&!File.Exists(Control("session-load.json")),"partial reset recovery never starts load");
  r=Make();Manager.throwReload=true;Until(r,"indexing");Check(!File.Exists(Control("session-load.json")),"reload exception retires committed load request");
  UnityEngine.Time.realtimeSinceStartup+=70;Tick(r);Check(Phase()=="failed"&&Manager.resets==1,"recovery timeout never repeats native reset");
  r=Make();Until(r,"indexing");r.Cancel(false);Check(File.Exists(Control("session-load.json")),"shutdown during index rebuild preserves restart request");
  r=Make();UnityEngine.Object.Mission.currentMissionData=new object();MiCutsceneHandler.bCutsceneRunning=true;
  File.WriteAllText(Control("live-import.json"),JsonSerializer.Serialize(new{schema=1,generation,operation="join-room",returnToMenu=true,world=new string('a',32),room="http://127.0.0.1:38477/abcdefghijklmnopqrstuvwx"}));
  UnityEngine.Time.realtimeSinceStartup+=1;r.Tick(()=>disconnects++,_=>{},false,false);
  Check(MiMissionHandler.menus==0&&MiCutsceneHandler.instance.skips==0,"native ship initialization cannot be interrupted even before loading flag is set");
  SaveProcessManager.bLoading=true;Tick(r);Check(MiMissionHandler.menus==0,"host return during intro still waits for actual native loading");
  SaveProcessManager.bLoading=false;SaveProcessManager.bAsyncWriteOperationRunning=true;Tick(r);Check(MiMissionHandler.menus==0,"host intro abandonment does not interrupt save writes");
  SaveProcessManager.bAsyncWriteOperationRunning=false;MiCutsceneHandler.instance.bSkippable=false;Tick(r);Check(MiMissionHandler.menus==0&&MiCutsceneHandler.instance.skips==0,"unskippable intro is never forcibly unloaded");MiCutsceneHandler.instance.bSkippable=true;Tick(r);Check(MiMissionHandler.menus==0&&MiCutsceneHandler.instance.skips==1,"remote departure skips original intro without unloading live timeline");
  Tick(r);Check(MiCutsceneHandler.instance.skips==1&&MiMissionHandler.menus==0,"pending native skip is not repeated or mistaken for completion");
  MiCutsceneHandler.bCutsceneRunning=false;UnityEngine.Time.realtimeSinceStartup+=2;Check(Tick(r)&&MiMissionHandler.menus==1,"native sequence completion permits later menu transition");
  Tick(r);Check(MiMissionHandler.menus==1,"intro abandonment submits one transition only");r.Cancel();
  r=Make();File.WriteAllText(Control("session-load.json"),"{}");Check(!Tick(r)&&Manager.resets==0&&!File.Exists(Control("live-import-result.json")),"existing bootstrap owns transition priority");
  r=Make();Check(!Tick(r,true)&&Manager.resets==0,"diagnostic index refresh owns transition priority");
  r=Make();File.WriteAllText(Control("live-import.json"),"invalid");Tick(r);Check(Phase()=="failed"&&Manager.resets==0&&File.Exists(Control("live-import.json.rejected")),"malformed requests retire without touching native state");
  r=Make();UnityEngine.Object.Mission.currentMissionData=new object();
  File.WriteAllText(Control("live-import.json"),JsonSerializer.Serialize(new{schema=1,generation,operation="verify-local-load",returnToMenu=true}));
  SaveProcessManager.bAsyncWriteOperationRunning=true;Tick(r);Check(MiMissionHandler.menus==0,"automatic return waits for native write completion");
  SaveProcessManager.bAsyncWriteOperationRunning=false;MiCutsceneHandler.bCutsceneRunning=true;Tick(r);Check(MiMissionHandler.menus==0,"automatic return waits for cutscene");
  MiCutsceneHandler.bCutsceneRunning=false;Check(Tick(r)&&MiMissionHandler.menus==1&&disconnects==1,"automatic return uses native flow once and suspends old sync");
  Tick(r);Check(MiMissionHandler.menus==1&&Manager.resets==0,"automatic return does not repeat while native scene unloads");
  UnityEngine.Object.Mission.currentMissionData=null;Until(r,"indexing");Indexed();Tick(r);Check(Phase()=="ready-to-load","automatic return feeds the same verified live installer");
  r=Make();UnityEngine.Object.Mission.currentMissionData=new object();
  File.WriteAllText(Control("live-import.json"),JsonSerializer.Serialize(new{schema=1,generation,operation="return-menu",room="http://127.0.0.1:38477/abcdefghijklmnopqrstuvwx"}));
  Tick(r);Check(MiMissionHandler.menus==1&&Manager.resets==0,"host menu return uses native flow without installing a slot");
  UnityEngine.Object.Mission.currentMissionData=null;Tick(r);
  Check(Phase()=="menu-ready"&&Manager.resets==0&&!File.Exists(Control("session-load.json"))&&File.ReadAllText(Path.Combine(account,"gameState_02","original.txt"))=="original slot","menu return preserves checkpoint and does not initiate download or reload");
  r=Make();UnityEngine.Object.Mission.currentMissionData=new object();
  int reloadMission;using(var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(share,"manifest.json"))))reloadMission=int.Parse(doc.RootElement.GetProperty("mission").GetString());
  File.WriteAllText(Control("live-import.json"),JsonSerializer.Serialize(new{schema=1,generation,operation="verify-local-load",reloadMission,returnToMenu=false}));
  Check(Tick(r)&&disconnects==1&&MiMissionHandler.menus==0,"same-mission reload suspends old sync without leaving mission");
  Until(r,"ready-to-load");
  Check(Manager.resets==0&&Manager.loads==0&&MiMissionHandler.menus==0,"same-mission reload never rebuilds active profile index or opens main menu");
  Check(File.ReadAllText(Path.Combine(account,"gameState_02","original.txt"))=="original slot","same-mission reload preserves existing slot files");
  using(var doc=JsonDocument.Parse(File.ReadAllText(Control("session-load.json"))))Check(doc.RootElement.GetProperty("reloadInPlace").GetBoolean()&&!doc.RootElement.GetProperty("joinAfterLoad").GetBoolean(),"local reload verification uses exact-load bootstrap without joining fake room");
  r=Make();Manager.saveProfile=null;
  Manager.saveProfileContainer.m_profileIndependentHolder=new SaveGameHolder{sPath=Path.Combine(account,"userData.save")};
  Until(r,"indexing");Indexed();Tick(r);
  Check(Phase()=="ready-to-load"&&Manager.resets==1,"brand-new guest account can import host progress without a preexisting active slot");
  r=Make();Manager.saveProfile.saveGameUser=null;Until(r,"indexing");Indexed();Tick(r);
  Check(Phase()=="ready-to-load","empty local progress is replaced by verified host progress rather than blocking at menu");
  r=Make();Manager.saveProfile=null;Manager.saveProfileContainer.m_profileIndependentHolder=new SaveGameHolder{sPath=Path.Combine(account,"not-user-data.save")};Tick(r);
  Check(Phase()=="waiting-menu"&&Manager.resets==0,"missing authenticated account path never guesses a directory");r.Cancel();
  return checks;
 }
}

public static class SessionQuickReload {public static bool Ready(int mission)=>UnityEngine.Object.Mission?.currentMissionData!=null;public static void Hold(){}public static void Release(){}}

public static class SceneLookup{public static T Find<T>()where T:class=>UnityEngine.Object.FindObjectOfType<T>();}
