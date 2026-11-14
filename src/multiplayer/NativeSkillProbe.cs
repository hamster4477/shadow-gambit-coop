using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityEngine;

// Local developer file interface. No room packet can enable this. The short
// lived arm file allows the game's Steam restart to retain a diagnostic run.
internal static class NativeSkillProbe
{
    private static string Root=>Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","research","native-skill-run");
    private static float next;
    private static MiCharacter watched;
    private static SkillCommand issued;
    private static float until;
    private static string nonce;
    private static ShipCrewLease shipLease;
    private static float shipLeaseUntil;
    private static readonly JsonSerializerOptions json=new(){WriteIndented=true};
    private static object Pos(Vector3 p)=>new{x=p.x,y=p.y,z=p.z};
    private static object Command(SkillCommand c)=>c==null?null:new{skill=c.m_skill?.eSkillType.ToString(),serial=InteractionSync.CommandSerial(c),target=c.m_target==null?null:Pos(c.m_target.v3Target),description=InteractionSync.Describe(c)};
    private static object Actor(MiCharacter c)=>new{id=InteractionSync.EntityIdentity(c),name=c.name,player=c.bControllerPlayer(),position=Pos(c.transform.position),health=c.health?.eHealthState.ToString(),input=Command(c.controller?.skillCommand),active=Command(c.controller?.skillCommandActive),process=Command(c.controller?.skillCommandProcess)};
    private static void Write(string file,object value)=>File.WriteAllText(Path.Combine(Root,file),JsonSerializer.Serialize(value,json));
    private static List<MiCharacter> Crew()
    {
        var result=new List<MiCharacter>();var handler=SceneLookup.Find<MiCharacterHandler>();
        var list=handler?.m_liPlayerCharactersInMission;
        if(list!=null)foreach(var c in list)if(c!=null&&c.bControllerPlayer())result.Add(c);
        return result;
    }
    private static object Skills(MiCharacter actor)
    {
        var result=new List<object>();
        foreach(Skill.SkillType kind in Enum.GetValues(typeof(Skill.SkillType)))
        {
            var skill=actor.controller?.getSkill(kind)?.TryCast<PlayerSkill>();if(skill==null)continue;
            result.Add(new{name=kind.ToString(),type=skill.GetIl2CppType().FullName,activatable=skill.bActivatable,targetless=skill.bCommandBySelectMouse});
        }
        return result;
    }
    internal static void Tick()
    {
        if(Time.realtimeSinceStartup<next)return;next=Time.realtimeSinceStartup+.2f;
        if(shipLease!=null)
        {
            try {
                if(Time.realtimeSinceStartup>=shipLeaseUntil||SaveProcessManager.bLoading)
                {shipLease.Release();shipLease=null;}
                else shipLease.Maintain();
            }catch(Exception ex){shipLease=null;MoveProbePlugin.ProbeLog.LogWarning("Ship lease diagnostic stopped: "+ex);}
        }
        string arm=Path.Combine(Root,"enabled");
        if(!File.Exists(arm)||DateTime.UtcNow-File.GetLastWriteTimeUtc(arm)>TimeSpan.FromHours(1))return;
        try
        {
            bool ready=SessionQuickReload.InMission&&!SaveProcessManager.bProcessing&&Time.timeScale>0&&!MiCutsceneHandler.bCutsceneRunning;
            Write("heartbeat.json",new{utc=DateTime.UtcNow,ready,loading=SaveProcessManager.bLoading,timeScale=Time.timeScale,focused=Application.isFocused});
            if(watched!=null&&Time.realtimeSinceStartup<until)
            {
                var frame=new{utc=DateTime.UtcNow,nonce,actor=Actor(watched),issued=Command(issued),cannonLaunches=CannonLaunchSync.Capture(),cannonFlights=CannonFlightSync.Capture()};
                File.AppendAllText(Path.Combine(Root,"trace.jsonl"),JsonSerializer.Serialize(frame)+Environment.NewLine);
            }
            string request=Path.Combine(Root,"request.json");if(!File.Exists(request))return;
            if(new FileInfo(request).Length>8192||DateTime.UtcNow-File.GetLastWriteTimeUtc(request)>TimeSpan.FromMinutes(10))
            {File.Move(request,request+".expired",true);throw new InvalidDataException("Expired or oversized native skill request retired");}
            using var doc=JsonDocument.Parse(File.ReadAllText(request));var r=doc.RootElement;
            string id=r.GetProperty("nonce").GetString(),action=r.GetProperty("action").GetString();
            if(!Regex.IsMatch(id??"","\\A[0-9a-f]{32}\\z"))throw new InvalidDataException("Invalid request nonce");
            // Quit remains available at menus or while a test task is paused.
            if(action=="quit"){File.Move(request,request+".accepted",true);Write("result.json",new{nonce=id,status="quitting"});Application.Quit();return;}
            if(!ready){Write("result.json",new{nonce=id,status="waiting-task"});return;}
            File.Move(request,request+".accepted",true);nonce=id;
            if(action=="ship-hold"||action=="ship-release")
            {
                shipLease?.Release();shipLease=null;
                if(action=="ship-release"){Write("result.json",new{nonce,status="ship-released"});return;}
                var hubScene=SceneLookup.Find<Mimimi.HUB.HUBLogic>();
                if(hubScene==null)throw new InvalidDataException("Not on the ship");
                string requested=r.GetProperty("actor").GetString();
                foreach(var data in hubScene.characters.m_liSpawnedCharacterData)
                    if(data.m_character!=null&&GuestMove.Identity(data.m_character)==requested)
                    {shipLease=new ShipCrewLease(hubScene,data);break;}
                if(shipLease==null)throw new InvalidDataException("Ship actor not available");
                shipLeaseUntil=Time.realtimeSinceStartup+30f;
                watched=shipLease.Actor;until=shipLeaseUntil;
                Write("result.json",new{nonce,status="ship-held-30s",actor=Actor(watched)});return;
            }
            var crew=Crew();
            if(action=="inspect")
            {
                var players=new List<object>();foreach(var c in crew)players.Add(new{actor=Actor(c),skills=Skills(c)});
                var guards=new List<MiCharacter>();foreach(var c in UnityEngine.Object.FindObjectsOfType<MiCharacter>())if(c!=null&&!c.bControllerPlayer()&&c.health!=null)guards.Add(c);
                if(crew.Count>0)guards.Sort((a,b)=>Vector3.Distance(a.transform.position,crew[0].transform.position).CompareTo(Vector3.Distance(b.transform.position,crew[0].transform.position)));
                var near=new List<object>();for(int i=0;i<Math.Min(32,guards.Count);i++)near.Add(Actor(guards[i]));
                Write("result.json",new{nonce,status="inventory",crew=players,guards=near});return;
            }
            if(action!="cast")throw new InvalidDataException("Unknown local action");
            string who=r.GetProperty("actor").GetString();var actor=crew.Find(c=>InteractionSync.EntityIdentity(c)==who);
            if(actor==null)throw new InvalidDataException("Requested actor is not in deployed crew");
            var kind=(Skill.SkillType)Enum.Parse(typeof(Skill.SkillType),r.GetProperty("skill").GetString(),false);
            var skill=actor.controller.getSkill(kind)?.TryCast<PlayerSkill>();if(skill==null)throw new InvalidDataException("Actor has no requested native player skill");
            MiCharacterTarget target=null;
            if(r.TryGetProperty("target",out var targetId))
            {
                var entity=InteractionSync.Resolve(targetId.GetString());if(entity==null)throw new InvalidDataException("Native target not resolved");target=new MiCharacterTarget(entity,null);
            }
            else if(r.TryGetProperty("point",out var point))
            {
                if(point.GetArrayLength()!=3)throw new InvalidDataException("Expected XYZ point");var p=new Vector3(point[0].GetSingle(),point[1].GetSingle(),point[2].GetSingle());
                if(!float.IsFinite(p.x)||!float.IsFinite(p.y)||!float.IsFinite(p.z))throw new InvalidDataException("Nonfinite point");target=new MiCharacterTarget(p);
            }
            bool valid=target==null?skill.bCommandBySelectMouse:skill.isTargetValid(target);
            if(!valid){Write("result.json",new{nonce,status="native-target-rejected",skill=kind.ToString(),actor=Actor(actor)});return;}
            // Use the normal native controller command path. Diagnostic casts
            // do not bypass ammo, cooldown, approach range, or execution delay.
            issued=new SkillCommand(skill,target);issued.setModifier(SkillCommand.ExecutionModifier.PlayerInduced,true);
            actor.controllerPlayer.skillCommand=issued;watched=actor;until=Time.realtimeSinceStartup+20;
            Write("result.json",new{nonce,status="queued",skill=kind.ToString(),actor=Actor(actor),command=Command(issued)});
        }
        catch(Exception ex)
        {
            next=Time.realtimeSinceStartup+2f;
            Write("result.json",new{nonce,status="error",error=ex.ToString()});
            MoveProbePlugin.ProbeLog.LogWarning("Native skill diagnostic: "+ex);
        }
    }
}
