using System;
using System.IO;
using System.Text.Json;
namespace UnityEngine{public static class Time{public static float realtimeSinceStartup;}}
public static class ShipCrewSession{public const int Mission=-379511569;}
public static class SessionHost{public static string DirectoryPath;}
public static class SyncProtocol{
 public static bool Session(string value)=>value!=null&&System.Text.RegularExpressions.Regex.IsMatch(value,"\\A[0-9a-f]{32}\\z");
 public static bool Identity(string value)=>value!=null&&System.Text.RegularExpressions.Regex.IsMatch(value,"\\A[0-9A-F]{64}\\z");
}
public static class SceneFollowTests{
 static string PathForResult(string name)=>Path.Combine(SessionHost.DirectoryPath,name);
 static int checks;static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
 public static int Run(string root){
  var epoch=new SceneEpoch();var instance=new IntPtr(100);
  Check(epoch.Observe(IntPtr.Zero,0,0,false)==null,"main menu has no playable world");
  string first=epoch.Observe(instance,42,0,false);
  Check(SyncProtocol.Session(first)&&first==epoch.Observe(instance,42,0,false),"polling and pauses retain world identity");
  string next=epoch.Observe(instance,42,1,false);Check(next!=first,"successful same-mission reload advances identity even if polling misses loading");
  Check(epoch.Observe(instance,42,1,true)==null,"loading never publishes an actionable world");
  first=epoch.Observe(instance,42,1,false);Check(first!=next&&first==epoch.Observe(instance,42,1,false),"loading boundary advances once");
  next=epoch.Observe(new IntPtr(200),42,1,false);Check(next!=first,"same mission with replaced native instance is a new world");
  first=epoch.Observe(new IntPtr(200),43,1,false);Check(first!=next,"different mission advances world identity");
  epoch.Observe(IntPtr.Zero,0,1,false);Check(epoch.Observe(new IntPtr(200),43,1,false)!=first,"menu return invalidates even a reused native pointer");
  string reserved=new string('e',32);epoch.Reserve(reserved,1);
  Check(epoch.Observe(new IntPtr(200),43,1,false)!=reserved,"announced epoch cannot expose old task before successful callback");
  Check(epoch.Observe(new IntPtr(200),43,2,true)==null,"reserved epoch remains hidden during loading");
  Check(epoch.Observe(new IntPtr(200),43,2,false)==reserved,"successful load adopts exactly the announced epoch");
  Check(epoch.Observe(new IntPtr(200),43,2,false)==reserved,"later polling preserves reserved epoch");
  epoch.Reserve(new string('f',32),2);epoch.CancelReservation();Check(epoch.Observe(new IntPtr(200),43,3,false)!=new string('f',32),"cancelled checkpoint never adopts a stale announcement");
  SessionHost.DirectoryPath=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(SessionHost.DirectoryPath);
  string path=Path.Combine(SessionHost.DirectoryPath,"live-import.json");
  var room=new Uri("http://127.0.0.1:38477/abcdefghijklmnopqrstuvwx");var follow=new SessionSceneFollow();
  string a=new string('a',32),b=new string('b',32),c=new string('c',32);
  var frame=new LobbyProtocol.Frame{room=new string('d',32),world=a,revision=1,phase="playing",mission=42};
  bool Tick(bool synced=true,string joined=null,bool busy=false){UnityEngine.Time.realtimeSinceStartup+=.6f;return follow.Tick(room,frame,42,synced,joined==null?null:room.AbsoluteUri,joined,busy,_=>{});}
  Check(!Tick(true,a)&&!File.Exists(path),"completed checkpoint join seeds epoch without redundant reload");
  frame.phase="paused";Check(!Tick(),"ordinary host pause never creates a join");
  frame.phase="map";frame.mission=99;Check(!Tick(),"highlighting another mission does not trigger checkpoint installation");
  frame.phase="playing";frame.mission=42;frame.world=b;
  Check(!Tick(),"new epoch requires stable observation");Tick();Check(Tick(),"same-mission reload queues one automatic handoff");
  using(var request=JsonDocument.Parse(File.ReadAllText(path))){var r=request.RootElement;Check(r.GetProperty("world").GetString()==b&&!r.GetProperty("returnToMenu").GetBoolean()&&r.GetProperty("reloadMission").GetInt32()==42&&r.GetProperty("operation").GetString()=="join-room","same-mission handoff pins exact epoch and avoids menu return");}
  string contents=File.ReadAllText(path);Check(!Tick()&&File.ReadAllText(path)==contents,"pending handoff never requeues every frame");
  File.Delete(path);Check(!Tick(true,b),"successful checkpoint join commits new host epoch");
  frame.world=a;Tick(true,b);Tick(true,b);Check(!Tick(true,b)&&!File.Exists(path),"late old-world frames cannot send guest backwards");
  frame.world=c;Tick(true,b);Tick(true,b);Check(!Tick(true,b,true)&&!File.Exists(path),"existing native load/index transition blocks follow");
  Check(Tick(true,b),"latest stable world follows once native transition clears");
  using(var request=JsonDocument.Parse(File.ReadAllText(path)))File.WriteAllText(Path.Combine(SessionHost.DirectoryPath,"live-import-result.json"),JsonSerializer.Serialize(new{generation=request.RootElement.GetProperty("generation").GetString(),phase="failed"}));
  File.Delete(path);Check(!Tick(false,b),"failed download starts backoff without forgetting confirmed world");
  for(int i=0;i<5;i++)Check(!Tick(false,b),"backoff suppresses immediate retry");
  UnityEngine.Time.realtimeSinceStartup+=6;Check(Tick(false,b),"fresh host world retries after transfer failure");
  File.Delete(path);follow.Close();frame.world=null;Check(!Tick(false)&&!File.Exists(path),"legacy or loading frame without epoch cannot drive automatic follow");
  frame.world=c;Tick();Tick();Check(Tick(),"unseeded manual same-mission connection receives a fresh checkpoint instead of guessing epoch");
  File.Delete(path);follow.Close();Check(!follow.Tick(null,frame,0,false,null,null,false,_=>{}),"explicit disconnect clears scene following");
  frame.world=c;frame.phase="playing";Tick(true,c);
  frame.world=null;frame.phase="loading";Tick();Tick();Check(!Tick()&&!File.Exists(path),"loading gap is not a host menu return");
  frame.phase="menu";Tick();Tick();Check(Tick(),"stable host menu leaves previously confirmed task");
  string menuGeneration;
  using(var request=JsonDocument.Parse(File.ReadAllText(path))){var r=request.RootElement;menuGeneration=r.GetProperty("generation").GetString();Check(r.GetProperty("operation").GetString()=="return-menu","menu return never requests a nonexistent checkpoint");}
  File.Delete(path);File.WriteAllText(Path.Combine(SessionHost.DirectoryPath,"live-import-result.json"),JsonSerializer.Serialize(new{generation=menuGeneration,phase="menu-ready"}));
  Tick();Tick();Check(!Tick()&&!File.Exists(path),"confirmed menu stays idle without repeated returns");
  frame.phase="playing";frame.world=c;Tick();Tick();Check(!Tick()&&!File.Exists(path),"late pre-menu scene cannot resurrect old task");
  frame.world=a;Tick();Tick();Check(Tick(),"new host task after menu starts a new checkpoint handoff");
  File.Delete(path);follow.Close();
  frame.world=a;frame.phase="playing";Tick(true,a);Tick(true,a);Tick(true,a);
  frame.world=b;frame.phase="loading";frame.reloading=true;
  Check(Tick(true,a),"accepted reload announcement starts receiving before host finishes load");
  using(var request=JsonDocument.Parse(File.ReadAllText(path)))Check(!request.RootElement.GetProperty("returnToMenu").GetBoolean(),"early reload stays in task");
  Check(!Tick(true,a),"repeated early announcements cannot start another download");File.Delete(path);
  Tick(true,b);frame.reloading=false;frame.phase="paused";Tick(true,b);Tick(true,b);
  Check(!Tick(true,b)&&!File.Exists(path),"host completion of reserved world does not trigger a second handoff");follow.Close();
  File.Delete(path);follow.Close();frame=new LobbyProtocol.Frame{room=new string('e',32),world=new string('f',32),revision=1,phase="loading",mission=123,entering=true};
  Check(LobbyProtocol.Valid(frame),"entry announcement is valid before playable crew exists");
  Check(follow.Tick(room,frame,0,false,null,null,false,_=>{}),"entry begins from main menu before host intro finishes");
  using(var entryRequest=JsonDocument.Parse(File.ReadAllText(path)))Check(entryRequest.RootElement.GetProperty("returnToMenu").GetBoolean()&&entryRequest.RootElement.GetProperty("reloadMission").GetInt32()==0,"fresh entry never uses locked same-mission quickload");
  frame.reloading=true;Check(!LobbyProtocol.Valid(frame),"entry and rewind flags cannot conflict");
  File.Delete(path);follow.Close();frame=new LobbyProtocol.Frame{world=a,phase="playing",mission=42};Tick(true,a);Tick(true,a);
  frame.world=b;frame.phase="loading";frame.reloading=true;Check(Tick(true,a),"old reload queued for supersession test");
  int cancellations=0;bool permit=false;
  bool Sup(){cancellations++;if(!permit)return false;File.Delete(path);return true;}
  bool NewTick(){UnityEngine.Time.realtimeSinceStartup+=.6f;return follow.Tick(room,frame,42,false,room.AbsoluteUri,a,true,_=>{},Sup);}
  frame.world=a;frame.phase="playing";frame.reloading=false;NewTick();NewTick();NewTick();Check(cancellations==0,"old confirmed frame cannot cancel an early reload");
  frame.world=c;frame.mission=-379511569;NewTick();NewTick();NewTick();Check(cancellations==1&&File.Exists(path),"native transition can refuse cancellation without losing old request");
  permit=true;Check(NewTick(),"stable new ship supersedes unfinished reload");
  using(var shipRequest=JsonDocument.Parse(File.ReadAllText(path)))Check(shipRequest.RootElement.GetProperty("world").GetString()==c&&shipRequest.RootElement.GetProperty("returnToMenu").GetBoolean(),"ship uses a fresh cross-scene checkpoint");
  File.Delete(path);follow.Close();frame=new LobbyProtocol.Frame{world=a,phase="playing",mission=42};Tick(true,a);Tick(true,a);
  int returns=0;bool spawned=false;frame.world=b;frame.mission=-379511569;
  bool HubTick(){UnityEngine.Time.realtimeSinceStartup+=.6f;return follow.Tick(room,frame,42,false,room.AbsoluteUri,a,false,_=>{},null,()=>{returns++;return true;},()=>spawned);}
  HubTick();HubTick();HubTick();Check(returns==0&&File.Exists(path),"ship return imports authoritative progress instead of reusing stale guest unlocks");
  Check(!follow.Matches(room,b),"ship epoch not confirmed before native spawn");
  spawned=true;HubTick();Check(!follow.Matches(room,b),"native spawn alone cannot confirm a pending verified profile handoff");
  follow.Close();File.Delete(path);frame=new LobbyProtocol.Frame{room=new string('d',32),world=a,phase="playing",mission=42};Tick(true,a);Tick(true,a);Tick(true,a);
  frame.phase="loading";frame.entering=true;
  Check(Tick(false,a),"post-selection checkpoint imports even when preparation already joined this world");
  using(var req=JsonDocument.Parse(File.ReadAllText(path))){
   Check(!req.RootElement.GetProperty("returnToMenu").GetBoolean(),"same-world alignment stays inside current mission");
   File.WriteAllText(Path.Combine(SessionHost.DirectoryPath,"live-import-result.json"),JsonSerializer.Serialize(new{generation=req.RootElement.GetProperty("generation").GetString(),phase="ready-to-load",checkpoint="checkpoint-test"}));
  }
  File.Delete(path);File.WriteAllText(Path.Combine(SessionHost.DirectoryPath,"session-load-result.json"),JsonSerializer.Serialize(new{generation="checkpoint-test",status="loaded"}));
  Tick(false,a);Tick(false,a);Check(!Tick(false,a)&&!File.Exists(path)&&follow.Matches(room,a),"loaded receipt releases same-world pending and repeated announcement cannot reimport");
  // Regression: guest tutorial starts its own rewind before the host announcement arrives.
  follow.Close();File.Delete(path);frame=new LobbyProtocol.Frame{room=new string('d',32),world=a,phase="playing",mission=42};Tick(true,a);Tick(true,a);Tick(true,a);
  frame.world=b;frame.phase="loading";frame.reloading=true;
  UnityEngine.Time.realtimeSinceStartup+=1;
  Check(!follow.Tick(room,frame,0,false,room.AbsoluteUri,a,false,_=>{},nativeTransition:true)&&!File.Exists(path),"transient native mission zero cannot queue a menu return");
  Check(follow.Matches(room,a),"local native reload retains confirmed host epoch");
  Check(Tick(false,a),"after native load finishes same-mission checkpoint still follows");
  string retryGeneration;
  using(var req=JsonDocument.Parse(File.ReadAllText(path))){
   Check(!req.RootElement.GetProperty("returnToMenu").GetBoolean()&&req.RootElement.GetProperty("reloadMission").GetInt32()==42,"tutorial rewind stays in mission after local loading gap");
   retryGeneration=req.RootElement.GetProperty("generation").GetString();
  }
  File.Delete(path);
  File.WriteAllText(PathForResult("live-import-result.json"),JsonSerializer.Serialize(new{generation=retryGeneration,checkpoint="failed-load",phase="ready-to-load"}));
  File.WriteAllText(PathForResult("session-load-result.json"),JsonSerializer.Serialize(new{generation="failed-load",status="failed"}));
  int resets=0;UnityEngine.Time.realtimeSinceStartup+=1;
  Check(!follow.Tick(room,frame,42,false,room.AbsoluteUri,a,true,_=>{},()=>{resets++;return true;})&&resets==1,"native load failure retires failed bootstrap without confirming wrong world");
  UnityEngine.Time.realtimeSinceStartup+=6;Check(Tick(false,a),"failed native load can retry the same host world after backoff");
  return checks;
 }
}
