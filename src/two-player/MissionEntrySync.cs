using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

// Observe the first native checkpoint of a fresh mission. Never manufacture
// a save while the game forbids saving, nor offer an old entry save mid-task.
internal static class MissionEntrySync
{
    private static float opened=float.NegativeInfinity;
    private static bool attempted;
    private static MiSceneLoaderTactics waitingLoader;
    private static bool wasWaiting;
    internal static void Install()
    {
        var h=new Harmony("local.shadowgambit.entrysync");
        h.PatchAll(typeof(Window));h.PatchAll(typeof(Save));h.PatchAll(typeof(Unlock));h.PatchAll(typeof(Written));h.PatchAll(typeof(Cutscene));
        h.PatchAll(typeof(ContinueGate));
        h.PatchAll(typeof(EntryRender));
    }
    internal static bool WaitingGuestEntry{get;private set;}
    internal static bool ReadyMatches(string json,string world,int mission)
    {
        try{using var doc=System.Text.Json.JsonDocument.Parse(json);var r=doc.RootElement;
            return r.GetProperty("schema").GetInt32()==1&&r.GetProperty("world").GetString()==world&&r.GetProperty("mission").GetInt32()==mission;
        }catch{return false;}
    }
    internal static void TickHostEntry(bool hosting)
    {
        WaitingGuestEntry=false;
        if(!hosting||!PeerPresent())return;
        var handler=SceneLookup.Find<MiMissionHandler>();
        if(handler?.currentMissionData==null||SessionWorld.Current==null||
           handler.currentMissionData.guid.iValue==-379511569||
           handler.eCurrentMissionState.ToString().StartsWith("Init_",StringComparison.Ordinal)!=true)return;
        // Ordinary play has no entry confirmation. Avoid searching the scene
        // for a missing loader until the native mission is actually entering.
        var loader=SceneLookup.Find<MiSceneLoaderTactics>();
        if(loader?.m_goRootContinue?.activeInHierarchy!=true)return;
        WaitingGuestEntry=true;
        try{
            string path=Path.Combine(SessionHost.DirectoryPath,"guest-entry-ready.json");
            if(File.Exists(path)&&DateTime.UtcNow-File.GetLastWriteTimeUtc(path)<TimeSpan.FromSeconds(15)&&
               ReadyMatches(File.ReadAllText(path),SessionWorld.Current,handler.currentMissionData.guid.iValue))WaitingGuestEntry=false;
        }catch(IOException){}
    }
    private static bool Waiting=>!TrainingSession.Suspended&&(GuestMissionPreparation.WaitingDirectContinue||WaitingGuestEntry||SessionHost.WaitingEntryContinue||SessionBootstrap.WaitingEntryContinue);
    internal static void TickContinue()
    {
        bool held=SessionHost.WaitingEntryContinue||WaitingGuestEntry;
        // Keep checking a previously held confirmation until its release has
        // completed, including the frame in which the waiting flags clear.
        if(!held&&!wasWaiting)return;
        var loader=SceneLookup.Find<MiSceneLoaderTactics>();
        bool visible=loader?.m_goRootContinue?.activeInHierarchy==true;
        // Guest continuation is owned by SessionTaskContinue; invoking it here
        // as well would click twice in one Update after the ready response.
        if(held){wasWaiting=true;if(visible)waitingLoader=loader;return;}
        if(!wasWaiting)return;
        var pending=waitingLoader;
        // Auto-release only the exact loader held by this entry, never a later
        // unrelated confirmation or scene.
        if(pending!=null&&pending==loader&&visible)
        {
            if(loader.m_buttonContinue?.interactable!=true)return;
            wasWaiting=false;waitingLoader=null;loader.onContinueButtonPressed();return;
        }
        wasWaiting=false;waitingLoader=null;
    }
    [HarmonyPatch]
    internal static class ContinueGate
    {
        private static System.Reflection.MethodBase TargetMethod()=>AccessTools.Method(typeof(MiSceneLoaderTactics),"onContinueButtonPressed");
        private static bool Prefix()=>!Waiting;
    }
    // Only a verified bootstrap Hidden load arms this one-shot cleanup.
    // Native storage loading installs AfterLoad masks but does not run the
    // rewind executor's effect-finish graph. Clear both component and shader
    // state before the intro can render, without touching inquisitor effects.
    [HarmonyPatch(typeof(MiSceneLoaderTactics),"continueToGame")]
    internal static class EntryRender
    {
        private static int? mission;
        internal static void Arm(int id){mission=id;}
        internal static void Cancel(){mission=null;}
        internal static bool Consume(int? current)
        {
            var expected=mission;mission=null;
            return expected.HasValue&&current==expected;
        }
        private static void Prefix()
        {
            if(!Consume(SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue))return;
            MissionEntryTrace.RenderState("before-entry-mask-restore");
            foreach(var effect in UnityEngine.Object.FindObjectsOfType<PostEffectsCombined>())
            {
                effect.resetSaveLoadEffect();
                effect.m_v4SaveLoadDepthMask=Vector4.zero;
            }
            Shader.SetGlobalVector(PostEffectsCombined._MiSaveLoadEffect,Vector4.zero);
            Shader.SetGlobalVector(PostEffectsCombined._MiSaveLoadEffectDepthMask,Vector4.zero);
            MissionEntryTrace.RenderState("after-entry-mask-restore");
        }
    }
    internal static bool PeerPresent()
    {
        if(TrainingSession.Suspended)return false;
        try {string p=Path.Combine(SessionHost.DirectoryPath,"lobby-peer.json");return File.Exists(p)&&DateTime.UtcNow-File.GetLastWriteTimeUtc(p)<TimeSpan.FromSeconds(15);}
        catch(IOException){return false;}
    }
    [HarmonyPatch(typeof(MiMissionHandler),"trySetSaveloadLocks")]
    internal static class Window
    {private static void Prefix(bool __0){opened=__0&&!SaveProcessManager.bLoading?Time.realtimeSinceStartup:float.NegativeInfinity;attempted=false;}}
    [HarmonyPatch(typeof(SaveProcessManager),"trySaveScene")]
    internal static class Save
    {
        private static void Prefix(out int __state)
        {
            __state=0;
            if(!SaveProcessManager.bLoading&&SceneLookup.Find<MiMissionHandler>()?.eCurrentMissionState.ToString()=="Init_InitialQuicksave"){
                if(PeerPresent()){SessionHost.BeginPreparedCheckpoint();__state=2;}return;
            }
            if(attempted||Time.realtimeSinceStartup-opened>45f||SaveProcessManager.bLoading||MiCutsceneHandler.bCutsceneRunning)return;
            attempted=true;
            if(PeerPresent()){SessionHost.BeginNativeEntry();__state=1;}
        }
        private static void Postfix(SaveProcessManager.SaveResult __result,int __state)
        {if(__result==SaveProcessManager.SaveResult.Success)return;if(__state==1)SessionHost.FailNativeEntry("Native entry save rejected: "+__result);if(__state==2)SessionHost.FailPreparedCheckpoint("Native preparation save rejected: "+__result);}
    }
    [HarmonyPatch(typeof(SaveProcessManager),"afterProcessUnlockGame")]
    internal static class Unlock
    {private static void Prefix(){SessionHost.HoldNativeEntry();}}
    [HarmonyPatch(typeof(SaveProcessManager),"onSaveSceneInternalContainerWriteFinished")]
    internal static class Written
    {private static void Postfix(SaveGameHolder __0){SessionHost.NativeEntryWritten(__0);}}
    [HarmonyPatch(typeof(MiCutsceneHandler),"startCutsceneSequenceInternal")]
    internal static class Cutscene
    {private static void Prefix(){attempted=true;SessionHost.HoldNativeEntry();}}
}
