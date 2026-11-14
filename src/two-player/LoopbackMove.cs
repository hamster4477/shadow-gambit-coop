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
    internal string ControlTicket => protocol.ControlTicket;

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
    }

    internal void Poll(Func<bool> execute, Func<string, bool> step, Func<string, bool> point, Func<string> state, Action<string,int,char> seen = null)
    {
        // Service a small burst before advancing host attack logic. A queued
        // state read must not cost a retreat one additional render frame.
        for (int i = 0; i < 4; i++)
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
        if (!IPAddress.IsLoopback(peer.Address) || bytes.Length > SyncProtocol.ControlMessageMaxBytes) return;
        foreach (byte b in bytes) if (b > 127) return;
        string reply = protocol.Handle(Encoding.ASCII.GetString(bytes), execute, step, point, state,seen);
        if (reply == null || reply.Length > 2048 && !SnapshotTransport.State(reply)) reply = "INVALID_STATE";
        var response = Encoding.ASCII.GetBytes(reply);
        socket.Send(response, response.Length, peer);
    }

    public void Dispose() => socket.Dispose();
}
