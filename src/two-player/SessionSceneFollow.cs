using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;

// Preparation polling survives the absence of A/B actors. Only a confirmed
// scene epoch change queues a handoff; pause/map selection is not a new world.
internal sealed class SessionSceneFollow
{
    private string address,world,lastJoined,candidate,pending,pendingWorld;
    private float candidateSince,retry;
    private string alignedTransaction;
    private string returningShipWorld,lastShipAttempt;
    private float shipDeadline;
    private readonly HashSet<string> retired=new HashSet<string>(StringComparer.Ordinal);
    private readonly Queue<string> retirementOrder=new Queue<string>();
    internal bool Matches(Uri target,string candidateWorld)=>target!=null&&target.AbsoluteUri==address&&world!=null&&world==candidateWorld&&pending==null;
    internal bool HasWorld(Uri target)=>target!=null&&target.AbsoluteUri==address&&world!=null&&pending==null;
    internal void Close()
    {alignedTransaction=null;address=world=lastJoined=candidate=pending=pendingWorld=returningShipWorld=lastShipAttempt=null;candidateSince=retry=0;retired.Clear();retirementOrder.Clear();}
    private void Confirm(string value)
    {
        if(world!=null&&world!=value&&retired.Add(world))
        {retirementOrder.Enqueue(world);if(retirementOrder.Count>64)retired.Remove(retirementOrder.Dequeue());}
        world=value;
    }
    private static string PathFor(string name)=>Path.Combine(SessionHost.DirectoryPath,name);
    internal bool Tick(Uri target,LobbyProtocol.Frame frame,int localMission,bool synchronized,
        string joinedRoom,string joinedWorld,bool transition,Action<string> report,Func<bool> supersede=null,Func<bool> returnPreparedShip=null,Func<bool> shipReady=null,bool nativeTransition=false)
    {
        if(target==null){Close();return false;}
        if(address!=target.AbsoluteUri){Close();address=target.AbsoluteUri;}
        float now=Time.realtimeSinceStartup;
        if(joinedRoom==address&&SyncProtocol.Session(joinedWorld)&&joinedWorld!=lastJoined)
        {
            lastJoined=joinedWorld;Confirm(joinedWorld);
            if(pendingWorld==joinedWorld){pending=pendingWorld=null;}
            candidate=null;retry=now+1f;
        }
        // Native tutorial rewinds also run locally. During teardown the mission
        // temporarily becomes zero: it is not evidence that a menu entry is needed.
        // Retain the confirmed epoch and decide only once native loading settles.
        if(nativeTransition)return false;
        if(returningShipWorld!=null){
            if(frame!=null&&frame.world!=returningShipWorld&&frame.phase!="loading"){
                returningShipWorld=null;report("房主再次换场景，等待新的目标…");
            }else if(shipReady?.Invoke()==true){
                Confirm(returningShipWorld);returningShipWorld=null;candidate=null;retry=now+1f;
                report("已通过原版返回红玛丽号。");return false;
            }else if(now>shipDeadline){returningShipWorld=null;retry=now+5;report("原版回船未完成，恢复接档流程。");}
            else return true;
        }
        if(pending!=null)
        {
            // A committed destination (e.g. the ship) supersedes an older
            // reload. Never act on the transient menu/loading frame between
            // scenes, or interrupt native loading/index mutation.
            bool newer=frame!=null&&SyncProtocol.Session(frame.world)&&frame.world!=pendingWorld&&frame.world!=world&&
                !retired.Contains(frame.world)&&(frame.phase=="playing"||frame.phase=="paused");
            if(newer){
                if(candidate!=frame.world){candidate=frame.world;candidateSince=now;}
                else if(now-candidateSince>=1f&&supersede!=null&&supersede()){
                    if(SyncProtocol.Session(pendingWorld)&&retired.Add(pendingWorld)){
                        retirementOrder.Enqueue(pendingWorld);if(retirementOrder.Count>64)retired.Remove(retirementOrder.Dequeue());
                    }
                    pending=pendingWorld=null;transition=false;retry=0;
                    report("Host changed scene. Retired the unfinished previous checkpoint handoff.");
                }
            }else candidate=null;
        }
        if(pending!=null)
        {
            try
            {
                string path=PathFor("live-import-result.json");
                if(File.Exists(path)&&new FileInfo(path).Length<=8192)
                {
                    using var result=JsonDocument.Parse(File.ReadAllText(path));var root=result.RootElement;
                    if(root.GetProperty("generation").GetString()==pending)
                    {
                        string phase=root.GetProperty("phase").GetString();
                        if(phase=="menu-ready"&&pendingWorld=="menu")
                        {Confirm(null);pending=pendingWorld=null;candidate=null;retry=now+1f;}
                        string loadedPath=PathFor("session-load-result.json");
                        if(root.TryGetProperty("checkpoint",out var checkpoint)&&checkpoint.ValueKind==JsonValueKind.String&&File.Exists(loadedPath)&&new FileInfo(loadedPath).Length<=8192){
                            using var loaded=JsonDocument.Parse(File.ReadAllText(loadedPath));
                            if(loaded.RootElement.GetProperty("generation").GetString()==checkpoint.GetString()){
                                string status=loaded.RootElement.GetProperty("status").GetString();
                                if(status=="loaded")
                                {Confirm(pendingWorld);pending=pendingWorld=null;candidate=null;retry=now+1f;}
                                else if(status=="failed"&&supersede!=null&&supersede()){
                                    pending=pendingWorld=null;candidate=null;transition=false;retry=now+5f;
                                    report("接档失败，正在重新获取房主检查点… / Checkpoint load failed; retrying the host checkpoint…");
                                }
                            }
                        }
                        if(phase=="failed"||phase=="cancelled")
                        {pending=pendingWorld=null;candidate=null;retry=now+5f;report("Scene handoff interrupted. Waiting for a fresh host scene...");}
                    }
                }
            }
            catch(IOException){}catch(JsonException){}
            if(pending!=null)return false;
        }
        bool menu=frame!=null&&frame.phase=="menu"&&frame.world==null&&world!=null;
        bool early=frame!=null&&frame.mission!=0&&frame.mission!=ShipCrewSession.Mission&&
            (frame.entering||frame.reloading||frame.nativeStage?.StartsWith("Init_",StringComparison.Ordinal)==true);
        if(!menu&&(frame==null||!SyncProtocol.Session(frame.world)||!early&&frame.phase!="playing"&&frame.phase!="paused"))
        {candidate=null;return false;}
        string destination=menu?"menu":frame.world;
        // Matching mission IDs or receiving an old gameplay frame cannot
        // establish an epoch: a reload may reuse both. An unseeded manual
        // connection therefore takes one fresh checkpoint handoff as well.
        bool sameSceneCheckpoint=early&&destination==world&&frame.room!=alignedTransaction;
        if(destination==world&&!sameSceneCheckpoint||retired.Contains(destination)){candidate=null;return false;}
        if(candidate!=destination){candidate=destination;candidateSince=now;if(!early)return false;}
        if(transition||now<retry||!early&&now-candidateSince<1f)return false;
        // Returning with the guest's old profile preserves stale mission unlocks.
        // Use the existing verified profile import for each new HUB epoch.
        string pathForRequest=PathFor("live-import.json");
        if(File.Exists(pathForRequest)||File.Exists(PathFor("session-load.json")))return false;
        string generation=Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(SessionHost.DirectoryPath);
        string temporary=pathForRequest+"."+generation+".new";
        try
        {
            bool reload=!menu&&frame.mission!=-379511569&&(!frame.entering||sameSceneCheckpoint)&&world!=null&&localMission!=0&&frame.mission==localMission;
            File.WriteAllText(temporary,JsonSerializer.Serialize(new{schema=1,generation,operation=menu?"return-menu":"join-room",room=address,world=frame.world,returnToMenu=!reload,reloadMission=reload?localMission:0}));
            File.Move(temporary,pathForRequest);
            pending=generation;pendingWorld=destination;
            if(sameSceneCheckpoint)alignedTransaction=frame.room;
            report(menu?"Host returned to the main menu. Leaving the previous task...":reload?"Host reloaded this mission. Receiving checkpoint without leaving the task...":"Host entered a new scene. Receiving its checkpoint...");return true;
        }
        catch(IOException){retry=now+1;return false;}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
