using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

// State samples can be skipped; player orders must never use this socket.
// Keep the originating read number and time so an old UDP packet cannot
// cross a newer order's acknowledgment barrier or grant contact freshness.
internal sealed class LanStateFeed : IDisposable
{
    internal struct Sample { internal int Read; internal float Started; internal string Body; }
    private readonly Socket socket;
    private readonly string token, nonce = CoopPeerIdentity.Id;
    private readonly byte[] buffer = new byte[SnapshotTransport.MaxDatagramBytes+1];
    private readonly SnapshotAssembler assembler=new SnapshotAssembler();
    private readonly (int id,float time)[] sent = new (int,float)[64];
    private string cookie;
    private int delivered;
    private float nextHello, nextFrame, lastReply = float.NegativeInfinity, probeStarted;
    internal bool Live(float now) => cookie != null && now-lastReply < 0.4f;

    internal static bool Supports(Uri room)
    {
        if(room==null||room.Scheme!="http"||room.Port!=38477||
            !IPAddress.TryParse(room.Host,out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork)return false;
        var b=ip.GetAddressBytes();
        // Match lan_remote.PRIVATE_NETWORKS, including the explicitly supported
        // overlay ranges. This is transport eligibility, not a claim that
        // 25/8 or26/8 is globally private. The gateway cookie authenticates it.
        return b[0]==10||b[0]==172&&b[1]>=16&&b[1]<=31||b[0]==192&&b[1]==168||
            b[0]==100&&b[1]>=64&&b[1]<=127||b[0]==25||b[0]==26;
    }

    internal static string TunnelEndpoint(Uri room,string path)
    {
        try
        {
            if(room==null||!System.IO.File.Exists(path)||new System.IO.FileInfo(path).Length>1024)return null;
            var lines=System.IO.File.ReadAllLines(path);
            return lines.Length==2&&lines[0]==room.GetLeftPart(UriPartial.Authority)?lines[1]:null;
        }
        catch(System.IO.IOException){return null;}
        catch(UnauthorizedAccessException){return null;}
    }
    internal static LanStateFeed Open(Uri room,bool loopbackFixture=false,string tunnelEndpoint=null)
    {
        if(room!=null&&(room.Scheme=="http"||room.Scheme=="https")&&tunnelEndpoint!=null)
        {
            if(!Uri.TryCreate("udp://"+tunnelEndpoint,UriKind.Absolute,out var endpoint)||endpoint.Port<1||endpoint.Port>65535||
                endpoint.UserInfo.Length!=0||endpoint.AbsolutePath!="/"||endpoint.Query.Length!=0||endpoint.Fragment.Length!=0||
                !IPAddress.TryParse(endpoint.Host,out var address)||address.AddressFamily!=AddressFamily.InterNetwork)return null;
            try{return new LanStateFeed(room,address,endpoint.Port);}catch(SocketException){return null;}
        }
        if (room == null || room.Scheme!="http" || !IPAddress.TryParse(room.Host,out var ip) ||
            ip.AddressFamily!=AddressFamily.InterNetwork) return null;
        if (!(Supports(room) || loopbackFixture && IPAddress.IsLoopback(ip))) return null;
        try { return new LanStateFeed(room,ip); }
        catch (SocketException) { return null; }
    }
    private LanStateFeed(Uri room,IPAddress ip,int port=0)
    {
        token=room.AbsolutePath.Substring(1);
        socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
        try { socket.Connect(new IPEndPoint(ip,port==0?room.Port:port)); socket.Blocking=false; }
        catch { socket.Dispose(); throw; }
    }
    private void BackOff(float now)
    {
        assembler.Clear(); cookie=null; lastReply=float.NegativeInfinity; nextHello=now+3f;
    }
    internal void Send(float now,string receipt,ref int nextRead)
    {
        try
        {
            if (cookie==null)
            {
                if (now<nextHello) return;
                nextHello=now+3f;
                socket.Send(Encoding.ASCII.GetBytes("HELLO "+token+" "+nonce));
                return;
            }
            if (now-Math.Max(probeStarted,lastReply)>1.1f) { BackOff(now); return; }
            if (now<nextFrame) return;
            nextFrame=now+0.05f;
            int id=++nextRead;
            sent[id%sent.Length]=(id,now);
            string header="FRAME2 "+token+" "+nonce+" "+cookie+" "+id.ToString(CultureInfo.InvariantCulture);
            socket.Send(Encoding.ASCII.GetBytes(receipt==null ? header : header+" "+receipt));
        }
        catch (SocketException) { BackOff(now); }
    }
    internal Sample Drain(float now)
    {
        Sample newest=default;
        try
        {
            for(int i=0;i<48 && socket.Poll(0,SelectMode.SelectRead);i++)
            {
                int length=socket.Receive(buffer);
                if(length==0 || length>=buffer.Length) continue;
                bool ascii=true;for(int n=0;n<length;n++)if(buffer[n]>127){ascii=false;break;}
                if(!ascii)continue;
                string text=Encoding.ASCII.GetString(buffer,0,length);
                string[] f=text.Split(new[]{' '},4);
                if (f.Length==3 && f[0]=="COOKIE" && f[1]==nonce && SyncProtocol.Session(f[2]))
                { cookie=f[2]; probeStarted=now; continue; }
                if(cookie==null || !assembler.Accept(text,nonce,delivered,now,
                    id=>sent[id%sent.Length].id==id ? (float?)sent[id%sent.Length].time : null,
                    out int read,out float started,out string body))continue;
                delivered=read;lastReply=now;
                newest=new Sample{Read=read,Started=started,Body=body};
            }
        }
        catch(SocketException) { BackOff(now); }
        return newest;
    }
    public void Dispose() => socket.Dispose();
}

