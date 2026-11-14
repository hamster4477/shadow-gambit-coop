using System;
using System.Collections.Generic;

internal sealed class GuestCommandQueue
{
    private sealed class Entry { internal string Actor, Text; internal float Time, ReadyAt; internal int WalkTag; internal long Revision; }
    private readonly List<Entry> entries = new List<Entry>();
    private string session;
    internal int Count => entries.Count;
    internal bool Contains(string actor)=>entries.Exists(e=>e.Actor==actor);
    internal void Clear() { entries.Clear(); session = null; }
    private static bool Point(string text) => text.Length > 0 && (char.IsDigit(text[0]) || text[0] == '-' ||
        (text.Length > 3 && text[2] == ' ' && (text[0] == 'G' || text[0] == 'R') && (text[1] == 'W' || text[1] == 'C')));
    private int Expire(float now)
    {
        int removed = 0;
        for (int i = entries.Count-1; i >= 0; i--)
            if (now - entries[i].Time > 2f || now < entries[i].Time) { entries.RemoveAt(i); removed++; }
        return removed;
    }
    internal bool Add(string newSession, string command, float now, int walkTag = 0, float delay = 0f, bool supersedeAttack = false)
        => Add(newSession,"",command,now,walkTag,delay,supersedeAttack);
    internal bool Add(string newSession,string actor,string command,float now,int walkTag=0,float delay=0f,bool supersedeAttack=false,long revision=0)
    {
        if (revision<0 || actor==null || string.IsNullOrEmpty(command) || !float.IsFinite(now) || !float.IsFinite(delay) || delay<0f || delay>2f) return false;
        if (session != newSession) Clear();
        session = newSession;
        Expire(now);
        if(command=="CROUCH" || command=="WALK")
            entries.RemoveAll(entry=>entry.Actor==actor && (Point(entry.Text) || entry.Text=="CROUCH" || entry.Text=="WALK" || entry.Text.StartsWith("HIT ",StringComparison.Ordinal)));
        if (Point(command))
            entries.RemoveAll(entry => entry.Actor==actor && (entry.Text.StartsWith("BODY ",StringComparison.Ordinal) ||
                supersedeAttack && entry.Text.StartsWith("HIT ", StringComparison.Ordinal)));
        int last=entries.FindLastIndex(e=>e.Actor==actor);
        var entry = new Entry { Actor=actor, Revision=revision, Text = command, Time = now, WalkTag = walkTag, ReadyAt = now + delay };
        if (last >= 0 && Point(command) && Point(entries[last].Text)) {
            entries[last] = entry; return true;
        }
        // A stance or skill following a click must not wait behind a double-click timer.
        if (last >= 0) entries[last].ReadyAt = now;
        if (entries.Count >= 512 || entries.FindAll(e=>e.Actor==actor).Count >= 8) return false;
        entries.Add(entry); return true;
    }
    internal string Take(string currentSession, float now, out int expired) => Take(currentSession, now, out expired, out _);
    internal void Release(string actor)=>entries.RemoveAll(e=>e.Actor==actor);
    internal string Take(string currentSession, float now, out int expired, out int walkTag)
        =>Take(currentSession,now,out _,out expired,out walkTag);
    internal string Take(string currentSession,float now,out string actor,out int expired,out int walkTag)
        =>Take(currentSession,now,out actor,out expired,out walkTag,out _);
    internal string Take(string currentSession,float now,out string actor,out int expired,out int walkTag,out long revision)
        =>Take(currentSession,now,out actor,out expired,out walkTag,out revision,null);
    internal string Take(string currentSession,float now,out string actor,out int expired,out int walkTag,out long revision,Func<string,bool> available)
    {
        revision=0;actor=null;expired = 0;
        walkTag = 0;
        if (session != currentSession || !float.IsFinite(now)) { Clear(); return null; }
        expired = Expire(now);
        // Preserve order within each actor, but one double-click timer must
        // not hold another crew member's ready attack/withdrawal behind it.
        var visited=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<entries.Count;i++)
        {
            var entry=entries[i];
            if(!visited.Add(entry.Actor)||now<entry.ReadyAt||available!=null&&!available(entry.Actor))continue;
            actor=entry.Actor;walkTag=entry.WalkTag;revision=entry.Revision;entries.RemoveAt(i);return entry.Text;
        }
        return null;
    }
}
