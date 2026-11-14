$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$source="using System;`nusing System.Collections.Generic;`nusing System.Linq;`nusing System.Globalization;`n"
foreach($file in 'GuestCrewSelection.cs','GuestOrderVersions.cs'){$source+=([IO.File]::ReadAllText((Join-Path $root $file))-replace '(?m)^using [^;]+;\r?\n','')+"`n"}
$crew=[IO.File]::ReadAllText((Join-Path $root 'CrewSync.cs'))
$start=$crew.IndexOf('    internal string EncodeOrder(');$end=$crew.IndexOf('    internal void ExtraOrderSending',$start)
$encoding=$crew.Substring($start,$end-$start)
$start=$crew.IndexOf('    internal bool ReceivingFrames');$end=$crew.IndexOf('    internal CrewSync(',$start)
$sending=$crew.Substring($start,$end-$start)
$source+='internal class GuestHarness{internal bool Spectating=true,ready=true,stopped;internal float lastGood;internal string Session=new string(''a'',32);internal Version crewOwners=new();internal GuestOrderVersions orderVersions=new();internal Pace pace=new();internal class Version{internal long Revision=4;}internal class Pace{internal bool World;}' + $encoding + $sending + "}`n"
$source+=@'
namespace UnityEngine{public static class Time{public static float realtimeSinceStartup;}}
internal static class GuestWorldMap{internal static bool NativeUiOwnsCrew;}
internal static class SceneLookup{internal static T Find<T>()where T:class=>null;}
internal class PlanningMode{internal bool bIsActive;internal MiCharacter planningModeCharacter;internal MiCharacter getCharacterForPlanningCharacter(MiCharacter c)=>c;}
internal static class SyncProtocol{internal static bool Session(string s)=>s?.Length==32;internal static bool Identity(string s)=>s?.Length==64;internal static bool Command(string s,out string[] f){f=s.Split(' ');return true;}}
internal class MiCharacter{internal string id;internal Controller controllerPlayer;internal MiCharacter(int n){id=n.ToString("X64");controllerPlayer=new(){m_selectable=new(){character=this}};}}
internal class Controller{internal MiSelectable m_selectable;}
internal class MiSelectable{internal MiCharacter character;internal struct SelectOptions{internal SelectOptions(bool value){}}}
internal class MiPlayerInput{internal List<MiCharacter> lSelectedCharacter=new();internal MiCharacter leader;internal SelectHandler m_selectHandler;internal MiPlayerInput(){m_selectHandler=new(){input=this};}}
internal class SelectHandler{internal MiPlayerInput input;internal List<MiSelectable> m_liSelected=>input.lSelectedCharacter.Select(c=>c.controllerPlayer.m_selectable).ToList();internal void deselectAll(MiSelectable.SelectOptions o){input.lSelectedCharacter.Clear();input.leader=null;}internal void select(MiSelectable s,MiSelectable.SelectOptions o){input.lSelectedCharacter.Add(s.character);}internal void setLeader(MiSelectable s,MiSelectable.SelectOptions o){input.leader=s.character;}}
internal static class GuestMove{internal static string Identity(MiCharacter c)=>c?.id;}
internal static class MoveProbe{internal static MiCharacter[] actors;internal static MiCharacter CrewById(string id)=>actors.FirstOrDefault(c=>c.id==id);}
public static class MultiSpectatorTests{
 static int checks;static void Check(bool b,string message){if(!b)throw new Exception(message);checks++;}
 static void Pick(MiPlayerInput input,params MiCharacter[] actors){input.lSelectedCharacter.Clear();input.lSelectedCharacter.AddRange(actors);input.leader=actors.FirstOrDefault();}
 public static int Run(){
  var actors=MoveProbe.actors=new[]{new MiCharacter(1),new MiCharacter(2),new MiCharacter(3)};var owners=new Dictionary<MiCharacter,char>{{actors[0],'H'},{actors[1],'U'},{actors[2],'H'}};Func<MiCharacter,char> owner=c=>owners[c];Func<MiCharacter,bool> known=c=>actors.Contains(c);
  var input=new MiPlayerInput();var selection=new GuestCrewSelection();selection.Context("room|mission");Pick(input,actors[0]);selection.Observe(input,actors[0],owner,known,0);
  Check(input.leader==null&&input.lSelectedCharacter.Count==0,"spectator discards imported host selection");Check(selection.Next(4,0,actors[0],actors,owner,_=>false)==null,"spectator does not reserve free crew automatically");
  Pick(input,actors[1]);selection.Observe(input,actors[0],owner,known,.1f);var claim=selection.Next(4,.1f,actors[0],actors,owner,_=>false);Check(claim!=null&&!claim.Release&&claim.Actors.SequenceEqual(new[]{actors[1].id}),"spectator selecting unowned native actor emits SELECT");
  var harness=new GuestHarness();Check(harness.CanSend,"spectator can request ownership while valid frames arrive");var encoded=harness.EncodeOrder(actors[0].id,claim.Command,4);Check(encoded!=null&&encoded.Contains(" Q 4 1"),"spectator SELECT passes production encoder with ownership revision");
  foreach(string cmd in new[]{"CLAIM","POINT 1 2 3","USE S1 target","SELECT","RELEASE "+actors[0].id})Check(harness.EncodeOrder(actors[0].id,cmd,4)==null,"spectator cannot send legacy claim, gameplay or release commands");Check(harness.EncodeOrder(actors[0].id,claim.Command,3)==null,"stale selection revision rejected");
  selection.Reply(claim.Serial,true,.2f);selection.Observe(input,actors[0],owner,known,.3f);Check(selection.Waiting&&!GuestCrewSelection.Ready(input,owner),"transport acceptance never grants native ownership");
  owners[actors[1]]='G';selection.Observe(input,actors[1],owner,known,.4f);Check(!selection.Waiting&&GuestCrewSelection.Ready(input,owner),"confirmed frame enables selected actor");
  Pick(input);selection.Observe(input,actors[1],owner,known,.5f);Check(selection.Next(5,.5f,actors[1],actors,owner,_=>true)==null,"deselection does not release busy native action");Check(selection.Next(5,.6f,actors[1],actors,owner,_=>false,_=>false)==null,"deselection does not release active proxy source");
  var release=selection.Next(5,.7f,actors[1],actors,owner,_=>false,_=>true);Check(release!=null&&release.Release&&release.Actors.SequenceEqual(new[]{actors[1].id}),"empty selection releases idle owned anchor as spectator choice");
  harness.Spectating=false;Check(harness.EncodeOrder(actors[1].id,release.Command,4)!=null,"controlled guest can emit explicit release");
  selection.Reply(release.Serial,true,.8f);owners[actors[1]]='U';selection.Observe(input,actors[0],owner,known,.9f);Check(!selection.Waiting&&input.leader==null,"confirmed release leaves native selection empty");
  Pick(input,actors[2]);selection.Observe(input,actors[0],owner,known,1);Check(input.leader==null,"other remote projected H remains unselectable");
  owners[actors[1]]=owners[actors[2]]='G';Pick(input,actors[2]);selection.Observe(input,actors[1],owner,known,1.1f);
  Check(selection.Next(6,1.1f,actors[1],actors,owner,actor=>actor==actors[1])==null,"retained busy member does not trigger repeated unchanged SELECT requests");
  var idleSwitch=selection.Next(6,1.2f,actors[1],actors,owner,_=>false);
  Check(idleSwitch!=null&&!idleSwitch.Release&&idleSwitch.Actors.SequenceEqual(new[]{actors[2].id}),"old member becomes releasable once native action ends");
  return checks;
 }
}
'@
$source="using UnityEngine;`n"+$source
Add-Type -TypeDefinition $source -IgnoreWarnings -WarningAction SilentlyContinue
"Multi-player spectator/selection checks: $([MultiSpectatorTests]::Run()). Production selection, encoder and send-readiness gates; native APIs and payload grammar stubbed."
