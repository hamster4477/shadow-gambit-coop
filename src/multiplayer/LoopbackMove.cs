using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

// The host offers one reachable target. Re-recording it invalidates old requests.
internal sealed class LoopbackMove : IDisposable
{
    internal const int Port = 38476;
    private readonly UdpClient socket;
    private readonly LoopbackProtocol protocol = new LoopbackProtocol();
    internal int BoundPort => ((IPEndPoint)socket.Client.LocalEndPoint).Port;
    private readonly System.Collections.Generic.Dictionary<string,LoopbackProtocol> protocols=new(StringComparer.Ordinal);
    private readonly System.Collections.Generic.Dictionary<string,DateTime> activity=new(StringComparer.Ordinal);
    private LoopbackProtocol Active
    {
        get
        {
            string id=MultiPeerContext.Current;
            if(id=="guest")return protocol;
            if(!protocols.TryGetValue(id,out var p)){p=new LoopbackProtocol();p.Arm();protocols.Add(id,p);}
            activity[id]=DateTime.UtcNow;return p;
        }
    }
    internal string ControlTicket => Active.ControlTicket;
    internal string[] ExpiredPeers()
    {
        var result=new System.Collections.Generic.List<string>();
        foreach(var p in activity)if(DateTime.UtcNow-p.Value>TimeSpan.FromSeconds(30))result.Add(p.Key);
        foreach(string id in result){activity.Remove(id);protocols.Remove(id);}return result.ToArray();
    }

    internal LoopbackMove(int port = Port)
    {
        socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.ExclusiveAddressUse = true;
            socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
        }
        catch { socket.Dispose(); throw; }
    }

    internal void Arm()
    {
        protocol.Arm();
        foreach(var p in protocols.Values)p.Arm();
    }

    internal void Poll(Func<bool> execute, Func<string, bool> step, Func<string, bool> point, Func<string> state, Action<string,int,char> seen = null)
    {
        // Service a small burst before advancing host attack logic. A queued
        // state read must not cost a retreat one additional render frame.
        var budget=System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 64 && budget.ElapsedMilliseconds<4; i++)
        {
            try
            {
                if(!socket.Client.Poll(0, SelectMode.SelectRead)) break;
                Reply(execute, step, point, state,seen);
            }
            // Windows reports ICMP from a timed-out request socket on the
            // next receive. This says nothing about our bound host listener.
            catch(SocketException ex) when(ex.SocketErrorCode==SocketError.ConnectionReset ||
                ex.SocketErrorCode==SocketError.ConnectionRefused) { }
        }
    }

    private void Reply(Func<bool> execute, Func<string, bool> step, Func<string, bool> point, Func<string> state,Action<string,int,char> seen)
    {
        var peer = new IPEndPoint(IPAddress.Any, 0);
        var bytes = socket.Receive(ref peer);
        if (!IPAddress.IsLoopback(peer.Address) || bytes.Length > SyncProtocol.ControlMessageMaxBytes+38) return;
        foreach (byte b in bytes) if (b > 127) return;
        string message=Encoding.ASCII.GetString(bytes),id="guest";
        if(message.StartsWith("PEER ",StringComparison.Ordinal))
        {
            if(message.Length<39||message[37]!=' '||!MultiPeerContext.Valid(message.Substring(5,32)))return;
            id=message.Substring(5,32);message=message.Substring(38);
        }
        using var context=MultiPeerContext.Scope(id);
        string reply = Active.Handle(message, execute, step, point, state,seen);
        if (reply == null || reply.Length > 2048 && !SnapshotTransport.State(reply)) reply = "INVALID_STATE";
        var response = Encoding.ASCII.GetBytes(reply);
        socket.Send(response, response.Length, peer);
    }

    public void Dispose() => socket.Dispose();
}
