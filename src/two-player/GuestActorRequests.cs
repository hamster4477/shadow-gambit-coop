using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// Unity-thread scheduler. A network delay for one actor must not serialize the
// whole selected group. Completion is consumed only on the Unity thread.
internal sealed class GuestActorRequests
{
    internal sealed class Entry
    {
        internal string Actor,Command,Session;
        internal long Sequence;
        internal int Epoch;
        internal float SentAt;
        internal Task<string> Task;
    }
    private readonly Dictionary<string,Entry> flights=new Dictionary<string,Entry>(StringComparer.Ordinal);
    internal int Count=>flights.Count;
    internal bool Available(string actor)=>actor!=null&&flights.Count<64&&!flights.ContainsKey(actor);
    internal bool Start(string actor,Func<Entry> begin)
    {
        if(!Available(actor))return false;
        var entry=begin();
        if(entry==null||entry.Task==null||entry.Actor!=actor)throw new InvalidOperationException("Invalid actor request");
        flights.Add(actor,entry);return true;
    }
    internal List<Entry> Completed()
    {
        var ready=new List<Entry>();
        foreach(var entry in flights.Values)if(entry.Task.IsCompleted)ready.Add(entry);
        foreach(var entry in ready)flights.Remove(entry.Actor);
        return ready;
    }
    internal void Clear()
    {
        foreach(var entry in flights.Values)
            _=entry.Task.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously);
        flights.Clear();
    }
}
