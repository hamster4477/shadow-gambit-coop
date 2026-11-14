using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

internal sealed class LobbyGuest
{
    private Uri room;
    private CancellationTokenSource cancel;
    private Task<string> read,command;
    private LobbyProtocol.Frame frame;
    private float next,lastGood;
    private string message;
    private Vector2 scroll;
    internal LobbyProtocol.Frame Current=>Time.realtimeSinceStartup-lastGood<=3?frame:null;
    internal bool SelectPrepared(string actor,bool selected)
    {
        var f=Current;if(room==null||command!=null||f?.phase!="crew"||!f.ready)return false;
        var request=new LobbyProtocol.Request{request=Guid.NewGuid().ToString("N"),room=f.room,revision=f.revision,character=actor,selected=selected};
        if(LobbyProtocol.Allowed(f,request)==null)return false;
        command=GuestMove.SelectLobby(room,JsonSerializer.Serialize(request),cancel.Token);message="等待房主确认船员…";return true;
    }
    internal bool SelectShip(string actor)
    {
        var f=Current;if(room==null||command!=null||!LobbyProtocol.ShipSelection(f)||!f.ready)return false;
        var request=new LobbyProtocol.Request{request=Guid.NewGuid().ToString("N"),room=f.room,revision=f.revision,character=actor,selected=true};
        if(LobbyProtocol.Allowed(f,request)==null)return false;
        command=GuestMove.SelectLobby(room,JsonSerializer.Serialize(request),cancel.Token);message="等待房主确认船员…";return true;
    }
    internal void Close()
    {
        cancel?.Cancel();cancel?.Dispose();cancel=null;room=null;frame=null;next=0;
        foreach(var task in new[]{read,command})if(task!=null)_=task.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously);
        read=command=null;message=null;
    }
    internal void Tick(Uri target)
    {
        if(target==null){Close();return;}
        if(target!=room){Close();room=target;cancel=new CancellationTokenSource();}
        float now=Time.realtimeSinceStartup;
        if(command?.IsCompleted==true)
        {
            try{var reply=JsonSerializer.Deserialize<LobbyProtocol.Reply>(command.GetAwaiter().GetResult());message=reply?.accepted==true?"已提交，等待房主选区更新":"选区已变化，请重新选择";MoveProbePlugin.ProbeLog.LogInfo("Guest lobby selection reply accepted="+(reply?.accepted==true)+" reason="+reply?.reason);}
            catch{message="请求未确认，正在刷新房主选区";}
            command=null;next=0;
        }
        if(read?.IsCompleted==true)
        {
            try
            {
                var incoming=LobbyProtocol.Read(read.GetAwaiter().GetResult());
                if(incoming!=null&&(frame==null||incoming.room!=frame.room||incoming.revision>=frame.revision)){
                    if(frame==null||incoming.room!=frame.room||incoming.revision!=frame.revision)
                        MoveProbePlugin.ProbeLog.LogInfo($"Guest lobby phase={incoming.phase} mission={incoming.mission} revision={incoming.revision} crew={incoming.characters.Length} ready={incoming.ready}");
                    frame=incoming;lastGood=now;
                }
            }
            catch{if(now-lastGood>3)frame=null;}
            read=null;
        }
        if(now-lastGood>3)frame=null;
        if(read==null&&now>=next){next=now+.3f;read=GuestMove.Lobby(room,cancel.Token);}
    }
    internal string ConnectionHint(bool gameplayReady=false)
    {
        if(room==null)return null;
        var visible=Current;
        if(visible==null)return "正在连接房主…\nConnecting to host…";
        if(gameplayReady||visible.phase=="map"||visible.phase=="crew"||LobbyProtocol.ShipSelection(visible))return null;
        return visible.phase=="menu"?"已连接，等待房主选择任务\nConnected. Waiting for the host to choose a mission.":
            "正在加入房主场景，请勿手动读档…\nJoining the host's scene. Please do not load a local save…";
    }
}
