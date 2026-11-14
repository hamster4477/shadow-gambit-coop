using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Mimimi.MiScript;

// Native pickup/drop runs on the host. Replicate the persistent carry roster,
// not a one-shot click: late or lost samples must still recover attachment.
internal sealed class BodySync
{
    private int cursor;
    private float retryAt;
    private readonly Dictionary<string,float> disposalRetryAt=new Dictionary<string,float>();
    internal static bool Skill(Skill.SkillType type) => type == global::Skill.SkillType.UseCarryBodyPickup ||
        type == global::Skill.SkillType.UseCarryBodyDrop || type == global::Skill.SkillType.UseCarryBodyThrow;

    internal static bool Corpse(MiCharacter actor) => actor != null && actor.bControllerAI() &&
        !actor.guid.isEmpty() && actor.health != null &&
        (actor.health.eHealthState == MiCharacterHealth.HealthState.Dead ||
         actor.health.eHealthState == MiCharacterHealth.HealthState.Knockout);

    private static SkillCarryBodyPickup Pickup(MiCharacter actor) =>
        actor?.controller?.getSkill(global::Skill.SkillType.UseCarryBodyPickup)?.TryCast<SkillCarryBodyPickup>();

    internal static MiCharacter GroundClickTarget(MiCharacter actor,Entity entity)
    {
        if(actor==null || entity==null || !entity.bAsCharacter(out var body) || !Corpse(body) || body.m_bInHiding ||
            body.m_carryable?.charCarryiedBy!=null || !float.IsFinite(Vector3.Distance(actor.transform.position,body.transform.position))) return null;
        return body;
    }

    private static List<string> Carried(MiCharacter actor)
    {
        var result=new List<string>();
        var bodies=Pickup(actor)?.liCarryables;
        if (bodies==null) return result;
        for (int i=0,count=bodies.Count;i<count;i++)
        {
            var entity=bodies[i]?.entity;
            if (entity!=null && entity.bAsCharacter(out var body) && Corpse(body)) result.Add(Melee.GuardId(body));
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static string Roster(List<string> ids) => ids.Count==0 ? "-" : string.Join(",",ids);
    internal static string CarryRoster(MiCharacter actor)=>Roster(Carried(actor));
    internal string Write(MiCharacter a,MiCharacter b,Dictionary<string,MiCharacter> guards,int budget)
    {
        string header=" C "+Roster(Carried(a))+" "+Roster(Carried(b));
        var ids=new List<string>();
        foreach(var pair in guards) if(Corpse(pair.Value) && !pair.Value.health.hasCondition(MiCharacterHealth.HealthCondition.InProcessOfBeingTiedUp) &&
            (!pair.Value.m_bInHiding || Disposed(pair.Value) || pair.Value.m_carryable?.charCarryiedBy!=null)) ids.Add(pair.Key);
        ids.Sort(StringComparer.Ordinal);
        string records="";
        int sent=0;
        for(int n=0;n<ids.Count && sent<2;n++)
        {
            int index=(cursor+n)%ids.Count;
            var body=guards[ids[index]];
            var p=body.transform.position;
            string state=body.health.eHealthState==MiCharacterHealth.HealthState.Dead ? "D" : "K";
            if(state=="K"&&body.health.hasCondition(MiCharacterHealth.HealthCondition.TiedUp))state="KT";
            if(body.m_carryable?.charCarryiedBy!=null)state="C"+state;
            else if(Disposed(body)) state="H"+state;
            string record=string.Format(CultureInfo.InvariantCulture," {0} {1} {2:0.##} {3:0.##} {4:0.##}",ids[index],state,p.x,p.y,p.z);
            if(header.Length+2+records.Length+record.Length>budget) break;
            records+=record; sent++;
        }
        if(ids.Count>0) cursor=(cursor+Math.Max(1,sent))%ids.Count;
        // A missing page is explicitly unknown, never an empty carry roster.
        return header.Length+2<=budget ? header+" "+sent+records : "";
    }

    internal static bool CanPickup(MiCharacter actor,MiCharacter body) => Corpse(body) &&
        actor?.health?.eHealthState==MiCharacterHealth.HealthState.Alive &&
        float.IsFinite(Vector3.Distance(actor.transform.position,body.transform.position)) &&
        body.m_carryable!=null && body.m_carryable.bCarryableByChar(actor) &&
        (body.m_carryable.charCarryiedBy==null || body.m_carryable.charCarryiedBy==actor) && Pickup(actor)!=null;

    internal static bool IssuePickup(MiCharacter actor,MiCharacter body,bool run)
    {
        if(!CanPickup(actor,body)) return false;
        if(body.m_carryable.charCarryiedBy==actor) return true;
        foreach(var existing in new[]{actor.controllerPlayer.skillCommand,actor.controller.skillCommandProcess})
            if(existing?.m_skill?.eSkillType==global::Skill.SkillType.UseCarryBodyPickup && existing.m_target?.m_entityTarget==body)
            {
                if(run && NativeGait.CanRun(actor)) existing.setModifier(SkillCommand.ExecutionModifier.Run,true);
                return true;
            }
        MiScriptLibraryProject.CharacterExecuteSkill(actor,global::Skill.SkillType.UseCarryBodyPickup,body);
        var command=actor.controllerPlayer.skillCommand;
        if(command?.m_skill?.eSkillType==global::Skill.SkillType.UseCarryBodyPickup)
        {
            command.setModifier(SkillCommand.ExecutionModifier.WasCrawling,actor.movementType.bIsCrouching);
            command.setModifier(SkillCommand.ExecutionModifier.Run,run && NativeGait.CanRun(actor));
        }
        return true;
    }

    internal static bool IssueDrop(MiCharacter actor)
    {
        if(actor?.health?.eHealthState!=MiCharacterHealth.HealthState.Alive ||
            Carried(actor).Count==0 || actor.controller.getSkill(global::Skill.SkillType.UseCarryBodyDrop)==null) return false;
        if(actor.controllerPlayer.skillCommand?.m_skill?.eSkillType==global::Skill.SkillType.UseCarryBodyDrop ||
            actor.controller.skillCommandProcess?.m_skill?.eSkillType==global::Skill.SkillType.UseCarryBodyDrop) return true;
        MiScriptLibraryProject.CharacterExecuteSkill(actor,global::Skill.SkillType.UseCarryBodyDrop,actor.transform.position);
        return true;
    }

    private static HashSet<string> ParseRoster(string value) => new HashSet<string>(value=="-" ? Array.Empty<string>() : value.Split(','));
    // Native disableCharVisInternal disables vis.gameObject, not necessarily
    // the character root. activeSelf distinguishes that from a hidden parent.
    // Foliage/animation in progress is not yet a completed disposal result.
    private static bool Disposed(MiCharacter body) => body.m_bInHiding &&
        body.vis?.gameObject!=null && !body.vis.gameObject.activeSelf;
    internal void Apply(string[] data,Dictionary<string,MiCharacter> guards,MiCharacter a,MiCharacter b,
        string protectedA,string protectedB,bool applyB,HashSet<string> protectedExtra=null)
    {
        if(data==null || Time.realtimeSinceStartup<retryAt) return;
        try
        {
            var left=ParseRoster(data[0]); var right=ParseRoster(data[1]);
            Reconcile(a,left,guards,protectedA,protectedB,protectedExtra);
            if(applyB) Reconcile(b,right,guards,protectedA,protectedB,protectedExtra);
            for(int i=3;i<data.Length;i+=5)
            {
                string id=data[i];
                if(id==protectedA || id==protectedB || protectedExtra?.Contains(id)==true ||
                    !guards.TryGetValue(id,out var body) || body==null) continue;
                // The original health synchronizer owns deaths/knockouts. Do
                // not interrupt their paired animation to move a living actor.
                if(!Corpse(body)) continue;
                if(body.health.hasCondition(MiCharacterHealth.HealthCondition.InProcessOfBeingTiedUp))continue;
                string result=data[i+1];
                bool carriedResult=result=="CD"||result=="CK"||result=="CKT";
                if(!carriedResult&&(left.Contains(id)||right.Contains(id)||body.m_carryable?.charCarryiedBy!=null))continue;
                if(body.health.eHealthState==MiCharacterHealth.HealthState.Knockout && result!="D"&&result!="HD"&&result!="CD")
                {
                    bool tied=result=="KT"||result=="HKT"||result=="CKT";
                    if(body.health.hasCondition(MiCharacterHealth.HealthCondition.TiedUp)!=tied)
                    {
                        // The native options capture the existing tied state
                        // and toggle it. Skip animation/events, not native
                        // health/rope/AI cleanup. Never replay the fishing cast.
                        if(body.processes==null||!body.processes.bStart(new MiCharacterProcessOptionsTieUp(null,body,true,true,true),0))continue;
                        if(body.health.hasCondition(MiCharacterHealth.HealthCondition.TiedUp)!=tied)continue;
                    }
                }
                // Carry attachment has its own roster/extra-crew replay. This
                // row supplies only health-condition result, never a drop,
                // positional warp, unhide or second pickup.
                if(carriedResult)continue;
                if(result=="HD" || result=="HK" || result=="HKT")
                {
                    if(!Disposed(body))
                    {
                        if(disposalRetryAt.TryGetValue(id,out float next) && Time.realtimeSinceStartup<next) continue;
                        disposalRetryAt[id]=Time.realtimeSinceStartup+3f;
                        // The host has already performed damage and statistics.
                        // Reuse native hiding cleanup without a second lethal cast.
                        MiUsableCorpseHiding.hideCharacterWithParams(new MiUsableCorpseHiding.HideParams {
                            m_target=body,
                            m_eDisableMode=MiUsableCorpseHiding.DisableMode.Default,
                            m_bIsDeadly=false,
                            m_bOmitHideFromStatistics=true
                        });
                    }
                    continue;
                }
                disposalRetryAt.Remove(id);
                // D/K rows are emitted only for explicitly non-hidden bodies.
                // They can restore a body after a host-side unhide; omission
                // never enters this branch. Paired animation protection above
                // still takes precedence over restoration.
                if(body.m_bInHiding) MiUsableCorpseHiding.unhide(body);
                var position=CrewSync.ReadPosition(data,i+2);
                if(Vector3.Distance(body.transform.position,position)>0.15f)
                    body.warp(position,body.transform.rotation);
            }
        }
        catch(Exception ex)
        {
            retryAt=Time.realtimeSinceStartup+2f;
            MoveProbePlugin.ProbeLog.LogWarning("Body state replay: "+ex);
        }
    }

    private static void Reconcile(MiCharacter actor,HashSet<string> expected,Dictionary<string,MiCharacter> guards,
        string protectedA,string protectedB,HashSet<string> protectedExtra=null)
    {
        var skill=Pickup(actor);
        if(skill==null || actor.health?.eHealthState!=MiCharacterHealth.HealthState.Alive) return;
        var actual=Carried(actor);
        bool removed=actual.Exists(id=>!expected.Contains(id));
        if(removed)
        {
            // Use the native carry skill so attachment, collision, movement
            // restrictions and the carry animation mode change together.
            skill.dropAll();
            if(skill.iCarryingCount==0 && skill.bInCarryMode) skill.endCarry();
            MoveProbePlugin.ProbeLog.LogInfo("Host confirmed body drop for "+actor.name);
        }
        foreach(string id in expected)
        {
            if(id==protectedA || id==protectedB || protectedExtra?.Contains(id)==true || !guards.TryGetValue(id,out var body) || !Corpse(body)) continue;
            var carryable=body.m_carryable;
            // A confirmed completed pickup includes restoration of a hidden
            // corpse, before the native carryability check and attachment.
            if(carryable!=null && carryable.charCarryiedBy==null && body.m_bInHiding)
                MiUsableCorpseHiding.unhide(body);
            if(carryable==null || carryable.charCarryiedBy==actor ||
                carryable.charCarryiedBy!=null || !carryable.bCarryableByChar(actor)) continue;
            // This is playback of an already completed host pickup. Do not
            // run a second approach to the body's old guest-side location.
            skill.pickup(carryable);
            if(carryable.charCarryiedBy!=actor) throw new InvalidOperationException("Native pickup did not attach body "+id);
            if(!skill.bInCarryMode) skill.startCarry();
            MoveProbePlugin.ProbeLog.LogInfo("Host confirmed body carry "+id+" by "+actor.name);
        }
    }
}
