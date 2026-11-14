using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;

// Only JSON/HTTP runs off-thread. Native pause and readiness run on Unity's thread.
internal sealed class SessionGuestBarrier
{
    private const int JoinPause=1<<29;
    private static readonly HttpClient Client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(6)};
    private readonly HttpClient client;
    internal SessionGuestBarrier(HttpClient client=null){this.client=client??Client;}
    internal SessionGuestBarrier Restart()
    {Cancel();return new SessionGuestBarrier(client);}
    private Task<string> pending;
    private bool held,done,frameReceived;
    private float next,deadline,unresponsiveSince=-1f,lastWarning=-10f;
    private string generation,room,hash,mission;
    private static SessionGuestBarrier frameWait;
    internal static bool AwaitingFrame=>frameWait!=null;
    internal static void ReceivedFrame()
    {var current=frameWait;if(current!=null){current.Release();current.frameReceived=true;frameWait=null;}}
    internal static bool TickFrameWait()
    {
        if(frameWait!=null&&Time.realtimeSinceStartup>frameWait.deadline)
        {frameWait.Cancel();MoveProbePlugin.ProbeLog.LogWarning("Session initial frame timed out. Rejoin the room.");return true;}
        return false;
    }
    internal void PreHold(){if(!held){MiTime.setPause(true,JoinPause);held=true;}}
    internal bool Tick(JsonElement request,string address,Action<string> report)
    {
        if(done)return true;
        if(generation==null)
        {
            generation=request.GetProperty("generation").GetString();room=address;
            hash=request.GetProperty("bundleSha256").GetString();mission=request.GetProperty("mission").GetString();
            deadline=Time.realtimeSinceStartup+600f;
            MoveProbePlugin.ProbeLog.LogInfo("Session guest barrier: begin "+generation);
        }
        if(Time.realtimeSinceStartup>deadline)throw new TimeoutException("Host checkpoint handoff timed out");
        if(unresponsiveSince>=0&&Time.realtimeSinceStartup-unresponsiveSince>30f)
            throw new TimeoutException("Host handoff is not responding. Rejoin the room.");
        // The successful load callback precedes the game's 'continue' dialog.
        // Do not tell the host to resume until the player has dismissed it.
        if(!held)
        {
            bool preparation=request.TryGetProperty("preparation",out var preparationFlag)&&preparationFlag.GetBoolean();
            // Preparation is called only after the matching native loader is
            // at Continue. Its pause/timeline is expected, not a reason to
            // defer the receipt until after the very gate it must release.
            if(!preparation&&(Time.timeScale<=0f||MiTime.pausedAny||MiCutsceneHandler.bCutsceneRunning))return false;
            MiTime.setPause(true,JoinPause);held=true;
            report("Checkpoint loaded. Waiting for host to resume...");
        }
        if(pending!=null)
        {
            if(!pending.IsCompleted)return false;
            var task=pending;pending=null;next=Time.realtimeSinceStartup+0.5f;
            if(task.IsFaulted||task.IsCanceled)
            {
                var error=task.Exception?.GetBaseException();
                if(error is InvalidOperationException||error is FormatException)throw error;
                if(unresponsiveSince<0)unresponsiveSince=Time.realtimeSinceStartup;
                if(Time.realtimeSinceStartup-lastWarning>=5f)
                {lastWarning=Time.realtimeSinceStartup;MoveProbePlugin.ProbeLog.LogWarning("Session handoff retry: "+(error?.GetType().Name??"Timeout"));}
                return false;
            }
            unresponsiveSince=-1f;
            using var reply=JsonDocument.Parse(task.Result);
            if(reply.RootElement.GetProperty("generation").GetString()!=generation)throw new InvalidOperationException("Host checkpoint changed");
            string phase=reply.RootElement.GetProperty("phase").GetString();
            if(phase=="failed")throw new InvalidOperationException("Host cancelled checkpoint handoff");
            if(phase=="connect"||phase=="playing")
            {if(request.TryGetProperty("preparation",out var prep)&&prep.GetBoolean()){Release();frameReceived=true;done=true;return true;}frameWait=this;deadline=Time.realtimeSinceStartup+30f;done=true;MoveProbePlugin.ProbeLog.LogInfo("Session guest barrier: awaiting first frame "+generation);return true;}
        }
        if(Time.realtimeSinceStartup>=next)
            pending=Send(room,"ready",new{generation,sha256=hash,mission});
        return false;
    }
    internal void Cancel()
    {
        Release();
        if(frameWait==this)frameWait=null;
        if(generation!=null&&!frameReceived)
        {
            var task=Send(room,"cancel",new{generation});
            _=task.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
        }
        done=true;
    }
    private void Release(){if(held){MiTime.setPause(false,JoinPause);held=false;}}
    private async Task<string> Send(string address,string action,object value)
    {
        using var content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json");
        using var request=new HttpRequestMessage(HttpMethod.Post,address+"/session/"+action){Content=content};
        request.Headers.Add("X-Coop-Session","1");
        CoopPeerIdentity.Stamp(request);
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        int status=(int)response.StatusCode;
        if(status>=400&&status<500&&status!=408&&status!=429)
            throw new InvalidOperationException("Checkpoint handoff rejected (HTTP "+status+"). Rejoin the room.");
        response.EnsureSuccessStatusCode();
        long? size=response.Content.Headers.ContentLength;
        if(!size.HasValue||size.Value<=0||size.Value>8192)throw new FormatException("Invalid session reply length");
        using var stream=await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var bytes=new byte[8193];int used=0,n;
        using var timeout=new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
        while(used<bytes.Length&&(n=await stream.ReadAsync(bytes.AsMemory(used),timeout.Token).ConfigureAwait(false))>0)used+=n;
        if(used!=size.Value)throw new FormatException("Truncated session reply");
        return Encoding.UTF8.GetString(bytes,0,used);
    }
}
