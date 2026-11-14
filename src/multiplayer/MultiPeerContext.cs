using System;
using System.Collections.Generic;

// Only the authenticated localhost gateway assigns this scope. Never Unity state
// on worker threads: native commands and sampling are dispatched on Update.
internal static class MultiPeerContext
{
    internal static bool Enabled=>true;
    [ThreadStatic] private static string current;
    internal static string Current=>current??"guest";
    internal static bool Valid(string id)
    {if(id==null||id.Length!=32)return false;foreach(char c in id)if(c<'0'||c>'9'&&c<'a'||c>'f')return false;return true;}
    internal static IDisposable Scope(string peer)=>new Context(peer);
    private sealed class Context:IDisposable
    {
        private readonly string previous;
        internal Context(string peer){previous=current;current=peer;}
        public void Dispose(){current=previous;}
    }
}

// Each guest has its own receipt window; a fast guest cannot acknowledge a
// slow guest's frame or clear another guest's pause.
internal sealed class MultiPeerPace
{
    private sealed class Entry {internal readonly PeerPace Pace=new();internal float Seen;}
    private readonly Dictionary<string,Entry> peers=new(StringComparer.Ordinal);
    internal bool Crew{get;private set;}
    internal bool World{get;private set;}
    internal bool PeerPaused{get;private set;}
    private Entry Current(float now)
    {if(!peers.TryGetValue(MultiPeerContext.Current,out var e))peers.Add(MultiPeerContext.Current,e=new Entry());e.Seen=now;return e;}
    internal void Publish(int seq,float now,bool hold)=>Current(now).Pace.Publish(seq,now,hold);
    internal void Observe(int seq,char phase,float now)=>Current(now).Pace.Observe(seq,phase,now);
    internal void Tick(float now,bool running)
    {
        Crew=World=PeerPaused=false;
        foreach(var id in new List<string>(peers.Keys))
        {
            var e=peers[id];if(now-e.Seen>30){peers.Remove(id);continue;}
            e.Pace.Tick(now,running);Crew|=e.Pace.Crew;World|=e.Pace.World;PeerPaused|=e.Pace.PeerPaused;
        }
    }
    internal void Reset(){peers.Clear();Crew=World=PeerPaused=false;}
}
