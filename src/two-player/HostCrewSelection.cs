using System;
using System.Collections.Generic;
using HarmonyLib;

// Selected squads own their crew; replacing a squad releases idle members.
// The native group is filtered before use, and peer claims share this ledger.
internal sealed class HostCrewSelection
{
    private readonly CrewOwnership ownership=new CrewOwnership();
    private readonly Dictionary<string,MiCharacter> actors=new Dictionary<string,MiCharacter>(StringComparer.Ordinal);
    private readonly Dictionary<string,MiCharacter> controlled=new Dictionary<string,MiCharacter>(StringComparer.Ordinal);
    private MiCharacter anchor;
    private readonly HashSet<string> retiredAnchors=new(StringComparer.Ordinal);
    internal long Revision=>ownership.Revision;
    internal string Generation=>ownership.Generation;
    internal int HostCount=>ownership.Owned("host").Length;
    internal IEnumerable<MiCharacter> OriginalActors=>actors.Values;
    internal IEnumerable<MiCharacter> PlanningActors
    {get{foreach(var actor in actors.Values)yield return actor;foreach(var actor in controlled.Values)yield return actor;}}
    internal void Reset(string generation,IEnumerable<MiCharacter> roster,MiCharacter host,MiCharacter guest,IEnumerable<string> preserve=null,
        IReadOnlyDictionary<string,char> assigned=null)
    {
        var ids=new List<string>();actors.Clear();controlled.Clear();
        foreach(var actor in roster)
        {
            string id=GuestMove.Identity(actor);
            if(actor==null||!SyncProtocol.Identity(id)||actors.ContainsKey(id))throw new InvalidOperationException("Invalid ownership roster");
            ids.Add(id);actors.Add(id,actor);
        }
        if(assigned!=null)
        {
            if(preserve!=null||assigned.Count!=ids.Count||!assigned.TryGetValue(GuestMove.Identity(host),out char hostOwner)||hostOwner!='H'||
                host!=guest&&(!assigned.TryGetValue(GuestMove.Identity(guest),out char guestOwner)||guestOwner!='G'))throw new InvalidOperationException("Invalid departure anchors");
            foreach(string id in ids)if(!assigned.TryGetValue(id,out char owner)||(owner!='H'&&owner!='G'&&owner!='U'))throw new InvalidOperationException("Invalid departure ownership");
        }
        ownership.Reset(generation,ids,GuestMove.Identity(host),host==guest?null:GuestMove.Identity(guest),
            ids.Count>8?CrewOwnership.RosterScope.Ship:CrewOwnership.RosterScope.Mission);
        anchor=host;retiredAnchors.Clear();
        if(assigned!=null)
        {
            var hosts=new List<string>();var guests=new List<string>();
            foreach(var entry in assigned){if(entry.Value=='H')hosts.Add(entry.Key);else if(entry.Value=='G')guests.Add(entry.Key);}
            if(!ownership.Select(generation,ownership.Revision,"guest",guests,_=>false)||
                !ownership.Select(generation,ownership.Revision,"host",hosts,_=>false))throw new InvalidOperationException("Departure ownership rejected");
        }
        if(preserve!=null)
        {
            var held=new HashSet<string>(ownership.Owned("host"),StringComparer.Ordinal);
            foreach(string id in preserve)if(actors.ContainsKey(id)&&id!=GuestMove.Identity(guest))held.Add(id);
            ownership.Select(generation,ownership.Revision,"host",held,_=>false);
        }
    }
    internal char OwnerCode(MiCharacter actor)
    {
        string peer=ownership.Owner(GuestMove.Identity(actor));return peer=="host"?'H':peer=="guest"?'G':'U';
    }
    internal bool SetControlled(IEnumerable<CrewOwnership.ControlledActor> sample,IReadOnlyDictionary<string,MiCharacter> native)
    {
        if(sample==null||native==null)return false;
        var all=new List<CrewOwnership.ControlledActor>();var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in sample)
        {
            if(!native.TryGetValue(p.Actor,out var actor)||actor==null||!seen.Add(p.Actor)||
                GuestMove.Identity(actor)!=p.Actor)return false;
            all.Add(p);
        }
        if(seen.Count!=native.Count||!ownership.ReplaceControlled(Generation,all))return false;
        controlled.Clear();foreach(var pair in native)controlled.Add(pair.Key,pair.Value);return true;
    }
    internal bool Controlled(MiCharacter actor)=>actor!=null&&ownership.IsControlled(GuestMove.Identity(actor));
    internal MiCharacter Original(string id)
    {if(controlled.ContainsKey(id))id=ownership.Source(id);return id!=null&&actors.TryGetValue(id,out var actor)?actor:null;}
    private void KeepControlledSources(HashSet<string> keep,string peer)
    {foreach(string id in controlled.Keys)if(ownership.Owner(id)==peer)keep.Add(ownership.Source(id));}
    internal string[] HostOwned()=>ownership.Owned("host");
    internal MiCharacter FollowHostSelection(MiPlayerInput input,Func<MiCharacter,bool> busy)
    {
        var selected=input?.lSelectedCharacter;var leader=input?.leader;
        leader=GuestCrewSelection.Source(leader);
        if(leader!=null&&leader!=anchor&&IsHost(leader)&&!Controlled(leader))
        {retiredAnchors.Add(GuestMove.Identity(anchor));anchor=leader;retiredAnchors.Remove(GuestMove.Identity(anchor));}
        var held=new HashSet<string>(StringComparer.Ordinal);
        if(selected!=null)for(int i=0;i<selected.Count;i++)if(IsHost(selected[i]))
        {string id=GuestMove.Identity(GuestCrewSelection.Source(selected[i]));held.Add(ownership.Source(id)??id);}
        if(anchor!=null)held.Add(GuestMove.Identity(anchor));
        foreach(string id in HostOwned())if(actors.TryGetValue(id,out var running)&&busy(running))held.Add(id);
        foreach(string id in new List<string>(retiredAnchors))
        {
            if(!actors.TryGetValue(id,out var actor)||!IsHost(actor)){retiredAnchors.Remove(id);continue;}
            bool picked=false;if(selected!=null)for(int i=0;i<selected.Count;i++)if(selected[i]==actor)picked=true;
            if(!picked&&!busy(actor)){held.Remove(id);retiredAnchors.Remove(id);}
        }
        KeepControlledSources(held,"host");
        ownership.Select(Generation,Revision,"host",held,id=>actors.TryGetValue(id,out var actor)&&busy(actor));
        return anchor;
    }
    internal Dictionary<string,char> CopyOwners(IEnumerable<MiCharacter> roster)
    {
        var result=new Dictionary<string,char>(StringComparer.Ordinal);
        foreach(var actor in roster)
        {
            if(actor==null)return null;string id=GuestMove.Identity(actor);
            if(!actors.TryGetValue(id,out var known)||known!=actor||result.ContainsKey(id))return null;
            result.Add(id,OwnerCode(actor));
        }
        return result.Count==actors.Count?result:null;
    }
    internal bool IsGuest(MiCharacter actor)=>actor!=null&&ownership.Owner(GuestMove.Identity(GuestCrewSelection.Source(actor)))=="guest";
    internal bool IsHost(MiCharacter actor)=>actor!=null&&ownership.Owner(GuestMove.Identity(GuestCrewSelection.Source(actor)))=="host";
    internal bool CanGuestClaim(MiCharacter actor)=>actor!=null&&actors.TryGetValue(GuestMove.Identity(actor),out var known)&&known==actor&&
        ownership.Owner(GuestMove.Identity(actor))!="host";
    internal bool Admit(string generation,long revision,string actor,long sequence)=>ownership.Admit(generation,revision,"guest",actor,sequence);
    internal bool SelectGuest(string generation,long revision,IEnumerable<string> requested)
    {
        var held=new HashSet<string>(ownership.Owned("guest"),StringComparer.Ordinal);
        foreach(string id in requested)
        {
            if(!actors.TryGetValue(id,out var actor)||actor==null||ownership.Owner(id)=="host")return false;
            held.Add(id);
        }
        return ownership.Select(generation,revision,"guest",held,_=>false);
    }
    internal bool SelectGuestGroup(string generation,long revision,IEnumerable<string> requested)
    {
        var wanted=new HashSet<string>(requested,StringComparer.Ordinal);
        if(wanted.Count==0)return false;
        foreach(string id in new List<string>(wanted))
        {
            if(ownership.Owner(id)=="host")return false;
            if(actors.ContainsKey(id))continue;
            if(!controlled.ContainsKey(id)||ownership.Owner(id)!="guest")return false;
            wanted.Remove(id);wanted.Add(ownership.Source(id));
        }
        KeepControlledSources(wanted,"guest");
        return ownership.Select(generation,revision,"guest",wanted,_=>false);
    }
    internal bool ReleaseGuest(string generation,long revision,IEnumerable<string> requested,MiCharacter primary,Func<MiCharacter,bool> busy)
    {
        var held=new HashSet<string>(ownership.Owned("guest"),StringComparer.Ordinal);
        foreach(string id in requested)
        {
            if(!held.Contains(id)||id==GuestMove.Identity(primary)||!actors.TryGetValue(id,out var actor)||busy(actor))return false;
            held.Remove(id);
        }
        return ownership.Select(generation,revision,"guest",held,id=>busy(actors[id]));
    }
    internal int ReleaseUnselected(MiPlayerInput input,Func<MiCharacter,bool> busy)
    {
        if(input==null||busy==null)return 0;
        var held=ownership.Owned("host");var keep=new HashSet<string>(StringComparer.Ordinal){GuestMove.Identity(anchor)};
        var selected=input.lSelectedCharacter;
        if(selected!=null)for(int i=0;i<selected.Count;i++)if(IsHost(selected[i]))
        {string id=GuestMove.Identity(GuestCrewSelection.Source(selected[i]));keep.Add(ownership.Source(id)??id);}
        foreach(string id in held)if(actors.TryGetValue(id,out var actor)&&busy(actor))keep.Add(id);
        KeepControlledSources(keep,"host");
        if(!ownership.Select(Generation,Revision,"host",keep,id=>actors.TryGetValue(id,out var actor)&&busy(actor)))return 0;
        return held.Length-ownership.Owned("host").Length;
    }
    internal bool Blocked(MiSelectable selectable)
    {
        if(selectable==null)return false;
        foreach(var actor in actors.Values)if(actor!=null&&actor.controllerPlayer?.m_selectable==selectable)return IsGuest(actor);
        foreach(var actor in controlled.Values)if(actor!=null&&actor.controllerPlayer?.m_selectable==selectable)return IsGuest(actor);
        return false;
    }
    internal void Reconcile(MiPlayerInput input)
    {
        if(input==null)return;
        var selected=input.lSelectedCharacter;
        var wanted=new List<MiCharacter>();var held=new HashSet<string>(ownership.Owned("host"),StringComparer.Ordinal);
        if(selected!=null)for(int i=0;i<selected.Count;i++)
        {
            var actor=selected[i];if(actor==null)continue;
            var original=GuestCrewSelection.Source(actor);
            string id=GuestMove.Identity(original);
            if((!actors.TryGetValue(id,out var known)&&!controlled.TryGetValue(id,out known))||known!=original||IsGuest(original))continue;
            if(!wanted.Exists(c=>c==actor))wanted.Add(actor);held.Add(ownership.Source(id)??id);
        }
        // Deselecting the squad does not hand its running orders to the peer.
        if(!ownership.Select(Generation,Revision,"host",held,_=>false))return;
        // Empty native selection is valid; the network anchor is not a UI selection.
        bool repair=selected==null||selected.Count!=wanted.Count;
        if(!repair)for(int i=0;i<wanted.Count;i++)if(selected[i]!=wanted[i]){repair=true;break;}
        // IL2CPP returns fresh managed wrappers for the same native actor.
        // List.Contains uses System.Object.Equals, not Unity's native ==.
        // A false mismatch here rebuilds selection every frame and cancels aim.
        var leader=input.leader;
        if(!repair && wanted.Count>0 && !wanted.Exists(c=>c==leader))repair=true;
        // Re-selecting the same native character can center its camera. Leave
        // all valid native selection/leader events alone, even if ownership
        // did not change; ownership equality is never an input dedup key.
        if(!repair)return;
        var options=new MiSelectable.SelectOptions(false);
        input.m_selectHandler.deselectAll(options);
        foreach(var actor in wanted)input.m_selectHandler.select(actor.controllerPlayer.m_selectable,options);
        if(wanted.Count>0)input.m_selectHandler.setLeader(wanted[0].controllerPlayer.m_selectable,options);
    }
    // These getters have no value-type parameters. Keep native SelectOptions
    // methods unpatched; invoke existing group APIs only when repairing a
    // forbidden selection rather than detouring their struct calling ABI.
    [HarmonyPatch(typeof(MiSelectable),"get_bSelectable")]
    internal static class Selectable
    {
        private static void Postfix(MiSelectable __instance,ref bool __result)
        {if(__result && NativeInput.Probe?.PeerSelectionBlocked(__instance)==true)__result=false;}
    }
    [HarmonyPatch(typeof(MiSelectable),"get_bSelectableByMouseInput")]
    internal static class MouseSelectable
    {
        private static void Postfix(MiSelectable __instance,ref bool __result)
        {if(__result && NativeInput.Probe?.PeerSelectionBlocked(__instance)==true)__result=false;}
    }
    [HarmonyPatch(typeof(MiCharacterControllerPlayer),"onCommand",new[]{typeof(SkillCommand)})]
    internal static class Commands
    {
        private static bool Prefix(MiCharacterControllerPlayer __instance,SkillCommand __0)=>NativeInput.Probe?.HostCommandAllowed(__instance.character,__0)!=false;
    }
    [HarmonyPatch(typeof(MiCharacterControllerPlayer),"onSlowCommand",new[]{typeof(SkillCommand)})]
    internal static class SlowCommands
    {
        private static bool Prefix(MiCharacterControllerPlayer __instance,SkillCommand __0)=>NativeInput.Probe?.HostCommandAllowed(__instance.character,__0)!=false;
    }
}
