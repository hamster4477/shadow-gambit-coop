using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using HarmonyLib;

// Tutorials run locally. The room remains reachable solely for presence;
// world/ownership/story replication is suspended until both players return.
internal static class TrainingSession
{
    private static bool local,peer,requested,finished,visited,announced,connected,host,held;
    private static float next;
    private static Task<string> update;
    private static bool sent;
    private static Uri lastRoom;
    private static string message;
    internal static bool Suspended=>connected&&(local||peer||held);
    internal static bool MapBlocked=>connected&&(Suspended||requested);
    private static string FilePath(string name)=>Path.Combine(SessionHost.DirectoryPath,name);
    internal static void BeginAutomatic(string entry)
    {
        if(!connected||local)return;
        local=true;requested=false;finished=false;visited=false;announced=false;next=0;
        message="正在自动进入独立训练；回船后恢复联机。";
        MoveProbePlugin.ProbeLog.LogInfo("Training independent entry="+entry+" host="+host);
        // Mark isolation synchronously, before the native door cutscene can
        // journal events. Native cleanup remains in Update, outside this hook.
    }
    internal static void ObserveDestination(MissionData data)
    {if(data?.bIsCharacterTutorialMission==true)BeginAutomatic("tutorial-destination:"+data.guid.iValue);}
    [HarmonyPatch(typeof(Mimimi.HUB.HUBTransitionLogic),"onCharacterTutorialInteracted")]
    internal static class Enter
    {private static void Prefix()=>BeginAutomatic("monkey-confirm");}
    [HarmonyPatch(typeof(MiMissionHandler),"loadFollowupMission")]
    internal static class Follow
    {private static void Prefix(MissionData __0)=>ObserveDestination(__0);}
    [HarmonyPatch(typeof(MiMissionHandler),"loadFollowMissionExternal")]
    internal static class External
    {private static void Prefix(MissionData __0)=>ObserveDestination(__0);}
    [HarmonyPatch(typeof(MiSceneLoaderUtility),"coroLoadMission")]
    internal static class Load
    {private static void Prefix(MissionData __0)=>ObserveDestination(__0);}
    internal static void Leave()
    {
        try
        {
            if(host)File.WriteAllText(FilePath("training-host.json"),"{\"schema\":1,\"active\":false}");
            else if(lastRoom!=null)
            {
                // Serialize the final departure after an in-flight active=true.
                var destination=lastRoom;var prior=update;
                _=Task.Run(async()=>{try{if(prior!=null)await prior;}catch{}try{await GuestMove.SelectLobby(destination,"{\"operation\":\"training\",\"active\":false}",CancellationToken.None);}catch{}});
            }
        }catch(IOException){}
        local=peer=requested=finished=visited=announced=connected=held=false;next=0;lastRoom=null;
        if(update!=null)_=update.ContinueWith(t=>{_=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
        update=null;
    }
    internal static void Draw()
    {
        if(!connected)return;
        bool ship=SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue==ShipCrewSession.Mission;

        if(ship&&local&&announced&&!visited&&GUI.Button(new Rect(12,110,420,32),"取消训练并重连 / Cancel training and reconnect"))finished=true;
        if(MapBlocked)HostLoadingView.Draw("训练期间暂不能选关，双方回船后自动重连。\nTraining in progress. Return to the ship to reconnect before choosing a mission.");
    }
    internal static bool Tick(bool hosting,Uri room,Action<string> report,Action suspend,Action resume)
    {
        host=hosting;connected=hosting||room!=null;if(room!=null)lastRoom=room;
        if(!connected)return false;
        float now=Time.realtimeSinceStartup;
        var handler=SceneLookup.Find<MiMissionHandler>();
        bool training=handler?.currentMissionData?.bIsCharacterTutorialMission==true;
        bool ship=handler?.currentMissionData?.guid.iValue==ShipCrewSession.Mission;
        bool stable=ship&&!SaveProcessManager.bLoading&&!SaveProcessManager.bProcessing&&
            SceneLookup.Find<Mimimi.HUB.HUBLogic>()?.characters?.m_bRoutineSpawningProcessEnded==true;
        if(requested&&stable&&!UIWorldMap.bOpenStatic&&!MiCutsceneHandler.bCutsceneRunning)
        {requested=false;local=true;visited=false;announced=false;next=0;message="正在通知另一端暂停联机，请稍候…";}
        if(training&&!local)BeginAutomatic("loaded-tutorial-fallback");
        if(local&&training)visited=true;
        if(local&&((visited&&stable)||finished))
        {local=false;finished=false;announced=false;next=0;message="已结束训练，等待另一端回船并恢复联机…";}
        if(update?.IsCompleted==true)
        {
            try
            {
                using var reply=JsonDocument.Parse(update.GetAwaiter().GetResult());
                var root=reply.RootElement;
                if(root.GetProperty("accepted").GetBoolean())
                {
                    if(sent==local)announced=true;
                    var value=root.GetProperty("host");
                    if(value.ValueKind==JsonValueKind.True||value.ValueKind==JsonValueKind.False)peer=value.GetBoolean();
                    else if(held)peer=true;
                }
            }
            catch(Exception){if(held)peer=true;message="训练状态通信暂未确认，保持单人训练与选关锁定，正在重试…";}
            update=null;
        }
        if(now>=next)
        {
            next=now+1;
            if(hosting)
            {
                try
                {
                    Directory.CreateDirectory(SessionHost.DirectoryPath);
                    string path=FilePath("training-host.json"),temp=path+".new";
                    File.WriteAllText(temp,JsonSerializer.Serialize(new{schema=1,active=local}));File.Move(temp,path,true);announced=true;
                    string guest=FilePath("training-guest.json");
                    if(File.Exists(guest))
                    {
                        using var value=JsonDocument.Parse(File.ReadAllText(guest));
                        peer=value.RootElement.GetProperty("active").GetBoolean();
                    }
                }
                catch(Exception){if(held)peer=true;}
            }
            else if(update==null&&room!=null)
            {sent=local;update=GuestMove.SelectLobby(room,JsonSerializer.Serialize(new{operation="training",active=sent}),CancellationToken.None);}
        }
        bool waiting=local||peer||held&&!announced;
        if(waiting)
        {
            if(!held){held=true;suspend();}
            if(local&&announced)message="独立训练中：本地操作与剧情不再同步，回船后自动重连。";
            else if(peer)message="另一位玩家正在单人训练；你也可以进入教程。双方回船前不能选关。";
            return true;
        }
        if(held)
        {
            held=false;resume();report("单人训练结束，正在恢复房间与船上进度…");
        }
        return false;
    }
}
