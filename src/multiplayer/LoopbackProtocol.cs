using System;
using System.Collections.Generic;

// Ticket/replay state is independent of sockets and Unity.
internal sealed class LoopbackProtocol
{
    private string target, control, targetReply;
    internal string ControlTicket => control;
    private readonly Dictionary<string, string> replies = new Dictionary<string, string>();
    private readonly Queue<string> recent = new Queue<string>();
    private readonly Dictionary<string,double> reserved=new Dictionary<string,double>();
    private readonly Queue<string> reservations=new Queue<string>();
    private readonly Func<double> now;
    internal LoopbackProtocol(Func<double> clock=null)
    {now=clock??(()=>System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency);}
    private string Reserve()
    {
        double at=now();
        while(reservations.Count>0 && (reservations.Count>=64 || !reserved.TryGetValue(reservations.Peek(),out double until) || until<=at))
            reserved.Remove(reservations.Dequeue());
        string token=Guid.NewGuid().ToString("N");reserved.Add(token,at+15);reservations.Enqueue(token);return token;
    }
    private bool Available(string token)=>token==control || reserved.TryGetValue(token,out double until)&&until>now();
    internal void Arm()
    {
        target = Guid.NewGuid().ToString("N");
        control = Guid.NewGuid().ToString("N");
        targetReply = null;
        replies.Clear(); recent.Clear();reserved.Clear();reservations.Clear();
    }
    private string Once(string request, Func<bool> action)
    {
        // Claim before invoking the game, including partial failures.
        reserved.Remove(request.Split(' ')[1]);
        control = Guid.NewGuid().ToString("N");
        replies[request] = "REJECTED";
        recent.Enqueue(request);
        if (recent.Count > 64) replies.Remove(recent.Dequeue());
        if (action()) replies[request] = "ACCEPTED";
        return replies[request];
    }
    internal string Handle(string request, Func<bool> execute, Func<string, bool> step, Func<string, bool> point, Func<string> state, Action<string,int,char> seen = null)
    {
        if (request == null || request.Length > SyncProtocol.ControlMessageMaxBytes || target == null) return "NOT_READY";
        foreach (char c in request) if (c > 127) return "REJECTED";
        if (request.StartsWith("ORDER ",StringComparison.Ordinal))
        {
            // Share the replay key with POINT; retries cannot execute the same order twice.
            string result = Handle("POINT " + request.Substring(6),execute,step,point,state);
            return result == "ACCEPTED" ? "ACCEPTED " + control : result;
        }
        if (request == "STATE") return state();
        if (request.StartsWith("STATE ",StringComparison.Ordinal))
        {
            var f = request.Split(' ');
            if (f.Length != 4 || !SyncProtocol.Session(f[1]) || !int.TryParse(f[2],System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,out int seq) || seq <= 0 || f[3].Length != 1 ||
                "RHIP".IndexOf(f[3][0]) < 0) return "REJECTED";
            seen?.Invoke(f[1],seq,f[3][0]);
            return state();
        }
        // Explicit fetches reserve independent one-use tokens for concurrent
        // actors. Snapshots still offer the rotating low-latency token.
        if (request == "CONTROL") return "CONTROL " + Reserve();
        if (replies.TryGetValue(request, out string reply)) return reply;
        if (request.StartsWith("POINT ",StringComparison.Ordinal) && request.Length>39 && request[38]==' ' && Available(request.Substring(6,32)))
        {
            string payload = request.Substring(39);
            return Once(request, () => SyncProtocol.Command(payload, out _) && point(payload));
        }
        if (request.StartsWith("STEP ",StringComparison.Ordinal) && request.Length>38 && request[37]==' ' && Available(request.Substring(5,32)))
        {
            string direction = request.Substring(38);
            if (direction == "up" || direction == "down" || direction == "left" || direction == "right")
                return Once(request, () => step(direction));
        }
        if (request == "TARGET") return targetReply == null ? "TARGET " + target : "DONE";
        if (request == "MOVE " + target)
        {
            if (targetReply == null)
            {
                targetReply = "REJECTED";
                if (execute()) targetReply = "ACCEPTED";
            }
            return targetReply;
        }
        return "NOT_READY";
    }
}
