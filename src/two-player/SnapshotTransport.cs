using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Shared limits for complete snapshots. LAN pieces fit below the common MTU;
// only an authenticated, complete and verified read is released to gameplay.
internal static class SnapshotTransport
{
    internal const int MaxBytes=32768, FragmentBytes=1024, MaxDatagramBytes=1280;
    internal static bool State(string body) => body!=null &&
        (body.StartsWith("S18 ",StringComparison.Ordinal) && body.Length<=2048 ||
         body.StartsWith("S19 ",StringComparison.Ordinal) && body.Length<=MaxBytes);
}

internal sealed class SnapshotAssembler
{
    private static readonly char[] Separator={' '};
    private sealed class Pending
    {
        internal int Length,Count,Received;
        internal float Started;
        internal string Digest;
        internal string[] Parts;
    }
    private readonly Dictionary<int,Pending> pending=new Dictionary<int,Pending>();
    // At most four reads are pending. Reuse the eviction scratch list rather
    // than allocating one for every received fragment (up to 32 per snapshot).
    private readonly List<int> expired=new List<int>(4);
    internal void Clear(){pending.Clear();expired.Clear();}
    private void Trim(int delivered,float now)
    {
        if(pending.Count==0)return;
        expired.Clear();
        foreach(var p in pending)if(p.Key<=delivered || now-p.Value.Started>0.8f)expired.Add(p.Key);
        foreach(int id in expired)pending.Remove(id);
        expired.Clear();
    }
    internal bool Accept(string packet,string nonce,int delivered,float now,Func<int,float?> sent,
        out int read,out float started,out string body)
    {
        read=0;started=0;body=null;
        Trim(delivered,now);
        if(packet==null || packet.Length>SnapshotTransport.MaxDatagramBytes)return false;
        foreach(char c in packet)if(c>127)return false;
        bool split=packet.StartsWith("PART ",StringComparison.Ordinal);
        var f=packet.Split(Separator,split?8:4);
        if(f.Length!=(split?8:4) || f[0]!=(split?"PART":"FRAME") || f[1]!=nonce ||
            !int.TryParse(f[2],NumberStyles.None,CultureInfo.InvariantCulture,out int id) || id<=delivered)return false;
        var origin=sent(id);
        if(!origin.HasValue || now-origin.Value>0.8f || now<origin.Value)return false;
        if(!split)
        {
            if(!SnapshotTransport.State(f[3]))return false;
            read=id;started=origin.Value;body=f[3];Trim(id,now);return true;
        }
        if(!int.TryParse(f[3],NumberStyles.None,CultureInfo.InvariantCulture,out int index) ||
            !int.TryParse(f[4],NumberStyles.None,CultureInfo.InvariantCulture,out int count) ||
            !int.TryParse(f[5],NumberStyles.None,CultureInfo.InvariantCulture,out int length) ||
            length<=0 || length>SnapshotTransport.MaxBytes || count!=(length+SnapshotTransport.FragmentBytes-1)/SnapshotTransport.FragmentBytes ||
            index<0 || index>=count || !Regex.IsMatch(f[6],"\\A[0-9a-f]{64}\\z") ||
            f[7].Length!=(index==count-1 ? length-index*SnapshotTransport.FragmentBytes : SnapshotTransport.FragmentBytes))return false;
        if(!pending.TryGetValue(id,out var frame))
        {
            if(pending.Count>=4)
            {
                int oldest=int.MaxValue;foreach(int key in pending.Keys)oldest=Math.Min(oldest,key);
                if(id<oldest)return false;
                pending.Remove(oldest);
            }
            frame=new Pending{Length=length,Count=count,Started=origin.Value,Digest=f[6],Parts=new string[count]};
            pending.Add(id,frame);
        }
        if(frame.Length!=length || frame.Count!=count || frame.Digest!=f[6] || frame.Started!=origin.Value)
        {pending.Remove(id);return false;}
        if(frame.Parts[index]!=null)
        {
            if(frame.Parts[index]!=f[7])pending.Remove(id);
            return false;
        }
        frame.Parts[index]=f[7];frame.Received++;
        if(frame.Received!=count)return false;
        pending.Remove(id);
        string complete=string.Concat(frame.Parts);
        if(complete.Length!=length || !SnapshotTransport.State(complete) ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(complete))),frame.Digest,StringComparison.OrdinalIgnoreCase))return false;
        read=id;started=frame.Started;body=complete;Trim(id,now);return true;
    }
}
