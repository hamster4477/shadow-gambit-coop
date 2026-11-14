$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$native=[IO.File]::ReadAllText((Join-Path $root 'SessionHost.cs'))
function Read-Method([string]$signature){
 $begin=$native.IndexOf($signature,[StringComparison]::Ordinal)
 if($begin -lt 0){throw "Method absent: $signature"}
 $brace=$native.IndexOf('{',$begin);$depth=1;$end=$brace+1
 while($depth -gt 0 -and $end -lt $native.Length){if($native[$end] -eq '{'){$depth++};if($native[$end] -eq '}'){$depth--};$end++}
 return $native.Substring($begin,$end-$begin)
}
$source="using System;`nusing System.Collections.Generic;`nusing UnityEngine;`n"
foreach($name in 'PeerPace.cs','MultiPeerContext.cs'){$source+=([IO.File]::ReadAllText((Join-Path $root $name)) -replace '(?m)^using [^;]+;\r?\n','')+"`n"}
$source+=@'
namespace UnityEngine {internal static class Time {internal static float realtimeSinceStartup=100;}}
internal static class MiTime {internal static int releases;internal static void setPause(bool paused,int mask){if(!paused)releases++;}}
internal static class MoveProbePlugin {internal static class ProbeLog{internal static void LogInfo(string text){}}}
internal sealed class SessionHost {
 private const int JoinPause=1<<29;private static SessionHost active;
 private string generation,phase="idle",receiptGeneration;private bool held,preparation;private float lastPeerSeen;
 private readonly HashSet<string> framePeers=new(StringComparer.Ordinal),requiredFramePeers=new(StringComparer.Ordinal);
'@
$source+=(Read-Method 'internal void PeerSawCheckpoint()')+"`n"+(Read-Method 'private void Release()')
$source+=@'
 internal void Begin(string id,string[] peers,bool prepared=false){generation=id;phase="attaching";held=true;preparation=prepared;requiredFramePeers.Clear();foreach(string p in peers)requiredFramePeers.Add(p);active=this;}
 internal void Required(params string[] peers){requiredFramePeers.Clear();foreach(string p in peers)requiredFramePeers.Add(p);}
 internal void Seen(string peer){using var scope=MultiPeerContext.Scope(peer);PeerSawCheckpoint();}
 internal string Phase=>phase;internal int SeenCount=>framePeers.Count;
 internal void SetReady(){phase="ready";}
}
public static class HostCheckpointPeerTests {
 public static int Run(){int checks=0;void Check(bool b,string why){if(!b)throw new Exception(why);checks++;}
 string a=new string('a',32),b=new string('b',32),c=new string('c',32),d=new string('d',32);
 var host=new SessionHost();host.Begin("one",new[]{a,b,c});
 host.Seen(a);Check(host.Phase=="attaching"&&MiTime.releases==0,"first player cannot release others");
 host.Seen(a);Check(host.SeenCount==1&&MiTime.releases==0,"duplicate receipt cannot count as another player");
 host.Seen(b);Check(host.Phase=="attaching","second player still waits for third");
 host.Seen(d);Check(host.Phase=="attaching","unrequired fourth player cannot acknowledge third");
 host.Seen(c);Check(host.Phase=="released"&&MiTime.releases==1,"all required players release exactly once");
 host.Seen(c);Check(MiTime.releases==1,"post-release receipt is harmless");
 host.Begin("two",new[]{a,b,c});host.Seen(a);
 Check(host.Phase=="attaching"&&host.SeenCount==1,"new generation discards previous frame receipts");
 host.Seen(c);Check(host.Phase=="attaching","old generation's b receipt cannot satisfy new checkpoint");
 host.Required(a,c);host.Seen(a);Check(host.Phase=="released"&&MiTime.releases==2,"disconnected required player retires without blocking ready remainder");
 host.Begin("three",new[]{a,b});host.Seen(a);host.Required(a,b,c);host.Seen(b);Check(host.Phase=="attaching","late participant enlarges barrier");host.Seen(c);Check(host.Phase=="released","late participant releases only on own receipt");
 host.Begin("preparation",new[]{a,b,c},true);host.Seen("guest");Check(host.Phase=="released","native preparation bypasses unavailable gameplay frames after gateway all-ready attach");
 host.Begin("not-ready",new[]{a,b,c});host.SetReady();host.Seen(a);Check(host.Phase=="ready","frame never bypasses loaded receipt phase");
 return checks;
 }
}
'@
Add-Type -TypeDefinition $source
"Extracted native checkpoint frame barrier checks passed: $([HostCheckpointPeerTests]::Run())."
