using System;
using System.Collections.Generic;
using Mimimi.HUB;
using UnityEngine;
using HarmonyLib;

// Ship controllers are created by HUB logic, not the deployed mission roster.
// Leases change controller lifetime; the existing room channel owns requests.
internal static class ShipCrewSession
{
    internal const int Mission=-379511569;
    private static HUBLogic scene;
    private static bool initialized,hostMode;
    private static bool applyingSelection;
    internal static Func<string,bool> RequestSelection;
    internal static void Install()=>new Harmony("local.shadowgambit.shipselection").PatchAll(typeof(Select));
    [HarmonyPatch(typeof(HUBCharactersLogic),"switchToCharacter")]
    internal static class Select
    {
        internal static bool Prefix(MiCharacter __0)
        {
            if(!Active||hostMode||applyingSelection)return true;
            if(ControlledCrew.NativeProxy(__0,Guest))
            {
                // A possessed skeleton has no HUBCharacterData. Select its
                // existing native controller without switching ship routines.
                if(__0.bControllerPlayer())MoveProbe.SelectAlone(__0);
                return false;
            }
            if(__0==Guest)return true;
            if(__0!=null&&__0!=Host)RequestSelection?.Invoke(GuestMove.Identity(__0));
            return false;
        }
    }
    private static string guestChoice;
    private static readonly Dictionary<string,string> remoteChoices=new(StringComparer.Ordinal);
    internal static IReadOnlyDictionary<string,MiCharacter> RemoteAssignments
    {get{var result=new Dictionary<string,MiCharacter>(StringComparer.Ordinal);foreach(var p in remoteChoices){var a=Find(p.Value)?.m_character;if(a!=null&&a!=Host)result[p.Key]=a;}return result;}}
    internal static IEnumerable<MiCharacter> RemoteActors=>RemoteAssignments.Values;
    internal static bool IsRemote(MiCharacter actor){if(actor==null)return false;foreach(var a in RemoteActors)if(a==actor||ControlledCrew.NativeProxy(actor,a))return true;return false;}

    private static readonly Dictionary<string,ShipCrewLease> leases=new();
    internal static MiCharacter Host{get;private set;}
    internal static MiCharacter Guest{get;private set;}
    internal static bool Active=>scene!=null&&Host!=null;
    internal static void Close()
    {
        foreach(var lease in leases.Values)lease.Release();leases.Clear();
        scene=null;Host=Guest=null;initialized=false;guestChoice=null;remoteChoices.Clear();
    }
    private static HUBCharactersLogic.HUBCharacterData Find(string id)
    {
        if(scene?.characters?.m_liSpawnedCharacterData==null)return null;
        foreach(var d in scene.characters.m_liSpawnedCharacterData)
            if(d!=null&&d.bUnlocked&&d.m_character!=null&&GuestMove.Identity(d.m_character)==id)return d;
        return null;
    }
    internal static void Tick(bool hosting,bool peer,LobbyProtocol.Frame frame,bool sameWorld)
    {
        var previousGuest=Guest;
        bool wanted=hosting?peer:sameWorld&&frame?.shipHost!=null;
        if(!wanted||SaveProcessManager.bLoading){if(scene!=null||leases.Count>0)Close();return;}
        var mission=SceneLookup.Find<MiMissionHandler>()?.currentMissionData;
        if(mission!=null&&mission.guid.iValue!=Mission){if(scene!=null||leases.Count>0)Close();return;}
        var current=SceneLookup.Find<HUBLogic>();
        if(current==null){if(scene!=null||leases.Count>0)Close();return;}
        if(scene!=null&&(scene!=current||hostMode!=hosting))Close();
        scene=current;hostMode=hosting;
        var hub=scene.characters;
        if(hub==null||!hub.m_bRoutineSpawningProcessEnded||SaveProcessManager.bProcessing||MiCutsceneHandler.bCutsceneRunning||UIWorldMap.bOpenStatic)return;
        if(hosting)
        {
            var active=new HashSet<string>(MultiplayerPeers.Active(),StringComparer.Ordinal);
            foreach(string peerId in new List<string>(remoteChoices.Keys))if(!active.Contains(peerId))remoteChoices.Remove(peerId);
            var leader=GuestCrewSelection.Source(hub.leader);
            if(ControlledCrew.NativeProxy(leader,Host))leader=Host;
            if(IsRemote(leader)&&Host!=null)hub.switchToCharacter(Host,false);
            else if(leader!=null)Host=leader;
            if(Host==null)return;
            // Remote clients start as spectators until their own native click
            // claims an available crew member. No reserved second slot.
            Guest=null;
            foreach(var pair in RemoteAssignments){Guest=pair.Value;break;}
        }
        else
        {
            Host=Find(frame.shipHost)?.m_character;
            Guest=Find(frame.shipGuest)?.m_character;
            if(Host==null)return;
        }
        var wantedIds=new HashSet<string>();
        if(!hosting)wantedIds.Add(GuestMove.Identity(Host));
        if(hosting)foreach(var actor in RemoteActors)wantedIds.Add(GuestMove.Identity(actor));
        else
        {
            if(Guest!=null)wantedIds.Add(GuestMove.Identity(Guest));
            if(frame?.characters!=null)foreach(var row in frame.characters)if(row.selected&&Find(row.id)?.m_character!=null)wantedIds.Add(row.id);
        }
        // Acquire before releasing: a failed acquisition never drops another lease.
        foreach(var id in wantedIds)
        {if(!leases.TryGetValue(id,out var lease)){lease=new ShipCrewLease(scene,Find(id));leases.Add(id,lease);}lease.Maintain();}
        foreach(var id in new List<string>(leases.Keys))if(!wantedIds.Contains(id)){leases[id].Release();leases.Remove(id);}
        if(!hosting&&Guest!=null&&Guest!=previousGuest)
        {
            applyingSelection=true;
            try{hub.switchToCharacter(Guest,false);}
            finally{applyingSelection=false;}
            // Switching the local leader schedules AI takeover for the host
            // replica; preserve that lease without rewriting native selection.
            foreach(var lease in leases.Values)lease.Maintain();
        }
    }
    internal static bool Choose(string id,bool selected)
    {
        if(!Active||!hostMode||MiCutsceneHandler.bCutsceneRunning||SaveProcessManager.bProcessing)return false;
        var actor=Find(id)?.m_character;
        if(actor==null||actor==Host)return false;
        string peer=MultiPeerContext.Current;
        foreach(var pair in remoteChoices)if(pair.Key!=peer&&pair.Value==id)return false;
        if(selected)remoteChoices[peer]=id;
        else if(remoteChoices.TryGetValue(peer,out var chosen)&&chosen==id)remoteChoices.Remove(peer);
        else return false;
        return true;
    }
    internal static void Describe(LobbyProtocol.Frame frame)
    {
        if(!Active||!hostMode)return;
        frame.shipHost=GuestMove.Identity(Host);frame.shipGuest=Guest==null?null:GuestMove.Identity(Guest);
        if(frame.phase!="playing"&&frame.phase!="paused")return;
        frame.limit=Math.Max(2,1+remoteChoices.Count);frame.shipOwners=new Dictionary<string,string>(StringComparer.Ordinal);frame.ready=!SaveProcessManager.bProcessing&&!MiCutsceneHandler.bCutsceneRunning;
        var rows=new List<LobbyProtocol.Character>();
        foreach(var d in scene.characters.m_liSpawnedCharacterData)
        {
            if(d==null||!d.bUnlocked||d.m_character==null)continue;
            var c=d.m_character;bool h=c==Host;string id=GuestMove.Identity(c),peer=null;
            foreach(var pair in remoteChoices)if(pair.Value==id){peer=pair.Key;break;}
            bool g=peer!=null;
            if(h)frame.shipOwners[id]="host";else if(g)frame.shipOwners[id]=peer;
            var label=c.uiData?.lstrName.strText??c.name;
            if(label.Length>128)label=label.Substring(0,128);
            rows.Add(new LobbyProtocol.Character{id=GuestMove.Identity(c),label=label,selected=h||g,slot=h?0:g?1+rows.FindAll(r=>r.owner=="G").Count:-1,owner=h?"H":g?"G":"U",unavailable=h});
        }
        frame.characters=rows.ToArray();
    }
}
