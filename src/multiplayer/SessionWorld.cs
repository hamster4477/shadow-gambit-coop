using System;
using HarmonyLib;
using UnityEngine;

internal static class SessionWorld
{
    private static readonly SceneEpoch epoch=new SceneEpoch();
    private static long loaded;
    private static bool ready;
    internal static void Reserve(string value)=>epoch.Reserve(value,loaded);
    internal static void CancelReservation()=>epoch.CancelReservation();
    internal static string Current
    {
        get
        {
            if(!ready)return null;
            var handler=SceneLookup.Find<MiMissionHandler>();
            var mission=handler?.currentMissionData;
            return epoch.Observe(mission==null?IntPtr.Zero:handler.Pointer,mission?.guid.iValue??0,loaded,SaveProcessManager.bLoading);
        }
    }
    internal static void Install()
    {
        try
        {
            var hooks=new Harmony("local.shadowgambit.sceneepoch");
            hooks.PatchAll(typeof(Loaded));EnemyRewind.Install();MissionEntryTrace.Install();MissionEntrySync.Install();ready=true;
            hooks.PatchAll(typeof(LoadRequested));hooks.PatchAll(typeof(ProcessLock));hooks.PatchAll(typeof(ProcessUnlock));
        }
        catch(Exception ex){ready=false;MoveProbePlugin.ProbeLog.LogWarning("Scene identity hooks unavailable: "+ex);}
    }
    [HarmonyPatch(typeof(SaveProcessManager),"invokeOnAfterLoadCallbacks")]
    internal static class Loaded
    {
        internal static void Postfix(SaveGameHolder __0,SaveProcessManager.LoadProcessResult __1)
        {
            SceneLookup.Invalidate();
            Trace("load-completed",__0,__1.ToString());
            if(__0!=null&&__1==SaveProcessManager.LoadProcessResult.Success)loaded++;
            SessionHost.NativeLoaded(__0,__1);
        }
    }

    // Read-only diagnostics for script-driven memories as well as player loads.
    // Native IL2CPP callers may not appear in the managed stack; retain the
    // actual save path, mission and process phases rather than guessing origin.
    private static long traceSequence;
    private static readonly bool diagnostics=System.IO.File.Exists(System.IO.Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","trace-skills.flag"));
    private static float nextDiagnostic,heartbeat;
    private static string lastDiagnostic;
    internal static void DiagnosticTick()
    {
        TrainingDeckTrace.Tick();
        if(!diagnostics||Time.realtimeSinceStartup<nextDiagnostic)return;
        nextDiagnostic=Time.realtimeSinceStartup+0.5f;
        try
        {
            var handler=SceneLookup.Find<MiMissionHandler>();
            var crew=SceneLookup.Find<MiCharacterHandler>()?.m_liPlayerCharactersInMission;
            var members=new System.Collections.Generic.List<string>();
            if(crew!=null)for(int i=0;i<crew.Count;i++)
                if(crew[i]!=null)members.Add(crew[i].name+":"+GuestMove.Identity(crew[i]));
            string sample=System.Text.Json.JsonSerializer.Serialize(new {
                mission=handler?.currentMissionData?.guid.iValue,phase=handler?.eCurrentMissionState.ToString(),
                members,loading=SaveProcessManager.bLoading,processing=SaveProcessManager.bProcessing,
                cutscene=MiCutsceneHandler.bCutsceneRunning,timeScale=Time.timeScale});
            if(sample==lastDiagnostic&&Time.realtimeSinceStartup<heartbeat)return;
            lastDiagnostic=sample;heartbeat=Time.realtimeSinceStartup+15f;
            MoveProbePlugin.ProbeLog.LogInfo("MissionTrace "+DateTime.UtcNow.ToString("O")+" "+sample);
        }
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("MissionTrace unavailable: "+ex.Message);nextDiagnostic=Time.realtimeSinceStartup+10f;}
    }
    internal static void Trace(string stage,SaveGameHolder holder=null,string result=null,bool stack=false)
    {
        try
        {
            var mission=SceneLookup.Find<MiMissionHandler>()?.currentMissionData;
            var crew=SceneLookup.Find<MiCharacterHandler>()?.m_liPlayerCharactersInMission;
            MoveProbePlugin.ProbeLog.LogInfo("MemoryTrace "+System.Text.Json.JsonSerializer.Serialize(new {
                sequence=++traceSequence,utc=DateTime.UtcNow.ToString("O"),stage,result,
                guest=NativeInput.Probe?.NativeGuest==true,path=holder?.sPath,
                targetMission=holder?.Header?.m_iMissionGuid,currentMission=mission?.guid.iValue,
                crewCount=crew?.Count,loading=SaveProcessManager.bLoading,processing=SaveProcessManager.bProcessing,
                stored=holder?.m_bDataIsWrittenToStorage,corrupt=holder?.bHeaderOrDataCorrupt,
                caller=stack&&diagnostics?Environment.StackTrace:null}));
        }
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("MemoryTrace unavailable: "+ex.Message);}
    }
    [HarmonyPatch(typeof(SaveProcessManager),"tryLoadSaveGame")]
    internal static class LoadRequested
    {
        private static void Prefix(SaveGameHolder __0){SceneLookup.Invalidate();Trace("load-request",__0,stack:true);}
        private static void Postfix(SaveGameHolder __0,SaveProcessManager.LoadResult __result){Trace("load-accepted-result",__0,__result.ToString());}
    }
    [HarmonyPatch(typeof(SaveProcessManager),"beforeProcessLockGame")]
    internal static class ProcessLock
    {private static void Prefix(){Trace("process-lock",stack:true);}}
    [HarmonyPatch(typeof(SaveProcessManager),"afterProcessUnlockGame")]
    internal static class ProcessUnlock
    {private static void Postfix(){Trace("process-unlock");}}
}
