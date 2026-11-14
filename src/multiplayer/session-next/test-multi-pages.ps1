$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$reinforcement=[IO.File]::ReadAllText((Join-Path $root 'ReinforcementSync.cs'))
$pursuit=[IO.File]::ReadAllText((Join-Path $root 'GuardPursuit.cs'))
function Section([string]$from,[string]$to){
    $start=$pursuit.IndexOf($from,[StringComparison]::Ordinal)
    $end=$pursuit.IndexOf($to,$start,[StringComparison]::Ordinal)
    if($start -lt 0 -or $end -le $start){throw "Production section missing: $from"}
    $pursuit.Substring($start,$end-$start)
}
$view='internal sealed class GuardPursuit {'+
    (Section 'private readonly List<MiCharacter> crew' '    private bool NearCrew')+
    (Section 'private sealed class Strike' '    private readonly Dictionary<string,int> played')+
    (Section 'private readonly Dictionary<string,(Vector3 point,float until)> waypoints' '    [HarmonyPatch(typeof(SkillInvestigate)')+@'
    private float nextScan;
    private readonly Dictionary<string,string> sightSamples=new(),sampledSight=new(),sentSight=new();
    internal void Put(string id,string victim,float until){strikes[id]=new Strike{command=new IntPtr(1),serial=7,kind="Gun",victim=victim,until=until};}
    internal string Victim(string id)=>strikes.TryGetValue(id,out var strike)?strike.victim:null;
    internal int Serial(string id)=>strikes[id].serial;
}
'@
$fixture=@'
internal static class MultiPeerContext{internal static string Current="guest";}
namespace UnityEngine{
 public struct Vector3{public float x,y,z;}
 public static class Time{public static float time,realtimeSinceStartup;}
 public static class Object{public static AlarmSystem Alarm;public static T FindObjectOfType<T>()where T:class=>Alarm as T;}
}
public class MiCharacter{public string id;}
public static class GuestMove{public static string Identity(MiCharacter actor)=>actor?.id;}
public class HouseBuilding{public string name="house";public HouseTransform transform=new();}
public class HouseTransform{public Vector3 position;}
public class HouseReference{public int m_guid;public HouseBuilding Resolve(bool value)=>new();}
public class AlarmHouse{public HouseReference m_alarmHouse=new();public bool bIsSpawningGuards=true;public List<MiCharacter> m_liSpawnedChars=new();public bool bHasSpawnableGuards()=>true;public void spawn(object info){}}
public class AlarmSquad{public object m_targetInfo=new();}
public class AlarmSystem{public List<AlarmHouse> m_liAlarmHouses=new();public AlarmSquad squadLastActive=new();}
public static class MoveProbePlugin{public static class ProbeLog{public static void LogInfo(string text){}}}
public static class MultiPageTests{
 static int count;static void Check(bool condition,string message){if(!condition)throw new Exception(message);count++;}
 public static int Run(){
  UnityEngine.Object.Alarm=new();for(int i=1;i<=16;i++)UnityEngine.Object.Alarm.m_liAlarmHouses.Add(new AlarmHouse{m_alarmHouse=new(){m_guid=i}});
  foreach(int peers in new[]{2,3,12,64}){
   var source=new ReinforcementSync();var seen=new HashSet<string>[peers];for(int peer=0;peer<peers;peer++)seen[peer]=new();
   for(int frame=0;frame<16;frame++)for(int peer=0;peer<peers;peer++){
    MultiPeerContext.Current=peer.ToString("x32");string house=source.Write("Alarm 1");Check(house!="-","active reinforcement house is published");seen[peer].Add(house);
   }
   foreach(var houses in seen)Check(houses.Count==16,"every peer eventually sees all reinforcement houses despite matching poll periods");
  }
  var expired=new ReinforcementSync();MultiPeerContext.Current="old";Check(expired.Write("Alarm 1")=="g1","fresh page starts at first house");
  Time.time=Time.realtimeSinceStartup=31;MultiPeerContext.Current="new";expired.Write("Alarm 1");MultiPeerContext.Current="old";
  Check(expired.Write("Alarm 1")=="g1","abandoned peer pagination expires without retaining participants forever");
  var a=new MiCharacter{id="a"};var b=new MiCharacter{id="b"};var c=new MiCharacter{id="c"};var d=new MiCharacter{id="d"};
  var observations=new GuardPursuit();observations.SetCrew(a,b,new[]{c,d});
  observations.SetWaypoint("guard",new Vector3{x=12},8);observations.Put("hitA","A",Time.time+2);observations.Put("hitB","B",Time.time+2);observations.Put("hitC","c",Time.time+2);observations.Put("ended","B",Time.time-1);
  var peerView=new GuardPursuit();peerView.SetCrew(a,c,new[]{b,d});peerView.CopyHostObservationsFrom(observations,a,c);
  Check(peerView.Waypoint("guard",out var point)&&point.x==12,"late peer receives current random search destination");
  Check(peerView.Victim("hitA")=="A"&&peerView.Victim("hitB")=="b"&&peerView.Victim("hitC")=="B","native strike targets reproject to each peer's A/B order");
  Check(peerView.Serial("hitB")==7&&peerView.Victim("ended")==null,"seeding preserves native strike serial and excludes ended cues");
  peerView.SetCrew(d,b,new[]{a,c});
  Check(peerView.Victim("hitA")=="a"&&peerView.Victim("hitB")=="B"&&peerView.Victim("hitC")=="c","changing primary actors never retargets retained strikes");
  peerView.SetCrew(d,b,new[]{a});Check(peerView.Victim("hitC")==null,"removed actor retires its retained strike instead of hitting another slot");
  observations.SetWaypoint("guard",new Vector3{x=99},8);
  Check(peerView.Waypoint("guard",out point)&&point.x==12,"seed is a value copy; later source changes do not mutate another peer's cache");
  Time.time=40;Check(!peerView.Waypoint("guard",out _),"late-join seeding does not extend original waypoint lifetime");
  var unknown=new GuardPursuit();unknown.Put("ambiguous","B",Time.time+2);peerView.CopyHostObservationsFrom(unknown,d,b);
  Check(peerView.Victim("ambiguous")==null,"unknown source A/B mapping cannot invent a strike target");
  return count;
 }
}
'@
Add-Type -TypeDefinition ($reinforcement+"`n"+$view+"`n"+$fixture) -IgnoreWarnings -WarningAction SilentlyContinue
"Multiplayer page/observation checks passed: $([MultiPageTests]::Run()). Production reinforcement capture and pursuit identity/seed methods; simulated native actors."
