using System;
using HarmonyLib;
using Mimimi.CharacterSelection;

// Observe committed native selections and protect guest-owned picks from host removal.
// Preserve native hover, camera and re-selection behavior.
internal static class LobbySelectionHooks
{
    internal static bool Ready;
    internal static LobbyHost Active;
    internal sealed class Change
    {
        internal LobbyHost host;
        internal CharacterSelection selection;
        internal CharacterSelectionCharacter actor;
        internal string id;
        internal bool picked,remote;
    }
    internal static void Install()
    {
        try
        {
            var hooks=new Harmony("local.shadowgambit.preparation");
            hooks.PatchAll(typeof(Pick));hooks.PatchAll(typeof(Unpick));hooks.PatchAll(typeof(Completed));
            Ready=true;
            MoveProbePlugin.ProbeLog.LogInfo("Preparation selection and departure hooks installed.");
        }
        catch(Exception ex){Ready=false;MoveProbePlugin.ProbeLog.LogWarning("Preparation selection hooks unavailable: "+ex);}
    }
    [HarmonyPatch(typeof(CharacterSelection),"bTryPick",new[]{typeof(CharacterSelectionCharacter),typeof(bool),typeof(bool)})]
    internal static class Pick
    {
        internal static bool Prefix(CharacterSelection __instance,CharacterSelectionCharacter __0,bool __1,out Change __state,ref bool __result)
        {
            __state=null;
            if(GuestMissionPreparation.Active&&!GuestMissionPreparation.Applying&&__1&&__instance.bIsActive&&__instance.m_bStartingFadeFinished){
                if(__0?.data!=null)GuestMissionPreparation.RequestPick?.Invoke(LobbyHost.Identity(__0.data),true);
                __result=false;return false;
            }
            __state=Active?.BeforeSelection(__instance,__0);return true;
        }
        internal static void Postfix(bool __result,Change __state)
        {__state?.host.AfterSelection(__state,__result);}
    }
    [HarmonyPatch(typeof(CharacterSelection),"bTryUnPick",new[]{typeof(int),typeof(bool),typeof(bool)})]
    internal static class Unpick
    {
        internal static bool Prefix(CharacterSelection __instance,int __0,bool __1,out Change __state,ref bool __result)
        {
            __state=null;
            if(GuestMissionPreparation.Active&&!GuestMissionPreparation.Applying&&__1&&__instance.bIsActive&&__instance.m_bStartingFadeFinished){
                foreach(var actor in __instance.m_arCharacters)if(actor?.data!=null&&actor.bIsPicked&&__instance.iGetCharSlotIndex(actor)==__0)
                    GuestMissionPreparation.RequestPick?.Invoke(LobbyHost.Identity(actor.data),false);
                __result=false;return false;
            }
            __state=Active?.BeforeUnpick(__instance,__0);
            if(__state!=null&&!__state.host.AllowUnpick(__state)){__result=false;return false;}
            return true;
        }
        internal static void Postfix(bool __result,Change __state)
        {__state?.host.AfterSelection(__state,__result);}
    }
    [HarmonyPatch(typeof(MiMissionCharacterSelectionHandler),"onCharacterSelectionCompleted",
        new[]{typeof(Il2CppSystem.Collections.Generic.List<MiCharacterDefinitionPlayer>)})]
    internal static class Completed
    {
        internal static void Prefix(Il2CppSystem.Collections.Generic.List<MiCharacterDefinitionPlayer> __0)
        {Active?.Completed(__0);}
    }
}
