using System;
using System.IO;
using System.Text.Json;
using HarmonyLib;
using UnityEngine;

// Transaction metadata accompanies a particular checkpoint, never a global
// "next load is red" flag. Normal loads carry no enemy context.
internal static class EnemyRewind
{
    internal sealed class Frame
    {
        public int schema {get;set;}=1;
        public bool enemy {get;set;}=true;
        public bool showEffect {get;set;}
        public float intensity {get;set;}
        public string[] names {get;set;}
        public int[] states {get;set;}
    }
    internal static Frame Parse(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Null)return null;
        if(value.GetRawText().Length>4096)throw new InvalidDataException("Enemy rewind metadata too large");
        if(value.GetProperty("schema").GetInt32()!=1||!value.GetProperty("enemy").GetBoolean())throw new InvalidDataException("Invalid enemy rewind origin");
        value.GetProperty("showEffect").GetBoolean();value.GetProperty("intensity").GetSingle();
        var f=JsonSerializer.Deserialize<Frame>(value.GetRawText());Validate(f);return f;
    }
    internal static void Validate(Frame f)
    {
        if(f==null||f.schema!=1||!f.enemy||!float.IsFinite(f.intensity)||Math.Abs(f.intensity)>10000||
            f.names==null||f.states==null||f.names.Length>32||f.names.Length!=f.states.Length)
            throw new InvalidDataException("Invalid enemy rewind metadata");
        foreach(string name in f.names)if(name==null||name.Length>64)throw new InvalidDataException("Invalid rewind state name");
        if(JsonSerializer.Serialize(f).Length>4096)throw new InvalidDataException("Enemy rewind state table too large");
    }
    private static Frame captured;
    private static string capturedPath,captureError;
    private static float commandAt=float.NegativeInfinity;
    private static bool show;
    private static float intensity;
    private static string expectedPath;
    private static Frame pending;
    private static int[] previousStates;
    private static InquisitorLoadHandlerSave previousHandler;
    private static InquisitorLoadHandlerNoSave previousMarker;
    private static bool previousNotified,consumed;
    private static bool enteringContext,entryApplied;
    private static string completionFailure;
    private static bool previousLastLoad;
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> previousBeforeLoad;
    private static float expires;
    internal static void Install()
    {
        var h=new Harmony("local.shadowgambit.enemyrewind");
        h.PatchAll(typeof(Request));h.PatchAll(typeof(Command));h.PatchAll(typeof(BeforeLoad));h.PatchAll(typeof(Complete));h.PatchAll(typeof(EntryContext));
    }
    [HarmonyPatch(typeof(CmdLoadLastQuicksave),"triggerOverride")]
    internal static class Command
    {
        private static void Prefix(CmdLoadLastQuicksave __instance)
        {
            commandAt=Time.realtimeSinceStartup;
            show=__instance.m_bInquisitorLoad&&__instance.m_bShowInquisitorLoadVFX;
            intensity=__instance.m_fInquisitorLoadVFXIntensity;
            MoveProbePlugin.ProbeLog.LogInfo("Enemy rewind command: enemy="+__instance.m_bInquisitorLoad+" effect="+show+" intensity="+intensity);
        }
    }
    [HarmonyPatch(typeof(InquisitorLoadHandlerNoSave),"onBeforeLoad")]
    internal static class BeforeLoad
    {
        private static void Postfix(InquisitorLoadHandlerNoSave __instance,SaveGameHolder __0)
        {
            if(expectedPath!=null)
            {
                if(Same(expectedPath,__0?.sPath))consumed=true;
                else Cancel();
                return;
            }
        }
    }
    [HarmonyPatch(typeof(SaveProcessManager),"tryLoadSaveGame")]
    internal static class Request
    {
        private static void Prefix(SaveGameHolder __0)
        {
            if(expectedPath!=null){if(!Same(expectedPath,__0?.sPath))Cancel();else return;}
            captured=null;capturedPath=__0?.sPath;captureError=null;
            var marker=UnityEngine.Object.FindObjectOfType<InquisitorLoadHandlerNoSave>();
            if(marker?.m_bWasNotifiedOfInquisitorLoad!=true)return;
            try
            {
                if(Time.realtimeSinceStartup-commandAt>120f)throw new InvalidDataException("Enemy rewind command parameters unavailable");
                var names=marker.m_arStatesNames;
                var states=UnityEngine.Object.FindObjectOfType<InquisitorLoadHandlerSave>()?.states;
                if(names==null||states==null||names.Length!=states.Length||names.Length>32)throw new InvalidDataException("Enemy rewind state table unavailable");
                var f=new Frame{showEffect=show,intensity=intensity,names=new string[names.Length],states=new int[states.Length]};
                for(int i=0;i<f.names.Length;i++){f.names[i]=names[i];f.states[i]=states[i];}
                Validate(f);captured=f;
                MoveProbePlugin.ProbeLog.LogInfo("Enemy rewind captured: "+JsonSerializer.Serialize(f));
            }
            catch(Exception ex){captureError=ex.Message;MoveProbePlugin.ProbeLog.LogWarning("Enemy rewind capture: "+ex.Message);}
            finally{commandAt=float.NegativeInfinity;}
        }
    }
    private static bool Same(string a,string b)=>a!=null&&b!=null&&string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
    internal static Frame ForCheckpoint(SaveGameHolder holder)
    {
        if(!Same(capturedPath,holder?.sPath))return null;
        if(captureError!=null)throw new InvalidDataException(captureError);
        return captured;
    }
    internal static void Prepare(JsonElement request,string path,bool inPlace)
    {
        Cancel();completionFailure=null;
        if(!request.TryGetProperty("rewind",out var value)||value.ValueKind==JsonValueKind.Null)return;
        var f=Parse(value);
        if(!inPlace){
            // A late join/restarted guest has no old scene handlers. Supply the
            // host's pre-rewind state when the destination handler is loaded,
            // before its original OnLoadingFinished starts the story commands.
            // Never replay the in-place red VFX against the main-menu camera.
            pending=f;expectedPath=path;expires=Time.realtimeSinceStartup+180f;
            enteringContext=true;entryApplied=consumed=false;
            MoveProbePlugin.ProbeLog.LogInfo("Guest enemy rewind context queued for scene entry");return;
        }
        var marker=UnityEngine.Object.FindObjectOfType<InquisitorLoadHandlerNoSave>();
        var save=UnityEngine.Object.FindObjectOfType<InquisitorLoadHandlerSave>();
        var fx=UnityEngine.Object.FindObjectOfType<PostEffectHandler>();
        if(marker?.m_arStatesNames==null||save?.states==null||marker.m_arStatesNames.Length!=f.names.Length||save.states.Length!=f.states.Length||f.showEffect&&fx==null)
            throw new InvalidOperationException("Guest enemy rewind handlers are not ready");
        for(int i=0;i<f.names.Length;i++)if(marker.m_arStatesNames[i]!=f.names[i])throw new InvalidDataException("Guest enemy rewind state table differs from host");
        previousStates=new int[f.states.Length];for(int i=0;i<previousStates.Length;i++)previousStates[i]=save.states[i];
        previousHandler=save;previousMarker=marker;previousNotified=marker.m_bWasNotifiedOfInquisitorLoad;
        pending=f;expectedPath=path;expires=Time.realtimeSinceStartup+180f;consumed=false;
        try
        {
            for(int i=0;i<f.states.Length;i++)save.setState(i,f.states[i]);
            marker.notifyOfInquisitorLoad();
            if(f.showEffect)fx.startInquisitorLoad(f.intensity);
            MoveProbePlugin.ProbeLog.LogInfo("Guest enemy rewind prepared: "+JsonSerializer.Serialize(f));
        }
        catch{Cancel();throw;}
    }
    [HarmonyPatch(typeof(InquisitorLoadHandlerSave),"OnLoadingFinished")]
    internal static class EntryContext
    {
        private static void Prefix(InquisitorLoadHandlerSave __instance)
        {
            if(!enteringContext||pending==null||entryApplied)return;
            try{
                var marker=UnityEngine.Object.FindObjectOfType<InquisitorLoadHandlerNoSave>();
                if(marker?.m_arStatesNames==null||__instance?.states==null||marker.m_arStatesNames.Length!=pending.names.Length||__instance.states.Length!=pending.states.Length)
                    throw new InvalidDataException("Entry rewind state table is unavailable");
                for(int i=0;i<pending.names.Length;i++)if(marker.m_arStatesNames[i]!=pending.names[i])
                    throw new InvalidDataException("Entry rewind state table differs from host");
                // Same values as native onBeforeLoad: copy previous states,
                // mark the completed load's origin, and consume the notification.
                // Keep the just-deserialized Save handler states untouched.
                var states=new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(pending.states.Length);
                for(int i=0;i<pending.states.Length;i++)states[i]=pending.states[i];
                previousMarker=marker;previousNotified=marker.m_bWasNotifiedOfInquisitorLoad;
                previousLastLoad=marker.m_bLastLoadIsFromInquisitor;previousBeforeLoad=marker.m_arStatesBeforeLoad;
                marker.m_arStatesBeforeLoad=states;
                marker.m_bLastLoadIsFromInquisitor=true;marker.m_bWasNotifiedOfInquisitorLoad=false;
                entryApplied=consumed=true;
                MoveProbePlugin.ProbeLog.LogInfo("Guest enemy rewind scene-entry context applied before native story commands");
            }catch(Exception ex){completionFailure=ex.Message;MoveProbePlugin.ProbeLog.LogWarning("Guest entry rewind context: "+ex.Message);}
        }
    }
    internal static void ValidateCompletion(){if(completionFailure!=null)throw new InvalidDataException(completionFailure);}
    [HarmonyPatch(typeof(SaveProcessManager),"invokeOnAfterLoadCallbacks")]
    internal static class Complete
    {
        private static void Postfix(SaveGameHolder __0,SaveProcessManager.LoadProcessResult __1)
        {
            if(expectedPath==null)return;
            if(enteringContext&&!entryApplied&&__1==SaveProcessManager.LoadProcessResult.Success)
                completionFailure=completionFailure??"Entry rewind handler did not receive its native completion callback";
            if(Same(expectedPath,__0?.sPath)&&consumed&&__1==SaveProcessManager.LoadProcessResult.Success)
            {MoveProbePlugin.ProbeLog.LogInfo("Guest enemy rewind completed with native context");Clear();}
            else Cancel(false);
        }
    }
    internal static void Tick(){if(expectedPath!=null&&Time.realtimeSinceStartup>expires)Cancel();}
    private static void Clear(){pending=null;expectedPath=null;previousStates=null;previousHandler=null;previousMarker=null;previousBeforeLoad=null;consumed=enteringContext=entryApplied=false;}
    internal static void Cancel(bool clearFailure=true)
    {
        if(clearFailure)completionFailure=null;
        if(expectedPath==null)return;
        try
        {
            if(previousHandler!=null&&previousStates!=null&&previousHandler.states?.Length==previousStates.Length)
                for(int i=0;i<previousStates.Length;i++)previousHandler.setState(i,previousStates[i]);
            if(previousMarker!=null)previousMarker.m_bWasNotifiedOfInquisitorLoad=previousNotified;
            if(enteringContext&&entryApplied&&previousMarker!=null){previousMarker.m_bLastLoadIsFromInquisitor=previousLastLoad;previousMarker.m_arStatesBeforeLoad=previousBeforeLoad;}
            if(!enteringContext&&pending?.showEffect==true)UnityEngine.Object.FindObjectOfType<PostEffectHandler>()?.startInquisitorLoad(0f);
        }
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Enemy rewind cleanup: "+ex.Message);}
        finally{Clear();}
    }
}
