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
    private static readonly Dictionary<string,ShipCrewLease> leases=new();
    internal static MiCharacter Host{get;private set;}
    internal static MiCharacter Guest{get;private set;}
    internal static bool Active=>scene!=null&&Host!=null;
    internal static void Close()
    {
        foreach(var lease in leases.Values)lease.Release();leases.Clear();
        scene=null;Host=Guest=null;initialized=false;guestChoice=null;
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
            var leader=GuestCrewSelection.Source(hub.leader);
            if(leader!=Host&&leader!=Guest)
            {
                if(ControlledCrew.NativeProxy(leader,Host))leader=Host;
                else if(ControlledCrew.NativeProxy(leader,Guest))leader=Guest;
            }
            // Native ship clicks must not steal the peer's confirmed actor.
            if(leader!=null&&leader==Guest&&Host!=null&&Host!=Guest)
                hub.switchToCharacter(Host,false);
            else if(leader!=null)Host=leader;
            if(Host==null)return;
            if(!initialized)
            {
                initialized=true;
                foreach(var d in hub.m_liSpawnedCharacterData)
                    if(d!=null&&d.bUnlocked&&d.m_character!=null&&d.m_character!=Host)
                    {guestChoice=GuestMove.Identity(d.m_character);break;}
            }
            var chosen=Find(guestChoice);Guest=chosen?.m_character;
            if(Guest==Host){Guest=null;guestChoice=null;}
        }
        else
        {
            Host=Find(frame.shipHost)?.m_character;
            Guest=Find(frame.shipGuest)?.m_character;
            if(Host==null)return;
        }
        var wantedIds=new HashSet<string>();
        if(!hosting)wantedIds.Add(GuestMove.Identity(Host));
        if(Guest!=null)wantedIds.Add(GuestMove.Identity(Guest));
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
        if(selected)guestChoice=id;
        else if(Guest==actor)guestChoice=null;
        else return false;
        return true;
    }
    internal static void Describe(LobbyProtocol.Frame frame)
    {
        if(!Active||!hostMode)return;
        frame.shipHost=GuestMove.Identity(Host);frame.shipGuest=Guest==null?null:GuestMove.Identity(Guest);
        if(frame.phase!="playing"&&frame.phase!="paused")return;
        frame.limit=2;frame.ready=!SaveProcessManager.bProcessing&&!MiCutsceneHandler.bCutsceneRunning;
        var rows=new List<LobbyProtocol.Character>();
        foreach(var d in scene.characters.m_liSpawnedCharacterData)
        {
            if(d==null||!d.bUnlocked||d.m_character==null)continue;
            var c=d.m_character;bool h=c==Host,g=c==Guest;
            var label=c.uiData?.lstrName.strText??c.name;
            if(label.Length>128)label=label.Substring(0,128);
            rows.Add(new LobbyProtocol.Character{id=GuestMove.Identity(c),label=label,selected=h||g,slot=h?0:g?1:-1,owner=h?"H":g?"G":"U",unavailable=h});
        }
        frame.characters=rows.ToArray();
    }
}
