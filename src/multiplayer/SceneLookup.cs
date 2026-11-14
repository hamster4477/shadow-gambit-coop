using System;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class SceneLookup
{
    private static int frame=-1,sceneHandle=int.MinValue,sceneCount=-1,epoch;
    private static bool loading;
    internal static void Invalidate(){epoch++;frame=-1;}
    private static void Observe()
    {
        if(frame==Time.frameCount)return;
        frame=Time.frameCount;
        int handle=SceneManager.GetActiveScene().handle,count=SceneManager.sceneCount;
        bool currentLoading=SaveProcessManager.bLoading;
        if(handle!=sceneHandle||count!=sceneCount||currentLoading!=loading)
        {epoch++;sceneHandle=handle;sceneCount=count;loading=currentLoading;CoopPerformance.Restart();}
    }
    private static class Slot<T> where T:Component
    {
        internal static readonly SceneLookupSlot<T> Cache=new();
        internal static readonly Func<T> Find=()=>{
            using var timing=CoopPerformance.Measure(3);
            return UnityEngine.Object.FindObjectOfType<T>();
        };
        internal static readonly Func<T,bool> Alive=value=>value!=null&&value.gameObject!=null&&value.gameObject.activeInHierarchy;
    }
    internal static T Find<T>() where T:Component
    {
        Observe();
        return Slot<T>.Cache.Get(epoch,Time.realtimeSinceStartup,Slot<T>.Find,Slot<T>.Alive);
    }
}
