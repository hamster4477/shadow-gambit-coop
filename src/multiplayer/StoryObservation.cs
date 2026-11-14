using System;
using HarmonyLib;
using UnityEngine;

// Read-only event evidence. Does not replay, suppress, skip or pause a command.
internal static class StoryObservation
{
    [HarmonyPatch(typeof(Mimimi.HUB.HUBCharactersLogic),"reviveCharacter",new[]{typeof(MiCharacter),typeof(bool),typeof(bool)})]
    internal static class ShipRevival
    {
        private static void Prefix(Mimimi.HUB.HUBCharactersLogic __instance,MiCharacter __0,bool __1,bool __2,out StorySync.RevivalScope __state)
        {
            __state=null;
            try{__state=StorySync.BeginRevival(__instance,__0,__1,__2);}
            catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Revival capture: "+ex.Message);}
        }
        private static Exception Finalizer(Exception __exception,StorySync.RevivalScope __state)
        {StorySync.EndRevival(__state,__exception);return __exception;}
    }
    [HarmonyPatch(typeof(MissionInteractionGeneric),"onClicked")]
    internal static class MissionUse
    {
        private static void Postfix(MissionInteractionGeneric __instance)
        {if(__instance.bOnce&&__instance.m_bFinished)StorySync.RecordNative(__instance,"finished",null);}
    }
    [HarmonyPatch(typeof(PlanningMode),"activate")]
    internal static class PlanningEnter
    {
        private static void Prefix(PlanningMode __instance,out bool __state){__state=__instance.bIsActive;}
        private static void Postfix(PlanningMode __instance,bool __state){if(!__state&&__instance.bIsActive)StorySync.RecordNative(__instance,"plan-enter",null);}
    }
    [HarmonyPatch(typeof(PlanningMode),"deactivate")]
    internal static class PlanningExit
    {
        private static void Prefix(PlanningMode __instance,out bool __state){__state=__instance.bIsActive;}
        private static void Postfix(PlanningMode __instance,bool __state){if(__state&&!__instance.bIsActive)StorySync.RecordNative(__instance,"plan-exit",null);}
    }
    [HarmonyPatch(typeof(PlanningMode),"execute",new Type[]{})]
    internal static class PlanningExecute
    {
        private static bool Prefix(PlanningMode __instance,out bool __state){__state=NativeInput.PlanningExecution;if(PlanningSync.InterceptExecute())return false;NativeInput.PlanningExecution=true;if(!PlanningSync.Executing)StorySync.RecordNative(__instance,"plan-execute",null);return true;}
        private static Exception Finalizer(Exception __exception,bool __state){NativeInput.PlanningExecution=__state;return __exception;}
    }
    private static int count;
    private static int clockCount,viewCount;
    private static float lastScale=-1;
    private static int lastOwner=int.MinValue;
    private static int useCount,hideCount,viewEndCount;
    [HarmonyPatch(typeof(CmdAbortableDialogCutscene),"triggerOverride",new[]{typeof(TriggerData)})]
    internal static class Dialog
    {
        private static void Prefix(CmdAbortableDialogCutscene __instance,TriggerData __0)=>StorySync.RecordNative(__instance,"dialog",__0);
        private static void Postfix(CmdAbortableDialogCutscene __instance,TriggerData __0)=>Record(__instance,"dialog",__0);
    }
    [HarmonyPatch(typeof(MiCutsceneHandler),"startCutscene",new[]{typeof(MiCutsceneHandler.CutsceneOptions)})]
    internal static class NativeStart
    {
        private static void Prefix(MiCutsceneHandler.CutsceneOptions __0)
        {
            if(useCount++>=64)return;
            try{MoveProbePlugin.ProbeLog.LogInfo("NativeStoryStart name="+__0?.m_strCutsceneName+" guest="+(NativeInput.Probe?.NativeGuest==true)+" speaker="+(__0?.m_speakerResolver!=null));}
            catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Native story observation: "+ex.GetType().Name);}
        }
    }
    [HarmonyPatch(typeof(MiUsable),"use",new[]{typeof(MiCharacter)})]
    internal static class Usable
    {
        private static void Prefix(MiUsable __instance,MiCharacter __0,out StorySync.UseContext __state)
        {
            __state=StorySync.BeginUse(__instance,__0);
            if(useCount++>=32)return;
            try{
                string commands="";var list=__instance.m_lCmdOnUse;
                if(list!=null)for(int i=0;i<list.Count&&i<16;i++)commands+=" | "+list[i]?.GetIl2CppType().FullName+":"+list[i]?.name;
                MoveProbePlugin.ProbeLog.LogInfo("UseObserved guest="+(NativeInput.Probe?.NativeGuest==true)+" usable="+__instance.name+" actor="+__0?.name+" commands="+commands);
            }catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Use observation: "+ex.GetType().Name);}
        }
        private static void Finalizer(StorySync.UseContext __state)=>StorySync.EndUse(__state);
    }
    [HarmonyPatch(typeof(HidingTriggerPlayer),"exitLast")]
    internal static class HideExit
    {
        private static void Postfix(HidingTriggerPlayer __instance)
        {
            if(hideCount++>=32)return;
            try{MoveProbePlugin.ProbeLog.LogInfo("HideExitObserved guest="+(NativeInput.Probe?.NativeGuest==true)+" object="+__instance.name+" state="+__instance.eHidingState+" hidden="+__instance.m_bHidden+" inside="+__instance.bInHidingSpotLogic()+" collider="+__instance.bInHidingSpotCollider());}
            catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Hide observation: "+ex.GetType().Name);}
        }
    }
    [HarmonyPatch(typeof(ViewConeState),"onVCDeactivate")]
    internal static class ViewEnd
    {
        private static void Postfix(ViewConeState __instance)
        {
            if(viewEndCount++>=32)return;
            try{MoveProbePlugin.ProbeLog.LogInfo("ViewEndObserved guest="+(NativeInput.Probe?.NativeGuest==true)+" mode="+__instance.eVCModeActive);}
            catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("View observation: "+ex.GetType().Name);}
        }
    }
    private static void Record(Component command,string kind,TriggerData data)
    {
        if(count>=128)return;
        try
        {
            count++;
            MoveProbePlugin.ProbeLog.LogInfo("StoryObserved kind="+kind+" guest="+(NativeInput.Probe?.NativeGuest==true)+
                " world="+SessionWorld.Current+" object="+command?.name+" trigger="+data?.m_collider?.name+
                " fromCutscene="+(data?.m_bTriggeredByCutscene==true)+" loading="+SaveProcessManager.bLoading+
                " playing="+MiCutsceneHandler.bCutsceneRunning);
        }
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Story observation: "+ex.GetType().Name);}
    }
    [HarmonyPatch(typeof(CmdCutscene),"triggerOverride")]
    internal static class Script
    {private static void Prefix(CmdCutscene __instance,TriggerData __0)=>StorySync.RecordNative(__instance,"script",__0);
     private static void Postfix(CmdCutscene __instance,TriggerData __0)=>Record(__instance,"script",__0);}
    [HarmonyPatch(typeof(CmdPlayTimelineCutscene),"triggerOverride")]
    internal static class Timeline
    {private static void Prefix(CmdPlayTimelineCutscene __instance,TriggerData __0)=>StorySync.RecordNative(__instance,"timeline",__0);
     private static void Postfix(CmdPlayTimelineCutscene __instance,TriggerData __0)=>Record(__instance,"timeline",__0);}
    [HarmonyPatch(typeof(MiGameTime),"setTimeScale")]
    internal static class Clock
    {
        private static void Postfix(float __0,int __1)
        {
            if(clockCount>=64||__1==lastOwner&&Math.Abs(__0-lastScale)<.05f)return;
            clockCount++;lastOwner=__1;lastScale=__0;
            MoveProbePlugin.ProbeLog.LogInfo("ClockObserved owner="+__1+" scale="+__0+" guest="+(NativeInput.Probe?.NativeGuest==true));
        }
    }
    [HarmonyPatch(typeof(ViewConeState),"activateVC",new[]{typeof(MiCharacterControllerAI),typeof(ViewConeState.ViewConeActivationMode),typeof(bool)})]
    internal static class View
    {
        private static void Postfix(ViewConeState.ViewConeActivationMode __1,bool __2)
        {
            if(viewCount++>=64)return;
            MoveProbePlugin.ProbeLog.LogInfo("ViewObserved mode="+__1+" option="+__2+" guest="+(NativeInput.Probe?.NativeGuest==true));
        }
    }
}

// Limited read-only training-deck evidence, independently armed from skill tracing.
internal static class TrainingDeckTrace
{
    private static float next,until;
    private static int remaining;
    private static string previous;
    internal static void Tick()
    {
        float now=Time.realtimeSinceStartup;if(now<next)return;next=now+.5f;
        try
        {
            string request=System.IO.Path.Combine(SessionHost.DirectoryPath,"monitor-training.request");
            if(System.IO.File.Exists(request))
            {
                System.IO.File.Delete(request);until=now+1200;remaining=400;previous=null;
                Event("armed-1200s");
            }
            if(now>=until||remaining<=0)return;
            var mission=SceneLookup.Find<MiMissionHandler>();
            var crew=SceneLookup.Find<MiCharacterHandler>()?.m_liPlayerCharactersInMission;
            var text=new System.Text.StringBuilder();
            text.Append("mission=").Append(mission?.currentMissionData?.guid.iValue).Append(" phase=").Append(mission?.eCurrentMissionState)
                .Append(" loading=").Append(SaveProcessManager.bLoading).Append(" processing=").Append(SaveProcessManager.bProcessing)
                .Append(" cutscene=").Append(MiCutsceneHandler.bCutsceneRunning);
            if(!SaveProcessManager.bLoading&&crew!=null)foreach(var actor in crew)
            {
                if(actor==null)continue;
                text.Append(" | actor=").Append(GuestMove.Identity(actor)).Append(" health=").Append(actor.health?.eHealthState);
                var input=actor.controllerPlayer?.m_playerInput;
                text.Append(" leader=").Append(GuestMove.Identity(input?.leader)).Append(" selected=").Append(input?.lSelectedCharacter?.Count??0);
            }
            string sample=text.ToString();if(sample!=previous){previous=sample;Event("state "+sample);}
        }
        catch(Exception ex){Event("read-error "+ex.GetType().Name);next=now+5;}
    }
    internal static void Event(string value)
    {
        if(Time.realtimeSinceStartup>=until||remaining<=0)return;
        remaining--;MoveProbePlugin.ProbeLog.LogInfo("TrainingDeckTrace "+DateTime.UtcNow.ToString("O")+" "+value);
    }
    [HarmonyPatch(typeof(Mimimi.HUB.CmdHUBLoadCharacterTutorial),"loadTutorialFor",new[]{typeof(MiCharacterDefinitionPlayer)})]
    internal static class Entry
    {private static void Prefix(MiCharacterDefinitionPlayer __0){try{Event("tutorial-character "+__0?.name);}catch(Exception){}}}
    [HarmonyPatch(typeof(MiMissionHandler),"loadMissionFromGuid")]
    internal static class Mission
    {private static void Prefix(int __0)=>Event("load-mission "+__0);}
    [HarmonyPatch(typeof(MiMissionHandler),"finishTutorial")]
    internal static class Finished
    {private static void Prefix()=>Event("finish-tutorial");}
    [HarmonyPatch(typeof(MiMissionHandler),"loadHUB")]
    internal static class Return
    {private static void Prefix()=>Event("return-hub");}
}
