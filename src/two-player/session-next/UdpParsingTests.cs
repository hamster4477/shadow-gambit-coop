// Compiled beside the real LanStateFeed and SnapshotAssembler. Only the socket
// is substituted: production Send/Drain and all frame validation execute.
public static class UdpParsingTests
{
    private static int checks;
    private static void Check(bool value,string why){if(!value)throw new Exception(why);checks++;}
    private static string Cookie=new string('a',32);
    private static LanStateFeed Open(out UdpParserSocket socket,out string nonce)
    {
        var feed=LanStateFeed.Open(new Uri("http://127.0.0.1:38477/fixture-room"),true);
        socket=UdpParserSocket.Last;int read=0;feed.Send(0,null,ref read);
        nonce=Encoding.ASCII.GetString(socket.LastSend).Split(' ')[2];
        return feed;
    }
    private static byte[][] Parts(string nonce,int id,string body)
    {
        string digest=Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(body))).ToLowerInvariant();
        int count=(body.Length+1023)/1024;var parts=new byte[count][];
        for(int n=0;n<count;n++)parts[n]=Encoding.ASCII.GetBytes($"PART {nonce} {id} {n} {count} {body.Length} {digest} "+body.Substring(n*1024,Math.Min(1024,body.Length-n*1024)));
        return parts;
    }
    private static void Authenticate(LanStateFeed feed,UdpParserSocket socket,string nonce)
    {socket.Queue("COOKIE "+nonce+" "+Cookie);feed.Drain(0);}
    public static int Run()
    {
        checks=0;
        using(var feed=Open(out var socket,out var nonce))
        {
            int next=0;
            foreach(string text in new[]{"COOKIE wrong "+Cookie,"COOKIE "+nonce+" bad","COOKIE "+nonce+" "+Cookie+" extra","COOKIE", "COOKIEX "+nonce+" "+Cookie})socket.Queue(text);
            socket.Input.Enqueue(new byte[]{255,1,2});socket.Input.Enqueue(new byte[1281]);
            socket.Queue("FRAME "+nonce+" 1 S19 unsolicited");
            Check(feed.Drain(0).Body==null,"invalid cookie and pre-auth frames do not grant state");
            feed.Send(.1f,null,ref next);Check(next==0,"invalid cookie cannot start state requests");
            Authenticate(feed,socket,nonce);feed.Send(.1f,null,ref next);Check(next==1,"valid cookie starts a read");
            string body="S19 "+new string('x',32764);var parts=Parts(nonce,1,body);
            for(int i=parts.Length-1;i>0;i--)socket.Input.Enqueue(parts[i]);
            socket.Input.Enqueue(parts[1]);socket.Input.Enqueue(parts[0]);
            var result=feed.Drain(.2f);
            Check(result.Read==1&&result.Started==.1f&&result.Body==body,"reversed maximum snapshot plus duplicate preserves original read and body");
            Check(feed.Live(.2f),"verified frame activates UDP freshness");
            socket.Input.Enqueue(parts[0]);Check(feed.Drain(.21f).Body==null,"already delivered read cannot replay");
            feed.Send(.22f,null,ref next);
            socket.Queue("COOKIE "+nonce+" bad");socket.Queue("FRAME "+nonce+" 2 S18 legacy");
            result=feed.Drain(.23f);Check(result.Read==2&&result.Body=="S18 legacy","malformed cookie does not erase an authenticated session");
            feed.Send(.3f,null,ref next);var corrupt=Parts(nonce,3,"S19 "+new string('z',2200));
            corrupt[corrupt.Length-1][corrupt[corrupt.Length-1].Length-1]=(byte)'q';
            foreach(var packet in corrupt)socket.Input.Enqueue(packet);
            Check(feed.Drain(.31f).Body==null,"digest mismatch cannot publish state");
            feed.Send(.4f,null,ref next);
            foreach(var packet in Parts(nonce,4,"S19 stale"))socket.Input.Enqueue(packet);
            Check(feed.Drain(1.21f).Body==null,"expired read remains rejected");
            socket.Queue("FRAME wrong 4 S19 wrong nonce");Check(feed.Drain(1.22f).Body==null,"nonce validation preserved");
        }
        using(var feed=Open(out var socket,out var nonce))
        {
            Authenticate(feed,socket,nonce);
            int before=socket.Received;
            for(int i=0;i<64;i++)socket.Queue("malformed");
            feed.Drain(.1f);Check(socket.Received-before==48&&socket.Input.Count==16,"per-frame receive budget remains 48 datagrams");
            feed.Drain(.2f);Check(socket.Input.Count==0,"next frame can drain remaining datagrams");
        }
        return checks;
    }
    public static UdpBenchResult Benchmark(int size,int iterations)
    {
        using var feed=Open(out var socket,out var nonce);Authenticate(feed,socket,nonce);
        string body="S19 "+new string('x',size-4);int read=0;
        long bytes=0,ticks=0;
        // Warm up crypto, regex, string parsing and JIT before counting. Packet
        // creation and Send are outside the timed/allocation-measured section.
        for(int i=0;i<iterations+200;i++)
        {
            float now=.1f+i*.0625f;feed.Send(now,null,ref read);
            foreach(var packet in Parts(nonce,read,body))socket.Input.Enqueue(packet);
            long allocated=GC.GetAllocatedBytesForCurrentThread(),started=System.Diagnostics.Stopwatch.GetTimestamp();
            var result=feed.Drain(now+.005f);
            long elapsed=System.Diagnostics.Stopwatch.GetTimestamp()-started;
            long added=GC.GetAllocatedBytesForCurrentThread()-allocated;
            if(result.Read!=read||result.Body!=body)throw new Exception("Benchmark failed complete snapshot validation");
            if(i>=200){bytes+=added;ticks+=elapsed;}
        }
        return new UdpBenchResult{SnapshotBytes=size,Iterations=iterations,AllocatedBytesPerSnapshot=(double)bytes/iterations,
            MicrosecondsPerSnapshot=ticks*1000000d/System.Diagnostics.Stopwatch.Frequency/iterations};
    }
}
