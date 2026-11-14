using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

// Opt-in host observation only. No native arguments, results or state changed.
internal static class SkillLifecycleTrace
{
    private static bool enabled;
    private static float nextArmCheck;
    private static bool Enabled
    {
        get {
            if(Time.realtimeSinceStartup<nextArmCheck)return enabled;
            nextArmCheck=Time.realtimeSinceStartup+1f;
            try {
                string path=Path.Combine(Paths.GameRootPath,"ShadowGambitCoop","trace-skills.flag");
                enabled=File.Exists(path)&&DateTime.UtcNow-File.GetLastWriteTimeUtc(path)<TimeSpan.FromMinutes(15);
            }catch(IOException){enabled=false;}catch(UnauthorizedAccessException){enabled=false;}
            return enabled;
        }
    }
    private static readonly Dictionary<IntPtr,(MiCharacter actor,float until)> watched=new();
    private static float next;
    private static readonly Dictionary<IntPtr,PlayerSkill> skills=new();
    private static readonly Dictionary<IntPtr,MiPossessable> items=new();
    private static readonly Dictionary<IntPtr,string> lastItems=new();
    private static readonly Dictionary<string,string> targetChecks=new();
    private static void CrystalTarget(SkillTargetCharacter skill,MiCharacter target,bool accepted,string stage)
    {
        if(!Active||skill==null||skill.eSkillType!=Skill.SkillType.FreezeDistract)return;
        try {
            string key=skill.Pointer+":"+stage;
            string value="target="+InteractionSync.EntityIdentity(target)+" accepted="+accepted+" health="+target?.health?.eHealthState;
            if(targetChecks.TryGetValue(key,out var old)&&old==value)return;
            targetChecks[key]=value;
            Write("crystal.target."+stage+" "+value+" caster="+State(skill.charOrigin));
        }catch(Exception ex){Write("crystal-target-error "+ex.GetType().Name);}
    }
    [HarmonyPatch(typeof(SkillTargetCharacter),"isTargetValid",new[]{typeof(MiCharacter)})]
    internal static class CrystalValid
    {private static void Postfix(SkillTargetCharacter __instance,MiCharacter __0,bool __result)=>CrystalTarget(__instance,__0,__result,"valid");}
    [HarmonyPatch(typeof(SkillTargetCharacter),"isTargetValidStatic",new[]{typeof(MiCharacter)})]
    internal static class CrystalStatic
    {private static void Postfix(SkillTargetCharacter __instance,MiCharacter __0,bool __result)=>CrystalTarget(__instance,__0,__result,"static");}
    [HarmonyPatch(typeof(SkillTargetCharacter),"isTargetValidDynamic",new[]{typeof(MiCharacter)})]
    internal static class CrystalDynamic
    {private static void Postfix(SkillTargetCharacter __instance,MiCharacter __0,bool __result)=>CrystalTarget(__instance,__0,__result,"dynamic");}
    internal static void SkillEvent(PlayerSkill skill,string stage,SkillCommand command=null)
    {
        var gun=skill?.TryCast<SkillGunAttackOvertake>();
        if(gun!=null&&NativeInput.Probe?.NativeGuest==true&&stage=="queue-after"){
            pendingGun=gun;pendingGunCommand=command;gunUntil=Time.realtimeSinceStartup+5f;gunNext=0;lastGunState=null;
        }
        if(gun!=null&&NativeInput.Probe?.NativeGuest==true)
            MoveProbePlugin.ProbeLog.LogInfo("Possessed gun "+stage+" weapon="+gun.m_bHasWeapon+" active="+gun.bActiveState+" ammo="+gun.iAmmoCount+" "+State(gun.charOrigin));
        if(!Active||skill?.charOrigin==null)return;
        try {
            Watch(skill.charOrigin);skills[skill.Pointer]=skill;
            Write("skill."+stage+" nativeType="+skill.GetIl2CppType().FullName+" kind="+skill.eSkillType+Target(command)+" "+State(skill.charOrigin));
            if(stage=="execute"){
                var point=command?.m_target?.m_v3TargetPos??skill.charOrigin.transform.position;
                var nearby=new List<MiCharacter>();
                foreach(var c in UnityEngine.Object.FindObjectsOfType<MiCharacter>())if(c!=null&&c.bControllerAI()&&Vector3.Distance(c.transform.position,point)<25)nearby.Add(c);
                nearby.Sort((a,b)=>Vector3.Distance(a.transform.position,point).CompareTo(Vector3.Distance(b.transform.position,point)));
                for(int i=0;i<Math.Min(8,nearby.Count);i++)Watch(nearby[i]);
            }
        }catch(Exception ex){Write("skill-event-error "+ex.GetType().Name);}
    }
    private static void TrackItem(MiPossessable item,MiCharacter owner)
    {
        if(item==null)return;items[item.Pointer]=item;
        string state="item="+item.Pointer+" active="+item.gameObject.activeInHierarchy+" pos="+CrewSync.Position(item.transform.position)+" inInventory="+(owner?.inventory?.bHasPossessable(item)==true);
        if(!lastItems.TryGetValue(item.Pointer,out var old)||old!=state){lastItems[item.Pointer]=state;Write("bait "+state);}
    }
    private static bool Active=>Enabled&&NativeInput.Probe?.NativeGuest!=true;
    private static void Write(string text){if(Active)MoveProbePlugin.ProbeLog.LogInfo("SKILLTRACE "+Time.realtimeSinceStartup.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+" "+text);}
    private static string State(MiCharacter actor)
    {
        if(actor==null)return "missing";
        var processes=new List<string>();
        var list=actor.processes?.m_lCharacterProcessExecutions;
        if(list!=null)foreach(var e in list)processes.Add(e.m_options?.GetIl2CppType().FullName+"/"+e.m_iHandleID);
        return InteractionSync.EntityIdentity(actor)+" pos="+CrewSync.Position(actor.transform.position)+" health="+actor.health?.eHealthState+" flags="+actor.health?.m_iHealthConditionFlag+
            " nav="+CrewSync.Position(GuestGroundTarget.NavigationPosition(actor))+" parent="+actor.transform.parent?.name+" facing="+actor.transform.eulerAngles.y+" sight="+actor.controllerAI?.m_detectionOrientation?.qDetectionRot.eulerAngles.y+" orientation="+actor.controllerAI?.m_detectionOrientation?.fOrientation+" focus="+actor.controllerAI?.m_detectionOrientation?.m_objFocus?.Pointer+" endFocus="+actor.controllerAI?.m_detectionOrientation?.m_bEndFocus+" looking="+actor.controllerAI?.m_detectionOrientation?.m_bLookAround+" lookTarget="+actor.controllerAI?.m_detectionOrientation?.m_fAngleTarget+" canMove="+actor.controllerPlayer?.bCanMove+" process="+actor.controller?.skillCommandProcess?.m_skill?.eSkillType+" native=["+string.Join(",",processes)+"]";
    }
    private static SkillGunAttackOvertake pendingGun;
    private static SkillCommand pendingGunCommand;
    private static float gunUntil,gunNext;
    private static string lastGunState;
    private static void TickGun()
    {
        if(pendingGun==null)return;
        if(NativeInput.Probe?.NativeGuest!=true||Time.realtimeSinceStartup>gunUntil){pendingGun=null;pendingGunCommand=null;return;}
        if(Time.realtimeSinceStartup<gunNext)return;gunNext=Time.realtimeSinceStartup+.2f;
        try{
            var controller=pendingGun.charOrigin?.controller;
            if(controller==null){pendingGun=null;pendingGunCommand=null;return;}
            string state="queued="+controller.skillCommand?.m_skill?.eSkillType+" process="+controller.skillCommandProcess?.m_skill?.eSkillType+
                " active="+controller.skillCommandActive?.m_skill?.eSkillType+" activatable="+pendingGun.bActivatable+
                " ammo="+pendingGun.iAmmoCount+" targetValid="+(pendingGunCommand?.m_target!=null&&pendingGun.isTargetValid(pendingGunCommand.m_target))+
                " timeScale="+Time.timeScale;
            if(state!=lastGunState){lastGunState=state;MoveProbePlugin.ProbeLog.LogInfo("Possessed gun replay-watch "+state);}
        }catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Possessed gun replay-watch stopped: "+ex.GetType().Name);pendingGun=null;pendingGunCommand=null;}
    }
    internal static void Tick()
    {
        TickGun();
        if(!Active){watched.Clear();skills.Clear();items.Clear();lastItems.Clear();targetChecks.Clear();return;}
        if(Time.realtimeSinceStartup<next)return;next=Time.realtimeSinceStartup+.5f;
        foreach(var skill in skills.Values)try {
            if(skill==null||skill.charOrigin==null||!watched.TryGetValue(skill.charOrigin.Pointer,out var watch)||Time.realtimeSinceStartup>watch.until)continue;
            Write("preview kind="+skill.eSkillType+" rangeExists="+(skill.m_rangeVisualisation?.bExists==true)+" targetNull="+(skill.m_target==null));
            var bait=skill.TryCast<SkillThrowBallisticPossessable>();
            if(bait!=null){TrackItem(bait.m_possessablePlaced,skill.charOrigin);
                if(bait.m_possessablePrefab!=null&&skill.charOrigin.inventory.tryGetPossessable(bait.m_possessablePrefab.m_eType,out var held))TrackItem(held,skill.charOrigin);
                foreach(var item in new List<MiPossessable>(items.Values))TrackItem(item,skill.charOrigin);
            }
        }catch(Exception ex){Write("preview-item-error "+ex.GetType().Name);}
        foreach(var p in new List<KeyValuePair<IntPtr,(MiCharacter actor,float until)>>(watched))
        {if(p.Value.actor==null||Time.realtimeSinceStartup>p.Value.until){watched.Remove(p.Key);continue;}try{Write("state "+State(p.Value.actor));}catch(Exception ex){Write("state-error "+ex.GetType().Name);}}
    }
    private static string Target(SkillCommand command)
    {
        var t=command?.m_target;
        return " commandNull="+(command==null)+" targetNull="+(t==null)+(t==null?"":" targetType="+t.GetIl2CppType().FullName+" entity="+InteractionSync.EntityIdentity(t.m_entityTarget)+" point="+CrewSync.Position(t.m_v3TargetPos)+" skillPoint="+CrewSync.Position(t.m_v3SkillTargetPos)+" alternative="+CrewSync.Position(t.m_v3AlternativeTarget));
    }
    private static void Watch(MiCharacter actor){if(actor!=null)watched[actor.Pointer]=(actor,Time.realtimeSinceStartup+25);}
    [HarmonyPatch(typeof(SkillPlacePossessableGhost),"commandMoveGhostToPlayer")]
    internal static class GhostRecall
    {private static bool Prefix(SkillPlacePossessableGhost __instance){
        SkillEvent(__instance,"ghost-recall-command");
        if(NativeInput.Restoring||NativeInput.Probe?.NativeGuest!=true)return true;
        NativeInput.Probe.TryCapture(__instance.charOrigin,"USE "+InteractionProtocol.Code("GhostCallBack")+" n 0 0 0 0");
        return false;
    }}
    [HarmonyPatch(typeof(SkillPlacePossessableGhost),"isInputEventCallGhostBack",new[]{typeof(MiPlayerInput)})]
    internal static class GhostRecallInput
    {private static void Postfix(SkillPlacePossessableGhost __instance,bool __result){if(__result)SkillEvent(__instance,"ghost-recall-input");}}
    [HarmonyPatch(typeof(SkillThrowBallisticPossessableBaitBase),"triggerFindAmmoInput")]
    internal static class BaitRecall
    {
        private static void Prefix(SkillThrowBallisticPossessableBaitBase __instance)=>SkillEvent(__instance,"bait-recall-before");
        private static void Postfix(SkillThrowBallisticPossessableBaitBase __instance)=>SkillEvent(__instance,"bait-recall-after");
    }
    [HarmonyPatch(typeof(SkillThrowBallisticPossessableBaitBase),"onTrySelect")]
    internal static class BaitSelect
    {private static void Prefix(SkillThrowBallisticPossessableBaitBase __instance)=>SkillEvent(__instance,"bait-select");}
    [HarmonyPatch(typeof(SkillGunAttackOvertake),"preExecute")]
    internal static class GunStart
    {private static void Prefix(SkillGunAttackOvertake __instance){try{SkillEvent(__instance,"pre-execute");}catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Possessed gun trace "+ex.GetType().Name);}}}
    [HarmonyPatch(typeof(SkillTeleportTeleport),"onCommand")]
    internal static class TeleportCommand
    {private static void Prefix(SkillTeleportTeleport __instance,SkillCommand __0){if(!Active)return;try{Watch(__instance.charOrigin);Write("teleport.onCommand "+__instance.eSkillType+" descriptor="+InteractionSync.Describe(__0)+Target(__0)+" playerTargetNull="+(__instance.m_target==null)+" "+State(__instance.charOrigin));}catch(Exception ex){Write("teleport-error "+ex.GetType().Name);}}}
    [HarmonyPatch(typeof(SkillTeleportTeleport),"execute")]
    internal static class TeleportExecute
    {private static void Prefix(SkillTeleportTeleport __instance){if(!Active)return;try{Watch(__instance.charOrigin);Write("teleport.execute "+__instance.eSkillType+" marker="+CrewSync.Position(__instance.m_v3MarkerPosition)+" destination="+CrewSync.Position(__instance.m_v3TargetPosition)+" "+State(__instance.charOrigin));}catch(Exception ex){Write("teleport-error "+ex.GetType().Name);}}}
    [HarmonyPatch(typeof(SkillCannonShoot),"executeGetFlying")]
    internal static class CannonCreate
    {private static void Postfix(SkillCannonShoot __instance,MiCharacter __0){if(!Active)return;try{Watch(__0);Write("cannon.create "+State(__0));}catch(Exception ex){Write("cannon-error "+ex.GetType().Name);}}}
    [HarmonyPatch(typeof(SkillCannonShoot),"onHit")]
    internal static class CannonHit
    {private static void Prefix(SkillCannonShoot __instance){if(!Active)return;Write("cannon.onHit water="+__instance.m_bCurrentShootDisposeWater+" alreadyLying="+__instance.m_bTargetAlreadyLying+" mindControl="+__instance.m_bIsMindControlledAndDropOutOnHit);}}
    [HarmonyPatch(typeof(MiCharacterProcessHandler),"bStart",new[]{typeof(MiCharacterProcessOptions),typeof(int)})]
    internal static class ProcessStart
    {private static void Postfix(MiCharacterProcessOptions __0,bool __result){if(!Active||__0?.charTarget==null||!watched.ContainsKey(__0.charTarget.Pointer))return;try{var wake=__0.TryCast<MiCharacterProcessOptionsComeTo>();
        var freeze=__0.TryCast<MiCharacterProcessOptionsTimeFreeze>();
        if(freeze!=null)Write("freeze.options duration="+(freeze.m_fDuration.HasValue?freeze.m_fDuration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture):"null")+" viewcone="+freeze.m_bActivateViewcone+" routine="+freeze.m_bPauseRoutine+" animations="+freeze.m_bFreezeAnimations+" vfx="+freeze.m_bUseFrozenVFXController+" origin="+InteractionSync.EntityIdentity(freeze.m_charOrigin));
        if(wake!=null)Write("comeTo.options cannon="+wake.m_bFromCannon+" skipAnimation="+wake.m_bSkipAnimation+" wasTied="+wake.m_bWasTiedUp+" state="+wake.m_eComeToState+" actionNull="+(wake.m_actionWakeUp==null)+" hierarchyNull="+(wake.m_animHierarchyNodeTarget==null));
        Write("process.start accepted="+__result+" type="+__0.GetIl2CppType().FullName+" skill="+__0.eSkillTypeOrigin+" "+State(__0.charTarget));}catch(Exception ex){Write("process-error "+ex.GetType().Name);}}}
}
