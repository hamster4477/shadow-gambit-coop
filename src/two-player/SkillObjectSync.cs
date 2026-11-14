using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// Reconcile native persistent results rather than issuing a second cast. This
// repairs missed casts even after the eight-second interaction cue expires.
internal sealed class SkillObjectSync
{
    private sealed class Stamp {internal IntPtr Object;internal int Generation;}
    private readonly Dictionary<string,Stamp> stamps=new(StringComparer.Ordinal);
    private readonly Dictionary<IntPtr,float> castWait=new();
    private readonly BaitObjectSync bait=new();
    private float nextApply,nextError;
    internal void Clear(){stamps.Clear();castWait.Clear();bait.Clear();nextApply=nextError=0;SkillObjectRegistry.Active.Clear();}
    private static SkillTeleportPlaceMark MarkerSkill(MiCharacter actor)=>
        actor?.controller?.getSkill(Skill.SkillType.TeleportPlaceMark)?.TryCast<SkillTeleportPlaceMark>();
    private static bool Active(TeleportBackMarker marker)=>marker!=null&&marker.m_bMarkerActive;
    private static bool Casting(MiCharacter actor,SkillTeleportPlaceMark skill)=>
        actor?.controller?.skillCommandProcess?.m_skill?.eSkillType==Skill.SkillType.TeleportPlaceMark ||
        CoroWait.bIsRunning(skill.m_cwThrow) ||
        skill.flying!=null&&skill.flying.bIsActive();
    internal List<string[]> Capture(IEnumerable<MiCharacter> actors)
    {
        var result=new List<string[]>();
        var crew=new List<MiCharacter>(actors);
        foreach(var actor in crew)
        {
            try
            {
                var skill=MarkerSkill(actor);if(skill==null)continue;
                string id=GuestMove.Identity(actor);if(!SyncProtocol.Identity(id))continue;
                var marker=skill.m_markerActive;bool exists=Active(marker);
                IntPtr pointer=exists?marker.Pointer:IntPtr.Zero;
                if(!stamps.TryGetValue(id,out var stamp))stamps.Add(id,stamp=new Stamp());
                if(stamp.Object!=pointer){stamp.Object=pointer;stamp.Generation=checked(stamp.Generation+1);}
                string attached=exists?InteractionSync.EntityIdentity(marker.m_charAttachedTo):"-";
                string target=exists?InteractionSync.EntityIdentity(marker.m_charActiveTarget):"-";
                if(attached==null||target==null)continue;
                Vector3 p=exists?marker.transform.position:default;
                int flags=(exists&&marker.m_bDisabledInCannonOrFlying?1:0)|(exists&&marker.m_bDisabledEnteredDoor?2:0)|(Casting(actor,skill)?4:0);
                string text=string.Format(CultureInfo.InvariantCulture,"{0} TM {1} {2} {3} {4} {5:0.###} {6:0.###} {7:0.###} {8}",
                    id,stamp.Generation,exists?"A":"N",attached,target,p.x,p.y,p.z,flags);
                var row=text.Split(' ');if(!SkillObjectProtocol.Row(row))throw new InvalidOperationException("Invalid native marker state");
                if(exists)SkillObjectRegistry.Active.Bind(id,"TM",stamp.Generation,marker);
                else SkillObjectRegistry.Active.Remove(id,"TM");
                result.Add(row);
            }
            catch(Exception ex){Report(ex);} // Unknown result is omitted, never reported as absent.
        }
        try{result.AddRange(bait.Capture(crew));}catch(Exception ex){Report(ex);}
        return result;
    }
    internal void Apply(string[][] rows,IEnumerable<MiCharacter> roster=null)
    {
        if(rows==null||Time.realtimeSinceStartup<nextApply)return;
        nextApply=Time.realtimeSinceStartup+0.1f;
        var crew=new List<MiCharacter>();var found=new HashSet<IntPtr>();
        if(roster!=null)foreach(var actor in roster)if(actor!=null&&found.Add(actor.Pointer))crew.Add(actor);
        foreach(var row in rows){var actor=MoveProbe.CrewById(row[0]);if(actor!=null&&found.Add(actor.Pointer))crew.Add(actor);}
        foreach(var row in rows)
        {
            try
            {
                if(row[1]!="TM"){bait.Apply(row,crew);continue;}
                var actor=MoveProbe.CrewById(row[0]);var skill=MarkerSkill(actor);
                SkillObjectRegistry.Active.Remove(row[0],row[1]);
                if(skill==null)continue;
                int flags=int.Parse(row[9],CultureInfo.InvariantCulture);
                // The host's current cast has not landed. Keep existing local
                // objects until a settled result explicitly describes them.
                if((flags&4)!=0)continue;
                if(Casting(actor,skill))
                {
                    if(!castWait.TryGetValue(actor.Pointer,out float started))castWait[actor.Pointer]=started=Time.realtimeSinceStartup;
                    // A slow local throw can still own the same pooled head.
                    // Synthesizing a second marker on a timeout duplicates it
                    // when the original native projectile eventually lands.
                    if(Time.realtimeSinceStartup-started<3f||row[3]=="A")continue;
                }
                else castWait.Remove(actor.Pointer);
                var marker=skill.m_markerActive;
                if(row[3]=="N")
                {
                    if(Active(marker))
                    {
                        // Use native teardown to release callbacks, visuals
                        // and range objects; hiding the GameObject leaks them.
                        marker.deleteMarkerOnReplace();
                        if(skill.m_markerStale==marker)skill.m_markerStale=null;
                    }
                    if(skill.m_markerActive==marker){skill.m_markerActive=null;skill.m_bHasMarker=false;}
                    continue;
                }
                MiCharacter attached=null,target=null;
                if(row[4]!="-")
                {attached=InteractionSync.Resolve(row[4])?.TryCast<MiCharacter>();if(attached==null)continue;}
                if(row[5]!="-")
                {target=InteractionSync.Resolve(row[5])?.TryCast<MiCharacter>();if(target==null)continue;}
                var point=CrewSync.ReadPosition(row,6);
                if(!Active(marker)||marker.m_charAttachedTo!=attached)
                {
                    skill.placeMarker(point,attached);
                    marker=skill.m_markerActive;
                    if(!Active(marker)||marker.m_charAttachedTo!=attached)continue;
                }
                // Attached marks follow their actual character; their world
                // position must not fight that character's own interpolation.
                if(attached==null&&Vector3.Distance(marker.transform.position,point)>0.08f)marker.transform.position=point;
                if(marker.m_charActiveTarget!=target)marker.setActiveTarget(target);
                marker.m_bDisabledInCannonOrFlying=(flags&1)!=0;
                marker.m_bDisabledEnteredDoor=(flags&2)!=0;
                SkillObjectRegistry.Active.Bind(row[0],row[1],int.Parse(row[2],CultureInfo.InvariantCulture),marker);
            }
            catch(Exception ex){Report(ex);}
        }
    }
    private void Report(Exception ex)
    {
        if(Time.realtimeSinceStartup<nextError)return;
        nextError=Time.realtimeSinceStartup+2f;
        MoveProbePlugin.ProbeLog.LogWarning("Persistent skill results: "+ex.GetType().Name+": "+ex.Message);
    }
}
