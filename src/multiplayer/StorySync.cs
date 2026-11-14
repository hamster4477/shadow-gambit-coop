using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// Observe native start commands; replay missing events only. Never intercept
// onTriggerOff or retain native UI/coroutine callbacks across scene teardown.
internal static class StorySync
{
    internal sealed class Event {
        public long seq{get;set;} public string kind{get;set;} public string key{get;set;}
        public long after{get;set;} public int hubDialog{get;set;} public bool consumeCurrency{get;set;} public bool userAction{get;set;}
        public string usable{get;set;} public string actor{get;set;}
        public string collider{get;set;} public bool fromCutscene{get;set;} public bool skipped{get;set;} public string segment{get;set;}
    }
    internal sealed class Frame {
        public int schema{get;set;}=1; public string world{get;set;} public int mission{get;set;}
        public long first{get;set;} public long last{get;set;} public long start{get;set;}
        public long guestSeen{get;set;}
        public string segment{get;set;} public bool playing{get;set;}
        public Event[] events{get;set;}=Array.Empty<Event>();
    }
    internal sealed class Ack {
        public int schema{get;set;}=1;public string world{get;set;}public long seen{get;set;}
        public string request{get;set;}public string[] ready{get;set;}=Array.Empty<string>();
        public bool playing{get;set;}
        public Event[] triggers{get;set;}=Array.Empty<Event>();
    }
    private static bool hosting,guest,transition,applying,held,sawPlaying;
    internal sealed class UseContext {internal MiUsable usable;internal MiCharacter actor;}
    private static UseContext useContext;
    private static readonly Dictionary<string,(MiUsable item,bool used)> usedItems=new();
    private static string itemsWorld;
    private static float nextItems,nextItemScan;
    private static void ObserveUsedItems()
    {
        if(transition||SaveProcessManager.bLoading||guest&&!alignedGuest)return;
        if(itemsWorld!=SessionWorld.Current){itemsWorld=SessionWorld.Current;usedItems.Clear();nextItemScan=0;}
        float now=Time.realtimeSinceStartup;if(now<nextItems)return;nextItems=now+.25f;
        if(now>=nextItemScan){
            nextItemScan=now+5;
            foreach(var item in UnityEngine.Object.FindObjectsOfType<MiUsable>(true))
                if(item!=null&&item.bOnce){string key=Key(item);if(key!=null&&!usedItems.ContainsKey(key))usedItems[key]=(item,item.bUsed);}
        }
        foreach(var key in new List<string>(usedItems.Keys)){
            var value=usedItems[key];if(value.item==null){usedItems.Remove(key);continue;}
            bool used=value.item.bUsed;
            usedItems[key]=(value.item,used);
            if(used&&!value.used)RecordNative(value.item,"used",null);
        }
    }
    internal static UseContext BeginUse(MiUsable usable,MiCharacter actor)
    {var previous=useContext;useContext=new UseContext{usable=usable,actor=actor};return previous;}
    internal static void EndUse(UseContext previous)=>useContext=previous;
    private static string world,guestWorld,localWorld,segment,applyingKey,applyingKind;
    private static int mission;
    private static long sequence,seen,start;
    private static long guestSequence,guestSeen;
    private static bool alignedGuest;
    private static float requestRetry;
    private static readonly List<Event> pending=new();
    private static readonly HashSet<long> guestMatched=new();
    private static readonly List<Event> history=new();
    private sealed class PeerStory {internal long seen;internal float retry;internal readonly HashSet<long> matched=new();}
    private static readonly Dictionary<string,PeerStory> peerStories=new();
    private static readonly StoryReplayLedger natural=new();
    private static Task<string> read,write;
    private static CancellationTokenSource cancel;
    private static Uri room;
    private static Frame remote;
    private static float next,nextLog,lastRemote,retry;
    private const int EndPause=1<<27;
    internal static bool GuestAuthority=>guest;
    internal static bool Waiting=>held;
    private static string Root=>SessionHost.DirectoryPath;
    private static void Log(string message)=>MoveProbePlugin.ProbeLog.LogInfo("Story sync: "+message);
    private static void Error(Exception ex){if(Time.realtimeSinceStartup<nextLog)return;nextLog=Time.realtimeSinceStartup+3;MoveProbePlugin.ProbeLog.LogWarning("Story sync: "+ex.Message);}
    private static void Hold(bool wanted){if(wanted==held)return;MiTime.setPause(wanted,EndPause);held=wanted;}
    internal static void Install(){} // Hooks are audited and installed with NativeSkillHooks.
    // Explicitly armed, read-only sampling; no native detours or callback replacement.
    private static float monitorNext,monitorUntil;
    private static int monitorLines;
    private static string monitorPrevious;
    private static void ObserveRevivalAndPlans()
    {
        float now=Time.realtimeSinceStartup;
        if(now<monitorNext)return;monitorNext=now+.5f;
        string request=Path.Combine(Root,"monitor-revival.request");
        if(File.Exists(request))
        {
            File.Delete(request);monitorUntil=now+600;monitorLines=0;monitorPrevious=null;
            Log("revival/plan monitor armed for 600 seconds");
        }
        if(now>=monitorUntil||monitorLines>=600||transition||SaveProcessManager.bLoading)return;
        var text=new StringBuilder("world=").Append(SessionWorld.Current).Append(" cutscene=").Append(MiCutsceneHandler.bCutsceneRunning);
        var hub=SceneLookup.Find<Mimimi.HUB.HUBLogic>();
        if(hub?.characters!=null)
        {
            text.Append(" dialog=").Append(hub.dialogs?.m_iPlayingDialogGUID??0);
            text.Append(" revived=").Append(GuestMove.Identity(hub.characters.m_lastRevivedCharacter));
            var roster=hub.characters.m_liSpawnedCharacterData;
            if(roster!=null)foreach(var d in roster)
            {
                if(d==null)continue;
                var actor=d.m_character;
                text.Append(" | crew=").Append(GuestMove.Identity(actor)).Append(" unlocked=").Append(d.bUnlocked)
                    .Append(" saved=").Append(d.m_saveData?.bUnlocked).Append(" active=").Append(d.m_saveData?.bActiveOnHub)
                    .Append(" health=").Append(actor?.health?.eHealthState).Append(" player=").Append(actor!=null&&actor.bControllerPlayer());
            }
        }
        var plan=SceneLookup.Find<PlanningMode>();
        if(plan!=null)
        {
            text.Append(" plan=").Append(plan.bIsActive).Append(" executing=").Append(plan.bExecuteMode);
            var commands=plan.m_lCommands;
            if(commands!=null)for(int i=0;i<commands.Count&&i<32;i++)
            {
                var pair=commands[i];var cmd=pair.second;
                text.Append(" | planned=").Append(GuestMove.Identity(pair.first)).Append(" skill=").Append(cmd?.skill?.eSkillType)
                    .Append(" state=").Append(cmd?.skillCommand?.m_eState).Append(" execute=").Append(cmd?.bExecute)
                    .Append(" chain=").Append(cmd?.lSkillCommandChain?.Count??0);
            }
        }
        string sample=text.ToString();
        if(sample==monitorPrevious)return;
        monitorPrevious=sample;monitorLines++;Log("revival/plan sample "+sample);
    }
    internal sealed class RevivalScope
    {
        internal Mimimi.HUB.HUBCharactersLogic.HUBCharacterData data;
        internal bool consume,userAction;
        internal RevivalScope parent;
        internal readonly List<Action> events=new();
    }
    [ThreadStatic] private static RevivalScope revivalScope;
    internal static RevivalScope BeginRevival(Mimimi.HUB.HUBCharactersLogic hub,MiCharacter actor,bool consume,bool userAction)
    {
        if((!hosting&&!guest)||actor==null||hub?.m_liSpawnedCharacterData==null)return null;
        foreach(var d in hub.m_liSpawnedCharacterData)
            if(d?.m_character==actor&&!d.bUnlocked)
            {var scope=new RevivalScope{data=d,consume=consume,userAction=userAction,parent=revivalScope};revivalScope=scope;return scope;}
        return null;
    }
    internal static void EndRevival(RevivalScope scope,Exception failure)
    {
        if(scope==null)return;
        revivalScope=scope.parent;
        try
        {
            if(failure==null&&scope.data.bUnlocked)
            {
                var previous=useContext;useContext=null;
                try{RecordNative(null,"revive",null,0,GuestMove.Identity(scope.data.m_character),scope.consume,scope.userAction);}
                finally{useContext=previous;}
            }
            foreach(var emit in scope.events)emit();
        }
        catch(Exception ex){Error(ex);}
    }
    private static int observedHubDialog;
    private static string HubKey(int id)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hub-dialog:"+id.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    private static void ObserveHubDialog(){
        if(transition||SaveProcessManager.bLoading||guest&&!alignedGuest)return;
        if(SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue!=ShipCrewSession.Mission){observedHubDialog=0;return;}
        var hub=SceneLookup.Find<Mimimi.HUB.HUBLogic>()?.dialogs;
        int id=hub?.m_iPlayingDialogGUID??0;
        if(id==observedHubDialog)return;
        observedHubDialog=id;
        if(id!=0&&MiCutsceneHandler.bCutsceneRunning)RecordNative(null,"hub",null,id);
    }
    private static string Key(Component value){
        if(value==null)return null;
        var t=value.transform;var path=new StringBuilder(value.gameObject.scene.name);
        var nodes=new List<string>();
        for(int n=0;t!=null&&n<80;n++,t=t.parent)nodes.Add(t.name+"["+t.GetSiblingIndex()+"]");
        for(int i=nodes.Count-1;i>=0;i--)path.Append('/').Append(nodes[i]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToString())));
    }
    internal static string PlanningKey(Component value)=>Key(value);
    internal static T PlanningFind<T>(string key) where T:Component=>Find<T>(key);
    private static T Find<T>(string key) where T:Component{
        T result=null;
        foreach(var value in UnityEngine.Object.FindObjectsOfType<T>(true))if(Key(value)==key){
            if(result!=null)throw new InvalidOperationException("Ambiguous story object");result=value;
        }
        return result;
    }
    private static bool Identity(string value)=>value!=null&&SyncProtocol.Identity(value);
    private static bool Planning(string kind)=>kind=="plan-enter"||kind=="plan-exit"||kind=="plan-execute";
    private static bool HubValid(Event e)=>(e.kind=="hub"?e.hubDialog!=0:e.hubDialog==0)&&(e.kind=="revive"||!e.consumeCurrency&&!e.userAction);
    private static bool UseValid(Event e)=>e.usable==null?e.actor==null:Identity(e.usable)&&Identity(e.actor);
    internal static bool Valid(Frame f){
        if(f==null||f.schema!=1||!SyncProtocol.Session(f.world)||f.mission==0||f.events==null||f.events.Length>64||f.first<1||f.last<f.first-1||f.start<0||f.start>f.last||f.guestSeen<0)return false;
        long n=f.first;
        foreach(var e in f.events)if(e==null||!UseValid(e)||!HubValid(e)||e.seq!=n++||e.kind!="script"&&e.kind!="timeline"&&e.kind!="dialog"&&e.kind!="used"&&e.kind!="finished"&&e.kind!="hub"&&e.kind!="revive"&&!Planning(e.kind)||!Identity(e.key)||e.collider!=null&&!Identity(e.collider)||e.segment!=null&&!SyncProtocol.Session(e.segment))return false;
        return n-1==f.last&&(f.segment==null||SyncProtocol.Session(f.segment));
    }
    private static void ObserveLocalWorld(){
        string current=SessionWorld.Current;if(current==localWorld)return;
        Hold(false);natural.Clear();pending.Clear();guestSequence=0;alignedGuest=false;observedHubDialog=0;localWorld=current;sawPlaying=false;
    }
    private static void RefreshHostWorld(){
        string current=SessionWorld.Current;
        mission=SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue??0;
        if(current==world)return;
        Hold(false);world=current;history.Clear();guestMatched.Clear();peerStories.Clear();sequence=start=guestSeen=0;requestRetry=0;segment=null;sawPlaying=false;
    }
    internal static void RecordNative(Component command,string kind,TriggerData data,int hubDialog=0,string revived=null,bool consumeCurrency=false,bool userAction=false){
        if(TrainingSession.Suspended||PlanningSync.Applying&&Planning(kind)||!hosting&&!guest)return;
        if(revivalScope!=null)
        {
            var context=useContext;
            revivalScope.events.Add(()=>{var previous=useContext;useContext=context;try{RecordNative(command,kind,data,hubDialog,revived,consumeCurrency,userAction);}finally{useContext=previous;}});
            return;
        }
        try{
            string key=kind=="revive"?revived:kind=="hub"?HubKey(hubDialog):Key(command);if(key==null)return;
            if(applying&&Planning(kind))return;
            if(Planning(kind)&&(transition||SaveProcessManager.bLoading||MiCutsceneHandler.bCutsceneRunning))return;
            if(guest&&applying&&key==applyingKey&&kind==applyingKind)return;
            if(guest){
                ObserveLocalWorld();natural.Record(kind,key);
                // Replay callbacks are already host events. Only locally started
                // commands in an aligned, live scene may request host execution.
                if(!applying&&alignedGuest&&!transition&&!SaveProcessManager.bLoading&&(!MiCutsceneHandler.bCutsceneRunning||kind=="used"||kind=="finished"||kind=="hub"||kind=="revive"||Planning(kind))){
                    if(pending.Count>=16){Error(new InvalidOperationException("Guest story request queue full"));return;}
                    pending.Add(new Event{seq=++guestSequence,after=seen,kind=kind,key=key,hubDialog=hubDialog,consumeCurrency=consumeCurrency,userAction=userAction,usable=Key(useContext?.usable),actor=Key(useContext?.actor),collider=Key(data?.m_collider),fromCutscene=data?.m_bTriggeredByCutscene==true});
                    next=0;
                }
                return;
            }
            RefreshHostWorld();if(world==null||mission==0)return;
            if(segment==null||!MiCutsceneHandler.bCutsceneRunning||kind=="hub"){segment=Guid.NewGuid().ToString("N");start=sequence+1;sawPlaying=false;}
            history.Add(new Event{seq=++sequence,kind=kind,key=key,hubDialog=hubDialog,consumeCurrency=consumeCurrency,userAction=userAction,usable=Key(useContext?.usable),actor=Key(useContext?.actor),collider=Key(data?.m_collider),fromCutscene=data?.m_bTriggeredByCutscene==true,segment=segment});
            if(history.Count>64){guestMatched.Remove(history[0].seq);history.RemoveAt(0);}
            next=0;Log("host event "+sequence+" "+kind+" "+(command?.name??("hub-dialog:"+hubDialog)));
        }catch(Exception ex){Error(ex);}
    }
    internal static void Tick(bool host,Uri target,bool changing,LobbyProtocol.Frame lobby,bool aligned=true){
        if(TrainingSession.Suspended){Close();return;}
        hosting=host;guest=!host&&target!=null;transition=changing;
        if(!hosting&&!guest){Close();return;}
        try{
            ObserveLocalWorld();
            ObserveUsedItems();
            ObserveHubDialog();
            ObserveRevivalAndPlans();
            if(transition||SaveProcessManager.bLoading)Hold(false);
            if(hosting){HostTick();return;}
            if(room!=target){CloseNetwork();room=target;cancel=new CancellationTokenSource();guestWorld=null;remote=null;seen=0;}
            if(write?.IsCompleted==true){write.GetAwaiter().GetResult();write=null;}
            if(read?.IsCompleted==true){
                var f=JsonSerializer.Deserialize<Frame>(read.GetAwaiter().GetResult());read=null;
                if(Valid(f)&&lobby?.world==f.world){remote=f;lastRemote=Time.realtimeSinceStartup;}
            }
            bool active=aligned&&!transition&&!SaveProcessManager.bLoading&&remote!=null&&Time.realtimeSinceStartup-lastRemote<3&&lobby?.world==remote.world&&SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue==remote.mission;
            alignedGuest=active;
            if(active){
                if(guestWorld!=remote.world){
                    guestWorld=remote.world;Hold(false);sawPlaying=false;
                    pending.Clear();guestSequence=remote.guestSeen;
                    // Completed events before this checkpoint are already in the save.
                    seen=remote.playing?Math.Max(remote.first-1,remote.start-1):remote.last;
                    Log("guest journal baseline="+seen+" playing="+remote.playing);
                }
                pending.RemoveAll(e=>e.seq<=remote.guestSeen);
                if(seen<remote.first-1)throw new InvalidOperationException("Story journal gap; a fresh checkpoint is needed");
                if(Time.realtimeSinceStartup>=retry)foreach(var e in remote.events){
                    if(e.seq<=seen)continue;
                    Hold(false);
                    if(!Apply(e)){retry=Time.realtimeSinceStartup+.5f;Error(new InvalidOperationException("Waiting for story object "+e.seq+" "+e.kind));break;}
                    seen=e.seq;retry=0;Log("guest applied "+seen+" "+e.kind);break;
                }
                bool playing=MiCutsceneHandler.bCutsceneRunning;if(playing)sawPlaying=true;
                Hold(sawPlaying&&!playing&&remote.playing&&seen>=remote.last);
            }else Hold(false);
            if(Time.realtimeSinceStartup<next)return;next=Time.realtimeSinceStartup+.15f;
            if(read==null)read=GuestMove.Story(room,cancel.Token);
            if(write==null&&guestWorld!=null&&remote?.world==guestWorld)
                write=GuestMove.SendStory(room,JsonSerializer.Serialize(new Ack{world=guestWorld,seen=seen,playing=MiCutsceneHandler.bCutsceneRunning,triggers=active?pending.ToArray():Array.Empty<Event>(),request=Guid.NewGuid().ToString("N")}),cancel.Token);
        }catch(Exception ex){Hold(false);if(read?.IsCompleted==true)read=null;if(write?.IsCompleted==true)write=null;Error(ex);}
    }
    private static bool Apply(Event e){
        if(guest&&natural.Consume(e.kind,e.key))return true;
        if(e.kind=="revive"){
            if(SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue!=ShipCrewSession.Mission)return false;
            var hub=SceneLookup.Find<Mimimi.HUB.HUBLogic>()?.characters;
            if(hub?.m_liSpawnedCharacterData==null||!hub.m_bRoutineSpawningProcessEnded)return false;
            foreach(var d in hub.m_liSpawnedCharacterData){
                if(d?.m_character==null||GuestMove.Identity(d.m_character)!=e.key)continue;
                if(d.bUnlocked)return true;
                if(MiCutsceneHandler.bCutsceneRunning)return false;
                applying=true;applyingKey=e.key;applyingKind=e.kind;
                try{hub.reviveCharacter(d.m_character,e.consumeCurrency,e.userAction);return d.bUnlocked;}
                finally{applying=false;applyingKey=applyingKind=null;}
            }
            return false;
        }
        if(Planning(e.kind)){
            if(MiCutsceneHandler.bCutsceneRunning)return false;
            var mode=Find<PlanningMode>(e.key);if(mode==null)return false;
            applying=true;applyingKey=e.key;applyingKind=e.kind;
            try{
                if(e.kind=="plan-enter"){
                    if(!mode.bIsActive)mode.activate();
                    var input=SceneLookup.Find<MiPlayerInput>();
                    input?.m_planningModeInput?.checkActive(input,false);
                    input?.m_planningModeInput?.checkLeaderChange(input);
                }
                else if(e.kind=="plan-exit"){
                    if(mode.bIsActive)mode.deactivate();
                    var input=SceneLookup.Find<MiPlayerInput>();
                    input?.m_planningModeInput?.checkActive(input,false);
                }
                else mode.execute();
            }finally{applying=false;applyingKey=applyingKind=null;}
            if(hosting)RecordNative(mode,e.kind,null);
            return true;
        }
        if(e.kind=="hub"){
            if(SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue!=ShipCrewSession.Mission||e.key!=HubKey(e.hubDialog))return false;
            var hub=SceneLookup.Find<Mimimi.HUB.HUBLogic>()?.dialogs;if(hub==null)return false;
            if(hub.m_iPlayingDialogGUID==e.hubDialog){observedHubDialog=e.hubDialog;return true;}
            if(MiCutsceneHandler.bCutsceneRunning)return false;
            var dialog=hub.getActiveDialogWithID(e.hubDialog);if(dialog==null)return false;
            // Native external entry supplies the game's own speaker resolver,
            // completion event and progress saving; no callback detours.
            applying=true;applyingKey=e.key;applyingKind=e.kind;
            try{hub.playDialogCutsceneExternal(dialog);observedHubDialog=hub.m_iPlayingDialogGUID;
                if(observedHubDialog!=e.hubDialog)return false;
                if(hosting)RecordNative(null,"hub",null,e.hubDialog);
                return true;
            }finally{applying=false;applyingKey=applyingKind=null;}
        }
        if(e.kind=="finished"){
            var interaction=Find<MissionInteractionGeneric>(e.key);if(interaction==null)return false;
            if(!interaction.bOnce)throw new InvalidOperationException("Interaction is repeatable");
            // Shipping onClicked unregisters the one-shot action, then marks
            // it finished before starting its commands. Replay only that
            // completion here; its dialogue/mission graph is already journaled.
            if(interaction.m_bRegistered)interaction.unregister();
            interaction.m_bFinished=true;
            if(hosting)RecordNative(interaction,"finished",null);
            return true;
        }
        if(e.kind=="used"){
            var item=Find<MiUsable>(e.key);if(item==null)return false;
            if(!item.bOnce)throw new InvalidOperationException("Used-state target is not one-shot");
            if(!item.bUsed)item.disableForever();
            usedItems[e.key]=(item,true);
            if(hosting)RecordNative(item,"used",null);
            return true;
        }
        if(e.usable!=null){
            var usable=Find<MiUsable>(e.usable);var actor=Find<MiCharacter>(e.actor);
            if(usable==null||actor==null)return false;
            if(usable.bOnce&&usable.bUsed)return true;
            // Execute the originating use graph, including once-only state and
            // mission commands, not just its dialogue child. Nested dialogue
            // observation is suppressed/consumed by the existing replay ledger.
            applying=true;applyingKey=e.key;applyingKind=e.kind;
            try{usable.use(actor);return true;}
            finally{applying=false;applyingKey=applyingKind=null;}
        }
        var collider=e.collider==null?null:Find<Collider>(e.collider);if(e.collider!=null&&collider==null)return false;
        Component command=e.kind=="script"?Find<CmdCutscene>(e.key):e.kind=="dialog"?Find<CmdAbortableDialogCutscene>(e.key):Find<CmdPlayTimelineCutscene>(e.key);
        if(command==null)return false;
        var data=collider==null?new TriggerData():new TriggerData(collider);data.m_bTriggeredByCutscene=e.fromCutscene;
        applying=true;applyingKey=e.key;applyingKind=e.kind;
        try{if(e.kind=="script")command.Cast<CmdCutscene>().triggerOverride(data);else if(e.kind=="dialog")command.Cast<CmdAbortableDialogCutscene>().triggerOverride(data);else command.Cast<CmdPlayTimelineCutscene>().triggerOverride(data);return true;}
        finally{applying=false;applyingKey=applyingKind=null;}
    }
    private static void HostTick(){
        RefreshHostWorld();if(world==null||mission==0)return;
        bool playing=MiCutsceneHandler.bCutsceneRunning;if(playing)sawPlaying=true;
        if(Time.realtimeSinceStartup<next)return;next=Time.realtimeSinceStartup+.15f;
        var f=new Frame{world=world,mission=mission,first=history.Count==0?1:history[0].seq,last=sequence,start=start,guestSeen=guestSeen,segment=segment,playing=playing,events=history.ToArray()};
        Atomic("host-story.json",f);
        if(transition||SaveProcessManager.bLoading){Hold(false);return;}
        bool waiting=false;
        var live=new HashSet<string>(MultiplayerPeers.Active(),StringComparer.Ordinal);
        foreach(string old in new List<string>(peerStories.Keys))if(!live.Contains(old))peerStories.Remove(old);
        foreach(string peer in live)
        {
            if(!peerStories.TryGetValue(peer,out var state))peerStories[peer]=state=new PeerStory();
            using var scope=MultiPeerContext.Scope(peer);
            string path=MultiplayerPeers.PathFor(peer,"story");
            if(!MultiplayerPeers.Fresh(path,3,8192)){waiting|=sawPlaying&&!playing;continue;}
            Ack ack;
            try{
                using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var reader=new StreamReader(input);ack=JsonSerializer.Deserialize<Ack>(reader.ReadToEnd());
            }catch(IOException){waiting|=sawPlaying&&!playing;continue;}
            bool valid=ack?.schema==1&&ack.world==world&&ack.seen>=0&&ack.seen<=sequence;
            if(valid&&ack.triggers!=null&&ack.triggers.Length<=16){
                foreach(var e in ack.triggers){
                    if(e==null||e.seq<=state.seen)continue;
                    if(e.seq!=state.seen+1||e.after<0||e.after>sequence||!UseValid(e)||!HubValid(e)||!Identity(e.key)||e.kind!="script"&&e.kind!="timeline"&&e.kind!="dialog"&&e.kind!="used"&&e.kind!="finished"&&e.kind!="hub"&&e.kind!="revive"&&!Planning(e.kind)||e.collider!=null&&!Identity(e.collider))break;
                    var matched=history.Find(h=>h.seq>e.after&&!state.matched.Contains(h.seq)&&h.kind==e.kind&&h.key==e.key);
                    if(matched!=null){state.matched.Add(matched.seq);state.seen=e.seq;state.retry=0;continue;}
                    if(state.retry==0){state.retry=Time.realtimeSinceStartup+.35f;break;}
                    if(Time.realtimeSinceStartup<state.retry)break;
                    Hold(false);long before=sequence;
                    if(!Apply(e)){state.retry=Time.realtimeSinceStartup+.5f;break;}
                    matched=history.Find(h=>h.seq>before&&h.kind==e.kind&&h.key==e.key);
                    if(matched!=null)state.matched.Add(matched.seq);
                    state.seen=e.seq;state.retry=0;
                    Log("host accepted peer story "+peer+" "+state.seen+" "+e.kind);break;
                }
            }
            if(peer=="guest")guestSeen=state.seen;
            else Atomic(Path.Combine("peers",peer,"story-receipt.json"),new{schema=1,world=world,seen=state.seen});
            playing=MiCutsceneHandler.bCutsceneRunning;if(playing)sawPlaying=true;
            waiting|=sawPlaying&&!playing&&(!valid||ack.playing||ack.seen<sequence);
        }
        Hold(waiting);
    }

    private static void Atomic(string name,object value){Directory.CreateDirectory(Root);string p=Path.Combine(Root,name);File.WriteAllText(p+".new",JsonSerializer.Serialize(value));File.Move(p+".new",p,true);}
    private static void CloseNetwork(){cancel?.Cancel();cancel?.Dispose();cancel=null;foreach(var task in new[]{read,write})if(task!=null)_=task.ContinueWith(t=>{_=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);read=write=null;room=null;}
    internal static void Close(){usedItems.Clear();itemsWorld=null;nextItems=nextItemScan=0;useContext=null;Hold(false);CloseNetwork();remote=null;guestWorld=world=localWorld=segment=null;history.Clear();natural.Clear();pending.Clear();guestMatched.Clear();peerStories.Clear();sequence=seen=start=guestSequence=guestSeen=0;next=retry=lastRemote=requestRetry=0;hosting=guest=sawPlaying=alignedGuest=false;}
}
