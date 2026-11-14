using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
namespace HarmonyLib {public class HarmonyPatch:Attribute{public HarmonyPatch(Type t,string n){}}public class Harmony{public Harmony(string s){}public void PatchAll(Type t){}}}
namespace UnityEngine {
 public static class Time{public static float realtimeSinceStartup;}
 public static class Object{public static InquisitorLoadHandlerNoSave Marker;public static InquisitorLoadHandlerSave Save;public static PostEffectHandler Fx;
 public static T FindObjectOfType<T>()where T:class=>typeof(T)==typeof(InquisitorLoadHandlerNoSave)?Marker as T:typeof(T)==typeof(InquisitorLoadHandlerSave)?Save as T:Fx as T;}
}
namespace Il2CppInterop.Runtime.InteropTypes.Arrays{public class Il2CppStructArray<T>{readonly T[] data;public Il2CppStructArray(int length){data=new T[length];}public int Length=>data.Length;public T this[int i]{get=>data[i];set=>data[i]=value;}}}
public class SaveGameHolder{public string sPath;}
public class SaveProcessManager{public enum LoadProcessResult{Success,Failure}}
public class CmdLoadLastQuicksave{public bool m_bInquisitorLoad,m_bShowInquisitorLoadVFX;public float m_fInquisitorLoadVFXIntensity;}
public class InquisitorLoadHandlerNoSave{
 public string[] m_arStatesNames=new[]{"First Quickload","After first Quickload","After Freezer Cutscene Dead"};
 public bool m_bWasNotifiedOfInquisitorLoad,m_bLastLoadIsFromInquisitor;
 public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> m_arStatesBeforeLoad;
 public void notifyOfInquisitorLoad(){m_bWasNotifiedOfInquisitorLoad=true;}
}
public class InquisitorLoadHandlerSave{public int[] states=new int[3];public void setState(int i,int v){states[i]=v;}}
public class PostEffectHandler{public int calls;public void startInquisitorLoad(float f){calls++;}}
public static class MoveProbePlugin{public static class ProbeLog{public static void LogInfo(object o){}public static void LogWarning(object o){}}}
public static class EnemyRewindTests{
 static int checks;static void Check(bool value,string why){if(!value)throw new Exception(why);checks++;}
 static void Hook(Type t,string name,params object[] args)=>t.GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);
 static JsonDocument Request(bool effect=false)=>JsonDocument.Parse(JsonSerializer.Serialize(new{rewind=new{schema=1,enemy=true,showEffect=effect,intensity=1f,names=new[]{"First Quickload","After first Quickload","After Freezer Cutscene Dead"},states=new[]{1,0,0}}}));
 static void Scene(){UnityEngine.Object.Marker=new();UnityEngine.Object.Save=new();UnityEngine.Object.Fx=new();}
 static bool Failure(){try{EnemyRewind.ValidateCompletion();return false;}catch(InvalidDataException){return true;}}
 public static int Run(){
  string path=Path.Combine(Path.GetTempPath(),"rewind-test.save");var holder=new SaveGameHolder{sPath=path};
  using var request=Request();using var red=Request(true);using var ordinary=JsonDocument.Parse("{}");
  Scene();EnemyRewind.Prepare(request.RootElement,path,true);
  Check(UnityEngine.Object.Save.states[0]==1&&UnityEngine.Object.Marker.m_bWasNotifiedOfInquisitorLoad,"in-place rewind restores host pre-load state and notifies native handler");
  Hook(typeof(EnemyRewind.BeforeLoad),"Postfix",UnityEngine.Object.Marker,holder);
  Hook(typeof(EnemyRewind.Complete),"Postfix",holder,SaveProcessManager.LoadProcessResult.Success);
  Check(!Failure(),"in-place success consumes matching native context");
  Scene();UnityEngine.Object.Marker=null;UnityEngine.Object.Save=null;
  EnemyRewind.Prepare(red.RootElement,path,false);
  Check(UnityEngine.Object.Fx.calls==0,"menu join does not require mission handlers or play rewind on menu camera");
  Scene();Hook(typeof(EnemyRewind.EntryContext),"Prefix",UnityEngine.Object.Save);
  Check(UnityEngine.Object.Marker.m_bLastLoadIsFromInquisitor&&!UnityEngine.Object.Marker.m_bWasNotifiedOfInquisitorLoad,"new scene receives original consumed rewind flags");
  Check(UnityEngine.Object.Marker.m_arStatesBeforeLoad[0]==1&&UnityEngine.Object.Save.states[0]==0,"host previous states transferred without overwriting deserialized checkpoint states");
  Check(UnityEngine.Object.Fx.calls==0,"late entry context does not replay in-place VFX");
  UnityEngine.Object.Marker.m_arStatesBeforeLoad[0]=5;Hook(typeof(EnemyRewind.EntryContext),"Prefix",UnityEngine.Object.Save);
  Check(UnityEngine.Object.Marker.m_arStatesBeforeLoad[0]==5,"repeat loading-finished callback does not reapply context");
  Hook(typeof(EnemyRewind.Complete),"Postfix",holder,SaveProcessManager.LoadProcessResult.Success);Check(!Failure(),"late entry completes with native story context");
  Scene();EnemyRewind.Prepare(request.RootElement,path,false);UnityEngine.Object.Marker.m_arStatesNames[0]="different mission";
  Hook(typeof(EnemyRewind.EntryContext),"Prefix",UnityEngine.Object.Save);
  Check(Failure()&&!UnityEngine.Object.Marker.m_bLastLoadIsFromInquisitor,"mismatched mission state names fail before mutation");
  EnemyRewind.Prepare(ordinary.RootElement,path,false);Check(!Failure(),"next ordinary checkpoint clears prior context error");
  Scene();EnemyRewind.Prepare(request.RootElement,path,false);
  Hook(typeof(EnemyRewind.Complete),"Postfix",holder,SaveProcessManager.LoadProcessResult.Success);
  Check(Failure(),"missing native handler callback cannot silently report synchronized success");
  EnemyRewind.Prepare(ordinary.RootElement,path,false);
  Scene();var previous=new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(3);previous[0]=7;UnityEngine.Object.Marker.m_arStatesBeforeLoad=previous;
  EnemyRewind.Prepare(request.RootElement,path,false);Hook(typeof(EnemyRewind.EntryContext),"Prefix",UnityEngine.Object.Save);EnemyRewind.Cancel();
  Check(!UnityEngine.Object.Marker.m_bLastLoadIsFromInquisitor&&ReferenceEquals(previous,UnityEngine.Object.Marker.m_arStatesBeforeLoad),"cancel restores entry marker and previous state array");
  Scene();EnemyRewind.Prepare(red.RootElement,path,false);EnemyRewind.Cancel();Check(UnityEngine.Object.Fx.calls==0,"cancel pending menu entry never changes another effect");
  Scene();EnemyRewind.Prepare(red.RootElement,path,true);Check(UnityEngine.Object.Fx.calls==1,"same-mission red rewind still starts native effect");EnemyRewind.Cancel();
  Check(UnityEngine.Object.Save.states[0]==0&&!UnityEngine.Object.Marker.m_bWasNotifiedOfInquisitorLoad&&UnityEngine.Object.Fx.calls==2,"in-place cancellation restores original states and effect");
  return checks;
 }
}
