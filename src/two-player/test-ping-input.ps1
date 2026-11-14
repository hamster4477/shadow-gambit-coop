$ErrorActionPreference='Stop'
$source=Get-Content (Join-Path $PSScriptRoot 'CoopPings.cs') -Raw
$start=$source.IndexOf('    private static bool WorldInput')
$end=$source.IndexOf('    [HarmonyPatch(typeof(MiMouseInput)', $start)
if($start -lt 0 -or $end -lt $start){throw 'Cannot locate production ping input methods'}
$methods=$source.Substring($start,$end-$start)
$fixture=@'
using System;
using System.Collections.Generic;
class MoveProbePlugin {public static Log ProbeLog=new();public class Log {public void LogWarning(string s){}}}
enum KeyCode {None,BackQuote,LeftControl,RightControl,LeftShift,RightShift,LeftAlt,RightAlt,E}
class ConfigKey {public KeyCode Value;}
static class Time {public static float timeScale=1,realtimeSinceStartup;public static int frameCount;}
static class Application {public static bool isFocused=true;}
static class Camera {public static object main=new();}
static class TrainingSession {public static bool Suspended;}
static class SaveProcessManager {public static bool bProcessing,bLoading;}
static class MiCutsceneHandler {public static bool bCutsceneRunning;}
static class StorySync {public static bool Waiting;}
static class UIWorldMap {public static bool bOpenStatic;}
static class GuestWorldMap {public static bool NativeUiOwnsCrew;}
namespace UnityEngine.EventSystems {class EventSystem {public static EventSystem current;public bool ui;public bool IsPointerOverGameObject()=>ui;}}
class Mouse {public bool bOverUI,bOverGuardHouseUI;}
class MiPlayerInput {public Mouse m_mouseInput=new();}
static class Input {
 public static HashSet<KeyCode> held=new(),down=new();public static bool left,leftDown,right,rightDown;
 public static bool GetKey(KeyCode k)=>held.Contains(k);public static bool GetKeyDown(KeyCode k)=>down.Contains(k);
 public static bool GetMouseButton(int b)=>b==0?left:right;public static bool GetMouseButtonDown(int b)=>b==0?leftDown:rightDown;
}
public static class PingInputTests {
 static bool enabled,clickHeld;static int consumedFrame=-1,inputFrame=-1,placed;static ConfigKey key=new(){Value=KeyCode.BackQuote};
 static void PlaceAtCursor(){placed++;}
'@
$methods="    private static float nextWarning;`n"+$methods
$fixture+=$methods
$fixture+=@'
 static void Reset(){enabled=true;clickHeld=false;consumedFrame=inputFrame=-1;placed=0;Time.frameCount=1;Time.timeScale=1;Application.isFocused=true;Camera.main=new();TrainingSession.Suspended=false;SaveProcessManager.bProcessing=SaveProcessManager.bLoading=false;MiCutsceneHandler.bCutsceneRunning=StorySync.Waiting=UIWorldMap.bOpenStatic=GuestWorldMap.NativeUiOwnsCrew=false;Input.held.Clear();Input.down.Clear();Input.left=Input.leftDown=Input.right=Input.rightDown=false;UnityEngine.EventSystems.EventSystem.current=null;key.Value=KeyCode.BackQuote;}
 static void Left(){Input.left=Input.leftDown=true;}
 public static int Run(){int n=0;void Check(bool b,string why){if(!b)throw new Exception(why);n++;}
  Reset();Input.held.Add(KeyCode.LeftAlt);Left();Check(CaptureInput(null)&&placed==1&&ConsumesClick,"Alt left must place and consume");CaptureInput(null);Check(placed==1,"Repeated native hook in one frame must not duplicate");
  Time.frameCount++;Input.leftDown=false;Input.held.Clear();Check(CaptureInput(null)&&ConsumesClick&&placed==1,"Hold must remain consumed after modifier release");
  Time.frameCount++;Input.left=false;Check(ConsumesClick&&ConsumesClick,"Mouse release frame must remain consumed across hooks");Time.frameCount++;Check(!ConsumesClick&&!CaptureInput(null),"Next frame restores ordinary clicks");
  Reset();Input.held.Add(KeyCode.RightAlt);Left();Check(CaptureInput(null)&&placed==1,"Right Alt works");
  Reset();Input.held.Add(KeyCode.LeftControl);Input.held.Add(KeyCode.E);Check(CaptureInput(null)&&placed==0,"Ctrl E alone blocks character input without a ping");Left();Check(CaptureInput(null)&&placed==1,"Ctrl E left works");
  Reset();Input.held.Add(KeyCode.RightControl);Input.held.Add(KeyCode.E);Left();Check(CaptureInput(null)&&placed==1,"Right Ctrl E works");
  Reset();Input.down.Add(KeyCode.BackQuote);CaptureInput(null);CaptureInput(null);Check(placed==1&&!ConsumesClick,"Single key places once without claiming mouse gesture");
  Reset();Input.down.Add(KeyCode.BackQuote);Input.held.Add(KeyCode.LeftControl);CaptureInput(null);Check(placed==0,"Modified single key is not a ping");
  Reset();Input.held.Add(KeyCode.LeftAlt);Input.right=Input.rightDown=true;Check(!CaptureInput(null)&&placed==0&&!ConsumesClick,"Right click remains native");
  Reset();Left();Check(!CaptureInput(null)&&placed==0,"Ordinary left click remains native");
  Reset();Input.held.Add(KeyCode.LeftAlt);Left();Check(!CaptureInput(new MiPlayerInput{m_mouseInput=new Mouse{bOverUI=true}})&&placed==0,"Native UI excludes pings");
  Reset();Input.down.Add(KeyCode.BackQuote);UnityEngine.EventSystems.EventSystem.current=new(){ui=true};Check(!CaptureInput(null)&&placed==0,"Event-system UI excludes single key pings");
  Reset();Input.held.Add(KeyCode.LeftAlt);Left();enabled=false;Check(!CaptureInput(null)&&placed==0,"Offline input remains native");
  foreach(int gate in new[]{0,1,2,3,4,5,6,7,8,9}){
   Reset();Input.held.Add(KeyCode.LeftAlt);Left();
   switch(gate){case 0:TrainingSession.Suspended=true;break;case 1:SaveProcessManager.bLoading=true;break;case 2:SaveProcessManager.bProcessing=true;break;case 3:MiCutsceneHandler.bCutsceneRunning=true;break;case 4:StorySync.Waiting=true;break;case 5:UIWorldMap.bOpenStatic=true;break;case 6:GuestWorldMap.NativeUiOwnsCrew=true;break;case 7:Time.timeScale=0;break;case 8:Application.isFocused=false;break;case 9:Camera.main=null;break;}
   Check(!CaptureInput(null)&&placed==0,"World gate "+gate+" must exclude pings");
  }
  Reset();Input.down.Add(KeyCode.BackQuote);key.Value=KeyCode.None;CaptureInput(null);Check(placed==0,"None disables single key");
  return n;
 }
}
'@
Add-Type -TypeDefinition $fixture -IgnoreWarnings -WarningAction SilentlyContinue
Write-Output "Production ping-input checks passed: $([PingInputTests]::Run()). Unity input/state simulated."
