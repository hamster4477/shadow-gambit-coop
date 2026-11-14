using System;
using System.Diagnostics;
using System.Globalization;

// Aggregate timing only: no per-frame disk IO or scene enumeration.
internal static class CoopPerformance
{
    private static readonly long[] sums=new long[4], maxima=new long[4];
    private static readonly int[] calls=new int[4];
    private static long started, window, previous;
    private static long frameSum, frameMax;
    private static int frames, gc0, gc1, gc2;
    internal static void Restart()
    {
        started=window=previous=frameSum=frameMax=0;frames=0;
        Array.Clear(sums,0,4);Array.Clear(maxima,0,4);Array.Clear(calls,0,4);
    }
    internal readonly struct Sample : IDisposable
    {
        private readonly int slot;
        private readonly long start;
        internal Sample(int slot,long start){this.slot=slot;this.start=start;}
        public void Dispose()
        {
            if(start==0)return;
            long ticks=Stopwatch.GetTimestamp()-start;
            sums[slot]+=ticks;maxima[slot]=Math.Max(maxima[slot],ticks);calls[slot]++;
        }
    }
    internal static Sample Measure(int slot)
    {
        long now=Stopwatch.GetTimestamp();
        if(started==0){started=window=now;gc0=GC.CollectionCount(0);gc1=GC.CollectionCount(1);gc2=GC.CollectionCount(2);}
        return new Sample(slot,(now-started)/(double)Stopwatch.Frequency<300?now:0);
    }
    internal static void Frame()
    {
        long now=Stopwatch.GetTimestamp();
        if(started==0||(now-started)/(double)Stopwatch.Frequency>=300)return;
        if(previous!=0){long delta=now-previous;frameSum+=delta;frameMax=Math.Max(frameMax,delta);frames++;}
        previous=now;
        double seconds=(now-window)/(double)Stopwatch.Frequency;
        if(seconds<5)return;
        double ms=1000d/Stopwatch.Frequency;
        string group(int n)=>string.Format(CultureInfo.InvariantCulture,"{0}/{1:0.0}/{2:0.0}",calls[n],calls[n]==0?0:sums[n]*ms/calls[n],maxima[n]*ms);
        MoveProbePlugin.ProbeLog.LogInfo(string.Format(CultureInfo.InvariantCulture,
            "CoopPerf fps={0:0.0} frameMaxMs={1:0.0} update(count/avgMs/maxMs)={2} preparation={3} snapshot={4} managedMB={5:0.0} gc={6}/{7}/{8} focused={9} sceneSearch={10}",
            frameSum==0?0:frames*1000d/(frameSum*ms),frameMax*ms,group(0),group(1),group(2),GC.GetTotalMemory(false)/1048576d,
            GC.CollectionCount(0)-gc0,GC.CollectionCount(1)-gc1,GC.CollectionCount(2)-gc2,UnityEngine.Application.isFocused,group(3)));
        Array.Clear(sums,0,4);Array.Clear(maxima,0,4);Array.Clear(calls,0,4);
        frameSum=frameMax=0;frames=0;window=now;
        gc0=GC.CollectionCount(0);gc1=GC.CollectionCount(1);gc2=GC.CollectionCount(2);
    }
}
