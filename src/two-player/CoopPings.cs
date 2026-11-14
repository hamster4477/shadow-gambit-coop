using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

// Cosmetic scene-scoped messages only. Never issues character or view-cone commands.
internal static class CoopPings
{
    private static readonly PingLedger ledger=new();
    private static readonly Dictionary<string,CoopPingVisual> visuals=new();
    private static readonly HashSet<string> visibleIds=new();
    private static readonly List<string> remove=new();
    private static string world,client;private static Uri room;private static bool host,enabled;
    private static float next,nextWarning;private static Task<string> exchange;private static CancellationTokenSource cancel;
    private static ConfigEntry<KeyCode> key;private static ConfigEntry<float> volume;
    private static AudioClip bell;private static AudioSource audio;private static GameObject audioObject;
    private static bool audioAttempted,clickHeld,available;private static int consumedFrame=-1,inputFrame=-1;
    internal static void Initialize(ConfigFile config)
    {
        key=config.Bind("Pings","Key",KeyCode.BackQuote,"Mark cursor position / 标记鼠标位置。Alt+Left click and Ctrl+E+Left click also work; None disables the single key.");
        volume=config.Bind("Pings","Volume",.6f,"Ping sound volume, 0..1 / 标点提示音音量，0关闭。");
        var h=new Harmony("local.shadowgambit.pings");try{h.PatchAll(typeof(Mouse));h.PatchAll(typeof(CharacterInput));h.PatchAll(typeof(SelectMouse));h.PatchAll(typeof(CaptureModifier));available=true;}catch(Exception ex){h.UnpatchSelf();available=false;MoveProbePlugin.ProbeLog.LogWarning("Pings unavailable: "+ex.Message);}
    }
    internal static void Clear()
    {
        enabled=false;world=client=null;room=null;ledger.Clear();clickHeld=false;consumedFrame=inputFrame=-1;next=0;
        cancel?.Cancel();cancel?.Dispose();cancel=null;
        if(exchange!=null)_=exchange.ContinueWith(t=>{_=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
        exchange=null;
        foreach(var v in visuals.Values)try{v?.Dispose();}catch{}visuals.Clear();
        if(audio!=null)audio.Stop();
    }
    internal static void Shutdown()
    {Clear();if(audioObject!=null)UnityEngine.Object.Destroy(audioObject);if(bell!=null)UnityEngine.Object.Destroy(bell);audio=null;bell=null;audioObject=null;audioAttempted=false;}
    private static bool WorldInput=>enabled&&!TrainingSession.Suspended&&!SaveProcessManager.bProcessing&&!SaveProcessManager.bLoading&&
        !MiCutsceneHandler.bCutsceneRunning&&!StorySync.Waiting&&!UIWorldMap.bOpenStatic&&!GuestWorldMap.NativeUiOwnsCrew&&
        Camera.main!=null&&Application.isFocused&&Time.timeScale>0;
    private static bool OverUi(MiPlayerInput input)=>input?.m_mouseInput?.bOverUI==true||input?.m_mouseInput?.bOverGuardHouseUI==true||
        UnityEngine.EventSystems.EventSystem.current?.IsPointerOverGameObject()==true;
    private static bool CtrlE=>(Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl))&&Input.GetKey(KeyCode.E);
    private static bool Alt=>Input.GetKey(KeyCode.LeftAlt)||Input.GetKey(KeyCode.RightAlt);
    internal static bool ConsumesClick
    {
        get{
            if(!WorldInput)return false;
            if(clickHeld){if(!Input.GetMouseButton(0)){clickHeld=false;consumedFrame=Time.frameCount;}return true;}
            return consumedFrame==Time.frameCount;
        }
    }
    private static bool CaptureInput(MiPlayerInput input)
    {
        if(!WorldInput)return false;
        if(ConsumesClick)return true;
        bool chord=Alt||CtrlE;
        bool press=chord&&Input.GetMouseButtonDown(0);
        bool single=key!=null&&key.Value!=KeyCode.None&&Input.GetKeyDown(key.Value)&&!Alt&&!Input.GetKey(KeyCode.LeftControl)&&!Input.GetKey(KeyCode.RightControl)&&!Input.GetKey(KeyCode.LeftShift)&&!Input.GetKey(KeyCode.RightShift);
        if(OverUi(input))return false;
        if(press){clickHeld=true;consumedFrame=Time.frameCount;}
        if((press||single)&&inputFrame!=Time.frameCount){inputFrame=Time.frameCount;try{PlaceAtCursor();}catch(Exception ex){if(Time.realtimeSinceStartup>=nextWarning){nextWarning=Time.realtimeSinceStartup+10;MoveProbePlugin.ProbeLog.LogWarning("Ping display: "+ex.Message);}}}
        return press||CtrlE;
    }
    [HarmonyPatch(typeof(MiMouseInput),"update")]
    internal static class Mouse
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(MiPlayerInput __0)=>!CaptureInput(__0);
    }
    [HarmonyPatch(typeof(MiSelectMouseInput),"update")]
    internal static class SelectMouse
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix()=>!CaptureInput(SceneLookup.Find<MiPlayerInput>());
    }
    [HarmonyPatch(typeof(MiPlayerInput),"updateCharacterInput")]
    internal static class CharacterInput
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(MiPlayerInput __instance)=>!CaptureInput(__instance);
    }
    [HarmonyPatch]
    internal static class CaptureModifier
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods(){
            foreach(var name in new[]{"GetButton","GetButtonDown","GetButtonUp"})yield return AccessTools.Method(typeof(Rewired.Player),name,new[]{typeof(int)});
        }
        private static bool Prefix(int __0,ref bool __result){
            if(WorldInput&&CtrlE&&__0==(int)MiInputActions.CaptureModeToggleAutoMoveVertical){__result=false;return false;}return true;
        }
    }
    private static void PlaceAtCursor()
    {
        var cam=Camera.main;var mouse=Input.mousePosition;
        if(cam==null||mouse.x<0||mouse.y<0||mouse.x>=Screen.width||mouse.y>=Screen.height)return;
        var ray=cam.ScreenPointToRay(mouse);float nearest=float.PositiveInfinity;Vector3 point=default;
        foreach(var hit in Physics.RaycastAll(ray,2000f,~(1<<2),QueryTriggerInteraction.Ignore))
        {
            if(hit.collider==null||hit.distance>=nearest||hit.collider.name.StartsWith("batch_Camera_",StringComparison.OrdinalIgnoreCase))continue;
            nearest=hit.distance;point=hit.point;
        }
        if(!float.IsFinite(nearest))return;
        if(ledger.AddLocal(point.x,point.y,point.z,Time.realtimeSinceStartup)!=null){next=0;RefreshVisuals();}
    }
    internal static void Tick(bool hosting,Uri target,string epoch,bool ready)
    {
        if(!available||!ready||!SyncProtocol.Session(epoch)||TrainingSession.Suspended||SaveProcessManager.bLoading||SaveProcessManager.bProcessing)
        {if(world!=null)Clear();return;}
        if(epoch!=world||hosting!=host||target!=room){Clear();host=hosting;room=target;world=epoch;client=Guid.NewGuid().ToString("N");ledger.SetContext(world,client);cancel=new CancellationTokenSource();}
        enabled=true;float now=Time.realtimeSinceStartup;
        try
        {
            if(exchange?.IsCompleted==true){var result=exchange;exchange=null;ledger.Observe(PingWire.Read(result.GetAwaiter().GetResult()),now,0);}
            CaptureInput(SceneLookup.Find<MiPlayerInput>());
            RefreshVisuals();
            if(now<next)return;next=now+(ledger.All(now).Count>0?.2f:.5f);
            if(host)
            {
                string path=Path.Combine(SessionHost.DirectoryPath,"host-ping.json");Directory.CreateDirectory(SessionHost.DirectoryPath);
                File.WriteAllText(path+".new",System.Text.Json.JsonSerializer.Serialize(ledger.Capture(now)));File.Move(path+".new",path,true);
                path=Path.Combine(SessionHost.DirectoryPath,"guest-ping.json");
                if(File.Exists(path)){
                    var info=new FileInfo(path);double age=(DateTime.UtcNow-info.LastWriteTimeUtc).TotalSeconds;
                    if(info.Length<=8192&&age>=0&&age<3){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(stream);ledger.Observe(PingWire.Read(reader.ReadToEnd()),now,age);}
                }
            }
            else if(room!=null&&exchange==null)exchange=GuestMove.SelectLobby(room,System.Text.Json.JsonSerializer.Serialize(ledger.Capture(now)),cancel.Token);
        }
        catch(Exception ex){if(now>=nextWarning){nextWarning=now+10;MoveProbePlugin.ProbeLog.LogWarning("Ping sync: "+ex.GetType().Name);}}
    }
    private static void RefreshVisuals()
    {
        visibleIds.Clear();bool sound=false;
        foreach(var p in ledger.All(Time.realtimeSinceStartup))
        {
            visibleIds.Add(p.Id);
            if(!visuals.TryGetValue(p.Id,out var visual)){
                bool fromHost=p.Local?host:!host;
                visual=CoopPingVisual.Create(new Vector3(p.X,p.Y,p.Z),fromHost?new Color(.2f,.9f,1f,1):new Color(1f,.65f,.15f,1));
                visuals[p.Id]=visual;sound=true;
            }
            visual?.Tick();
        }
        remove.Clear();foreach(var pair in visuals)if(!visibleIds.Contains(pair.Key))remove.Add(pair.Key);
        foreach(var id in remove){visuals[id]?.Dispose();visuals.Remove(id);}
        if(sound)PlayBell();
    }
    private static void PlayBell()
    {
        float gain=volume?.Value??.6f;if(!float.IsFinite(gain)||gain<=0)return;
        try{
            if(!audioAttempted){audioAttempted=true;bell=ReadBell(Path.Combine(Paths.GameRootPath,"ShadowGambitCoop/media/bell_b.wav"));}
            if(bell==null)return;
            if(audio==null){audioObject=new GameObject("CoopPing.Audio");UnityEngine.Object.DontDestroyOnLoad(audioObject);audio=audioObject.AddComponent<AudioSource>();audio.playOnAwake=false;audio.spatialBlend=0;}
            audio.PlayOneShot(bell,Mathf.Clamp01(gain));
        }catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Ping bell: "+ex.Message);}
    }
    private static AudioClip ReadBell(string path)
    {
        if(new FileInfo(path).Length>1024*1024)throw new InvalidDataException("Bell too large");var bytes=File.ReadAllBytes(path);
        using var r=new BinaryReader(new MemoryStream(bytes));
        if(new string(r.ReadChars(4))!="RIFF")throw new InvalidDataException("WAV expected");r.ReadInt32();if(new string(r.ReadChars(4))!="WAVE")throw new InvalidDataException("WAV expected");
        int channels=0,rate=0,bits=0,format=0;byte[] pcm=null;
        while(r.BaseStream.Position+8<=r.BaseStream.Length){string tag=new string(r.ReadChars(4));int size=r.ReadInt32();long end=r.BaseStream.Position+size;if(size<0||end>r.BaseStream.Length)throw new InvalidDataException("WAV chunk");
            if(tag=="fmt "){if(size<16)throw new InvalidDataException("WAV format");format=r.ReadUInt16();channels=r.ReadUInt16();rate=r.ReadInt32();r.ReadInt32();r.ReadUInt16();bits=r.ReadUInt16();}
            else if(tag=="data")pcm=r.ReadBytes(size);r.BaseStream.Position=end+(size&1);
        }
        if(format!=1||bits!=16||channels<1||channels>2||rate<8000||rate>96000||pcm==null||pcm.Length==0||pcm.Length%(channels*2)!=0)throw new InvalidDataException("PCM16 bell required");
        var samples=new float[pcm.Length/2];for(int i=0;i<samples.Length;i++)samples[i]=(short)(pcm[i*2]|pcm[i*2+1]<<8)/32768f;
        var clip=AudioClip.Create("CoopPing.Bell",samples.Length/channels,channels,rate,false);
        try{if(!clip.SetData(samples,0))throw new InvalidDataException("Bell upload failed");return clip;}catch{UnityEngine.Object.Destroy(clip);throw;}
    }
}
