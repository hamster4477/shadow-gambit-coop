$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$native=[IO.File]::ReadAllText((Join-Path $root 'PlanningSync.cs'))
$begin=$native.IndexOf('internal static void RetirePeerQueues(')
if($begin -lt 0){throw 'Missing queue retirement adapter'}
$end=$native.IndexOf('{',$begin)+1;$depth=1
while($depth -gt 0){if($native[$end] -eq '{'){$depth++};if($native[$end] -eq '}'){$depth--};$end++}
$method=$native.Substring($begin,$end-$begin)
$source=@'
using System;
using System.Collections.Generic;
internal class MiCharacter {internal string id,owner;}
internal class PlanningMode {}
internal static class MoveProbe {internal static Dictionary<string,MiCharacter> actors=new();internal static MiCharacter CrewById(string id)=>actors.TryGetValue(id,out var actor)?actor:null;}
internal static class NativeInput {internal static Adapter Probe=new();internal class Adapter {internal string PlanningOwner(MiCharacter actor)=>actor.owner;}}
internal static class PlanningWire {internal sealed class Queue {public string actor;}}
internal static class PlanningNative {
 internal static readonly List<string> cleared=new();internal static int calls;
 internal static bool Replace(PlanningMode mode,PlanningWire.Queue[] queues,Func<MiCharacter,bool> allowed,bool validate){
  calls++;foreach(var q in queues){if(!allowed(MoveProbe.CrewById(q.actor)))throw new Exception("retirement lost ownership guard");cleared.Add(q.actor);}return true;
 }
}
internal static class PlanningSync {
'@
$source+=$method+@'
}
public static class MultiplayerPlanningRetireTests {
 public static int Run(){int checks=0;void Check(bool b,string why){if(!b)throw new Exception(why);checks++;}
  MoveProbe.actors["departed"]=new MiCharacter{id="departed",owner="old"};
  MoveProbe.actors["released"]=new MiCharacter{id="released",owner=null};
  MoveProbe.actors["new-player"]=new MiCharacter{id="new-player",owner="new"};
  MoveProbe.actors["host"]=new MiCharacter{id="host",owner="host"};
  PlanningSync.RetirePeerQueues(new PlanningMode(),"old",new[]{"departed","released","new-player","host","gone"});
  Check(PlanningNative.calls==1,"batch only one native preview update");
  Check(PlanningNative.cleared.Count==2,"only old or unowned queues are retired");
  Check(PlanningNative.cleared.Contains("departed"),"departed player preview cleared explicitly");
  Check(PlanningNative.cleared.Contains("released"),"released preview cleared explicitly");
  Check(!PlanningNative.cleared.Contains("new-player")&&!PlanningNative.cleared.Contains("host"),"new player's and host's orders survive handoff");
  PlanningSync.RetirePeerQueues(new PlanningMode(),"old",new[]{"new-player","host","gone"});
  Check(PlanningNative.calls==1,"no native call when all actors belong to others or disappeared");
  return checks;
 }
}
'@
Add-Type -TypeDefinition $source
"Extracted native planning retirement checks passed: $([MultiplayerPlanningRetireTests]::Run())."
