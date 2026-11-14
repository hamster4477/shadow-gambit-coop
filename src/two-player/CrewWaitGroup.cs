using System;
using System.Collections.Generic;

// One movement lock and elapsed interval per peer-owned actor. This object
// never owns or releases the shared world clock; save entry still clears all
// native transient bits through CoopWaitLock's registered save filter.
internal sealed class CrewWaitGroup
{
    private sealed class Entry
    {
        internal MiCharacter Actor;
        internal readonly CoopWaitLock Lock=new CoopWaitLock();
        internal bool Suspended;
        internal float Since;
    }
    private readonly Dictionary<IntPtr,Entry> entries=new Dictionary<IntPtr,Entry>();
    internal bool AnyHeld {get{foreach(var e in entries.Values)if(e.Lock.CrewHeld)return true;return false;}}
    internal bool Suspended(MiCharacter actor)=>actor!=null&&entries.TryGetValue(actor.Pointer,out var e)&&e.Suspended;
    private static void Transition(Entry e,bool suspended,float now,Action<MiCharacter,float> resumed)
    {
        if(suspended&&!e.Suspended)e.Since=now;
        if(!suspended&&e.Suspended&&now>=e.Since)resumed?.Invoke(e.Actor,now-e.Since);
        e.Suspended=suspended;
    }
    internal void Update(IEnumerable<MiCharacter> roster,Func<MiCharacter,bool> owned,bool hold,bool world,float now,
        Func<MiCharacter,bool> busy,Action<MiCharacter,float> resumed=null)
    {
        var live=new HashSet<IntPtr>();
        foreach(var actor in roster)
        {
            if(actor==null||!owned(actor)||!live.Add(actor.Pointer))continue;
            if(!entries.TryGetValue(actor.Pointer,out var e))entries.Add(actor.Pointer,e=new Entry{Actor=actor});
            bool crew=hold&&!world&&!busy(actor)&&CoopWaitLock.CanHoldTransient;
            e.Lock.SetCrew(actor,crew);
            // A guest route can intentionally cancel its GoTo while waiting.
            // Its timers remain suspended even though no native GoTo is left.
            Transition(e,world||crew,now,resumed);
        }
        var removed=new List<IntPtr>();
        foreach(var pair in entries)if(!live.Contains(pair.Key))
        {
            pair.Value.Lock.ReleaseCrew();Transition(pair.Value,false,now,resumed);removed.Add(pair.Key);
        }
        foreach(var key in removed)entries.Remove(key);
    }
    internal void Clear()
    {
        foreach(var e in entries.Values)e.Lock.ReleaseCrew();
        entries.Clear();
    }
}
