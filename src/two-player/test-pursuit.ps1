# Production pursuit/health decisions on inert actors, no game or network side effects.
$ErrorActionPreference='Stop'
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'GuardPursuit.cs'))
$authority=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'GuardAuthority.cs'))
$authority=$authority[$authority.IndexOf('internal sealed class GuardAuthority')..($authority.Length-1)] -join ''
$fixture=@"
internal sealed class GuardIcons {internal GuardIcons(Func<MiCharacter,bool> owns){} internal void Tick(){} internal void Release(){} internal void Record(MiCharacter c,int mask){}}
namespace UnityEngine {
 public struct Vector3 {public float x,y,z;
 public static Vector3 operator -(Vector3 a,Vector3 b)=>new Vector3{x=a.x-b.x,y=a.y-b.y,z=a.z-b.z};
 public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;
 public static float Distance(Vector3 a,Vector3 b)=>(float)Math.Sqrt(Dot(a-b,a-b));
 public Vector3 normalized{get{float len=Distance(this,default);return len>0?new Vector3{x=x/len,y=y/len,z=z/len}:default;}}
 public static Vector3 MoveTowards(Vector3 a,Vector3 b,float cap){var d=(b-a).normalized;float n=Math.Min(cap,Distance(a,b));return new Vector3{x=a.x+d.x*n,y=a.y+d.y*n,z=a.z+d.z*n};}}
 public struct Quaternion {public Vector3 eulerAngles;public static Quaternion Euler(float x,float y,float z)=>new Quaternion{eulerAngles=new Vector3{x=x,y=y,z=z}};}
 public static class Time {public static float time,realtimeSinceStartup;}
}
namespace UnityEngine.AI {
 public struct Hit { public Vector3 position;public int mask; }
 public struct NavMeshQueryFilter { public int areaMask; public int agentTypeID {set{throw new NotSupportedException("Method unstripping failed");}} }
 public static class NavMesh {public static bool blocked; public static int lastAgent;public static float shore=float.PositiveInfinity;
 public static bool SamplePosition(Vector3 p,out Hit h,float range,int area){h=new Hit{position=p,mask=p.x>=shore?2:1};return true;}
 public static bool SamplePositionFilter(Vector3 p,out Hit h,float range,int agent,int mask){lastAgent=agent;return SamplePosition(p,out h,range,mask);}
 public static bool RaycastFilter(Vector3 p,Vector3 q,out Hit h,int agent,int mask){h=default;return blocked;}}
}
namespace HarmonyLib {public class HarmonyPatch:Attribute {public HarmonyPatch(){}public HarmonyPatch(Type t,string n){}}}

namespace Il2CppSystem {public class Nullable<T>{public Nullable(){throw new Exception("Unavailable empty IL2CPP nullable constructor");} public Nullable(T x){HasValue=true;} public bool HasValue; }}
public class TrackedObject {public MiCharacter objRef;public int eType;public Detection.DetectionState m_detectionState;public Detection.DetectionState state=>m_detectionState;}
public class DetectionData {public string token;} public class DetectionDataSwitcher {public DetectionData m_dataInstanceCurrent;} public static class NativeDetectionProfile {public static string Capture(DetectionData d)=>d?.token??"-";public static bool TryBuild(string s,out DetectionData d){d=new(){token=s};return true;}} public class Detection {public DetectionDataSwitcher m_dataSwitcher=new();public DetectionData applied;public void applyData(DetectionData d,bool instant){applied=d;}public float m_fDetectionRange;public float fDetectionRangeCurrent=>m_fDetectionRange;public bool shown;public void setViewConeActive(bool v){shown=v;}public void updateViewCone(){}public int footprintClears;public void clearFootprint(){footprintClears++;}public MiCharacter character;public enum DetectionState {NotVisible,Registering,WaitForRaycast,Visible,PreDetected,Detected};public List<TrackedObject> m_liObjectsInRange=new List<TrackedObject>();public bool updateObjState(TrackedObject o,DetectionState state){o.m_detectionState=state;return true;}public TrackedObject addObjectToTrack(MiCharacter c){var e=new TrackedObject{objRef=c};m_liObjectsInRange.Add(e);return e;}}
public class DetectionOrientation {static int next;public IntPtr Pointer=new(++next);public float fOrientation;public float bodyYaw;public Quaternion qDetectionRot=>Quaternion.Euler(0,fOrientation+bodyYaw,0);public TrackedObject m_objFocus;public void setFocusObject(int type,TrackedObject entry){m_objFocus=entry;}public void endFocus(){m_objFocus=null;}}
public class Sight:DetectionOrientation {}
public class Navigation {public int agentType=3,areaMask=-1;public void setOrientation(Quaternion q){}}
public static class Mathf {public static float Abs(float x)=>Math.Abs(x);public static float Clamp(float x,float lo,float hi)=>Math.Clamp(x,lo,hi);public static float Clamp01(float x)=>Clamp(x,0,1);public static float DeltaAngle(float x,float y){float d=(y-x)%360;return d>180?d-360:d< -180?d+360:d;}}
public class Transform {public Vector3 position,eulerAngles;public Quaternion rotation{get=>Quaternion.Euler(eulerAngles.x,eulerAngles.y,eulerAngles.z);set=>eulerAngles=value.eulerAngles;}}
public class Entity {}
public static class GuestMove {public static string Identity(MiCharacter c)=>c?.id;}
public class MiCharacter : Entity { public MiCharacter(){controller.character=this;controller.m_ai.owner=this;} public IntPtr Pointer=>new IntPtr(GetInstanceID());
 public string id="42";public Movement movement=new Movement();public Navigation navigation=new Navigation();public Transform transform=new Transform();public MiCharacterControllerAI controller=new MiCharacterControllerAI();
 public MiCharacterMovementType movementType=new MiCharacterMovementType();
 public bool climbing;
 public Dictionary<UIWorldInfoBatchIconID,UIWorldInfoBatchIconData> icons=new Dictionary<UIWorldInfoBatchIconID,UIWorldInfoBatchIconData>();
 public UIWorldInfoBatchIconData getIconData(UIWorldInfoBatchIconID id)=>icons.TryGetValue(id,out var data)?data:null;
 public UIWorldInfoBatchIconData showOrHideIcon(UIWorldInfoBatchIconID id,bool show){if(!icons.TryGetValue(id,out var data))icons[id]=data=new UIWorldInfoBatchIconData();data.eState=show?UIData.State.Connected:UIData.State.Disconnected;return data;}
 public MiCharacterControllerAI controllerAI=>npc?controller:null;public MiCharacterHealth health=new MiCharacterHealth();public bool civilian;public bool bIsCivilian()=>civilian;public HighlighterGroup highlighterGroup=new();
 public string name="actor";public bool npc=true;public bool bControllerAI()=>npc;public bool bAsCharacter(out MiCharacter c){c=this;return true;}
 public int warps;public void warp(Vector3 p,Quaternion r){warps++;transform.position=p;}public int GetInstanceID()=>GetHashCode();
}
public static class CoopWaitLock {public static bool CanHoldTransient=true;}
public static class MiCutsceneHandler {public static bool bCutsceneRunning;}
public static class SaveProcessManager {public static bool bLoading;}
namespace Mimimi.HighlighterSystem {public enum HighlighterTrigger{ViewConeInputActiveChar}public enum HighlighterType{Enemy}}
public class HighlighterGroup {public int frames;public void enableHighlighterTriggerAndTypeFrame(Mimimi.HighlighterSystem.HighlighterTrigger trigger,Mimimi.HighlighterSystem.HighlighterType type){frames++;}}
public class ViewConeState {public static bool bInstanceExists=true;public static ViewConeState instance=new();public enum ViewConeActivationMode{RegularAI,HighPrioAI};public MiCharacterControllerAI charActive;public ViewConeActivationMode eVCModeActive;public int activations;public void activateVC(MiCharacterControllerAI c,ViewConeActivationMode mode,bool active){activations++;charActive=c;eVCModeActive=mode;}public void deactivateVC(MiCharacterControllerAI c){if(charActive==c)charActive=null;}}
public class PriorityLockBool { static int next;public IntPtr Pointer=new IntPtr(++next);public int releases;
 public Dictionary<int,bool> owners=new Dictionary<int,bool>();
 public void set(bool value,int owner,string name,bool notify){owners[owner]=value;}
 public bool release(int owner){releases++;return owners.Remove(owner);}
 public bool currentValue{get{foreach(var x in owners.Values)if(x)return true;return false;}}
}
public class AIHandler { static int next;public IntPtr Pointer=new IntPtr(++next);
 public void setMovementType(MiCharacterMovementType.MovementStyle style,MiCharacterMovementType.MovementModifier modifier){owner.movementType.eMovementStyle=style;owner.movementType.eMovementModifier=modifier;}
 public MiCharacter owner; public bool allowExternal=true,dropProcess,dropTarget,wrongTarget;
 public bool setSkillExtern(Skill.SkillType kind,MiCharacterTarget target,NPCClique.SpreadType spread,bool pause,bool process,object cb,object data){
  if(!allowExternal)return false;
  if(spread!=NPCClique.SpreadType.None||pause)throw new Exception("replica order must not spread or pause native routine owners");
  var cmd=new SkillCommand{m_skill=new Skill{eSkillType=kind},m_target=target};
   if(dropTarget)cmd.m_target=null;
   if(wrongTarget)cmd.m_target=new MiCharacterTarget(new Vector3{x=100});
   if(!process){owner.controller.skillCommand=cmd;return true;}
   if(dropProcess)return true;
   // Native process dispatch preserves the input slot. Production must
   // explicitly remove an old local patrol order before replacing it.
   owner.controller.skillCommandProcess=cmd;
  if(kind==Skill.SkillType.GoTo){Mimimi.MiScript.MiScriptLibraryProject.moves++;owner.controller.v3CurrentTargetPosition=target.v3Target;owner.movement.target=cmd.m_target;owner.movement.isStoppedOrInactive=cmd.m_target==null;}else Melee.attacks++;
  return true;
 }
 public PriorityLockBool m_varLockAIBlocked=new PriorityLockBool();
 public enum RoutineBlockedReason {None=0,Dialog=32};public int routine,reaction;
 public bool bAIIsBlocked()=>m_varLockAIBlocked.currentValue;
 public void setRoutineBlocked(bool on,RoutineBlockedReason r){routine=on?routine|(int)r:routine&~(int)r;}
 public void setDetectionReactionSkillBlocked(bool on,RoutineBlockedReason r){reaction=on?reaction|(int)r:reaction&~(int)r;}

 public enum AlertState {Idle,Alert,Suspicious,Distracted};public AlertState eAlertState;public MiCharacter transTarget;public object alarmSquad;
 public void setTarget(MiCharacter c,object v){transTarget=c;}public void setAlertState(AlertState v){eAlertState=v;}
}
public class Skill {public enum SkillType {Follow,GoToRoutinePoint,GoTo,Investigate,InvestigateBadFortune,FollowFootprints,WalkTowards,Gun,GunGatling,Kill,CounterMeleeKill,Knockout,SnipeKill};public SkillType eSkillType;}
public class Movement{public Target target;public bool isStoppedOrInactive=true;public float fMaxSpeed=4f,fSpeedMultiplier=1f,m_fMaxSpeedReal=4f;public int eMovementDriver;}
public static class InteractionSync{public static bool Climbing(MiCharacter c)=>c.climbing;}
public class MiCharacterMovementType{public int eMovementType;public enum MovementStyle{Crouch,Run,Walk};public enum MovementModifier{None=0,Investigate=9,InvestigateBush=10,Stunned=12};public MovementStyle eMovementStyle=MovementStyle.Walk;public MovementModifier eMovementModifier;}
public enum UIWorldInfoBatchIconID{Alarm,Investigate,AlarmingAction,AlarmBell,Knockout}
public class UIData{public enum State{Disconnected,Connected};}
public class UIWorldInfoBatchIconData{public UIData.State eState;}
public class NPCClique {public enum SpreadType{None,Spread};}
public class MiCharacterTarget:Target {public MiCharacterTarget(Vector3 p){v3Target=p;}public MiCharacterTarget(MiCharacter c,object unused){m_entityTarget=c;}}
public class Target {public Vector3 v3Target;public MiCharacter m_entityTarget;}
public class MiCharacterControllerAI:MiCharacterController{}
public class SkillCommand {public Skill m_skill=new Skill();public Target m_target=new Target();public IntPtr Pointer=new IntPtr(1);public enum ExecutionModifier{Run=1,WasCrawling=2};public int flags;public void setModifier(ExecutionModifier flag,bool on){flags=on?flags|(int)flag:flags&~(int)flag;} }
public class MiCharacterController {public Detection m_detection=new Detection();public Sight m_detectionOrientation=new Sight();public AIHandler m_ai=new AIHandler();
 private SkillCommand input,process;public SkillCommand skillCommandActive;
 // Observed in GameAssembly and in the live mission: BOTH setters normalize GoTo.
 static SkillCommand Normalize(SkillCommand c){if(c?.m_skill?.eSkillType==Skill.SkillType.GoTo)c.m_skill=null;return c;}
 public SkillCommand skillCommand{get=>input;set{input=Normalize(value);process=null;}}
 public SkillCommand skillCommandProcess{get=>process;set{process=Normalize(value);}}
 public Vector3 v3CurrentTargetPosition;public MiCharacter character;
 public void Step(float dt){var c=process??input;if(c!=null&&c.m_skill==null&&c.m_target!=null){float speed=character.movementType.eMovementStyle==MiCharacterMovementType.MovementStyle.Run?4f:1.3f;character.transform.position=Vector3.MoveTowards(character.transform.position,c.m_target.v3Target,speed*dt);if(Vector3.Distance(character.transform.position,c.m_target.v3Target)<.01f)character.movement.isStoppedOrInactive=true;}}
}
public class SkillInvestigate {public MiCharacter character;}
public class MiCharacterHealth {public enum HealthState{Alive,Dead,Knockout,PreDying,Dying};public HealthState eHealthState;public int iHealth=3,iHealthMax=3;public MiCharacter character;public void comeTo(){eHealthState=HealthState.Alive;}}
public class MiCharacterImpulseHandler {public class ImpulseOptionsDamage{public MiCharacter charOrigin;}}
public class Probe {public void PlayerPointProcess(MiCharacter a,SkillCommand c){}public bool AllowGuardOrder(MiCharacter a,SkillCommand c)=>true;public void SyncSight(MiCharacter c,MiCharacter target,ref Detection.DetectionState state){}public bool MirroredCrew(MiCharacter c)=>true;public void SearchPoint(MiCharacter c,ref Vector3 p){}public void GuardProcess(MiCharacter c,SkillCommand cmd){}}
public static class CannonStateSync{public static bool ReconcileWake(MiCharacter a,string s)=>false;}
public static class NativeInput{public static Probe Probe;}
public static class MoveProbePlugin{public static class ProbeLog{public static void LogInfo(string s){}public static void LogWarning(string s){throw new Exception(s);}}}
public static class Melee{public static bool Point(Skill.SkillType k)=>false;public static string GuardId(MiCharacter c)=>c.id;public static int attacks;public static void Execute(MiCharacter a,MiCharacter b,Skill.SkillType k){attacks++;}}
public static class CrewSync{public static string Position(Vector3 p)=>p.x+" 0 0";public static Vector3 ReadPosition(string[] f,int i)=>new Vector3{x=float.Parse(f[i],CultureInfo.InvariantCulture)};}
namespace Mimimi.MiScript {public static class MiScriptLibraryProject {
 public static int moves,hits;public static void CharacterExecuteSkill(MiCharacter c,Skill.SkillType k,Vector3 v){throw new Exception("NPC script helper must not silently swallow a guard order");}
 public static void CharacterHeal(MiCharacter c,int d){c.health.iHealth+=d;}public static void CharacterDamage(MiCharacter c,int d){hits++;c.health.iHealth-=d;}
 public static void CharacterKill(MiCharacter c){c.health.eHealthState=MiCharacterHealth.HealthState.Dead;}
 public static void CharacterKnockout(MiCharacter c){c.health.eHealthState=MiCharacterHealth.HealthState.Knockout;}
 public static void CharacterRevive(MiCharacter c){c.health.eHealthState=MiCharacterHealth.HealthState.Alive;}
}}
public static class PursuitTests{
 static int checks;
 static void Check(bool b,string why){checks++;if(!b)throw new Exception(why);}
 static void DetectionPagingRegression(){
  var a=new MiCharacter{npc=false};var b=new MiCharacter{npc=false};const string quiet="None 0 0 0 0 0 0";
  Dictionary<string,MiCharacter> Crowd(int count){var guards=new Dictionary<string,MiCharacter>();for(int n=0;n<count;n++)guards[n.ToString("D2")]=new MiCharacter();return guards;}
  List<string> Rows(string packet){var words=packet.Trim().Split(' ');var ids=new List<string>();for(int n=6;n<words.Length;n+=20)ids.Add(words[n]);return ids;}
  void DetectionCrowd(int detecting){
   var guards=Crowd(64+detecting);var sender=new GuardPursuit();
   for(int n=64;n<guards.Count;n++)guards[n.ToString("D2")].controller.m_detection.addObjectToTrack(a).m_detectionState=Detection.DetectionState.PreDetected;
   var seen=new Dictionary<string,float>();var counts=new Dictionary<string,int>();float detectionGap=0,ordinaryGap=0;
   float started=Time.realtimeSinceStartup;
   for(int frame=0;frame<160;frame++){
    Time.realtimeSinceStartup+=.05f;
    var ids=Rows(sender.Write(guards,a,b,quiet,2048));
    Check(ids.Count<=4,"detection priority does not increase packet row limit");
    foreach(string id in ids){float previous=seen.TryGetValue(id,out float last)?last:started;
     if(int.Parse(id)>=64)detectionGap=Math.Max(detectionGap,Time.realtimeSinceStartup-previous);
     else if(seen.ContainsKey(id))ordinaryGap=Math.Max(ordinaryGap,Time.realtimeSinceStartup-previous);
     seen[id]=Time.realtimeSinceStartup;counts[id]=counts.TryGetValue(id,out int value)?value+1:1;}
   }
   Check(seen.Count==guards.Count,"detection priority preserves fairness for every ordinary enemy");
   foreach(var row in counts)Check(row.Value>=2,"ordinary enemies continue to refresh under sustained detection");
   Check(detectionGap<.55f,"one/two detecting enemies stay inside sight lease with 64 ordinary enemies and normal budget");
   Console.WriteLine("Detection paging (synthetic 64 ordinary + "+detecting+", 20 snapshots/s): detectorGapSeconds="+detectionGap.ToString("F3",CultureInfo.InvariantCulture)+" ordinaryGapSeconds="+ordinaryGap.ToString("F3",CultureInfo.InvariantCulture));
  }
  DetectionCrowd(1);DetectionCrowd(2);
  var clipped=Crowd(6);clipped["04"].controller.m_detection.addObjectToTrack(a).m_detectionState=Detection.DetectionState.PreDetected;
  clipped["05"].controller.m_detection.addObjectToTrack(a).m_detectionState=Detection.DetectionState.PreDetected;
  int oneBudget=new GuardPursuit().Write(new Dictionary<string,MiCharacter>{{"00",clipped["00"]}},a,b,quiet,2048).Length;
  var tightSender=new GuardPursuit();var first=Rows(tightSender.Write(clipped,a,b,quiet,oneBudget));
  Check(first.Count==1&&first[0]=="00","single-row budget gives the first slot to ordinary rotation");
  Time.realtimeSinceStartup+=.05f;var expanded=Rows(tightSender.Write(clipped,a,b,quiet,2048));
  Check(expanded.Count==4&&expanded[1]=="04","planned but clipped detector remains oldest until actually transmitted");
  tightSender=new GuardPursuit();var tinySeen=new HashSet<string>();
  for(int i=0;i<18;i++){Time.realtimeSinceStartup+=.05f;var ids=Rows(tightSender.Write(clipped,a,b,quiet,oneBudget));Check(ids.Count==1,"single-row budget is respected during detection priority");foreach(string id in ids)tinySeen.Add(id);}
  Check(tinySeen.Count==clipped.Count,"single-row packets do not starve either ordinary or detecting enemies");

  var eligibility=Crowd(65);var watched=eligibility["64"];var seenCrew=watched.controller.m_detection.addObjectToTrack(a);
  seenCrew.m_detectionState=Detection.DetectionState.PreDetected;var eligibilitySender=new GuardPursuit();
  void Warm(){for(int i=0;i<25;i++){Time.realtimeSinceStartup+=.05f;eligibilitySender.Write(eligibility,a,b,quiet,2048);}}
  int Frequency(){int count=0;for(int i=0;i<8;i++){Time.realtimeSinceStartup+=.05f;if(Rows(eligibilitySender.Write(eligibility,a,b,quiet,2048)).Contains("64"))count++;}return count;}
  Warm();Check(Frequency()>=7,"active enemy detection gets repeated refresh rather than only its first transition");
  seenCrew.m_detectionState=Detection.DetectionState.NotVisible;Warm();Check(Frequency()<=1,"ended detection releases the refresh slot");
  seenCrew.m_detectionState=Detection.DetectionState.Detected;watched.civilian=true;Warm();Check(Frequency()<=1,"civilian observation cannot consume sustained detection priority");
  watched.civilian=false;seenCrew.m_detectionState=Detection.DetectionState.Visible;Warm();Check(Frequency()<=1,"ordinary visibility without crew focus is not active detection priority");
  watched.controller.m_detectionOrientation.m_objFocus=seenCrew;Warm();Check(Frequency()>=7,"explicit visible crew focus refreshes before PreDetected transition");
  watched.controller.m_detectionOrientation.m_objFocus=new TrackedObject{objRef=new MiCharacter()};Warm();Check(Frequency()<=1,"a non-crew focus does not consume the detection refresh slot");
  var changes=Crowd(12);var changeSender=new GuardPursuit();
  for(int i=0;i<3;i++)changeSender.Write(changes,a,b,quiet,2048);
  changes["10"].showOrHideIcon(UIWorldInfoBatchIconID.Alarm,true);changes["11"].showOrHideIcon(UIWorldInfoBatchIconID.Alarm,true);Time.realtimeSinceStartup+=.2f;
  var changedRows=Rows(changeSender.Write(changes,a,b,quiet,2048));
  Check(changedRows.Count==4&&changedRows[0]=="00"&&changedRows[1]=="10"&&changedRows[2]=="11"&&changedRows[3]=="01","without an active detector both existing changed-state slots are preserved");
 }
 static void ViewRegression(){
  var a=new MiCharacter{npc=false};var b=new MiCharacter{npc=false};
  var ca=new MiCharacter{npc=false};var cb=new MiCharacter{npc=false};
  var first=new MiCharacter{id="view-a"};var second=new MiCharacter{id="view-b"};
  var firstCopy=new MiCharacter{id=first.id};var secondCopy=new MiCharacter{id=second.id};
  var receiver=new GuardPursuit();
  var copies=new Dictionary<string,MiCharacter>{{first.id,firstCopy},{second.id,secondCopy}};
  void Send(MiCharacter c){
   // A new sender removes the rotating packet cursor from this UI lifecycle test.
   var packet=(new string(' ',19)+new GuardPursuit().Write(new Dictionary<string,MiCharacter>{{c.id,c}},a,b,"None 0 0 0 0 0 0",1024).TrimStart()).Split(' ');
   receiver.Apply(packet,copies,ca,cb,null,null);
  }
  var firstSeen=first.controller.m_detection.addObjectToTrack(a);firstSeen.m_detectionState=Detection.DetectionState.PreDetected;
  var secondSeen=second.controller.m_detection.addObjectToTrack(a);secondSeen.m_detectionState=Detection.DetectionState.PreDetected;
  ViewConeState.instance=new();ViewConeState.bInstanceExists=false;
  Send(first);ViewConeState.bInstanceExists=true;receiver.RestoreDetectionView();
  Check(ViewConeState.instance.charActive==firstCopy.controllerAI,"enemy first seen before view UI initializes is displayed when UI becomes ready");
  Send(second);receiver.RestoreDetectionView();
  Check(ViewConeState.instance.charActive==firstCopy.controllerAI,"second detecting enemy does not alternate the active priority cone every packet");
  int activations=ViewConeState.instance.activations;
  for(int i=0;i<4;i++){Send(second);Send(first);receiver.RestoreDetectionView();}
  Check(ViewConeState.instance.activations==activations&&firstCopy.highlighterGroup.frames>0,"repeated detection restores native enemy highlight without reopening or swapping cones");
  firstSeen.m_detectionState=Detection.DetectionState.NotVisible;Send(first);
  receiver.RestoreDetectionView();
  Check(ViewConeState.instance.charActive==secondCopy.controllerAI&&ViewConeState.instance.eVCModeActive==ViewConeState.ViewConeActivationMode.HighPrioAI,"ending active enemy hands priority to already-known detecting enemy without waiting another packet");
  secondSeen.m_detectionState=Detection.DetectionState.NotVisible;Send(second);receiver.RestoreDetectionView();
  Check(ViewConeState.instance.charActive==secondCopy.controllerAI&&ViewConeState.instance.eVCModeActive==ViewConeState.ViewConeActivationMode.RegularAI,"last detection end retains native ordinary cone memory");
  int enemyHighlightFrames=firstCopy.highlighterGroup.frames;
  first.civilian=true;firstCopy.civilian=true;firstSeen.m_detectionState=Detection.DetectionState.Detected;Send(first);receiver.RestoreDetectionView();
  Check(ViewConeState.instance.charActive!=firstCopy.controllerAI&&firstCopy.highlighterGroup.frames==enemyHighlightFrames,"civilian observation does not activate an enemy priority cone or red highlight");
  first.civilian=false;firstCopy.civilian=false;Send(first);firstCopy.npc=false;receiver.RestoreDetectionView();
  Check(ViewConeState.instance.charActive==null,"possession releases enemy priority cone instead of retaining a friendly cone");
  firstCopy.npc=true;Send(first);firstCopy.health.eHealthState=MiCharacterHealth.HealthState.Knockout;receiver.RestoreDetectionView();
  Check(ViewConeState.instance.charActive==null,"knocked-out enemy does not retain an automatic cone");
  firstCopy.health.eHealthState=MiCharacterHealth.HealthState.Alive;Send(first);SaveProcessManager.bLoading=true;receiver.RestoreDetectionView();SaveProcessManager.bLoading=false;receiver.RestoreDetectionView();
  Check(ViewConeState.instance.charActive==null,"loading discards queued enemy UI until another authoritative sample");
  Send(first);MiCutsceneHandler.bCutsceneRunning=true;receiver.RestoreDetectionView();MiCutsceneHandler.bCutsceneRunning=false;receiver.RestoreDetectionView();
  Check(ViewConeState.instance.charActive==null,"cutscene discards queued enemy UI until another authoritative sample");
  receiver.Release();

  // Real-time native orientation updates can run between receipt of the last
  // guard sample and the next scene/controller lifecycle notification.
  void StaleSample(string reason,Action<MiCharacter> interrupt,Action restore){
   var host=new MiCharacter{id="stale"};var copy=new MiCharacter{id="stale"};var h=new GuardPursuit();var g=new GuardPursuit();
   host.controller.m_detectionOrientation.fOrientation=45;host.transform.eulerAngles=new Vector3{y=30};
   var packet=(new string(' ',19)+h.Write(new Dictionary<string,MiCharacter>{{host.id,host}},a,b,"None 0 0 0 0 0 0",1024).TrimStart()).Split(' ');
   g.Apply(packet,new Dictionary<string,MiCharacter>{{copy.id,copy}},ca,cb,null,null);
   var oldSight=copy.controller.m_detectionOrientation;oldSight.fOrientation=123;copy.transform.eulerAngles=new Vector3{y=67};
   interrupt(copy);GuardPursuit.RestoreSight(oldSight);
   Check(oldSight.fOrientation==123&&copy.transform.eulerAngles.y==67,reason+" cannot be overwritten by a stale guard direction");
   restore();GuardPursuit.RestoreSight(oldSight);
   Check(oldSight.fOrientation==123&&copy.transform.eulerAngles.y==67,reason+" invalidates the sample until a new host snapshot");g.Release();
  }
  StaleSample("scene loading",_=>SaveProcessManager.bLoading=true,()=>SaveProcessManager.bLoading=false);
  StaleSample("native cutscene",_=>MiCutsceneHandler.bCutsceneRunning=true,()=>MiCutsceneHandler.bCutsceneRunning=false);
  StaleSample("possession",c=>c.npc=false,()=>{});
  StaleSample("AI controller replacement",c=>c.controller=new MiCharacterControllerAI(),()=>{});
  StaleSample("expired sample",_=>Time.realtimeSinceStartup+=.6f,()=>{});

  var smoothHost=new MiCharacter{id="smooth"};var smoothCopy=new MiCharacter{id="smooth"};var smoothSender=new GuardPursuit();var smoothReceiver=new GuardPursuit();
  void SmoothSend(float yaw){smoothHost.transform.eulerAngles=new Vector3{y=yaw};smoothHost.controller.m_detectionOrientation.fOrientation=yaw;
   var packet=(new string(' ',19)+smoothSender.Write(new Dictionary<string,MiCharacter>{{smoothHost.id,smoothHost}},a,b,"None 0 0 0 0 0 0",1024).TrimStart()).Split(' ');
   smoothReceiver.Apply(packet,new Dictionary<string,MiCharacter>{{smoothCopy.id,smoothCopy}},ca,cb,null,null);}
  SmoothSend(350);Time.realtimeSinceStartup+=.1f;SmoothSend(10);Time.realtimeSinceStartup+=.05f;
  GuardPursuit.RestoreSight(smoothCopy.controller.m_detectionOrientation);
  Check(Math.Abs(Mathf.DeltaAngle(smoothCopy.controller.m_detectionOrientation.qDetectionRot.eulerAngles.y,0))<.1f&&Math.Abs(Mathf.DeltaAngle(smoothCopy.transform.eulerAngles.y,0))<.1f,"head and body interpolate across 360 by the shortest turn");
  smoothReceiver.Release();smoothCopy.controller.m_detectionOrientation.fOrientation=88;smoothCopy.transform.eulerAngles=new Vector3{y=77};GuardPursuit.RestoreSight(smoothCopy.controller.m_detectionOrientation);
  Check(smoothCopy.controller.m_detectionOrientation.fOrientation==88&&smoothCopy.transform.eulerAngles.y==77,"disconnect releases both native body and view direction");
  // Characterize, do not hide, the finite paging/lease boundary. This is a
  // synthetic fixed 20 Hz delivery schedule, not a real network benchmark.
  var crowd=new Dictionary<string,MiCharacter>();for(int i=0;i<64;i++)crowd[i.ToString("D2")]=new MiCharacter();
  var pager=new GuardPursuit();var seenAt=new Dictionary<string,float>();float maxGap=0;int maxRows=0;
  for(int i=0;i<64;i++){
   Time.realtimeSinceStartup+=.05f;
   var words=pager.Write(crowd,a,b,"None 0 0 0 0 0 0",2048).Trim().Split(' ');
   maxRows=Math.Max(maxRows,(words.Length-6)/20);
   for(int n=6;n<words.Length;n+=20){string id=words[n];if(seenAt.TryGetValue(id,out float last))maxGap=Math.Max(maxGap,Time.realtimeSinceStartup-last);seenAt[id]=Time.realtimeSinceStartup;}
  }
  Check(seenAt.Count==64&&maxRows==4,"crowded production paging remains fair and bounded to four enemies per frame");
  Console.WriteLine("Paging boundary (synthetic 64 guards, 20 snapshots/s): maxGapSeconds="+maxGap.ToString("F3",CultureInfo.InvariantCulture)+" sightLeaseSeconds=0.550");
 }
 public static void Run(){
 GuardAuthority.VerifyNativeLocks();
 var host=new GuardPursuit();var guest=new GuardPursuit();var a=new MiCharacter();var b=new MiCharacter();var c=new MiCharacter();
 var ca=new MiCharacter();var cb=new MiCharacter();var guard=new MiCharacter();
 var registry=new Dictionary<string,MiCharacter>{{"42",c}};var copies=new Dictionary<string,MiCharacter>{{"42",guard}};
 c.controller.m_detection.addObjectToTrack(b).m_detectionState=Detection.DetectionState.PreDetected;
 c.controller.m_detectionOrientation.fOrientation=41.5f;
 c.controller.m_ai.eAlertState=AIHandler.AlertState.Alert;c.controller.m_ai.transTarget=b;
 c.controller.skillCommandActive=new SkillCommand();c.controller.skillCommandActive.m_skill.eSkillType=Skill.SkillType.Investigate;
 host.SetWaypoint("42",new Vector3{x=12},8);b.health.iHealth=2;
 string[] Packet(){return (new string(' ',19)+host.Write(registry,a,b,"Alarm 0 0 0 0 0 0",540).TrimStart()).Split(' ');}
 void Apply(string[] packet){guest.Apply(packet,copies,ca,cb,null,null);}
 var search=Packet();Apply(search);Apply(search);
 Check(guest.Waypoint("42",out var goal)&&goal.x==0&&MiScriptLibraryProject.moves==1,"stationary host with a distant random goal cannot start guest search early");
 c.transform.position=new Vector3{x=1.7f};Time.time+=0.35f;Time.realtimeSinceStartup+=0.35f;
 var moving=Packet();Apply(moving);Apply(moving);
 Check(guard.warps==0 && guard.transform.position.x==0 && guard.controller.v3CurrentTargetPosition.x==1.7f &&
    MiScriptLibraryProject.moves==2,"measured 1.6m drift fixes with one confirmed path and no warps");
 // The native guest briefly chose another point. It must return on foot to
 // the path actually reached by the host, even when the error is large.
 guard.transform.position=new Vector3{x=18};guard.controller.v3CurrentTargetPosition=new Vector3{x=23};
 guard.movement.target=new MiCharacterTarget(new Vector3{x=23});
 Time.time+=0.35f;Time.realtimeSinceStartup+=0.35f;Apply(Packet());
 Check(guard.warps==0&&guard.controller.v3CurrentTargetPosition.x==1.7f&&MiScriptLibraryProject.moves==3,"search override corrected on foot");
 c.transform.position=new Vector3{x=11.8f};Time.time+=0.35f;Time.realtimeSinceStartup+=0.35f;Apply(Packet());
 Check(guest.Waypoint("42",out goal)&&goal.x==12&&guard.controller.v3CurrentTargetPosition.x==12&&guard.warps==0,"confirm final step of search route");
 Check(guard.controller.m_detection.m_liObjectsInRange.Count==1 && guard.controller.m_detection.m_liObjectsInRange[0].state==Detection.DetectionState.PreDetected,"pre-detection before shot");
 Check(guard.controller.m_detectionOrientation.fOrientation==41.5f,"independent native sight direction");
 guard.controller.m_detectionOrientation.fOrientation=179f;
 GuardPursuit.RestoreSight(guard.controller.m_detectionOrientation);
 Check(guard.controller.m_detectionOrientation.fOrientation==41.5f,"native detection tick cannot overwrite fresh authoritative vision heading");
 guard.controller.m_detectionOrientation.bodyYaw=20f;GuardPursuit.RestoreSight(guard.controller.m_detectionOrientation);
 Check(guard.controller.m_detectionOrientation.qDetectionRot.eulerAngles.y==41.5f,"world sight remains aligned while local body heading differs");
 guard.controller.m_detectionOrientation.bodyYaw=0;GuardPursuit.RestoreSight(guard.controller.m_detectionOrientation);
 Check(guard.controller.m_detection.shown,"host pre-detection immediately reveals native cone");
 Check(guard.controller.m_ai.transTarget==cb && guard.controller.m_ai.eAlertState==AIHandler.AlertState.Alert,"correct pursuer and target");
 Check(cb.health.iHealth==2&&ca.health.iHealth==3&&MiScriptLibraryProject.hits==1,"correct damage without duplicate");
 c.controller.skillCommandProcess=new SkillCommand();c.controller.skillCommandProcess.m_skill.eSkillType=Skill.SkillType.Gun;c.controller.skillCommandProcess.m_target.m_entityTarget=b;
 var shot=Packet();guard.controller.m_ai.allowExternal=false;Apply(shot);Check(Melee.attacks==0,"rejected native AI shot is not marked played");
 guard.controller.m_ai.allowExternal=true;guard.controller.m_ai.dropProcess=true;Apply(shot);
 Check(Melee.attacks==0,"true external return with no native process must not mark a shot played");
 guard.controller.m_ai.dropProcess=false;Apply(shot);Apply(shot);Check(Melee.attacks==1,"same serial retries once native AI executes, then deduplicates");
 Time.time+=2;c.controller.skillCommandProcess=null;c.controller.m_ai.transTarget=null;c.controller.m_ai.eAlertState=AIHandler.AlertState.Idle;b.health.iHealth=3;
 Apply(Packet());Check(guard.controller.m_ai.transTarget==null&&cb.health.iHealth==3,"release target and restore health");
 var localDecision=new SkillCommand();localDecision.m_skill.eSkillType=Skill.SkillType.Investigate;
 Check(guard.controller.m_ai.bAIIsBlocked(),"fresh host search cannot be overwritten by local random AI order");
 Time.realtimeSinceStartup+=2.1f;guest.Tick();Check(!guard.controller.m_ai.bAIIsBlocked(),"expired authority releases native AI decisions");
 var distant=new MiCharacter();distant.transform.position=new Vector3{x=120};distant.controller.m_ai.alarmSquad=new object();
 registry.Add("99",distant);Time.realtimeSinceStartup+=1;
 Check(host.Write(registry,a,b,"Alarm 0 0 0 0 0 0",600).Contains(" 99 Idle "),"incoming squad beyond crew radius");
 Check(!guest.Visibility("42",cb,out _),"stale visibility released");
 var page=host.Write(registry,a,b,"None 0 0 0 0 0 0",22);Check(page.Length<=22,"packet byte budget");
 var many=new Dictionary<string,MiCharacter>();for(int n=0;n<8;n++)many[n.ToString()]=new MiCharacter();
 var pages=new GuardPursuit();var seen=new HashSet<string>();
 for(int n=0;n<8;n++){
   var words=pages.Write(many,a,b,"Alarm 0 0 0 0 0 0",94).Trim().Split(' ');
   Check(words.Length>=26 && words.Length<46,"small budget fits one record");seen.Add(words[6]);}
 Check(seen.Count==8,"guards at the end of a truncated page still get updates");
 // Straight search on the same confirmed leg. The native movement target
 // can span several snapshots, bounded independently of the distant goal.
 var straightHost=new GuardPursuit();var straightGuest=new GuardPursuit();
 var original=new MiCharacter();var replica=new MiCharacter();
 var source=new Dictionary<string,MiCharacter>{{"7",original}};var dest=new Dictionary<string,MiCharacter>{{"7",replica}};
 original.controller.skillCommandActive=new SkillCommand();original.controller.skillCommandActive.m_skill.eSkillType=Skill.SkillType.Investigate;
 straightHost.SetWaypoint("7",new Vector3{x=20},15);
 void SearchFrame(float hx,float gx){
   Time.time+=.35f;Time.realtimeSinceStartup+=.35f;
   original.transform.position=new Vector3{x=hx};replica.transform.position=new Vector3{x=gx};
   string data=straightHost.Write(source,a,b,"Alarm 0 0 0 0 0 0",512);
   straightGuest.Apply((new string(' ',19)+data.TrimStart()).Split(' '),dest,ca,cb,null,null);}
 SearchFrame(0,0);SearchFrame(1,.9f);SearchFrame(2,1.8f);
 Check(straightGuest.Waypoint("7",out goal)&&goal.x>2.6f&&goal.x<=3.5f&&NavMesh.lastAgent==3,"stable native search permits only bounded agent-filtered lead");
 float firstLead=goal.x;SearchFrame(2.25f,2.4f);
 Check(straightGuest.Waypoint("7",out goal)&&goal.x==firstLead,"minor lead keeps moving forward rather than re-aiming behind guard");
 NavMesh.blocked=true;SearchFrame(firstLead,firstLead);
 Check(straightGuest.Waypoint("7",out goal)&&Math.Abs(goal.x-firstLead)<.01f,"wall blocks speculative corridor");
 NavMesh.blocked=false;NavMesh.shore=4.3f;SearchFrame(4f,3.9f);
 Check(straightGuest.Waypoint("7",out goal)&&goal.x==4f,"lead never projects ahead across navigation surface boundary");
 NavMesh.shore=float.PositiveInfinity;straightHost.SetWaypoint("7",new Vector3{x=-12},15);SearchFrame(3.4f,3.5f);
 Check(straightGuest.Waypoint("7",out goal)&&Math.Abs(goal.x-3.4f)<.01f,"changed random waypoint clears lead immediately");
 SearchFrame(3.1f,3);SearchFrame(2.8f,2.9f);SearchFrame(2.5f,6);
 Check(straightGuest.Waypoint("7",out goal)&&Math.Abs(goal.x-2.5f)<.01f&&replica.warps==0,"divergence returns to latest host point on foot");

 // Matching coordinates do not legitimize an old local random order.
 var paged=Packet();int guardOffset=-1;
 for(int offset=25;offset<paged.Length;offset+=20)if(paged[offset]=="42")guardOffset=offset;
 Check(guardOffset>=25,"test selects the intended guard by identity, not page order");
 var idlePacket=new string[45];Array.Copy(paged,idlePacket,25);Array.Copy(paged,guardOffset,idlePacket,25,20);
 idlePacket[26]="Suspicious";idlePacket[27]="-";idlePacket[36]="-";
 guard.transform.position=CrewSync.ReadPosition(idlePacket,28);
 guard.controller.v3CurrentTargetPosition=guard.transform.position;
 guard.controller.skillCommand=new SkillCommand{m_skill=new Skill{eSkillType=Skill.SkillType.Investigate}};
 Time.realtimeSinceStartup+=.4f;Apply(idlePacket);
 Check(guard.controller.skillCommandProcess!=null&&guard.controller.skillCommandProcess.m_skill==null&&guard.controller.skillCommandProcess.m_target!=null,"host waiting cancels a random local order with native default GoTo");
 guard.controller.skillCommand=new SkillCommand{m_skill=new Skill{eSkillType=Skill.SkillType.Gun}};
 Time.realtimeSinceStartup+=.4f;Apply(idlePacket);
 Check(guard.controller.skillCommandProcess!=null&&guard.controller.skillCommandProcess.m_skill==null&&guard.controller.skillCommandProcess.m_target!=null,"host waiting stops an old local shooting command with native default GoTo");
 idlePacket[26]="Idle";Time.realtimeSinceStartup+=2.1f;Apply(idlePacket);
 Check(guard.controller.m_ai.bAIIsBlocked(),"nearby ordinary patrol stays host-owned before the next alarm");
 idlePacket[28]="100";Time.realtimeSinceStartup+=2.1f;Apply(idlePacket);
 Check(!guard.controller.m_ai.bAIIsBlocked(),"distant settled ordinary patrol releases authority");
 // IL2CPP property reads can return distinct managed wrappers for the same native pointer.
 var wrapperLease=new GuardAuthority();var wrapperActor=new MiCharacter();var originalAI=wrapperActor.controller.m_ai;
 wrapperLease.Acquire("wrapper",wrapperActor);var originalLock=originalAI.m_varLockAIBlocked;
 for(int n=0;n<50;n++){
  wrapperActor.controller.m_ai=new AIHandler{Pointer=originalAI.Pointer,m_varLockAIBlocked=new PriorityLockBool{Pointer=originalLock.Pointer,owners=originalLock.owners}};
  Check(wrapperLease.Acquire("wrapper",wrapperActor),"same native pointer remains owned across wrapper churn");
 }
 Check(originalLock.releases==0,"wrapper churn never releases and reacquires native authority");wrapperLease.Release();
 Check(originalLock.owners.Count==0,"disconnect releases the native owner after wrapper churn");
 // Native decision ownership does not erase cutscene/dialog/skill owners.
 var owners=new GuardAuthority();var owned=new MiCharacter();var ai=owned.controller.m_ai;
 ai.m_varLockAIBlocked.set(true,3,"cutscene",false);ai.routine=32;ai.reaction=8;
 Check(owners.Acquire("owned",owned)&&ai.bAIIsBlocked(),"decision lease acquired");
 Check(owned.controller.m_detection.footprintClears==1,"cached local trail cleared on first authority acquisition");
 owners.Acquire("owned",owned);Check(owned.controller.m_detection.footprintClears==1,"renewal does not repeatedly reset detection");
 owners.Release();Check(ai.m_varLockAIBlocked.owners.Count==1 && ai.routine==32 && ai.reaction==8,"release preserves all game-owned reasons");
 ai.m_varLockAIBlocked.release(3);owners.Acquire("owned",owned);
 ai.m_varLockAIBlocked.set(true,2,"skill",false);owners.Release();
 Check(ai.m_varLockAIBlocked.owners.ContainsKey(2)&&ai.routine==32,"game reason added during lease survives release");
 owners.Acquire("owned",owned);CoopWaitLock.CanHoldTransient=false;GuardAuthority.BeforeSave();
 Check(ai.m_varLockAIBlocked.owners.Count==1&&!owners.Acquire("owned",owned),"save entry releases and prohibits reacquisition");
 CoopWaitLock.CanHoldTransient=true;owners.Acquire("owned",owned);MiCutsceneHandler.bCutsceneRunning=true;owners.Tick();
 Check(ai.m_varLockAIBlocked.owners.Count==1,"cutscene releases replica ownership");MiCutsceneHandler.bCutsceneRunning=false;
 owners.Acquire("owned",owned);owned.health.eHealthState=MiCharacterHealth.HealthState.Dead;owners.Tick();
 Check(ai.m_varLockAIBlocked.owners.Count==1,"death releases replica ownership");owned.health.eHealthState=MiCharacterHealth.HealthState.Alive;
 owners.Acquire("owned",owned);var replacement=new MiCharacter();owners.Acquire("owned",replacement);
 Check(ai.m_varLockAIBlocked.owners.Count==1&&replacement.controller.m_ai.bAIIsBlocked(),"recreated entity releases old object and leases replacement");owners.Release();
 Check(!replacement.controller.m_ai.bAIIsBlocked(),"disconnect releases replacement");
 var takeoverLease=new GuardAuthority();var takeover=new MiCharacter();var formerBrain=takeover.controller.m_ai;
 takeoverLease.Acquire("takeover",takeover);takeover.npc=false;
 Check(!takeoverLease.Owns("takeover",takeover),"possession relinquishes NPC UI ownership immediately");
 takeoverLease.Tick();Check(!formerBrain.bAIIsBlocked(),"possession releases old AI decisions before lease timeout");
 Check(!takeoverLease.Acquire("takeover",takeover),"player-controlled body cannot acquire NPC decision lease");
 takeover.npc=true;takeoverLease.Acquire("takeover",takeover);takeover.controller.m_ai=new AIHandler();
 Check(!takeoverLease.Owns("takeover",takeover),"new brain is not owned through old brain identity");
 takeoverLease.Tick();Check(!formerBrain.bAIIsBlocked()&&!takeover.controller.m_ai.bAIIsBlocked(),"brain replacement releases former lock without blocking new brain");

 // A native default GoTo must be exported as Search even without a cached
 // Investigate waypoint. Missing commands and targets must remain distinct.
 var normalizedHost=new GuardPursuit();var normalizedGuest=new GuardPursuit();
 var native=new MiCharacter();var copy=new MiCharacter();
 native.transform.position=new Vector3{x=2};native.controller.m_ai.eAlertState=AIHandler.AlertState.Suspicious;
 native.controller.skillCommandProcess=new SkillCommand{m_skill=new Skill{eSkillType=Skill.SkillType.GoTo},m_target=new MiCharacterTarget(new Vector3{x=8})};
 var nativeSource=new Dictionary<string,MiCharacter>{{"native",native}};
 var nativeDest=new Dictionary<string,MiCharacter>{{"native",copy}};
 string[] NativePacket()=> (new string(' ',19)+normalizedHost.Write(nativeSource,a,b,"Alarm 0 0 0 0 0 0",540).TrimStart()).Split(' ');
 var nativePacket=NativePacket();
 Check(nativePacket[36]=="Search"&&nativePacket[33]=="8","normalized host process exports search and its retained destination before movement initializes");
 native.controller.skillCommand=native.controller.skillCommandProcess;
 Check(NativePacket()[36]=="Search","normalized ordinary host command exports search too");
 native.controller.skillCommand=new SkillCommand{m_skill=null,m_target=null};
 Check(NativePacket()[36]=="-","empty native command is not a search path");
 native.controller.skillCommand=null;Check(NativePacket()[36]=="-","missing native command is not a search path");
 int before=MiScriptLibraryProject.moves;
 copy.controller.m_ai.dropProcess=true;normalizedGuest.Apply(nativePacket,nativeDest,ca,cb,null,null);
 Check(copy.controller.skillCommandProcess==null,"true return without native process remains absent");
 copy.controller.m_ai.dropProcess=false;copy.controller.m_ai.dropTarget=true;
 Time.realtimeSinceStartup+=.4f;normalizedGuest.Apply(nativePacket,nativeDest,ca,cb,null,null);
 Check(copy.controller.skillCommandProcess.m_target==null,"process without target cannot satisfy movement acknowledgement");
 copy.controller.m_ai.dropTarget=false;copy.controller.m_ai.wrongTarget=true;
 Time.realtimeSinceStartup+=.4f;normalizedGuest.Apply(nativePacket,nativeDest,ca,cb,null,null);
 Check(copy.controller.skillCommandProcess.m_target.v3Target.x==100,"wrong target must allow next retry");
 copy.controller.m_ai.wrongTarget=false;
 Time.realtimeSinceStartup+=.4f;normalizedGuest.Apply(nativePacket,nativeDest,ca,cb,null,null);
 Check(copy.controller.skillCommandProcess.m_target.v3Target.x==2,"native default GoTo retries rejected process/target then accepts correct route");
 int acceptedMoves=MiScriptLibraryProject.moves;
 for(int n=0;n<12;n++){copy.controller.Step(.15f);Time.realtimeSinceStartup+=.15f;normalizedGuest.Apply(nativePacket,nativeDest,ca,cb,null,null);}
 Check(copy.transform.position.x==2&&MiScriptLibraryProject.moves==acceptedMoves,"accepted normalized route advances to endpoint without repeated restarts");
 Check(copy.controller.m_ai.bAIIsBlocked()&&copy.warps==0,"native locomotion remains compatible with decision ownership and no warping");

 // Reproduce a running responder with a long confirmed native route. The
 // guest must receive the run/investigate gait and whole leg, not repeatedly
 // approach trailing host positions at its original idle walking speed.
 var runnerHost=new GuardPursuit();var runnerGuest=new GuardPursuit();
 var responder=new MiCharacter();var follower=new MiCharacter();
 var responders=new Dictionary<string,MiCharacter>{{"88",responder}};var followers=new Dictionary<string,MiCharacter>{{"88",follower}};
 responder.controller.m_ai.eAlertState=AIHandler.AlertState.Suspicious;
 responder.movementType.eMovementStyle=MiCharacterMovementType.MovementStyle.Run;
 responder.movementType.eMovementModifier=MiCharacterMovementType.MovementModifier.Investigate;
 responder.controller.skillCommandProcess=new SkillCommand{m_skill=new Skill{eSkillType=Skill.SkillType.GoTo},m_target=new MiCharacterTarget(new Vector3{x=20})};
 responder.movement.target=responder.controller.skillCommandProcess.m_target;responder.movement.isStoppedOrInactive=false;
 responder.showOrHideIcon(UIWorldInfoBatchIconID.Alarm,true);
 follower.showOrHideIcon(UIWorldInfoBatchIconID.Investigate,true);follower.showOrHideIcon(UIWorldInfoBatchIconID.Knockout,true);
 string[] RunnerPacket()=>(new string(' ',19)+runnerHost.Write(responders,a,b,"Alarm 0 0 0 0 0 0",540).TrimStart()).Split(' ');
 void RunnerApply(){runnerGuest.Apply(RunnerPacket(),followers,ca,cb,null,null);}
 RunnerApply();int routeStarts=MiScriptLibraryProject.moves;
 Check(follower.controller.v3CurrentTargetPosition.x==20&&follower.movementType.eMovementStyle==MiCharacterMovementType.MovementStyle.Run&&
    follower.movementType.eMovementModifier==MiCharacterMovementType.MovementModifier.Investigate,"responder gets actual long route and run/search movement type");
 Check(follower.getIconData(UIWorldInfoBatchIconID.Alarm).eState==UIData.State.Connected&&
    follower.getIconData(UIWorldInfoBatchIconID.Investigate).eState==UIData.State.Disconnected&&
    follower.getIconData(UIWorldInfoBatchIconID.Knockout).eState==UIData.State.Connected,"exact head marker replication removes stale question mark without hiding unrelated icons");
 for(int n=0;n<30;n++){responder.controller.Step(.1f);follower.controller.Step(.1f);Time.realtimeSinceStartup+=.1f;RunnerApply();}
 Check(MiScriptLibraryProject.moves==routeStarts&&Vector3.Distance(responder.transform.position,follower.transform.position)<.01f&&follower.transform.position.x>11.9f,"running reinforcement advances continuously with one route instead of accumulating lag");
 // Native changes can alter the icon set without changing AlertState.
 responder.showOrHideIcon(UIWorldInfoBatchIconID.Alarm,false);responder.showOrHideIcon(UIWorldInfoBatchIconID.Investigate,true);
 Time.realtimeSinceStartup+=.4f;RunnerApply();
 Check(follower.getIconData(UIWorldInfoBatchIconID.Alarm).eState==UIData.State.Disconnected&&follower.getIconData(UIWorldInfoBatchIconID.Investigate).eState==UIData.State.Connected,"same AI alert enum still updates independently changed head marker");
 // A changed host destination replaces the leg immediately, not after a
 // stale route cooldown. Stopping then cancels the old long route too.
 responder.movement.target=new MiCharacterTarget(new Vector3{x=5});
 RunnerApply();Check(follower.controller.v3CurrentTargetPosition.x==5,"host native turn replaces old leg immediately");
 responder.movement.isStoppedOrInactive=true;responder.movement.target=null;responder.controller.skillCommandProcess=null;
 responder.controller.m_ai.eAlertState=AIHandler.AlertState.Alert;
 Time.realtimeSinceStartup+=.4f;RunnerApply();
 Check(Math.Abs(follower.controller.v3CurrentTargetPosition.x-responder.transform.position.x)<.01f,"host stopped point replaces the running route");
 responder.movementType.eMovementStyle=MiCharacterMovementType.MovementStyle.Walk;
 responder.movementType.eMovementModifier=MiCharacterMovementType.MovementModifier.None;
 RunnerApply();Check(follower.movementType.eMovementStyle==MiCharacterMovementType.MovementStyle.Walk&&follower.movementType.eMovementModifier==MiCharacterMovementType.MovementModifier.None,"host walking transition does not retain search/run modifier");
 // Host gun commands may be in the active/input slot rather than process.
 responder.controller.skillCommandActive=new SkillCommand{m_skill=new Skill{eSkillType=Skill.SkillType.Gun},m_target=new MiCharacterTarget(b,null)};
 int attacksBefore=Melee.attacks;RunnerApply();RunnerApply();
 Check(Melee.attacks==attacksBefore+1,"active-slot host shot is captured and replayed once");
 Time.time+=2;Time.realtimeSinceStartup+=2;
 Check(RunnerPacket()[36]=="Gun","continuing host gun action does not expire while its native command is still active");
 RunnerApply();Check(Melee.attacks==attacksBefore+1,"continuing gun cue does not invent a new shot serial");
  follower.controller.skillCommand=new SkillCommand{m_skill=new Skill{eSkillType=Skill.SkillType.GoToRoutinePoint}};
  follower.climbing=true;
  responder.controller.skillCommandActive=null;responder.movement.target=new MiCharacterTarget(new Vector3{x=30});responder.movement.isStoppedOrInactive=false;
  Time.time+=2;Time.realtimeSinceStartup+=2;int climbMoves=MiScriptLibraryProject.moves;
  RunnerApply();Check(MiScriptLibraryProject.moves==climbMoves,"NPC climbing transition is not replaced by a ground GoTo");
  follower.climbing=false;responder.movementType.eMovementStyle=MiCharacterMovementType.MovementStyle.Run;
  RunnerApply();
  Check(follower.controller.skillCommand==null&&MiScriptLibraryProject.moves==climbMoves+1,"completed NPC climb clears stale local patrol input before confirmed route");
  Check((follower.controller.skillCommandProcess.flags&1)!=0,"native default GoTo carries host run execution modifier as well as movement style");
  GuardPursuit.EffectProtection=actor=>actor==follower;int freezeMoves=MiScriptLibraryProject.moves,freezeAttacks=Melee.attacks;
  responder.movement.target=new MiCharacterTarget(new Vector3{x=34});Time.realtimeSinceStartup+=1;RunnerApply();
  Check(MiScriptLibraryProject.moves==freezeMoves&&Melee.attacks==freezeAttacks,"active native freeze is not overwritten by pursuit movement or attacks");
  responder.controller.m_detectionOrientation.fOrientation=123;RunnerApply();Time.realtimeSinceStartup+=.16f;follower.controller.m_detectionOrientation.fOrientation=0;GuardPursuit.RestoreSight(follower.controller.m_detectionOrientation);Check(Math.Abs(follower.controller.m_detectionOrientation.qDetectionRot.eulerAngles.y-123)<.01,"effect-protected guard still follows host sight direction without restarting movement");
  GuardPursuit.EffectProtection=null;RunnerApply();Check(MiScriptLibraryProject.moves>freezeMoves,"native effect end releases guard for confirmed pursuit again");

 // A walk/run change on the same retained path must update its command flags.
 var retained=follower.controller.skillCommandProcess;
 responder.movementType.eMovementStyle=MiCharacterMovementType.MovementStyle.Walk;
 RunnerApply();Check(ReferenceEquals(retained,follower.controller.skillCommandProcess)&&(retained.flags&1)==0,"same path changes to walking without restart");
 responder.movementType.eMovementStyle=MiCharacterMovementType.MovementStyle.Run;
 RunnerApply();Check(ReferenceEquals(retained,follower.controller.skillCommandProcess)&&(retained.flags&1)!=0,"same path changes to running without restart");
 // Real log: old Gun cue masked new Investigate destination for 1.5 seconds.
 responder.controller.skillCommandActive=new SkillCommand{m_skill=new Skill{eSkillType=Skill.SkillType.Gun},m_target=new MiCharacterTarget(b,null)};
 RunnerPacket();
 responder.controller.skillCommandActive=new SkillCommand{m_skill=new Skill{eSkillType=Skill.SkillType.Investigate},m_target=new MiCharacterTarget(new Vector3{x=30})};
 Check(RunnerPacket()[36]=="Search","confirmed search immediately supersedes retained gun cue");
 // An idle patrol outside the former 18m radius is maintained before alarm.
 var patrolHost=new GuardPursuit();var patrolGuest=new GuardPursuit();
 var patrol=new MiCharacter();var patrolCopy=new MiCharacter();
 patrol.transform.position=new Vector3{x=30};patrolCopy.transform.position=new Vector3{x=16};
 var patrols=new Dictionary<string,MiCharacter>{{"patrol",patrol}};var patrolCopies=new Dictionary<string,MiCharacter>{{"patrol",patrolCopy}};
 string[] PatrolPacket(){var packet=(new string(' ',19)+patrolHost.Write(patrols,a,b,"None 0 0 0 0 0 0",540).TrimStart()).Split(' ');packet[12]="None";return packet;}
 var patrolPacket=PatrolPacket();Check(patrolPacket.Length==45,"35m guard tracked before mission alarm");
 patrolGuest.Apply(patrolPacket,patrolCopies,ca,cb,null,null);
 Check(patrolCopy.transform.position.x==30&&patrolCopy.warps==1&&patrolCopy.controller.m_ai.bAIIsBlocked(),"initial peaceful placement fixes pre-existing 14m offset before alarm");
 patrol.movement.target=new MiCharacterTarget(new Vector3{x=33});patrol.movement.isStoppedOrInactive=false;
 Time.realtimeSinceStartup+=.4f;
 patrolPacket=PatrolPacket();Check(patrolPacket[36]=="Search","ordinary confirmed patrol route exported before alarm");
 patrolGuest.Apply(patrolPacket,patrolCopies,ca,cb,null,null);
 Check(patrolCopy.controller.v3CurrentTargetPosition.x==33,"ordinary patrol follows the host destination instead of drifting locally");
 // Changed head markers with no new sighting get a priority slot, while the
 // first rotating record still guarantees fairness under a clipped budget.
 var urgentHost=new GuardPursuit();var urgent=new Dictionary<string,MiCharacter>();
 for(int n=0;n<12;n++)urgent[n.ToString("D2")]=new MiCharacter();
 for(int n=0;n<3;n++)urgentHost.Write(urgent,a,b,"None 0 0 0 0 0 0",1000);
 urgent["11"].showOrHideIcon(UIWorldInfoBatchIconID.Alarm,true);
 Time.realtimeSinceStartup+=.2f;
 var urgentWords=urgentHost.Write(urgent,a,b,"Alarm 0 0 0 0 0 0",1000).Trim().Split(' ');
 Check(urgentWords[6]=="00"&&urgentWords[26]=="11","new alarm icon precedes ordinary patrol records without starving rotation");

 // Confirmed lethal state must survive a frozen game clock and the throttle
 // from a wound applied just before game-over pause.
 var life=new GuardPursuit();var lifeA=new MiCharacter();var lifeB=new MiCharacter();var lifeWire=new string[25];
 lifeWire[20]="2";lifeWire[21]="Alive";lifeWire[22]="3";lifeWire[23]="Alive";
 life.ApplyCrewHealth(lifeWire,lifeA,lifeB);Check(lifeA.health.iHealth==2,"pre-lethal wound applies");
 lifeWire[20]="0";lifeWire[21]="PreDying";
 life.ApplyCrewHealth(lifeWire,lifeA,lifeB);Check(lifeA.health.eHealthState==MiCharacterHealth.HealthState.Dead&&lifeB.health.eHealthState==MiCharacterHealth.HealthState.Alive,"pre-death bypasses previous wound throttle without killing the other crew");
 lifeWire[22]="0";lifeWire[23]="Dying";
 life.ApplyCrewHealth(lifeWire,lifeA,lifeB);Check(lifeB.health.eHealthState==MiCharacterHealth.HealthState.Dead,"other crew death applies with unchanged simulation clock");
 lifeA.health.eHealthState=MiCharacterHealth.HealthState.Alive;lifeA.health.iHealth=1;lifeWire[21]="Dead";
 life.ApplyCrewHealth(lifeWire,lifeA,lifeB);Check(lifeA.health.eHealthState==MiCharacterHealth.HealthState.Dead,"final Dead sample recovers a missed transitional death");
 // Full production capture and replay outside A/B, including hidden and DLC actors.
 var wholeHost=new GuardPursuit();var wholeGuest=new GuardPursuit();
 var hidden=new MiCharacter{id=11.ToString("X64"),npc=false};hidden.transform.position=new Vector3{x=200};
 var hiddenCopy=new MiCharacter{id=hidden.id,npc=false};hiddenCopy.transform.position=hidden.transform.position;
 var dlc=new MiCharacter{id=10.ToString("X64"),npc=false};var dlcCopy=new MiCharacter{id=dlc.id,npc=false};
 wholeHost.SetCrew(a,b,new[]{dlc,hidden});wholeGuest.SetCrew(ca,cb,new[]{dlcCopy,hiddenCopy});
 var hunter=new MiCharacter{id="88"};hunter.transform.position=new Vector3{x=202};
 var hunterCopy=new MiCharacter{id="88"};hunterCopy.transform.position=hunter.transform.position;
 var originals=new Dictionary<string,MiCharacter>{{"88",hunter}};var mirrored=new Dictionary<string,MiCharacter>{{"88",hunterCopy}};
 hunter.controller.m_detection.addObjectToTrack(hidden).m_detectionState=Detection.DetectionState.Detected;
 hunter.controller.m_detection.addObjectToTrack(dlc).m_detectionState=Detection.DetectionState.PreDetected;
 string[] WholePacket()=> (new string(' ',19)+wholeHost.Write(originals,a,b,"None 0 0 0 0 0 0",1024).TrimStart()).Split(' ');
 var world=WholePacket();Check(world.Length==45&&world[40]=="0045","nearby hidden crew brings distant guard onto wire with independent detection");
 wholeGuest.Apply(world,mirrored,ca,cb,null,null);
 Check(hunterCopy.controller.m_ai.bAIIsBlocked(),"patrol near hidden crew stays host-owned even far from A/B");
 Check(wholeGuest.Visibility("88",hiddenCopy,out var hiddenSeen)&&hiddenSeen==Detection.DetectionState.Detected&&wholeGuest.Visibility("88",dlcCopy,out var dlcSeen)&&dlcSeen==Detection.DetectionState.PreDetected,"hidden and DLC visibility remain independent");
 hunter.controller.m_ai.transTarget=hidden;hunter.controller.m_ai.eAlertState=AIHandler.AlertState.Alert;
 hunter.controller.skillCommandProcess=new SkillCommand{m_skill=new Skill{eSkillType=Skill.SkillType.Gun},m_target=new MiCharacterTarget(hidden,null),Pointer=new IntPtr(900)};
 Time.time+=.2f;Time.realtimeSinceStartup+=.2f;world=WholePacket();
 int fullShots=Melee.attacks;wholeGuest.Apply(world,mirrored,ca,cb,null,null);wholeGuest.Apply(world,mirrored,ca,cb,null,null);
 Check(world[27]==hidden.id&&world[38]==hidden.id&&hunterCopy.controller.m_ai.transTarget==hiddenCopy,"AI target and shot victim retain hidden identity");
 Check(Melee.attacks==fullShots+1&&hunterCopy.controller.skillCommandProcess.m_target.m_entityTarget==hiddenCopy,"shot plays on intended hidden actor once, never B");
 wholeGuest.SetCrew(ca,cb,new[]{hiddenCopy,dlcCopy});
 Check(!wholeGuest.Visibility("88",hiddenCopy,out _),"roster reorder invalidates old indexed visibility");
 wholeGuest.SetCrew(ca,cb,new MiCharacter[]{null,hiddenCopy},preserveMissing:true);
 Time.time+=.2f;Time.realtimeSinceStartup+=.2f;world=WholePacket();wholeGuest.Apply(world,mirrored,ca,cb,null,null);
 Check(wholeGuest.Visibility("88",hiddenCopy,out var delayedSeen)&&delayedSeen==Detection.DetectionState.Detected,"unresolved controlled actor retains sight slot so following actor does not inherit its visibility");
 hunter.controller.m_detection.m_dataSwitcher.m_dataInstanceCurrent=new DetectionData{token="0~2~1~2~1~25~0~0~0~0~0"};Time.time+=.2f;Time.realtimeSinceStartup+=.2f;world=WholePacket();wholeGuest.Apply(world,mirrored,ca,cb,null,null);Check(hunterCopy.controller.m_detection.applied?.token==hunter.controller.m_detection.m_dataSwitcher.m_dataInstanceCurrent.token,"host reduced sight profile reaches guest sensor");
 wholeGuest.Release();ViewRegression();DetectionPagingRegression();Console.WriteLine("Pursuit checks passed: "+checks);
 }}
"@
Add-Type -TypeDefinition ($source+[Environment]::NewLine+$authority+[Environment]::NewLine+$fixture)
[PursuitTests]::Run()
'Pursuit, random point, attack replay, wounds, healing and packet budget passed (simulated actors).'
