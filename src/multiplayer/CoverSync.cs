using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// Persistent host results, not another attempt to cast against guest-side AI.
// Only the paired sailors' native ThrowBush object lists are touched.
internal sealed class CoverSync
{
    private readonly Dictionary<string,CreateCoverThrowBush> replicas=new();
    private readonly Dictionary<IntPtr,SkillCreateCoverThrowBush> abilities=new();
    private readonly Dictionary<IntPtr,float> castWait=new();
    private float nextApply, nextError;
    private string lastWire;
    private IntPtr boundA,boundB;
    private void Bind(MiCharacter a,MiCharacter b)
    {
        var pa=a?.Pointer??IntPtr.Zero;var pb=b?.Pointer??IntPtr.Zero;
        if(pa==boundA && pb==boundB)return;
        boundA=pa;boundB=pb;replicas.Clear();abilities.Clear();castWait.Clear();lastWire=null;
    }
    private static SkillCreateCoverThrowBush Ability(MiCharacter actor) =>
        actor?.controller?.getSkill(Skill.SkillType.ThrowBush)?.TryCast<SkillCreateCoverThrowBush>();
    private SkillCreateCoverThrowBush CachedAbility(MiCharacter actor)
    {
        if(actor==null)return null;
        var skill=Ability(actor);
        if(skill!=null) abilities[actor.Pointer]=skill;
        else abilities.TryGetValue(actor.Pointer,out skill);
        return skill;
    }
    private static List<CreateCoverThrowBush> Objects(SkillCreateCoverThrowBush skill)
    {
        var result=new List<CreateCoverThrowBush>();
        var list=skill?.m_lGameObjects;
        if(list!=null) for(int i=0;i<list.Count;i++)
        {
            var bush=list[i]?.m_gameObject?.GetComponentInChildren<CreateCoverThrowBush>(true);
            if(bush!=null) result.Add(bush);
        }
        return result;
    }
    private static bool Grown(CreateCoverThrowBush b) => b!=null && b.gameObject.activeInHierarchy && b.m_goBush!=null && b.m_goBush.activeSelf;
    internal string Write(MiCharacter a,MiCharacter b)
    {
        try
        {
            Bind(a,b);
            string flags="",records="";int count=0;
            foreach(var pair in new[]{(a,"A"),(b,"B")})
            {
                var actor=pair.Item1;
                var skill=CachedAbility(actor);
                if(actor==null || actor.controller==null && skill==null){flags+="x";continue;}
                var objects=Objects(skill).FindAll(Grown);
                if(objects.Count>4){flags+="x";continue;}
                flags+="1";
                foreach(var bush in objects)
                {
                    var pos=bush.transform.position;
                    records+=string.Format(CultureInfo.InvariantCulture," {0} {1} {2:0.###} {3:0.###} {4:0.###} {5:0.##}",
                        pair.Item2,bush.GetInstanceID(),pos.x,pos.y,pos.z,Math.Round(bush.transform.eulerAngles.y,2)%360);
                    count++;
                }
            }
            string wire=" V "+flags+" "+count+records;
            if(wire!=lastWire){MoveProbePlugin.ProbeLog.LogInfo("Host bush results"+wire);lastWire=wire;}
            return wire;
        }
        catch(Exception ex){Report(ex);return " V xx 0";}
    }
    internal void Apply(string[] data,MiCharacter a,MiCharacter b)
    {
        if(data==null || Time.realtimeSinceStartup<nextApply)return;
        nextApply=Time.realtimeSinceStartup+0.1f;
        try
        {
            Bind(a,b);
            for(int owner=0;owner<2;owner++)
            {
                if(data[0][owner]!='1')continue; // unknown is never an empty roster
                var actor=owner==0?a:b;var label=owner==0?"A":"B";
                var ability=CachedAbility(actor);
                if(ability==null)continue;
                var desired=new Dictionary<string,(Vector3 point,float yaw)>();
                for(int i=2;i<data.Length;i+=6) if(data[i]==label)
                    desired[label+":"+data[i+1]]=(CrewSync.ReadPosition(data,i+2),float.Parse(data[i+5],CultureInfo.InvariantCulture));
                // Reconcile only settled results. A native throw may still be
                // holding a projectile; do not delete that future result early.
                bool casting=actor?.controller?.skillCommandProcess?.m_skill?.eSkillType==Skill.SkillType.ThrowBush ||
                    CoroWait.bIsRunning(ability.m_cwThrow) || ability.flying!=null && ability.flying.bIsActive();
                if(casting)
                {
                    if(!castWait.TryGetValue(actor.Pointer,out var started))castWait[actor.Pointer]=started=Time.realtimeSinceStartup;
                    if(Time.realtimeSinceStartup-started<3f)continue;
                }
                else if(actor!=null)castWait.Remove(actor.Pointer);
                var stale=new List<string>();
                foreach(var pair in replicas) if(pair.Key.StartsWith(label+":",StringComparison.Ordinal) && !desired.ContainsKey(pair.Key))
                { Remove(pair.Value); stale.Add(pair.Key); }
                foreach(var key in stale)replicas.Remove(key);
                var used=new HashSet<IntPtr>();
                foreach(var d in desired)
                    if(replicas.TryGetValue(d.Key,out var known) && known!=null)used.Add(known.Pointer);
                foreach(var d in desired)
                {
                    if(!replicas.TryGetValue(d.Key,out var bush) || bush==null)
                    {
                        // Adopt native replay output instead of duplicating it.
                        foreach(var candidate in Objects(ability))
                            if(!used.Contains(candidate.Pointer) && Grown(candidate) && Vector3.Distance(candidate.transform.position,d.Value.point)<0.75f)
                            {bush=candidate;break;}
                        if(bush==null)
                            bush=ability.getNextGameObject()?.m_gameObject?.GetComponentInChildren<CreateCoverThrowBush>(true);
                        if(bush==null || used.Contains(bush.Pointer))continue;
                        replicas[d.Key]=bush;used.Add(bush.Pointer);
                        MoveProbePlugin.ProbeLog.LogInfo("Guest bush result "+d.Key+" at "+CrewSync.Position(d.Value.point));
                    }
                    Show(bush,d.Value.point,d.Value.yaw);
                }
                // Remove any locally-grown extra that the complete host roster
                // does not contain, including delayed/repeated local casts.
                foreach(var extra in Objects(ability)) if(Grown(extra) && !used.Contains(extra.Pointer))Remove(extra);
            }
        }
        catch(Exception ex){Report(ex);}
    }
    private void Report(Exception ex)
    {
        if(Time.realtimeSinceStartup<nextError)return;nextError=Time.realtimeSinceStartup+3f;
        MoveProbePlugin.ProbeLog.LogWarning("Bush result sync: "+ex.GetType().Name+" "+ex.Message);
    }
    private static void StopGrowth(CreateCoverThrowBush bush)
    {
        var grow=bush.m_coroGrow;grow?.stop(ref grow);bush.m_coroGrow=grow;
        var disable=bush.m_coroDisable;disable?.stop(ref disable);bush.m_coroDisable=disable;
    }
    private static void Show(CreateCoverThrowBush bush,Vector3 point,float yaw)
    {
        bool changed=!Grown(bush) || Vector3.Distance(bush.transform.position,point)>0.025f || Math.Abs(Mathf.DeltaAngle(bush.transform.eulerAngles.y,yaw))>0.5f;
        StopGrowth(bush);
        if(!changed)return;
        var manager=VCRendererCollectionManager.instance;
        if(Grown(bush)) manager.removeMeshAtRuntime(bush.m_vcRenderMeshOccluder);
        bush.transform.position=point;bush.transform.rotation=Quaternion.Euler(0,yaw,0);
        bush.gameObject.SetActive(true);
        // Same successful-growth result as native coroGrow: activate the real
        // hiding object and register its view-cone occluder. No NPC-range re-roll.
        bush.m_bVisible=false;
        bush.m_goBush.SetActive(true);
        manager.addOccluderAtRuntime(bush.m_vcRenderMeshOccluder,true);
    }
    private static void Remove(CreateCoverThrowBush bush)
    {
        if(bush==null)return;
        StopGrowth(bush);
        if(Grown(bush))
        {
            VCRendererCollectionManager.instance.removeMeshAtRuntime(bush.m_vcRenderMeshOccluder);
            bush.m_goBush.SetActive(false);
            MoveProbePlugin.ProbeLog.LogInfo("Guest bush result removed");
        }
        bush.m_bVisible=false;
    }
}
