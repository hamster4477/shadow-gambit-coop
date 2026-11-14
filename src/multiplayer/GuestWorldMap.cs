using System;
using HarmonyLib;
using UnityEngine;

// Show the original world map for the host's current preparation selection.
// Only the host starts missions; the guest crew choices use the lobby channel.
internal sealed class GuestWorldMap
{
    private static bool hooks,blockingPlay;
    internal static bool NativeUiOwnsCrew=>blockingPlay&&UIWorldMap.bOpenStatic;
    [ThreadStatic] internal static bool AutomaticDeparture;
    private UIWorldMap owned;
    private int selected;
    private bool hasSelection;
    private float nextOpen,nextSelect,nextError;
    private bool opening,closePending;
    private string lastTrace;
    internal static void Install()
    {
        try{hooks=true;}
        catch(Exception ex){hooks=false;MoveProbePlugin.ProbeLog.LogWarning("Guest map hooks unavailable: "+ex);}
    }
    [HarmonyPatch(typeof(UIWorldMapMissionDetailsWindow),"playButtonPressed")]
    internal static class Play{internal static bool Prefix()=>!TrainingSession.MapBlocked&&(!blockingPlay||AutomaticDeparture);}
    internal void Close(bool releaseGuestRole=true)
    {
        if(releaseGuestRole)blockingPlay=false;
        closePending=true;
        FinishClose();
    }
    private void FinishClose()
    {
        if(!closePending)return;
        var window=owned;
        // Scene unload owns disposal. Do not call into a retained UI wrapper
        // from the old scene or close it while the native fade callback runs.
        if(window==null||window!=UIWorldMap.s_worldMapInstance||SaveProcessManager.bLoading)
        {owned=null;opening=closePending=hasSelection=false;return;}
        if(opening&&!window.m_bFadedIn)return;
        if(UIWorldMap.bOpenStatic&&!window.m_bFadedIn)return;
        closePending=false;owned=null;opening=hasSelection=false;nextOpen=nextSelect=0;
        try{if(UIWorldMap.bOpenStatic){NativeUiEvidence.Capture("map-close");window.onBackButtonPressed();}}
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Guest map close: "+ex.GetType().Name);}
    }
    internal void Tick(bool guestActive,bool sameWorld,LobbyProtocol.Frame frame,Action<string> report)
    {
        try
        {
            // Role ownership survives missing lobby frames and preparation
            // changes. Closing a mirrored map must not enable local departure.
            blockingPlay=hooks&&guestActive;
            bool follow=blockingPlay&&sameWorld&&frame!=null&&frame.phase=="map";
            string trace=$"guest={guestActive} sameWorld={sameWorld} phase={frame?.phase} mission={frame?.mission} localOpen={UIWorldMap.bOpenStatic} follow={follow}";
            if(trace!=lastTrace){lastTrace=trace;MoveProbePlugin.ProbeLog.LogInfo("MapFollow "+trace);}
            if(!guestActive){Close();return;}
            // A missing lobby heartbeat is not a host request to close the UI.
            if(frame==null){FinishClose();return;}
            // A new host world is published before Init_* is available. Keep
            // the already-open ship map for GuestMissionPreparation to consume;
            // closing it here forced a second open/fade before native departure.
            bool departing=!sameWorld&&owned!=null&&owned==UIWorldMap.s_worldMapInstance&&
                frame.mission!=-379511569&&(frame.phase=="loading"||frame.phase=="map"||
                frame.phase=="paused"&&frame.mission!=0);
            if(!follow&&departing){closePending=false;return;}
            if(!follow){Close(false);return;}
            closePending=false;
            if(SaveProcessManager.bLoading||SaveProcessManager.bProcessing||MiCutsceneHandler.bCutsceneRunning)return;
            var window=UIWorldMap.s_worldMapInstance;
            if(window==null)return; // The native UI scene has not loaded yet.
            if(owned!=null&&owned!=window){owned=null;opening=hasSelection=false;}
            float now=Time.realtimeSinceStartup;
            if(opening)
            {
                if(!window.m_bFadedIn)return;
                opening=false;
                MoveProbePlugin.ProbeLog.LogInfo("MapFollow native fade completed");
            }
            if(!UIWorldMap.bOpenStatic)
            {
                if(now<nextOpen)return;
                nextOpen=now+2;owned=window;hasSelection=false;opening=true;
                MoveProbePlugin.ProbeLog.LogInfo("MapFollow opening native shared map");try{NativeUiEvidence.Capture("map-open");window.openOnGlobalStack();}
                catch{opening=false;throw;}
                return;
            }
            // A guest may have opened the map themselves. Track this shared
            // view so the host closing its map also closes the matching view.
            owned=window;
            if(!window.m_bFadedIn)return;
            // Opening the table precedes choosing a mission. The lobby still
            // carries the current HUB id then; it is not a selectable map task.
            // Never pass that id (or the menu sentinel) into the native lookup.
            if(frame.mission==0||frame.mission==-379511569)
            {
                // Apply the host's transition once. Afterwards the guest may
                // browse independently until the host chooses another task.
                if(hasSelection){window.m_locationDetailWindow?.onBackButtonPressed();hasSelection=false;selected=0;MoveProbePlugin.ProbeLog.LogInfo("MapFollow returned to overview");}
                return;
            }
            if((!hasSelection||selected!=frame.mission)&&now>=nextSelect)
            {
                nextSelect=now+1;window.selectMissionExternal(frame.mission);
                MoveProbePlugin.ProbeLog.LogInfo("MapFollow selected mission="+frame.mission);
                selected=frame.mission;hasSelection=true;
            }
        }
        catch(Exception ex)
        {
            if(Time.realtimeSinceStartup>=nextError)
            {nextError=Time.realtimeSinceStartup+5;MoveProbePlugin.ProbeLog.LogWarning("Guest world map: "+ex);report("Waiting for the game's world map to become ready...");}
        }
    }
}
