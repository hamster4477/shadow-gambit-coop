using System;
using System.Collections.Generic;
using Mimimi.MiScript;
using UnityEngine;

// Session-local registry. Repeat current results so a dropped snapshot or guest
// reconnect does not lose a knockout. Ambiguous IDs never select a target.
internal sealed class GuardOutcomes
{
    internal static Func<MiCharacter,bool> GeneratedActor;
    private readonly Dictionary<string, MiCharacter> guards = new Dictionary<string, MiCharacter>();
    private readonly Dictionary<string, float> retryAt = new Dictionary<string, float>();
    private readonly Dictionary<string,MiCharacter> knockedOut=new(StringComparer.Ordinal);
    private float nextScan;
    private bool alarmActive;
    internal void SetAlarmActive(bool active)
    {
        alarmActive=active;
        if(active) nextScan=Math.Min(nextScan,Time.realtimeSinceStartup+0.5f);
    }
    private int cursor;
    private float nextHealthScan;
    private readonly List<string> results = new List<string>();
    private string completePage = "";
    internal Dictionary<string, MiCharacter> Registry
    {
        get
        {
            Refresh();
            ExcludeGenerated();
            return guards;
        }
    }
    private void ExcludeGenerated()
    {
        if(GeneratedActor==null)return;List<string> removed=null;
        foreach(var pair in guards)if(pair.Value!=null&&GeneratedActor(pair.Value))(removed??=new()).Add(pair.Key);
        if(removed==null)return;
        foreach(string id in removed){guards.Remove(id);knockedOut.Remove(id);}
        results.RemoveAll(result=>removed.Contains(result.Substring(1)));
        if(results.Count<=16)completePage=results.Count==0?"":" "+string.Join(" ",results);
    }

    private void Refresh()
    {
        if (Time.realtimeSinceStartup < nextScan) return;
        nextScan = Time.realtimeSinceStartup + (alarmActive ? 0.5f : 5f);
        // Native disposal may deactivate a known corpse. Keep that explicit
        // terminal identity across active-only scans so result/body replay can
        // still address it. Do not discover dormant mission actors globally.
        var inactiveBodies = new Dictionary<string, MiCharacter>();
        foreach (var pair in guards)
        {
            var body = pair.Value;
            if (body != null && body.bControllerAI() && body.health != null &&
                body.gameObject != null && !body.gameObject.activeInHierarchy &&
                GeneratedActor?.Invoke(body) != true &&
                (body.health.eHealthState == MiCharacterHealth.HealthState.Dead ||
                 body.health.eHealthState == MiCharacterHealth.HealthState.Knockout))
                inactiveBodies[pair.Key] = body;
        }
        guards.Clear();
        var mission=SceneLookup.Find<MiMissionHandler>()?.currentMissionData;
        var shipCrew=mission==null||mission.guid.iValue==ShipCrewSession.Mission?
            SceneLookup.Find<Mimimi.HUB.HUBLogic>()?.characters?.m_liSpawnedCharacters:null;
        foreach (var actor in UnityEngine.Object.FindObjectsOfType<MiCharacter>())
        {
            if (!actor.bControllerAI() || actor.guid.isEmpty() || actor.health == null || GeneratedActor?.Invoke(actor)==true) continue;
            bool routineCrew=false;
            if(shipCrew!=null)foreach(var member in shipCrew)if(member==actor){routineCrew=true;break;}
            if(routineCrew)continue; // Idle shipmates are not hostile guard replicas.
            string id = Melee.GuardId(actor);
            guards[id] = guards.ContainsKey(id) ? null : actor;
        }
        foreach (var pair in inactiveBodies)
        {
            // A replacement with the same ID is ambiguous, never select it.
            guards[pair.Key] = guards.ContainsKey(pair.Key) ? null : pair.Value;
        }
    }

    internal string Write(int limit=16)
    {
        Refresh();
        ExcludeGenerated();
        if (Time.realtimeSinceStartup >= nextHealthScan)
        {
            nextHealthScan=Time.realtimeSinceStartup+0.25f;
            results.Clear();
            foreach (var entry in guards)
            {
                if (entry.Value == null || GeneratedActor?.Invoke(entry.Value)==true) continue;
                var status = entry.Value.health;
                if (status == null) continue;
                var health = status.eHealthState;
                if (health == MiCharacterHealth.HealthState.Knockout){results.Add("K" + entry.Key);knockedOut[entry.Key]=entry.Value;}
                if (health == MiCharacterHealth.HealthState.Dead) results.Add("D" + entry.Key);
                if (health == MiCharacterHealth.HealthState.Alive&&knockedOut.TryGetValue(entry.Key,out var previous)&&previous==entry.Value)
                    results.Add("A"+entry.Key);
            }
            results.Sort(StringComparer.Ordinal);
            if (results.Count <= 16)
                completePage = results.Count == 0 ? "" : " " + string.Join(" ", results);
        }
        limit=Math.Max(1,Math.Min(16,limit));
        if (results.Count <= limit) return completePage;
        var page = new List<string>();
        for (int i = 0; i < Math.Min(results.Count, limit); i++) page.Add(results[(cursor+i) % results.Count]);
        cursor = (cursor + page.Count) % results.Count;
        return " " + string.Join(" ", page);
    }

    internal bool IsApplied(string result)
    {
        if(!guards.TryGetValue(result.Substring(1),out var target)||target==null||target.health==null)return false;
        var state=target.health.eHealthState;
        if(result[0]=='A')return state==MiCharacterHealth.HealthState.Alive;
        return state==MiCharacterHealth.HealthState.Dead||result[0]=='K'&&state==MiCharacterHealth.HealthState.Knockout;
    }
    internal void Apply(string[] results, string protectedA = null, string protectedB = null, HashSet<string> protectedExtra=null)
    {
        if (results.Length == 0) return;
        Refresh();
        foreach (var result in results)
        {
            string id = result.Substring(1);
            if (id == protectedA || id == protectedB || protectedExtra?.Contains(id)==true) continue;
            // Possession can replace a controller before the registry refresh.
            // A cached NPC result must not kill/knock out its new player owner.
            if (!guards.TryGetValue(id, out var target) || target == null || !target.bControllerAI() || target.health == null || GeneratedActor?.Invoke(target)==true) continue;
            var local = target.health.eHealthState;
            if(result[0]=='A')
            {
                // An explicit host wake result, never absence of a K row.
                // Death/revival belongs to another lifecycle; don't resurrect
                // a locally dead target from a delayed awakening result.
                if(local!=MiCharacterHealth.HealthState.Knockout||target.m_carryable?.charCarryiedBy!=null||
                    target.health.hasCondition(MiCharacterHealth.HealthCondition.InProcessOfBeingTiedUp))continue;
                if(retryAt.TryGetValue(result,out float wakeNext)&&Time.realtimeSinceStartup<wakeNext)continue;
                retryAt[result]=Time.realtimeSinceStartup+.6f;
                try
                {
                    if(target.health.hasCondition(MiCharacterHealth.HealthCondition.TiedUp))
                    {
                        if(target.processes==null||!target.processes.bStart(new MiCharacterProcessOptionsTieUp(null,target,true,true,true),0))continue;
                        if(target.health.hasCondition(MiCharacterHealth.HealthCondition.TiedUp))continue;
                    }
                    if(target.m_bInHiding)MiUsableCorpseHiding.unhide(target);
                    target.health.comeTo();
                }
                catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Guard wake "+id+": "+ex.GetType().Name);}
                continue;
            }
            bool kill = result[0] == 'D';
            if (local != MiCharacterHealth.HealthState.Alive && !(kill && local == MiCharacterHealth.HealthState.Knockout)) continue;
            if (retryAt.TryGetValue(result, out float next) && Time.time < next) continue;
            retryAt[result] = Time.time + 3f;
            try
            {
                if (kill) MiScriptLibraryProject.CharacterKill(target);
                else MiScriptLibraryProject.CharacterKnockout(target);
                MoveProbePlugin.ProbeLog.LogInfo($"Guard result applied: {result}, previously {local}");
            }
            catch (Exception ex) { MoveProbePlugin.ProbeLog.LogWarning($"Guard result {result}: {ex.GetType().Name}"); }
        }
    }
}
