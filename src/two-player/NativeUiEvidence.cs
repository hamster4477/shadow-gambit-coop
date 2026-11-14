using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using UnityEngine;

// Read-only evidence for the UnityPlayer+6429b0 failure. Do not repair native
// pointers or invoke recursive component queries on a possibly damaged tree.
internal static class NativeUiEvidence
{
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool ReadProcessMemory(IntPtr process,IntPtr address,byte[] bytes,UIntPtr size,out UIntPtr read);
    private static readonly IntPtr Self=new IntPtr(-1);
    private static int sequence,lastRetireFrame=-1;
    private static bool initialized,componentsInitialized;
    private static readonly HashSet<long> owners=new();
    private static readonly Dictionary<long,string> ownerPaths=new();
    private static readonly HashSet<IntPtr> visitedTransforms=new();
    private static float sampleAt;private static int samplesLeft;
    private static void CaptureIconChildren(UIBase ui)
    {
        var root=ui.transform;if(root==null)return;
        var pending=new Stack<(Transform node,string path)>();pending.Push((root,"UIWorldInfoBatchIcon"));
        while(pending.Count>0&&visitedTransforms.Count<16384){
            var entry=pending.Pop();var t=entry.node;
            if(t==null||!visitedTransforms.Add(t.Pointer))continue;
            var go=t.gameObject;if(go==null)continue;
            long address=go.m_CachedPtr.ToInt64();
            if(address!=0){owners.Add(address);ownerPaths[address]=entry.path;}
            int count=Math.Min(t.childCount,256);
            for(int i=count-1;i>=0;i--){var child=t.GetChild(i);if(child!=null)pending.Push((child,entry.path+"/"+i+":"+child.name));}
        }
    }
    internal static void Tick()
    {
        if(samplesLeft==0||Time.realtimeSinceStartup<sampleAt)return;
        samplesLeft--;sampleAt=Time.realtimeSinceStartup+2;
        CaptureComponents("after-retire-"+samplesLeft);
    }
    internal static void CaptureComponents(string stage)
    {
        try{
            var rows=new List<object>();int invalid=0;
            foreach(long address in owners){
                var head=Read(new IntPtr(address),0x60);
                long count=head==null?0:BitConverter.ToInt64(head,0x40);
                long list=head==null?0:BitConverter.ToInt64(head,0x30);
                var components=new List<object>();
                var table=count>0&&count<=128?Read(new IntPtr(list),checked((int)count*16)):null;
                if(table!=null)for(int i=0;i<count;i++){
                    long ptr=BitConverter.ToInt64(table,i*16+8);
                    var component=Read(new IntPtr(ptr),0x40);
                    bool bad=ptr!=0&&(component==null||BitConverter.ToInt64(component,0)==0);
                    if(bad)invalid++;
                    components.Add(new{slot=i,native=ptr.ToString("X"),bad,header=component==null?null:Convert.ToHexString(component)});
                }
                rows.Add(new{native=address.ToString("X"),path=ownerPaths.TryGetValue(address,out var pathLabel)?pathLabel:null,header=head==null?null:Convert.ToHexString(head),count,list=list.ToString("X"),components});
            }
            string path=Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","scene-components.jsonl");
            if(!componentsInitialized){File.WriteAllText(path,"");File.Delete(path+".previous");componentsInitialized=true;}
            if(new FileInfo(path).Length>16*1024*1024)File.Move(path,path+".previous",true);
            File.AppendAllText(path,JsonSerializer.Serialize(new{utc=DateTime.UtcNow,stage,invalid,rows})+Environment.NewLine);
            MoveProbePlugin.ProbeLog.LogInfo("Native component evidence stage="+stage+" owners="+rows.Count+" invalid="+invalid);
        }catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Native component evidence unavailable: "+ex.GetType().Name);}
    }
    internal static byte[] Read(IntPtr address,int length)
    {
        if(address==IntPtr.Zero)return null;
        var data=new byte[length];
        return ReadProcessMemory(Self,address,data,(UIntPtr)length,out var read)&&read.ToUInt64()==(ulong)length?data:null;
    }
    internal static void Capture(string stage)
    {
        try
        {
            if(!File.Exists(Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","trace-native-ui.flag")))return;
            if(stage=="scene-retire")
            {if(lastRetireFrame==Time.frameCount)return;lastRetireFrame=Time.frameCount;}
            if(!MiVersion.Version.Display.StartsWith("1.2.133",StringComparison.Ordinal))return;
            var registry=UIBase.s_hsAllUIBase;if(registry==null)return;
            owners.Clear();ownerPaths.Clear();visitedTransforms.Clear();var rows=new List<object>();int exactSignature=0;
            foreach(var ui in registry)
            {
                // ReferenceEquals avoids Unity's native equality/liveness query.
                if(ReferenceEquals(ui,null))continue;
                string type=ui.GetIl2CppType().FullName;
                if(type=="UIWorldInfoBatchIcon")CaptureIconChildren(ui);
                var native=ui.m_CachedPtr;
                var bytes=Read(native,0x110);
                if(bytes!=null){long owner=BitConverter.ToInt64(bytes,0x30);if(owner!=0)owners.Add(owner);}
                long script=bytes==null?0:BitConverter.ToInt64(bytes,0xf0);
                var scriptBytes=Read(new IntPtr(script),0x48);
                if(script==0x4900000001L)exactSignature++;
                rows.Add(new {managed=ui.Pointer.ToInt64().ToString("X"),native=native.ToInt64().ToString("X"),
                    type,header=bytes==null?null:Convert.ToHexString(bytes),
                    script=script.ToString("X"),scriptHeader=scriptBytes==null?null:Convert.ToHexString(scriptBytes)});
            }
            CaptureComponents(stage);
            if(stage=="scene-retire"){sampleAt=Time.realtimeSinceStartup+.4f;samplesLeft=2;}
            string path=Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","scene-ui-evidence.jsonl");
            if(!initialized){File.WriteAllText(path,"");File.Delete(path+".previous");initialized=true;}
            // Bound on-disk evidence to two 8 MB segments. No game memory writes.
            if(new FileInfo(path).Length>8*1024*1024)File.Move(path,path+".previous",true);
            File.AppendAllText(path,JsonSerializer.Serialize(new{sequence=++sequence,utc=DateTime.UtcNow,
                stage,frame=Time.frameCount,exactSignature,rows})+Environment.NewLine);
            MoveProbePlugin.ProbeLog.LogInfo("Native UI evidence stage="+stage+" objects="+rows.Count+" exactCrashSignature="+exactSignature);
        }
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Native UI evidence unavailable: "+ex.GetType().Name);}
    }
}
