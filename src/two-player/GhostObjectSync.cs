using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using HarmonyLib;

// GhostPlace owns both a possessable and a character. Keep its native
// callbacks/lure state, with persistent host results independent of cast cues.
internal sealed class GhostObjectSync
{
    private sealed class Stamp{internal IntPtr Pointer;internal int Generation;internal bool Out;}
    private sealed class Replica{internal IntPtr Pointer;internal int Generation;internal string State;internal Vector3 Goal;internal float CorrectAt;}
    private readonly Dictionary<string,Stamp> stamps=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Replica> replicas=new(StringComparer.Ordinal);
    private readonly Dictionary<IntPtr,MiPossessable> known=new();
    private readonly Dictionary<IntPtr,float> castWait=new();
    private float nextApply,nextError;
    private static readonly HashSet<IntPtr> confirmedOnly=new();
    private static readonly Dictionary<string,(IntPtr Ghost,IntPtr Caster)> followerOwners=new(StringComparer.Ordinal);
    [ThreadStatic] private static bool applyingFollowers;
    private static string Code=>InteractionProtocol.Code("GhostPlace");
    internal void Clear(){stamps.Clear();replicas.Clear();known.Clear();castWait.Clear();confirmedOnly.Clear();followerOwners.Clear();nextApply=nextError=0;}
    private static SkillPlacePossessableGhost Ability(MiCharacter actor)=>actor?.controller?.getSkill(Skill.SkillType.GhostPlace)?.TryCast<SkillPlacePossessableGhost>();
    private MiPossessable Item(string source,MiCharacter actor,SkillPlacePossessableGhost skill)
    {
        if(skill.m_possessablePlaced!=null)return known[skill.Pointer]=skill.m_possessablePlaced;
        if(skill.m_possessablePrefab!=null&&actor.inventory!=null&&actor.inventory.tryGetPossessable(skill.m_possessablePrefab.m_eType,out var item))return known[skill.Pointer]=item;
        return known.TryGetValue(skill.Pointer,out var previous)?previous:null;
    }
    private static bool Casting(MiCharacter actor,SkillPlacePossessableGhost skill)=>actor?.controller?.skillCommandProcess?.m_skill?.eSkillType==Skill.SkillType.GhostPlace||skill.m_coroPlace!=null&&!skill.m_coroPlace.bFinished;
    private static string FollowerIds(SkillBeingACuteGhost cute)
    {
        var followers=new List<string>();var unique=new HashSet<string>(StringComparer.Ordinal);
        if(cute?.m_liFollower!=null)for(int i=0;i<cute.m_liFollower.Count;i++)
        {
            string id=Melee.GuardId(cute.m_liFollower[i]);
            if(!SyncProtocol.GuardId(id)||!unique.Add(id))throw new InvalidOperationException("Invalid ghost follower identity");followers.Add(id);
        }
        followers.Sort(StringComparer.Ordinal);return followers.Count==0?"-":string.Join(",",followers);
    }
    private static void Bind(string source,int generation,MiPossessable item,MiCharacter ghost,bool controlled)
    {
        if(generation<=0||item==null)return;
        // Preserve the native component type: a hook retrieving the item must
        // not resolve to its character component, or vice versa. Controlled
        // ghosts keep C1 for the character and the generated item slot below.
        if(controlled||ghost==null)SkillObjectRegistry.Active.Remove(source,Code);
        else SkillObjectRegistry.Active.Bind(source,Code,generation,ghost);
        SkillObjectRegistry.Active.Bind(source,Code,generation,item,1);
    }
    internal List<string[]> Capture(IReadOnlyList<MiCharacter> crew)
    {
        var rows=new List<string[]>();
        foreach(var actor in crew)try
        {
            var skill=Ability(actor);if(skill==null||skill.m_possessablePrefab==null)continue;
            string source=GuestMove.Identity(actor);var item=Item(source,actor,skill);var ghost=skill.m_charGhost;
            string state=item==null||ghost==null?"N":((int)skill.eState).ToString(CultureInfo.InvariantCulture);
            bool outside=state!="N"&&state!="0",controlled=outside&&ControlledCrew.Active.Find(ghost)!=null;
            IntPtr pointer=ghost!=null?ghost.Pointer:IntPtr.Zero;
            if(!stamps.TryGetValue(source,out var stamp))stamps.Add(source,stamp=new());
            if(stamp.Pointer!=pointer||outside&&!stamp.Out)stamp.Generation=checked(stamp.Generation+1);
            stamp.Pointer=pointer;stamp.Out=outside;
            if(state!="N"&&stamp.Generation==0)stamp.Generation=1;
            if(state=="N"){SkillObjectRegistry.Active.Remove(source,Code);SkillObjectRegistry.Active.Remove(source,Code,1);}
            else Bind(source,stamp.Generation,item,ghost,controlled);
            var cute=skill.m_skillBeingACuteGhost_ghost;
            var casterCute=skill.m_skillBeingACuteGhost_char;
            string lure=InteractionSync.EntityIdentity(cute?.charLureTarget??casterCute?.charLureTarget);if(lure==null)continue;
            Vector3 position=ghost!=null?ghost.transform.position:default;
            var target=ghost?.controller?.skillCommandProcess?.m_target??ghost?.controller?.skillCommand?.m_target;
            Vector3 goal=state=="3"?actor.transform.position:target!=null?target.v3Target:position;
            float yaw=ghost!=null?ghost.transform.eulerAngles.y:0;
            var row=string.Format(CultureInfo.InvariantCulture,"{0} {1} {2} {3} {4:0.###} {5:0.###} {6:0.###} {7:0.###} {8:0.###} {9:0.###} {10:0.###} {11} {12} {13} {14} {15} {16} {17} {18}",
                source,stamp.Generation,state,state=="N"?"-":controlled?"P":"A",position.x,position.y,position.z,yaw,goal.x,goal.y,goal.z,lure,
                cute==null?0:(int)cute.m_eState,FollowerIds(cute),Casting(actor,skill)?1:0,Math.Max(0,ghost?.health?.iHealth??0),ghost?.health?.eHealthState.ToString()??"Alive",
                casterCute==null?0:(int)casterCute.m_eState,FollowerIds(casterCute)).Split(' ');
            if(!GhostProtocol.Row(row))throw new InvalidOperationException("Invalid native ghost result");rows.Add(row);
        }
        catch(Exception ex){Report(ex);}
        return rows;
    }
    private static void Stop(MiCharacter ghost)
    {
        if(ghost?.controller==null)return;
        if(ghost.controller.skillCommand!=null)ghost.controller.skillCommand=null;
        ghost.controller.clearActiveSkillCommand();
    }
    private static void Return(SkillPlacePossessableGhost skill,MiPossessable item,MiCharacter ghost)
    {
        Stop(ghost);
        // This is the native arrival callback, not another cast. It returns
        // the item, stops loop audio, restores UI and the caster's visibility.
        skill.onMoveToPlayerExecute(SkillCommandCallback.CallbackEventType.OnExecute,
            new SkillPlacePossessableGhost.CallbackDataPossessableGhost{m_possessable=item});
    }
    private static void Followers(SkillBeingACuteGhost cute,string token)
    {
        if(cute?.m_liFollower==null)return;
        var wanted=new HashSet<string>(token=="-"?Array.Empty<string>():token.Split(','),StringComparer.Ordinal);
        var resolved=new Dictionary<string,MiCharacter>(StringComparer.Ordinal);
        foreach(string id in wanted)
        {
            var actor=InteractionSync.Resolve("e"+id)?.TryCast<MiCharacter>();
            // Missing reinforcements may not have spawned locally yet. Never
            // clear the existing whole list from a partially resolved result.
            if(actor==null)return;resolved.Add(id,actor);
        }
        var present=new HashSet<string>(StringComparer.Ordinal);
        var old=new List<MiCharacter>();for(int i=0;i<cute.m_liFollower.Count;i++)old.Add(cute.m_liFollower[i]);
        bool previous=applyingFollowers;applyingFollowers=true;
        try
        {
            foreach(var actor in old)
            {
                string id=Melee.GuardId(actor);
                if(id==null||!wanted.Contains(id)||actor!=resolved[id])cute.removeFollower(actor);else present.Add(id);
            }
            foreach(var pair in resolved)if(!present.Contains(pair.Key))cute.addFollower(pair.Value);
        }
        finally{applyingFollowers=previous;}
    }
    private static void CuteState(SkillBeingACuteGhost cute,string state)
    {
        if(cute==null||(int)cute.m_eState==int.Parse(state,CultureInfo.InvariantCulture))return;
        if(state=="0")cute.onMove();else if(state=="1")cute.setStateIdle();else cute.setStatePerforming();
    }
    private static void ApplyCute(SkillPlacePossessableGhost skill,string[] row)
    {
        var ghost=skill.m_skillBeingACuteGhost_ghost;var caster=skill.m_skillBeingACuteGhost_char;
        var pair=(Ghost:ghost!=null?ghost.Pointer:IntPtr.Zero,Caster:caster!=null?caster.Pointer:IntPtr.Zero);
        if(!followerOwners.TryGetValue(row[0],out var old)||old!=pair)
        {
            followerOwners[row[0]]=pair;confirmedOnly.Clear();
            foreach(var owner in followerOwners.Values)
            {if(owner.Ghost!=IntPtr.Zero)confirmedOnly.Add(owner.Ghost);if(owner.Caster!=IntPtr.Zero)confirmedOnly.Add(owner.Caster);}
        }
        MiCharacter lure=null;
        bool ready=row[11]=="-"||(lure=InteractionSync.Resolve(row[11])?.TryCast<MiCharacter>())!=null;
        if(ready&&(ghost?.charLureTarget!=lure||caster?.charLureTarget!=lure))skill.setLureTargetOnCuteGhostSkills(lure);
        CuteState(ghost,row[12]);CuteState(caster,row[17]);
        Followers(ghost,row[13]);Followers(caster,row[18]);
    }
    internal void Apply(string[][] rows,Action<MiCharacter,int,string> health)
    {
        if(rows==null||Time.realtimeSinceStartup<nextApply)return;nextApply=Time.realtimeSinceStartup+.1f;
        foreach(var row in rows)try
        {
            string source=row[0];var actor=MoveProbe.CrewById(source);var skill=Ability(actor);
            if(skill==null||actor.inventory==null||skill.m_possessablePrefab==null||row[14]=="1")continue;
            if(Casting(actor,skill))
            {
                if(!castWait.TryGetValue(skill.Pointer,out float started))castWait[skill.Pointer]=started=Time.realtimeSinceStartup;
                if(Time.realtimeSinceStartup-started<3f)continue;
                if(skill.m_coroPlace!=null&&!skill.m_coroPlace.bFinished)
                {var pending=skill.m_coroPlace;pending.stop(ref pending);skill.m_coroPlace=pending;}
            }
            else castWait.Remove(skill.Pointer);
            var item=Item(source,actor,skill);var ghost=skill.m_charGhost;
            if(row[2]=="N")
            {
                SkillObjectRegistry.Active.Remove(source,Code);
                SkillObjectRegistry.Active.Remove(source,Code,1);
                if(item!=null&&ghost!=null&&(skill.eState!=SkillPlacePossessableGhost.State.AtPlayer||!actor.inventory.bHasPossessable(item)))Return(skill,item,ghost);
                ApplyCute(skill,row);
                replicas.Remove(source);continue;
            }
            if(item==null||ghost==null)
            {
                item=skill.spawnPossessable(actor);ghost=skill.m_charGhost;
                if(item==null||ghost==null)continue;known[skill.Pointer]=item;
            }
            int generation=int.Parse(row[1],CultureInfo.InvariantCulture);
            bool controlled=row[3]=="P";
            Bind(source,generation,item,ghost,controlled);
            if(!replicas.TryGetValue(source,out var replica))replicas.Add(source,replica=new());
            bool fresh=replica.Pointer!=ghost.Pointer||replica.Generation!=generation;
            var position=CrewSync.ReadPosition(row,4);var goal=CrewSync.ReadPosition(row,8);
            var state=(SkillPlacePossessableGhost.State)int.Parse(row[2],CultureInfo.InvariantCulture);
            if(state==SkillPlacePossessableGhost.State.AtPlayer)
            {
                if(skill.eState!=state||!actor.inventory.bHasPossessable(item))Return(skill,item,ghost);
            }
            else
            {
                bool placing=!item.gameObject.activeInHierarchy||actor.inventory.bHasPossessable(item)||!skill.m_bPossessablePlaced;
                if(placing)
                {
                    var options=new MiCharacterInventory.ThrowOptions{m_v3Position=position,m_qRotation=Quaternion.Euler(0,float.Parse(row[7],CultureInfo.InvariantCulture),0),m_bMoveToNavMesh=false,m_bTransfer=false};
                    if(actor.inventory.bHasPossessable(item))actor.inventory.throwPossessable(item,options);else actor.inventory.unparentAndActivate(item,options);
                    skill.m_possessablePlaced=item;skill.m_bPossessablePlaced=true;skill.onPlace(item);
                }
                if(!controlled)
                {
                    bool changed=fresh||placing||replica.State!=row[2]||state==SkillPlacePossessableGhost.State.MovingToTarget&&Vector3.Distance(replica.Goal,goal)>.08f;
                    float distance=Vector3.Distance(ghost.transform.position,position);
                    if(fresh||placing||distance>1f&&Time.realtimeSinceStartup>=replica.CorrectAt)
                    {ghost.warp(position,Quaternion.Euler(0,float.Parse(row[7],CultureInfo.InvariantCulture),0));replica.CorrectAt=Time.realtimeSinceStartup+.4f;}
                    if(state==SkillPlacePossessableGhost.State.AtTarget)
                    {
                        if(changed||skill.eState!=state)
                        {
                            Stop(ghost);ghost.warp(position,Quaternion.Euler(0,float.Parse(row[7],CultureInfo.InvariantCulture),0));
                            skill.onMoveToTargetExecute(SkillCommandCallback.CallbackEventType.OnExecute,null);
                        }
                    }
                    else if(changed||skill.eState!=state)
                    {
                        if(state==SkillPlacePossessableGhost.State.MovingToPlayer)skill.moveToPlayer(ghost,item);
                        else skill.moveToTarget(ghost,goal);
                    }
                }
                skill.updatePlacedState(state,false);
            }
            ApplyCute(skill,row);
            if(!controlled&&ghost.health!=null)health?.Invoke(ghost,int.Parse(row[15],CultureInfo.InvariantCulture),row[16]);
            replica.Pointer=ghost.Pointer;replica.Generation=generation;replica.State=row[2];replica.Goal=goal;
        }
        catch(Exception ex){Report(ex);}
    }
    private static bool AllowFollowers(SkillBeingACuteGhost skill)=>applyingFollowers||NativeInput.Probe?.NativeGuest!=true||skill==null||!confirmedOnly.Contains(skill.Pointer);
    [HarmonyPatch(typeof(SkillBeingACuteGhost),"addFollower")]
    internal static class AddFollower{private static bool Prefix(SkillBeingACuteGhost __instance)=>AllowFollowers(__instance);}
    [HarmonyPatch(typeof(SkillBeingACuteGhost),"removeFollower")]
    internal static class RemoveFollower{private static bool Prefix(SkillBeingACuteGhost __instance)=>AllowFollowers(__instance);}
    private void Report(Exception ex)
    {
        if(Time.realtimeSinceStartup<nextError)return;nextError=Time.realtimeSinceStartup+2f;
        MoveProbePlugin.ProbeLog.LogWarning("Ghost results: "+ex.GetType().Name+": "+ex.Message);
    }
}
