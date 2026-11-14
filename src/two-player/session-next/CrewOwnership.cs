using System;
using System.Collections.Generic;
using System.Linq;

// Host-thread state. Actor keys are stable roster identities, never local instance IDs.
// Used by the native host selection adapter. Guest group admission is still being integrated.
internal sealed class CrewOwnership
{
    internal enum RosterScope { Mission, Ship }
    internal const int MaximumMissionCrew=8;
    internal RosterScope Scope {get;private set;}

    private readonly Dictionary<string,string> owners=new Dictionary<string,string>(StringComparer.Ordinal);
    private readonly Dictionary<string,long> sequences=new Dictionary<string,long>(StringComparer.Ordinal);
    private readonly HashSet<string> roster=new HashSet<string>(StringComparer.Ordinal);
    // A skill-created controller borrows its caster's authority. It is not a
    // selectable roster slot and must never acquire an independent owner.
    internal readonly struct ControlledActor
    {
        internal readonly string Actor,Source,Incarnation;
        internal ControlledActor(string actor,string source,string incarnation)
        {Actor=actor;Source=source;Incarnation=incarnation;}
    }
    private Dictionary<string,ControlledActor> controlled=new Dictionary<string,ControlledActor>(StringComparer.Ordinal);
    internal string Generation {get;private set;}
    internal long Revision {get;private set;}
    internal string Owner(string actor)
    {
        if(actor==null)return null;
        if(controlled.TryGetValue(actor,out var proxy))actor=proxy.Source;
        return owners.TryGetValue(actor,out var peer)?peer:null;
    }
    internal bool IsControlled(string actor)=>actor!=null&&controlled.ContainsKey(actor);
    internal string Source(string actor)=>actor!=null&&controlled.TryGetValue(actor,out var proxy)?proxy.Source:null;
    // Called with one complete native discovery sample. Invalid or conflicting
    // samples leave the previous authority untouched. Incarnation is a local
    // binding token, not a wire identity: replacing a native object invalidates
    // all commands admitted under the previous ownership revision.
    internal bool ReplaceControlled(string generation,IEnumerable<ControlledActor> sample)
    {
        if(generation!=Generation||sample==null)return false;
        var next=new Dictionary<string,ControlledActor>(StringComparer.Ordinal);
        foreach(var proxy in sample)
        {
            if(next.Count+roster.Count>=64||string.IsNullOrWhiteSpace(proxy.Actor)||string.IsNullOrWhiteSpace(proxy.Source)||
                string.IsNullOrWhiteSpace(proxy.Incarnation)||roster.Contains(proxy.Actor)||!roster.Contains(proxy.Source)||
                Owner(proxy.Source)==null||next.ContainsKey(proxy.Actor))return false;
            next.Add(proxy.Actor,proxy);
        }
        bool changed=next.Count!=controlled.Count;
        foreach(var pair in next)
            if(!controlled.TryGetValue(pair.Key,out var old)||old.Source!=pair.Value.Source||old.Incarnation!=pair.Value.Incarnation)changed=true;
        if(!changed)return true;
        controlled=next;Revision++;return true;
    }
    internal string[] Owned(string peer)=>owners.Where(p=>p.Value==peer).Select(p=>p.Key).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
    internal void Reset(string generation,IEnumerable<string> actors,string hostActor,string guestActor,RosterScope scope=RosterScope.Mission)
    {
        var next=actors?.ToArray();
        if(string.IsNullOrWhiteSpace(generation)||generation==Generation||next==null||next.Length<1||
            (scope!=RosterScope.Mission&&scope!=RosterScope.Ship)||(scope==RosterScope.Mission&&next.Length>MaximumMissionCrew)||
            next.Any(string.IsNullOrWhiteSpace)||next.Distinct(StringComparer.Ordinal).Count()!=next.Length||
            hostActor==guestActor||!next.Contains(hostActor)||(guestActor==null?next.Length!=1:!next.Contains(guestActor)))throw new ArgumentException("Invalid roster generation");
        roster.Clear();foreach(var a in next)roster.Add(a);
        owners.Clear();sequences.Clear();controlled.Clear();Generation=generation;Revision=1;Scope=scope;
        owners[hostActor]="host";if(guestActor!=null)owners[guestActor]="guest";
    }
    // Ship roster comes from the game, including DLC/hidden crew; only the
    // deployed mission roster is capped at eight. Keys never mean slot numbers.
    // One atomic full selection: overlap or a busy release rejects the whole
    // request. The caller returns the authoritative selection to both peers.
    internal bool Select(string generation,long revision,string peer,IEnumerable<string> selection,Func<string,bool> busy)
    {
        if(generation!=Generation||revision!=Revision||(peer!="host"&&peer!="guest")||selection==null||busy==null)return false;
        var wanted=selection.ToArray();
        if(wanted.Length>roster.Count||wanted.Any(a=>a==null||!roster.Contains(a))||wanted.Distinct(StringComparer.Ordinal).Count()!=wanted.Length)return false;
        foreach(var a in wanted)if(Owner(a)!=null&&Owner(a)!=peer)return false;
        var released=Owned(peer).Except(wanted,StringComparer.Ordinal).ToArray();
        if(released.Any(a=>busy(a)||controlled.Values.Any(proxy=>proxy.Source==a)))return false;
        bool changed=released.Length!=0||wanted.Any(a=>Owner(a)!=peer);
        if(!changed)return true;
        foreach(var a in released)owners.Remove(a);
        foreach(var a in wanted)owners[a]=peer;
        // Keep actor sequence high-water marks across handoffs. A delayed
        // command from a former ownership revision cannot regain authority.
        Revision++;
        return true;
    }
    internal bool Admit(string generation,long revision,string peer,string actor,long sequence)
    {
        if(generation!=Generation||revision!=Revision||actor==null||Owner(actor)!=peer||
            (peer!="host"&&peer!="guest")||sequence<=0)return false;
        string key=peer+":"+actor;
        if(sequences.TryGetValue(key,out long old)&&sequence<=old)return false;
        sequences[key]=sequence;return true;
    }
}
