using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

internal sealed class FootprintSync
{
    private sealed class Replica {internal Footprint Print;internal FootprintTrailBase Trail;internal int Pool;}
    private readonly Dictionary<int,Replica> replicas=new();
    private readonly HashSet<IntPtr> trails=new(),crew=new();
    private readonly HashSet<int> sent=new();
    private int cursor;
    private float nextError;
    private float nextReport;
    private static FootprintSync active;
    internal static bool HooksReady;
    [HarmonyPatch(typeof(FootprintTrailBase),"addFootprint")]
    internal static class LocalStep
    {
        private static bool Prefix(FootprintTrailBase __instance)=>active==null || __instance.TryCast<FootprintTrailNormal>()==null ||
            __instance.character==null || !active.crew.Contains(__instance.character.Pointer);
    }
    internal static void Install()
    {
        try{new Harmony("local.shadowgambit.footprints").PatchAll(typeof(LocalStep));HooksReady=true;}
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Footprint hook unavailable: "+ex);}
    }
    private void Report(Exception ex)
    {if(Time.realtimeSinceStartup<nextError)return;nextError=Time.realtimeSinceStartup+5;MoveProbePlugin.ProbeLog.LogWarning("Footprint sync: "+ex);}
    internal string Attach(string snapshot,IReadOnlyList<MiCharacter> roster)
    {
        try
        {
            var rows=new List<(int id,string row)>();var ids=new HashSet<int>();bool complete=true;
            foreach(var actor in roster)
            {
                if(actor==null)continue;
                var trail=actor.footprintTrailActive??actor.footprintTrail;
                if(trail?.TryCast<FootprintTrailNormal>()==null)continue;
                var queue=trail?.m_qFootprintsActive;if(queue==null)continue;
                string owner=GuestMove.Identity(actor);
                foreach(var item in queue)
                {
                    var fp=item?.TryCast<Footprint>();if(fp==null||!fp.gameObject.activeInHierarchy)continue;
                    float remaining=fp.fTimeDead-MiTime.time;if(remaining<=0||remaining>600)continue;
                    if(ids.Count>=FootprintProtocol.MaxActive){complete=false;break;}
                    if(!ids.Add(fp.PoolID))continue;
                    int side=0;var normal=trail.TryCast<FootprintTrailNormal>();
                    if(normal?.m_footprintRightPrefab!=null && fp.name.StartsWith(normal.m_footprintRightPrefab.name,StringComparison.Ordinal))side=1;
                    var pos=fp.transform.position;var rot=fp.transform.rotation;
                    rows.Add((fp.PoolID,string.Format(CultureInfo.InvariantCulture,
                        "{0} {1} {2} {3:0.###} {4:0.###} {5:0.###} {6:0.#####} {7:0.#####} {8:0.#####} {9:0.#####} {10:0.###} {11}",
                        fp.PoolID,owner,side,pos.x,pos.y,pos.z,rot.x,rot.y,rot.z,rot.w,Math.Max(.001f,remaining),fp.m_goFootprint?.activeSelf==true?1:0)));
                }
            }
            sent.IntersectWith(ids);
            var page=new List<string>();var selected=new HashSet<int>();
            foreach(var r in rows)if(!sent.Contains(r.id)&&page.Count<FootprintProtocol.MaxPage){page.Add(r.row);selected.Add(r.id);sent.Add(r.id);}
            for(int i=0;i<rows.Count&&page.Count<FootprintProtocol.MaxPage;i++)
            {
                var r=rows[(cursor+i)%rows.Count];if(selected.Add(r.id)){page.Add(r.row);sent.Add(r.id);}
            }
            cursor=rows.Count==0?0:(cursor+FootprintProtocol.MaxPage)%rows.Count;
            string line="F1 "+(complete?"1":"0")+" "+(ids.Count==0?"-":string.Join(",",ids))+" "+page.Count+ (page.Count==0?"":" "+string.Join(" ",page));
            if(!FootprintProtocol.Read(line,out _) || snapshot.Length+line.Length+1>SnapshotTransport.MaxBytes)
                throw new InvalidOperationException("Footprint page exceeds validated transport bounds");
            if(Time.realtimeSinceStartup>=nextReport)
            {nextReport=Time.realtimeSinceStartup+5;MoveProbePlugin.ProbeLog.LogInfo($"Host footprints active={ids.Count} page={page.Count} complete={complete}");}
            return snapshot+"\n"+line;
        }
        catch(Exception ex){Report(ex);return snapshot;}
    }
    private static void ClearTrail(FootprintTrailBase trail)
    {
        var queue=trail.m_qFootprintsActive;
        if(queue!=null)while(queue.Count>0)
        {var fp=queue.Dequeue()?.TryCast<Footprint>();if(fp?.gameObject.activeInHierarchy==true)fp.setFree();}
        var normal=trail.TryCast<FootprintTrailNormal>();if(normal!=null)normal.m_fpLatest=null;
        ScheduleExpiry(trail);
    }
    // Native MiUpdate dequeues as soon as this deadline expires; it checks
    // Count only AFTER the dequeue. Every external queue edit must update it.
    private static void ScheduleExpiry(FootprintTrailBase trail)
    {
        if(trail==null)return;
        var queue=trail.m_qFootprintsActive;
        trail.m_fKillNextFootprint=queue!=null&&queue.Count>0?queue.Peek().fTimeDead:float.MaxValue;
    }
    private static void Remove(Replica r)
    {
        var fp=r.Print;if(fp==null||fp.PoolID!=r.Pool)return; // pool reuse is never ours
        var queue=r.Trail?.m_qFootprintsActive;
        if(queue!=null)for(int n=queue.Count;n>0;n--)
        {var item=queue.Dequeue();if(item!=null&&item.Pointer!=fp.Pointer)queue.Enqueue(item);}
        var normal=r.Trail?.TryCast<FootprintTrailNormal>();
        if(normal?.m_fpLatest?.Pointer==fp.Pointer)normal.m_fpLatest=null;
        if(fp.gameObject.activeInHierarchy)fp.setFree();
        ScheduleExpiry(r.Trail);
    }
    internal void Apply(FootprintProtocol.Page page,IEnumerable<MiCharacter> actors,float age)
    {
        if(page==null||!HooksReady)return;
        try
        {
            var byId=new Dictionary<string,MiCharacter>();
            foreach(var actor in actors)if(actor!=null)byId[GuestMove.Identity(actor)]=actor;
            foreach(var row in page.Rows)if(!byId.ContainsKey(row[1]))throw new InvalidOperationException("Footprint owner outside confirmed crew");
            active=this;crew.Clear();
            foreach(var actor in byId.Values)
            {
                crew.Add(actor.Pointer);var trail=actor.footprintTrailActive??actor.footprintTrail;
                if(trail?.TryCast<FootprintTrailNormal>()!=null&&trails.Add(trail.Pointer))ClearTrail(trail);
            }
            foreach(int id in new List<int>(replicas.Keys))
                if(page.Complete&&!page.Active.Contains(id)){Remove(replicas[id]);replicas.Remove(id);}
            foreach(var row in page.Rows)
            {
                int id=int.Parse(row[0],CultureInfo.InvariantCulture);
                float Float(int i)=>float.Parse(row[i],CultureInfo.InvariantCulture);
                float remaining=Float(10)-Math.Min(.8f,Math.Max(0,age));if(remaining<=0)continue;
                var actor=byId[row[1]];var trail=actor.footprintTrailActive??actor.footprintTrail;
                if(trail==null)continue;
                if(!replicas.TryGetValue(id,out var r)||r.Print==null||r.Print.PoolID!=r.Pool||!r.Print.gameObject.activeInHierarchy)
                {
                    if(r!=null)Remove(r);
                    var point=new Vector3(Float(3),Float(4),Float(5));
                    // Native spawning owns decals, detection registration and
                    // pooling. Bypass only the local footstep producer.
                    var normal=trail.TryCast<FootprintTrailNormal>();
                    if(normal==null)continue;
                    var fp=normal.spawnFootprint((FootprintTrailBase.Side)int.Parse(row[2]),point)?.TryCast<Footprint>();
                    if(fp==null)continue;
                    r=new Replica{Print=fp,Trail=trail,Pool=fp.PoolID};replicas[id]=r;
                    var queue=trail.m_qFootprintsActive;
                    bool queued=false;if(queue!=null)foreach(var item in queue)if(item?.Pointer==fp.Pointer){queued=true;break;}
                    if(queue!=null&&!queued)queue.Enqueue(fp.Cast<IFootprint>());
                }
                r.Print.transform.position=new Vector3(Float(3),Float(4),Float(5));
                r.Print.transform.rotation=new Quaternion(Float(6),Float(7),Float(8),Float(9));
                if(r.Print.m_goFootprint!=null)r.Print.m_goFootprint.SetActive(row[11]=="1");
                r.Print.m_fTimeDead=MiTime.time+remaining;
                ScheduleExpiry(trail);
            }
            if(Time.realtimeSinceStartup>=nextReport)
            {nextReport=Time.realtimeSinceStartup+5;MoveProbePlugin.ProbeLog.LogInfo($"Guest footprints host={page.Active.Count} replicas={replicas.Count} page={page.Rows.Length}");}
        }
        catch(Exception ex){Report(ex);}
    }
    internal void Release()
    {
        if(active==this)active=null;
        foreach(var r in replicas.Values)try{Remove(r);}catch(Exception ex){Report(ex);}
        replicas.Clear();trails.Clear();crew.Clear();
    }
}
