using System;
using System.IO;
using HarmonyLib;

// Read-only evidence for native entry checkpoints and save locks. No original
// method is skipped, no result is changed and no additional save is requested.
internal static class MissionEntryTrace
{
    private static bool enabled;
    private static float nextShip;
    private static string lastShip;
    internal static void TickShip()
    {
        if(!enabled||UnityEngine.Time.realtimeSinceStartup<nextShip)return;
        nextShip=UnityEngine.Time.realtimeSinceStartup+1f;
        if(SaveProcessManager.bLoading||SaveProcessManager.bProcessing)return;
        try {
            var hub=SceneLookup.Find<Mimimi.HUB.HUBLogic>()?.characters;
            if(hub==null){lastShip=null;return;}
            var rows=new System.Collections.Generic.List<string>();
            if(hub.m_liSpawnedCharacters!=null)foreach(var actor in hub.m_liSpawnedCharacters)
                if(actor!=null)rows.Add(GuestMove.Identity(actor)+":"+(actor.bControllerPlayer()?"player":actor.bControllerAI()?"routine":"other"));
            string state="leader="+(hub.leader==null?"none":GuestMove.Identity(hub.leader))+" spawned="+hub.m_bCharactersSpawned+" routines="+hub.m_bRoutineSpawningProcessEnded+" actors="+string.Join(",",rows);
            if(state!=lastShip){lastShip=state;MoveProbePlugin.ProbeLog.LogInfo("ShipControl "+state);}
        }catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("ShipControl observation: "+ex.GetType().Name);nextShip+=9f;}
    }
    internal static void Install()
    {
        enabled=File.Exists(Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","trace-mission-entry.flag"));
        if(!enabled)return;
        try {
            var h=new Harmony("local.shadowgambit.missionentry.trace");
            h.PatchAll(typeof(Save));h.PatchAll(typeof(Written));h.PatchAll(typeof(Mission));
            h.PatchAll(typeof(Locks));h.PatchAll(typeof(CutsceneStart));h.PatchAll(typeof(CutsceneEnd));
            h.PatchAll(typeof(Continue));h.PatchAll(typeof(Hub));
            h.PatchAll(typeof(Skip));h.PatchAll(typeof(SkipButton));
            h.PatchAll(typeof(RenderReset));
            h.PatchAll(typeof(Abandon));h.PatchAll(typeof(ReturnMenu));h.PatchAll(typeof(AbortCutscene));
            MoveProbePlugin.ProbeLog.LogInfo("Mission entry read-only tracing enabled");
        }catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Mission entry trace installation failed: "+ex);}
    }
    [HarmonyPatch(typeof(SaveProcessManager),"trySaveScene")]
    internal static class Save
    {
        private static void Prefix(SaveGame.SaveGameType __0,string __1,bool __4)
        {SessionWorld.Trace("entry-save-request",result:"type="+__0+" path="+__1+" customCache="+__4,stack:true);}
        private static void Postfix(SaveProcessManager.SaveResult __result)
        {SessionWorld.Trace("entry-save-result",result:__result.ToString());}
    }
    [HarmonyPatch(typeof(SaveProcessManager),"onSaveSceneInternalContainerWriteFinished")]
    internal static class Written
    {
        private static void Postfix(SaveGameHolder __0)
        {SessionWorld.Trace("entry-save-written",__0);}
    }
    [HarmonyPatch(typeof(MiMissionHandler),"loadMissionFromGuid")]
    internal static class Mission
    {
        private static void Prefix(int __0)
        {SessionWorld.Trace("entry-load-mission",result:"target="+__0,stack:true);}
    }
    [HarmonyPatch(typeof(MiMissionHandler),"trySetSaveloadLocks")]
    internal static class Locks
    {
        private static void Prefix(bool __0)
        {SessionWorld.Trace("entry-saveload-lock",result:__0.ToString());}
    }
    [HarmonyPatch(typeof(MiCutsceneHandler),"startCutsceneSequenceInternal")]
    internal static class CutsceneStart
    {private static void Prefix(){SessionWorld.Trace("entry-cutscene-start",stack:true);}}
    [HarmonyPatch(typeof(MiCutsceneHandler),"endCutsceneSequenceInternal")]
    internal static class CutsceneEnd
    {private static void Postfix(){SessionWorld.Trace("entry-cutscene-end");}}
    [HarmonyPatch(typeof(MiSceneLoaderTactics),"continueToGame")]
    internal static class Continue
    {private static void Prefix(){SessionWorld.Trace("entry-continue-to-game",stack:true);RenderState("before-continue");}
     private static void Postfix(){RenderState("after-continue");}}
    [HarmonyPatch(typeof(MiMissionHandler),"loadHUB")]
    internal static class Hub
    {private static void Prefix(){SessionWorld.Trace("entry-return-hub",stack:true);}}
    [HarmonyPatch(typeof(MiCutsceneHandler),"skipCutscene")]
    internal static class Skip
    {private static void Prefix(){SessionWorld.Trace("entry-cutscene-skip",stack:true);}}
    [HarmonyPatch(typeof(UICutsceneHUD),"onSkipButtonPressed")]
    internal static class SkipButton
    {private static void Prefix(){SessionWorld.Trace("entry-cutscene-skip-button",stack:true);RenderState("before-skip");}
     private static void Postfix(){RenderState("after-skip");}}
    private static void ExitTrace(string stage){
        try{SessionWorld.Trace(stage,result:"cutscene="+MiCutsceneHandler.bCutsceneRunning+" paused="+MiTime.pausedAny,stack:true);}
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Exit trace: "+ex.GetType().Name);}
    }
    [HarmonyPatch(typeof(MiMissionHandler),"abandonMission")]
    internal static class Abandon {
        private static void Prefix(){ExitTrace("exit-abandon-before");}
        private static void Postfix(){ExitTrace("exit-abandon-after");}
    }
    [HarmonyPatch(typeof(MiMissionHandler),"goBackToMainMenu")]
    internal static class ReturnMenu {
        private static void Prefix(){ExitTrace("exit-menu-before");}
        private static void Postfix(){ExitTrace("exit-menu-after");}
    }
    [HarmonyPatch(typeof(MiCutsceneHandler),"abortCutscene")]
    internal static class AbortCutscene {
        private static void Prefix(){ExitTrace("exit-abort-cutscene-before");}
        private static void Postfix(){ExitTrace("exit-abort-cutscene-after");}
    }
    internal static void RenderState(string stage)
    {
        if(!enabled)return;
        try {
            // Component fields can be zero while a shader global still carries
            // a previous camera's load effect. Observe both; do not reset either.
            SessionWorld.Trace("entry-render-globals-"+stage,result:System.Text.Json.JsonSerializer.Serialize(new {
                saveLoad=UnityEngine.Shader.GetGlobalVector(PostEffectsCombined._MiSaveLoadEffect).ToString(),
                depth=UnityEngine.Shader.GetGlobalVector(PostEffectsCombined._MiSaveLoadEffectDepthMask).ToString(),
                inquisitor=UnityEngine.Shader.GetGlobalVector(PostEffectsCombined._MiGlobalInquisitorLoad).ToString()}));
            var effects=UnityEngine.Object.FindObjectsOfType<PostEffectsCombined>(true);
            int count=0;
            foreach(var effect in effects){if(count++>=8)break;
                SessionWorld.Trace("entry-render-"+stage,result:System.Text.Json.JsonSerializer.Serialize(new {
                    name=effect.name,active=effect.gameObject.activeInHierarchy,
                    inquisitorLocal=effect.v4SaveLoadInquisitor.ToString(),
                    saveLoad=effect.m_v4SaveLoad.ToString(),depth=effect.m_v4SaveLoadDepthMask.ToString(),
                    afterLoad=effect.m_v4SaveLoadAfterLoad.ToString(),afterDepth=effect.m_v4SaveLoadDepthMaskAfterLoad.ToString(),
                    overlay=PostEffectsCombined.s_bDisplayLoadOverlay,intensity=effect.m_fIntensity,sceneIntensity=effect.m_fSceneIntensity}));
            }
        }catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Entry render observation: "+ex.Message);}
    }
    [HarmonyPatch(typeof(PostEffectsCombined),"resetSaveLoadEffect")]
    internal static class RenderReset
    {private static void Postfix(){RenderState("native-reset");}}
}


