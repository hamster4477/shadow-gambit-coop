using System;
using HarmonyLib;
using Il2CppSystem.Collections.Generic;

// Version 1.2.133 native deserializeMi uses these two keys to resolve visual
// coroutine/last-command references from the class table. Entry checkpoints
// can contain a reference absent from that table (guest 20261111-183332).
// Leave real command data, actors, assets and valid references untouched.
internal static class MovementLineLoadRepair
{
    internal const long CoroutineKey=unchecked((long)0xC73F457A6D0C32C4UL);
    internal const long CommandKey=unchecked((long)0xFBA5229DFB64FFD2UL);
    internal static void Install()=>new Harmony("local.shadowgambit.movementline.load").PatchAll(typeof(Load));
    [HarmonyPatch(typeof(MiMovementLine),"deserializeMi")]
    internal static class Load
    {
        internal static void Prefix(MiMovementLine __instance,Dictionary<long,Il2CppSystem.Object> __0,Dictionary<long,Il2CppSystem.Object> __3)
        {
            if(!SessionBootstrap.LoadingCheckpoint||!MiVersion.Version.Display.StartsWith("1.2.133",StringComparison.Ordinal)||__0==null||__3==null)return;
            Repair(__instance,__0,__3,CoroutineKey);
            Repair(__instance,__0,__3,CommandKey);
        }
    }
    private static void Repair(MiMovementLine line,Dictionary<long,Il2CppSystem.Object> data,Dictionary<long,Il2CppSystem.Object> classes,long key)
    {
        if(!data.TryGetValue(key,out var boxed)||boxed==null)return;
        long reference;
        try{reference=boxed.Unbox<long>();}
        catch(Exception){return;} // Unknown representation: native loader owns it.
        if(classes.ContainsKey(reference))return;
        data.Remove(key);
        if(key==CoroutineKey)line.m_coroShowLineRenderer=null;
        else line.m_skillCommandMovementLineProcessed=null;
        MoveProbePlugin.ProbeLog.LogInfo($"Checkpoint visual reference cleared: field={key} missing={reference}");
    }
}
