using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Web.Script.Serialization;

// Public traffic is SGCTCP1 binary framing. Existing game APIs use a private
// loopback adapter, so save verification and command authorization stay intact.
public sealed class NativeTcpBridge : IDisposable
{
    const int MaxBody=100*1024*1024;
    static readonly byte[] Magic=Encoding.ASCII.GetBytes("SGCTCP1\n");
    readonly string host;readonly int port;readonly TcpListener listener;
    readonly Semaphore slots=new Semaphore(32,32);
    sealed class Channel {public TcpClient Client; public DateTime Used;}
    readonly Queue<Channel> pool=new Queue<Channel>();
    volatile bool stopped;
    TcpClient Connect()
    {
        lock(pool){while(pool.Count>0){var c=pool.Dequeue();if((DateTime.UtcNow-c.Used).TotalSeconds<5)return c.Client;c.Client.Close();}}
        var tcp=new TcpClient();tcp.NoDelay=true;tcp.ReceiveTimeout=20000;tcp.SendTimeout=20000;
        try{
            var connect=tcp.BeginConnect(host,port,null,null);
            try{if(!connect.AsyncWaitHandle.WaitOne(6000))throw new IOException("Native TCP connection timed out: "+host+":"+port);tcp.EndConnect(connect);}
            finally{connect.AsyncWaitHandle.Close();}
            return tcp;
        }catch{tcp.Close();throw;}
    }
    public string RoomUrl {get;private set;}
    public string RemoteRoom {get;private set;}
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=16384};}
    static byte[] Read(Stream stream,int size){var data=new byte[size];int at=0;while(at<size){int n=stream.Read(data,at,size-at);if(n==0)throw new EndOfStreamException();at+=n;}return data;}
    static void WriteFrame(Stream stream,Dictionary<string,object> head,byte[] body)
    {
        head["bytes"]=body.Length;byte[] header=Encoding.UTF8.GetBytes(Json().Serialize(head));
        if(header.Length>16384)throw new IOException("TCP header too large");
        // NoDelay is intentional for commands. Coalesce their framing instead
        // of sending magic, length, JSON and body as separate tiny writes.
        // Large saves retain a separate body write to avoid another large copy.
        bool inline=body.Length<=16384;
        byte[] packet=new byte[12+header.Length+(inline?body.Length:0)];
        Buffer.BlockCopy(Magic,0,packet,0,8);
        byte[] size=BitConverter.GetBytes(IPAddress.HostToNetworkOrder(header.Length));
        Buffer.BlockCopy(size,0,packet,8,4);Buffer.BlockCopy(header,0,packet,12,header.Length);
        if(inline)Buffer.BlockCopy(body,0,packet,12+header.Length,body.Length);
        stream.Write(packet,0,packet.Length);
        if(!inline)stream.Write(body,0,body.Length);
    }
    byte[] Exchange(Dictionary<string,object> head,byte[] body,out Dictionary<string,object> response)
    {
        var tcp=Connect();bool success=false;
        try {
            var stream=tcp.GetStream();
            {
                WriteFrame(stream,head,body);
                if(Encoding.ASCII.GetString(Read(stream,8))!="SGCTCP1\n")throw new IOException("Endpoint is not an SGCTCP1 gateway. Map TCP to 127.0.0.1:38479, with automatic HTTPS disabled.");
                int size=IPAddress.NetworkToHostOrder(BitConverter.ToInt32(Read(stream,4),0));if(size<1||size>16384)throw new IOException("Invalid TCP response header");
                response=Json().DeserializeObject(Encoding.UTF8.GetString(Read(stream,size))) as Dictionary<string,object>;
                if(response==null||!response.ContainsKey("bytes"))throw new IOException("Invalid TCP response");
                int length=Convert.ToInt32(response["bytes"]);if(length<0||length>MaxBody)throw new IOException("Invalid TCP payload size");
                byte[] result=Read(stream,length);if(response.ContainsKey("transportError"))throw new IOException("Native TCP backend connection failed");success=true;return result;
            }
        }
        finally{lock(pool){if(success&&!stopped&&pool.Count<16)pool.Enqueue(new Channel{Client=tcp,Used=DateTime.UtcNow});else tcp.Close();}}
    }
    public NativeTcpBridge(string endpoint)
    {
        Uri uri;if(!Uri.TryCreate(endpoint,UriKind.Absolute,out uri)||uri.Scheme!="tcp"||uri.Port<1||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!=""||uri.AbsolutePath!="/")throw new ArgumentException("Native TCP address must be tcp://hostname:port");
        host=uri.Host;port=uri.Port;
        Dictionary<string,object> hello;Exchange(new Dictionary<string,object>{{"op","hello"}},new byte[0],out hello);
        if(Convert.ToInt32(hello["status"])!=200||!hello.ContainsKey("protocol")||Convert.ToInt32(hello["protocol"])!=1)throw new IOException("Native TCP handshake rejected");
        RemoteRoom=Convert.ToString(hello["room"]);
        if(!System.Text.RegularExpressions.Regex.IsMatch(RemoteRoom,@"\A/[A-Za-z0-9_-]{24}\z"))throw new IOException("Invalid native room identity");
        listener=new TcpListener(IPAddress.Loopback,0);listener.Start(32);
        RoomUrl="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+RemoteRoom;
        new Thread(Accept){IsBackground=true,Name="Coop native TCP bridge"}.Start();
    }
    void Accept()
    {
        while(!stopped){try{var client=listener.AcceptTcpClient();if(!slots.WaitOne(0)){client.Close();continue;}ThreadPool.QueueUserWorkItem(_=>Handle(client));}catch(SocketException){if(stopped)return;}catch(ObjectDisposedException){return;}}
    }
    static string Line(Stream stream)
    {
        var data=new MemoryStream();int ch;
        while((ch=stream.ReadByte())!=-1){if(ch==10)return Encoding.ASCII.GetString(data.ToArray()).TrimEnd('\r');if(data.Length>=8192)throw new IOException("Local header too large");data.WriteByte((byte)ch);}
        throw new EndOfStreamException();
    }
    void Handle(TcpClient client)
    {
        try{using(client){client.NoDelay=true;client.ReceiveTimeout=20000;client.SendTimeout=20000;using(var stream=client.GetStream()){
            try{
                string[] line=Line(stream).Split(' ');if(line.Length!=3||(line[0]!="GET"&&line[0]!="POST")||!line[1].StartsWith(RemoteRoom+"/",StringComparison.Ordinal))throw new IOException("Invalid local request");
                var headers=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);int total=0;
                while(true){string row=Line(stream);total+=row.Length;if(total>16384)throw new IOException("Local headers too large");if(row.Length==0)break;int colon=row.IndexOf(':');if(colon<1)throw new IOException("Invalid header");string key=row.Substring(0,colon);if(headers.ContainsKey(key))throw new IOException("Duplicate header");headers[key]=row.Substring(colon+1).Trim();}
                if(headers.ContainsKey("Origin")||headers.ContainsKey("Transfer-Encoding"))throw new IOException("Unsupported local request");
                int length=headers.ContainsKey("Content-Length")?int.Parse((string)headers["Content-Length"]):0;if(length<0||length>65536)throw new IOException("Local payload too large");
                Dictionary<string,object> response;
                byte[] body=Exchange(new Dictionary<string,object>{{"op","request"},{"method",line[0]},{"path",line[1]},{"headers",headers}},Read(stream,length),out response);
                int status=Convert.ToInt32(response["status"]);if(status<100||status>599)throw new IOException("Invalid status");
                string type=response.ContainsKey("type")?Convert.ToString(response["type"]):"application/octet-stream";if(type.Contains("\r")||type.Contains("\n"))throw new IOException("Invalid content type");
                byte[] prefix=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+" Result\r\nContent-Length: "+body.Length+"\r\nContent-Type: "+type+"\r\nConnection: close\r\n\r\n");stream.Write(prefix,0,prefix.Length);stream.Write(body,0,body.Length);
            }catch(Exception){
                // A transport failure must remain a transport failure. Returning
                // an invented HTTP status would bypass existing retry/cancel logic.
                client.Close();
            }
        }}}
        catch(IOException){}catch(SocketException){}finally{slots.Release();}
    }
    public void Dispose(){stopped=true;listener.Stop();lock(pool){while(pool.Count>0)pool.Dequeue().Client.Close();}}
}
