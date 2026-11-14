$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
function Read-Method([string]$source,[string]$signature) {
    $start=$source.IndexOf($signature,[StringComparison]::Ordinal)
    if($start-lt 0){throw "Missing production method: $signature"}
    $open=$source.IndexOf('{',$start);$depth=1;$end=$open+1
    while($depth-gt 0 -and $end-lt $source.Length){
        if($source[$end]-eq '{'){$depth++}elseif($source[$end]-eq '}'){$depth--};$end++
    }
    if($depth-ne 0){throw "Unbalanced production method: $signature"}
    $source.Substring($start,$end-$start)
}
$entry=[IO.File]::ReadAllText((Join-Path $root 'MissionEntrySync.cs'))
$hostMethod=Read-Method $entry '    internal static void TickHostEntry(bool hosting)'
$continueMethod=Read-Method $entry '    internal static void TickContinue()'
$helpers=(Read-Method $entry '    internal static bool ReadyMatches(')+"`n"+(Read-Method $entry '    internal static bool PeerPresent()')+"`n"+[regex]::Match($entry,'(?m)^    private static bool Waiting=>[^\r\n]+').Value
# Reconstruct only the former lookup ordering, retaining the same production
# logic and the same negative-cache implementation for the comparison.
$oldHost=$hostMethod.Replace('        var loader=SceneLookup.Find<MiSceneLoaderTactics>();','').Replace('        var handler=SceneLookup.Find<MiMissionHandler>();',"        var handler=SceneLookup.Find<MiMissionHandler>();`n        var loader=SceneLookup.Find<MiSceneLoaderTactics>();")
$oldContinue=$continueMethod.Replace('        if(!held&&!wasWaiting)return;','')
$lookup=[IO.File]::ReadAllText((Join-Path $root 'SceneLookup.cs'))
$lookup=[regex]::Replace($lookup,'(?m)^using [^\r\n]+\r?\n','')
$lookup=$lookup.Replace('        Observe();','        Probe.Query(typeof(T));Observe();')
$lookupBefore=$lookup.Replace('class SceneLookup','class SceneLookupBefore')
$lookupBefore=[regex]::Replace($lookupBefore,'(?s)internal static readonly Func<T,bool> Alive=value=>\{.*?\};','internal static readonly Func<T,bool> Alive=value=>value!=null&&value.gameObject!=null&&value.gameObject.activeInHierarchy;')
$slot=[regex]::Replace([IO.File]::ReadAllText((Join-Path $root 'SceneLookupSlot.cs')),'(?m)^using [^\r\n]+\r?\n','')
$class=@'
internal static class EntryCurrent {
    private static MiSceneLoaderTactics waitingLoader;
    private static bool wasWaiting;
    internal static bool WaitingGuestEntry{get;private set;}
    internal static bool IsGated=>Waiting;
    internal static void Reset(){waitingLoader=null;wasWaiting=false;WaitingGuestEntry=false;}
    // METHODS
}
'@
$current=$class.Replace('// METHODS',($helpers+"`n"+$hostMethod+"`n"+$continueMethod))
$old=$class.Replace('EntryCurrent','EntryBefore').Replace('// METHODS',($helpers+"`n"+$oldHost+"`n"+$oldContinue)).Replace('SceneLookup.','SceneLookupBefore.')
$fixture=@'
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace UnityEngine {
    public class GameObject {public bool activeInHierarchy=true;}
    public class Component {
        public GameObject backing=new();public int gameObjectReads;
        public GameObject gameObject {get{gameObjectReads++;return backing;}}
    }
    public static class Time {public static int frameCount;public static float realtimeSinceStartup;}
    public static class Object {
        internal static readonly Dictionary<Type,Component> objects=new();
        public static T FindObjectOfType<T>()where T:Component {
            Probe.Native(typeof(T));
            return objects.TryGetValue(typeof(T),out var value)&&value.backing?.activeInHierarchy==true?value as T:null;
        }
    }
}
namespace UnityEngine.SceneManagement {
    public struct Scene {public int handle;}
    public static class SceneManager {
        public static int handle=1,sceneCount=1;
        public static Scene GetActiveScene()=>new Scene{handle=handle};
    }
}
static class SaveProcessManager {public static bool bLoading;}
static class CoopPerformance {
    public static void Restart(){}
    public static Timing Measure(int stage)=>new();
    public struct Timing:IDisposable{public void Dispose(){}}
}
static class Probe {
    static readonly Dictionary<Type,int> queries=new(),native=new();
    public static void Query(Type t){queries[t]=QueryCount(t)+1;}
    public static void Native(Type t){native[t]=NativeCount(t)+1;}
    public static int QueryCount(Type t)=>queries.TryGetValue(t,out int n)?n:0;
    public static int NativeCount(Type t)=>native.TryGetValue(t,out int n)?n:0;
    public static void Reset(){queries.Clear();native.Clear();}
}
enum MissionState {Init_InitialQuicksave,Playing_Objectives,Playing_Exit}
class Mission {public MissionGuid guid=new();}
class MissionGuid {public int iValue=42;}
class MiMissionHandler:Component {public Mission currentMissionData=new();public MissionState eCurrentMissionState=MissionState.Playing_Objectives;}
class NativeButton {public bool interactable=true;}
class MiSceneLoaderTactics:Component {
    public GameObject m_goRootContinue=new();public NativeButton m_buttonContinue=new();public int clicks;
    public void onContinueButtonPressed(){clicks++;}
}
static class TrainingSession {public static bool Suspended;}
static class GuestMissionPreparation {public static bool WaitingDirectContinue;}
static class SessionHost {public static bool WaitingEntryContinue;public static string DirectoryPath;}
static class SessionBootstrap {public static bool WaitingEntryContinue;}
static class SessionWorld {public static string Current="world";}
// CACHE
// LOOKUP
// LOOKUP BEFORE
// CURRENT
// BEFORE
public static class EntryLookupPerformanceTests {
    static int checks;
    static void Check(bool value,string reason){if(!value)throw new Exception(reason);checks++;}
    static void Reset(){
        UnityEngine.Object.objects.Clear();Probe.Reset();Time.frameCount++;Time.realtimeSinceStartup=0;
        SceneLookup.Invalidate();SceneLookupBefore.Invalidate();EntryCurrent.Reset();EntryBefore.Reset();
        SessionHost.WaitingEntryContinue=false;SessionBootstrap.WaitingEntryContinue=false;
        TrainingSession.Suspended=false;GuestMissionPreparation.WaitingDirectContinue=false;
        SessionWorld.Current="world";SaveProcessManager.bLoading=false;
        File.Delete(Path.Combine(SessionHost.DirectoryPath,"guest-entry-ready.json"));
    }
    static MiMissionHandler Handler(MissionState state=MissionState.Playing_Objectives){
        var h=new MiMissionHandler{eCurrentMissionState=state};UnityEngine.Object.objects[typeof(MiMissionHandler)]=h;return h;
    }
    static MiSceneLoaderTactics Loader(){var l=new MiSceneLoaderTactics();UnityEngine.Object.objects[typeof(MiSceneLoaderTactics)]=l;return l;}
    static void Next(float seconds=.02f){Time.frameCount++;Time.realtimeSinceStartup+=seconds;}
    static (int queries,int scans) Idle(bool before){
        Reset();Handler();
        for(int i=0;i<1800;i++){
            Time.realtimeSinceStartup=i/60f;Time.frameCount++;
            if(before){EntryBefore.TickHostEntry(true);EntryBefore.TickContinue();}
            else{EntryCurrent.TickHostEntry(true);EntryCurrent.TickContinue();}
        }
        return (Probe.QueryCount(typeof(MiSceneLoaderTactics)),Probe.NativeCount(typeof(MiSceneLoaderTactics)));
    }
    public static string Run(string directory){
        SessionHost.DirectoryPath=directory;File.WriteAllText(Path.Combine(directory,"lobby-peer.json"),"{}");
        var before=Idle(true);var after=Idle(false);
        Check(before.queries==3600&&before.scans>50&&before.scans<80,"baseline must retain 0.5-second negative caching, not scan every frame");
        Check(after.queries==0&&after.scans==0,"normal gameplay must make no entry-loader query or native scan");
        Reset();var h=Handler();h.currentMissionData.guid.iValue=-379511569;EntryCurrent.TickHostEntry(true);
        Check(Probe.QueryCount(typeof(MiSceneLoaderTactics))==0,"ship without entry transition skips loader query");
        h.currentMissionData=null;EntryCurrent.TickHostEntry(true);
        Check(Probe.QueryCount(typeof(MiSceneLoaderTactics))==0,"missing mission skips loader query");
        Reset();h=Handler(MissionState.Init_InitialQuicksave);var l=Loader();
        EntryCurrent.TickHostEntry(true);EntryCurrent.TickContinue();
        Check(EntryCurrent.WaitingGuestEntry&&EntryCurrent.IsGated&&l.clicks==0,"Init phase still finds confirmation and waits");
        Check(Probe.NativeCount(typeof(MiSceneLoaderTactics))==1,"two entry consumers share one native search");
        File.WriteAllText(Path.Combine(directory,"guest-entry-ready.json"),"{\"schema\":1,\"world\":\"other\",\"mission\":42}");
        EntryCurrent.TickHostEntry(true);Check(EntryCurrent.WaitingGuestEntry,"unrelated ready acknowledgement cannot release entry");
        File.WriteAllText(Path.Combine(directory,"guest-entry-ready.json"),"{\"schema\":1,\"world\":\"world\",\"mission\":42}");
        EntryCurrent.TickHostEntry(true);EntryCurrent.TickContinue();EntryCurrent.TickContinue();
        Check(!EntryCurrent.IsGated&&l.clicks==1,"matching guest ready releases native Continue exactly once");
        Reset();h=Handler(MissionState.Init_InitialQuicksave);l=Loader();EntryCurrent.TickHostEntry(true);EntryCurrent.TickContinue();
        h.eCurrentMissionState=MissionState.Playing_Objectives;EntryCurrent.TickHostEntry(true);EntryCurrent.TickContinue();
        Check(l.clicks==1&&!EntryCurrent.IsGated,"leaving Init still releases the previously held native confirmation");
        Reset();l=Loader();SessionHost.WaitingEntryContinue=true;EntryCurrent.TickContinue();
        SessionHost.WaitingEntryContinue=false;l.m_buttonContinue.interactable=false;EntryCurrent.TickContinue();
        Check(l.clicks==0,"native button fade preserves pending release");
        l.m_buttonContinue.interactable=true;EntryCurrent.TickContinue();EntryCurrent.TickContinue();
        Check(l.clicks==1,"release survives button fade without a second click");
        SessionBootstrap.WaitingEntryContinue=true;EntryCurrent.TickContinue();
        Check(EntryCurrent.IsGated&&l.clicks==1,"guest wait remains gated and owned by guest coordinator");
        SessionBootstrap.WaitingEntryContinue=false;EntryCurrent.TickContinue();Check(!EntryCurrent.IsGated&&l.clicks==1,"guest release never adds a duplicate native click");
        Reset();l=Loader();SessionHost.WaitingEntryContinue=true;EntryCurrent.TickContinue();
        l.backing.activeInHierarchy=false;var replacement=Loader();SessionHost.WaitingEntryContinue=false;EntryCurrent.TickContinue();
        Check(replacement.clicks==0,"replacement loader never receives a stale release");
        Reset();l=Loader();SessionHost.WaitingEntryContinue=true;EntryCurrent.TickContinue();
        l.m_goRootContinue.activeInHierarchy=false;SessionHost.WaitingEntryContinue=false;EntryCurrent.TickContinue();
        Check(l.clicks==0,"hidden confirmation is not invoked");
        Reset();SessionHost.WaitingEntryContinue=true;EntryCurrent.TickContinue();l=Loader();Next(.11f);EntryCurrent.TickContinue();
        SessionHost.WaitingEntryContinue=false;EntryCurrent.TickContinue();
        Check(l.clicks==1,"loader appearing after negative-cache delay is still acquired while waiting");
        Reset();l=Loader();SceneLookupBefore.Find<MiSceneLoaderTactics>();l.gameObjectReads=0;
        for(int i=0;i<100;i++){Next();SceneLookupBefore.Find<MiSceneLoaderTactics>();}
        int beforeReads=l.gameObjectReads;Check(beforeReads==200,"baseline performs two gameObject reads per active validation");
        Reset();l=Loader();SceneLookup.Find<MiSceneLoaderTactics>();l.gameObjectReads=0;
        bool same=true;for(int i=0;i<100;i++){Next();same&=ReferenceEquals(SceneLookup.Find<MiSceneLoaderTactics>(),l);}
        Check(same,"cached active loader remains valid across 100 frames");
        Check(l.gameObjectReads==100&&Probe.NativeCount(typeof(MiSceneLoaderTactics))==1,"100 active validations read gameObject once each without a new native scan");
        l.backing.activeInHierarchy=false;replacement=Loader();
        Check(ReferenceEquals(SceneLookup.Find<MiSceneLoaderTactics>(),replacement),"inactive cached loader is replaced immediately, even in the same frame");
        replacement.backing=null;Check(SceneLookup.Find<MiSceneLoaderTactics>()==null,"missing gameObject cannot be reused");
        l=Loader();SceneLookup.Invalidate();Check(ReferenceEquals(SceneLookup.Find<MiSceneLoaderTactics>(),l),"explicit scene invalidation clears negative cache immediately");
        replacement=Loader();SceneManager.handle++;Next();
        Check(ReferenceEquals(SceneLookup.Find<MiSceneLoaderTactics>(),replacement),"scene change invalidates cached active loader");
        l=Loader();SaveProcessManager.bLoading=true;Next();
        Check(ReferenceEquals(SceneLookup.Find<MiSceneLoaderTactics>(),l),"native load transition invalidates cached active loader");
        return $"{checks} checks passed. 30 simulated seconds at 60 Hz: loader queries {before.queries} -> {after.queries}; actual native-search callbacks {before.scans} -> {after.scans}. Cached active validations: gameObject reads {beforeReads} -> 100 per 100 validations. This is a call-count benchmark, not an in-game FPS measurement.";
    }
}
'@
$fixture=$fixture.Replace('// CACHE',$slot).Replace('// LOOKUP BEFORE',$lookupBefore).Replace('// LOOKUP',$lookup).Replace('// CURRENT',$current).Replace('// BEFORE',$old)
$temporary=Join-Path $PSScriptRoot ('.entry-lookup-test-'+[Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($temporary)|Out-Null
    Add-Type -TypeDefinition $fixture -IgnoreWarnings -WarningAction SilentlyContinue
    [EntryLookupPerformanceTests]::Run($temporary)
} finally {
    # Only remove the unique test directory created directly under this script.
    $resolved=[IO.Path]::GetFullPath($temporary)
    if([IO.Path]::GetDirectoryName($resolved)-ne [IO.Path]::GetFullPath($PSScriptRoot)){throw 'Unexpected test cleanup path'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
