using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using Mimimi.HUB;

// Only idle, unowned shipmates. Native host routines remain authoritative.
// Replay locomotion through native AI; never convert an idle actor to a player.
internal static class ShipRoutineSync
{
    private static readonly GuardAuthority authority=new();
    private static readonly Dictionary<string,float> nextOrder=new();
    private static readonly HashSet<string> held=new();
    private static float lastFrame;
    private static HUBLogic scene;
    private static bool Safe=>ShipCrewSession.Active&&!SaveProcessManager.bProcessing&&!MiCutsceneHandler.bCutsceneRunning&&!StorySync.Waiting;
    private static bool LocomotionOnly(MiCharacter c)
    {
        var controller=c.controller;if(controller==null)return false;
        foreach(var command in new[]{controller.skillCommandProcess,controller.skillCommandActive,controller.skillCommand})
            if(command!=null&&command.m_skill!=null&&command.m_skill.eSkillType!=Skill.SkillType.GoTo)return false;
        return true;
    }
    internal static void Clear()
    {if(scene==null&&held.Count==0&&nextOrder.Count==0)return;authority.Release();held.Clear();nextOrder.Clear();scene=null;}
    internal static void Tick(bool hosting)
    {
        if(hosting||!Safe||Time.realtimeSinceStartup-lastFrame>1f||scene!=SceneLookup.Find<HUBLogic>()){Clear();return;}
        authority.Tick();
        foreach(var id in new List<string>(held))
            if(id==GuestMove.Identity(ShipCrewSession.Host)||ShipCrewSession.Guest!=null&&id==GuestMove.Identity(ShipCrewSession.Guest))Release(id);
    }
    private static void Release(string id){authority.Release(id);held.Remove(id);nextOrder.Remove(id);}
    internal static string Capture()
    {
        if(!Safe)return "";
        var hub=SceneLookup.Find<HUBLogic>()?.characters;
        if(hub?.m_liSpawnedCharacterData==null)return "";
        var rows=new List<string>();
        foreach(var d in hub.m_liSpawnedCharacterData)
        {
            var c=d?.m_character;
            if(d==null||!d.bUnlocked||c==null||c==ShipCrewSession.Host||c==ShipCrewSession.Guest||!c.bControllerAI()||InteractionSync.Climbing(c))continue;
            if(c.health?.eHealthState!=MiCharacterHealth.HealthState.Alive||!LocomotionOnly(c))continue;
            var p=c.transform.position;float yaw=c.transform.eulerAngles.y;
            rows.Add(GuestMove.Identity(c)+" "+p.x.ToString("R",CultureInfo.InvariantCulture)+" "+p.y.ToString("R",CultureInfo.InvariantCulture)+" "+p.z.ToString("R",CultureInfo.InvariantCulture)+" "+yaw.ToString("R",CultureInfo.InvariantCulture)+" "+(c.movement!=null&&!c.movement.isStoppedOrInactive?"1":"0"));
            if(rows.Count==ShipRoutineProtocol.MaxRows)break;
        }
        string tail="\nSH1 "+rows.Count.ToString(CultureInfo.InvariantCulture)+(rows.Count==0?"":" "+string.Join(" ",rows));
        return tail.Length<=ShipRoutineProtocol.MaxLength?tail:"";
    }
    internal static void Apply(string[][] rows)
    {
        if(!Safe||rows.Length==0){Clear();return;}
        var current=SceneLookup.Find<HUBLogic>();
        if(scene!=current)Clear();scene=current;
        if(scene?.characters?.m_liSpawnedCharacterData==null)return;
        lastFrame=Time.realtimeSinceStartup;var wanted=new HashSet<string>();
        foreach(var r in rows)
        {
            MiCharacter c=null;
            foreach(var d in scene.characters.m_liSpawnedCharacterData)
                if(d!=null&&d.bUnlocked&&d.m_character!=null&&GuestMove.Identity(d.m_character)==r[0]){c=d.m_character;break;}
            if(c==null||c==ShipCrewSession.Host||c==ShipCrewSession.Guest||!c.bControllerAI()||InteractionSync.Climbing(c)||!LocomotionOnly(c))continue;
            if(!authority.Acquire(r[0],c))continue;
            held.Add(r[0]);wanted.Add(r[0]);
            if(nextOrder.TryGetValue(r[0],out float next)&&lastFrame<next)continue;
            var point=new Vector3(float.Parse(r[1],CultureInfo.InvariantCulture),float.Parse(r[2],CultureInfo.InvariantCulture),float.Parse(r[3],CultureInfo.InvariantCulture));
            if(c.navigation==null||!NavMesh.SamplePositionFilter(point,out var nav,.8f,c.navigation.agentType,c.navigation.areaMask)||Math.Abs(nav.position.y-point.y)>.45f)continue;
            var movement=c.movement;
            bool onPath=movement?.target!=null&&!movement.isStoppedOrInactive;
            bool close=Vector3.Distance(c.transform.position,point)<.15f;
            if(close&&!onPath)
            {if(r[5]=="0")c.transform.rotation=Quaternion.Euler(0,float.Parse(r[4],CultureInfo.InvariantCulture),0);continue;}
            if(onPath&&Vector3.Distance(movement.target.v3Target,nav.position)<.15f)continue;
            // Snapshot position rather than a locally chosen routine target.
            // A final stationary sample replaces the old path, preventing drift.
            nextOrder[r[0]]=lastFrame+.2f;
            if(c.controller.skillCommand!=null)c.controller.skillCommand=null;
            c.controllerAI.m_ai.setSkillExtern(Skill.SkillType.GoTo,new MiCharacterTarget(nav.position),NPCClique.SpreadType.None,false,true,null,null);
        }
        foreach(var id in new List<string>(held))if(!wanted.Contains(id))Release(id);
    }
}
