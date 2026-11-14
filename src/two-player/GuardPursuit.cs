using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using Mimimi.MiScript;
using HarmonyLib;

internal sealed class GuardPursuit
{
    internal static Func<MiCharacter,bool> EffectProtection {get;set;}
    private sealed class SightSample
    {
        internal GuardPursuit owner;internal MiCharacter actor;
        internal float angle,bodyYaw,fromAngle,fromBody,at,duration,until;
        internal float? range;
        internal float Angle(float now)=>fromAngle+Mathf.DeltaAngle(fromAngle,angle)*Mathf.Clamp01((now-at)/duration);
        internal float Body(float now)=>fromBody+Mathf.DeltaAngle(fromBody,bodyYaw)*Mathf.Clamp01((now-at)/duration);
    }
    private static readonly Dictionary<IntPtr,SightSample> sightDirections=new();
    internal static void RestoreSight(DetectionOrientation sight)
    {
        if(sight==null||!sightDirections.TryGetValue(sight.Pointer,out var s))return;
        if(s.actor==null||Time.realtimeSinceStartup>s.until||
            !GuardAuthority.Available||SaveProcessManager.bLoading||
            s.actor.health?.eHealthState!=MiCharacterHealth.HealthState.Alive||
            !s.actor.bControllerAI()||s.actor.controllerAI?.m_detectionOrientation?.Pointer!=sight.Pointer)
        {sightDirections.Remove(sight.Pointer);return;}
        // qDetectionRot is a relative head/view offset, not world facing.
        // Native LookAt can overwrite the body after snapshot application.
        float now=Time.realtimeSinceStartup,body=s.Body(now);
        s.actor.navigation?.setOrientation(Quaternion.Euler(0,body,0));
        s.actor.transform.rotation=Quaternion.Euler(0,body,0);
        sight.fOrientation+=Mathf.DeltaAngle(sight.qDetectionRot.eulerAngles.y,s.Angle(now));
        var sensor=s.actor.controllerAI?.m_detection;
        if(s.range.HasValue&&sensor!=null)
        {sensor.m_fDetectionRange=s.range.Value;sensor.updateViewCone();}
    }
    [HarmonyPatch(typeof(DetectionOrientation),"MiUpdateInternal")]
    internal static class SightDirection
    {private static void Postfix(DetectionOrientation __instance)=>RestoreSight(__instance);}
    // Same order as S19 actor rows: A, B, then every remaining actor.
    // Never infer a DLC/hidden actor from a slot or substitute B for it.
    private readonly List<MiCharacter> crew=new List<MiCharacter>();
    internal void SetCrew(MiCharacter a,MiCharacter b,IEnumerable<MiCharacter> extra,bool preserveMissing=false)
    {
        var next=new List<MiCharacter>{a,b};
        if(extra!=null)foreach(var actor in extra)
            if(actor==null?preserveMissing:actor!=a&&actor!=b)next.Add(actor);
        bool changed=next.Count!=crew.Count;
        if(!changed)for(int i=0;i<next.Count;i++)if(next[i]!=crew[i]){changed=true;break;}
        if(!changed)return;
        crew.Clear();crew.AddRange(next);sightSamples.Clear();
        nextScan=0f;sampledSight.Clear();sentSight.Clear();
    }
    private string CrewToken(MiCharacter actor,MiCharacter a,MiCharacter b)
    {
        if(actor==a)return "A";if(actor==b)return "B";
        foreach(var c in crew)if(c==actor)return GuestMove.Identity(c);
        return null;
    }
    private MiCharacter ResolveCrew(string token,MiCharacter a,MiCharacter b)
    {
        if(token=="A")return a;if(token=="B")return b;
        foreach(var actor in crew)if(GuestMove.Identity(actor)==token)return actor;
        return null;
    }
    private bool NearCrew(Vector3 position,MiCharacter a,MiCharacter b)
    {
        if(Vector3.Distance(position,a.transform.position)<=35f || Vector3.Distance(position,b.transform.position)<=35f)return true;
        foreach(var actor in crew)if(actor!=null && Vector3.Distance(position,actor.transform.position)<=35f)return true;
        return false;
    }
    private sealed class Strike
    {
        internal IntPtr command;
        internal int serial;
        internal string kind, victim;
        internal float until;
    }
    private readonly Dictionary<string,Strike> strikes = new Dictionary<string,Strike>();
    private readonly Dictionary<string,int> played = new Dictionary<string,int>();
    private readonly Dictionary<int,float> healthRetry = new Dictionary<int,float>();
    private sealed class Replica
    {
        internal Vector3 host, local;
        internal float seenAt, nextOrderAt, searchUntil;
        internal bool observed, forceOrder;
        internal Vector3? issued;
        internal Vector3? legGoal, lead;
        internal int forwardSamples;
        internal float advancedAt;
        internal Vector3 progressPoint;
        internal float progressAt;
    }
    private readonly Dictionary<string,Replica> replicas = new Dictionary<string,Replica>();
    private readonly Dictionary<string,(string token,DetectionData data)> sightProfiles=new(StringComparer.Ordinal);
    private readonly Dictionary<string,float> hostLogAt = new Dictionary<string,float>();
    private readonly Dictionary<string,float> guestLogAt = new Dictionary<string,float>();
    private readonly GuardAuthority authority = new GuardAuthority();
    private readonly GuardIcons icons;
    internal GuardPursuit() { icons=new GuardIcons(c=>authority.Owns(Melee.GuardId(c),c)); }
    private readonly Dictionary<string,(MiCharacter actor,float until)> detectionViews=new();
    private readonly List<string> expiredDetectionViews=new();
    private string detectedId;
    private MiCharacterControllerAI detectedView;
    private MiCharacter detectedActor;
    private float detectedViewUntil;
    internal void RestoreDetectionView()
    {
        if(SaveProcessManager.bLoading||!GuardAuthority.Available)
        {ClearDetectionView();return;}
        float now=Time.realtimeSinceStartup;
        if(detectedView!=null&&(now>detectedViewUntil||!ValidDetectionActor(detectedActor)||
            detectedActor.controllerAI!=detectedView))ClearDetectionView(true,false);
        if(detectedView==null)
        {
            // HighPrioAI is one native UI slot. Keep its current enemy while it
            // still observes crew, then fall back to another fresh observer.
            // Retain candidates even if the scene's ViewConeState is not ready.
            expiredDetectionViews.Clear();
            foreach(var entry in detectionViews)
            {
                var candidate=entry.Value;
                if(now>candidate.until||!ValidDetectionActor(candidate.actor))
                {expiredDetectionViews.Add(entry.Key);continue;}
                if(detectedView!=null)continue;
                detectedId=entry.Key;detectedActor=candidate.actor;
                detectedView=detectedActor.controllerAI;detectedViewUntil=candidate.until;
            }
            foreach(string id in expiredDetectionViews)detectionViews.Remove(id);
            expiredDetectionViews.Clear();
        }
        if(detectedView==null)return;
        if(!ViewConeState.bInstanceExists)return;
        var view=ViewConeState.instance;
        if(view.charActive!=detectedView||view.eVCModeActive!=ViewConeState.ViewConeActivationMode.HighPrioAI)
            view.activateVC(detectedView,ViewConeState.ViewConeActivationMode.HighPrioAI,true);
        // Native AI/view updates can clear the sensor's display while the UI
        // still reports this guard as active. Restore both halves after update.
        if(view.charActive==detectedView&&view.eVCModeActive==ViewConeState.ViewConeActivationMode.HighPrioAI)
        {
            detectedView.m_detection?.setViewConeActive(true);
            detectedView.m_detection?.updateViewCone();
            detectedActor?.highlighterGroup?.enableHighlighterTriggerAndTypeFrame(
                Mimimi.HighlighterSystem.HighlighterTrigger.ViewConeInputActiveChar,
                Mimimi.HighlighterSystem.HighlighterType.Enemy);
        }
    }
    private static bool ValidDetectionActor(MiCharacter actor)=>actor!=null&&actor.bControllerAI()&&
        !actor.bIsCivilian()&&actor.health?.eHealthState==MiCharacterHealth.HealthState.Alive&&actor.controllerAI!=null;
    private void ForgetDetectionView(string id,bool retain)
    {
        detectionViews.Remove(id);
        if(detectedId!=id)return;
        ClearDetectionView(retain,false);
        RestoreDetectionView();
    }
    private void ClearDetectionView(bool retain=false,bool clearCandidates=true)
    {
        if(detectedView!=null&&ViewConeState.bInstanceExists&&ViewConeState.instance.charActive==detectedView&&
            ViewConeState.instance.eVCModeActive==ViewConeState.ViewConeActivationMode.HighPrioAI)
        {
            if(retain&&ValidDetectionActor(detectedActor)&&detectedActor.controllerAI==detectedView)
                ViewConeState.instance.activateVC(detectedView,ViewConeState.ViewConeActivationMode.RegularAI,true);
            else ViewConeState.instance.deactivateVC(detectedView);
        }
        detectedView=null;detectedActor=null;detectedId=null;
        if(clearCandidates){detectionViews.Clear();expiredDetectionViews.Clear();}
    }
    internal void Tick() { authority.Tick(); icons.Tick(); }
    internal void Release() { ClearDetectionView();icons.Release(); authority.Release();foreach(var key in new List<IntPtr>(sightDirections.Keys))if(sightDirections[key].owner==this)sightDirections.Remove(key); }
    private readonly Dictionary<string,(Vector3 point,float until)> waypoints = new Dictionary<string,(Vector3,float)>();
    internal void SetWaypoint(string id, Vector3 point, float lifetime) => waypoints[id] = (point,Time.time+lifetime);
    internal bool Waypoint(string id, out Vector3 point)
    {
        if (waypoints.TryGetValue(id,out var saved) && saved.until >= Time.time)
        { point=saved.point; return true; }
        point=default; return false;
    }

    [HarmonyPatch(typeof(SkillInvestigate),"findPoint")]
    internal static class RandomSearch
    {
        private static void Postfix(SkillInvestigate __instance, ref Vector3 __result)
        {
            if (NativeInput.Probe != null) NativeInput.Probe.SearchPoint(__instance.character,ref __result);
        }
    }
    private int cursor;
    private readonly HashSet<string> onWire = new HashSet<string>();
    private static string NavPoint(Vector3 p) => string.Format(CultureInfo.InvariantCulture,
        "{0:0.##} {1:0.##} {2:0.##}",p.x,p.y,p.z);
    [ThreadStatic] private static bool applyingHealth;

    [HarmonyPatch(typeof(MiCharacterHealth),"damage")]
    internal static class ConfirmedDamage
    {
        private static bool Prefix(MiCharacterHealth __instance, MiCharacterImpulseHandler.ImpulseOptionsDamage __0)
        {
            return applyingHealth || __0 == null || NativeInput.Probe == null || !NativeInput.Probe.MirroredCrew(__instance.character) ||
                __0.charOrigin == null || !__0.charOrigin.bControllerAI();
        }
    }

    private static bool Search(Skill.SkillType? kind) => kind is Skill.SkillType.Investigate or Skill.SkillType.InvestigateBadFortune
        or Skill.SkillType.Follow or Skill.SkillType.GoToRoutinePoint or Skill.SkillType.FollowFootprints or Skill.SkillType.GoTo or Skill.SkillType.WalkTowards;
    // Both native NPC setters clear the GoTo skill object. A retained command
    // and target with no skill is the engine's default locomotion command.
    // A missing command/target is not evidence that an order was accepted.
    private static Skill.SkillType? CommandKind(SkillCommand command) => command?.m_skill != null ?
        command.m_skill.eSkillType : command?.m_target != null ? Skill.SkillType.GoTo : (Skill.SkillType?)null;
    private static string DescribeCommand(SkillCommand command) => command == null ? "missing" :
        (CommandKind(command)?.ToString() ?? "no-skill-or-target") + (command.m_target == null ? "/no-target" : "/target");
    // AlertState drives AI, while these independent connected UI data objects
    // drive the actual head markers. Copy the host's displayed set, not an
    // inferred question/exclamation mapping from the alert enum.
    private static readonly UIWorldInfoBatchIconID[] AlertIcons = {
        UIWorldInfoBatchIconID.Alarm, UIWorldInfoBatchIconID.Investigate,
        UIWorldInfoBatchIconID.AlarmingAction, UIWorldInfoBatchIconID.AlarmBell
    };
    private static int ReadIcons(MiCharacter actor)
    {
        int mask=0;
        for(int i=0;i<AlertIcons.Length;i++)
            if(actor.getIconData(AlertIcons[i])?.eState==UIData.State.Connected) mask|=1<<i;
        return mask;
    }
    private static void ApplyIcons(MiCharacter actor,int wanted)
    {
        int current=ReadIcons(actor);
        for(int i=0;i<AlertIcons.Length;i++)
            if(((current^wanted)&(1<<i))!=0) actor.showOrHideIcon(AlertIcons[i],(wanted&(1<<i))!=0);
    }
    private static SkillCommand ObservedCommand(MiCharacter actor) =>
        actor.controller.skillCommandProcess ?? actor.controller.skillCommandActive ?? actor.controller.skillCommand;
    private static bool Offensive(Skill.SkillType? kind) => kind is Skill.SkillType.Gun or Skill.SkillType.GunGatling or Skill.SkillType.Kill
        or Skill.SkillType.CounterMeleeKill or Skill.SkillType.Knockout or Skill.SkillType.SnipeKill;
    private static string Health(MiCharacter c) => $"{Math.Max(0,c.health.iHealth)} {c.health.eHealthState}";

    [HarmonyPatch(typeof(MiCharacterController), "set_skillCommandProcess")]
    internal static class ProcessStarted
    {
        private static void Postfix(MiCharacterController __instance, SkillCommand __0)
        {
            if (__0?.m_skill != null && Offensive(__0.m_skill.eSkillType))
                NativeInput.Probe?.GuardProcess(__instance.character,__0);
        }
    }

    internal bool ObserveStrike(string id, SkillCommand cmd, MiCharacter a, MiCharacter b)
    {
        strikes.TryGetValue(id,out var strike);
        string victim = null;
        if (Offensive(cmd?.m_skill?.eSkillType) && cmd.m_target?.m_entityTarget != null &&
            cmd.m_target.m_entityTarget.bAsCharacter(out var target))
            victim = CrewToken(target,a,b);
        if (victim != null)
        {
            if (strike == null) strikes[id] = strike = new Strike();
            string kind = cmd.m_skill.eSkillType.ToString();
            if (cmd.Pointer != strike.command || kind != strike.kind || victim != strike.victim)
            {
                strike.serial = strike.serial == int.MaxValue ? 1 : strike.serial+1;
                strike.command = cmd.Pointer; strike.kind = kind; strike.victim = victim;
            }
            // A retained native Gun can continue firing for several seconds.
            // Keep its cue alive without inventing new shots/serials; otherwise
            // the guest resumes ground-follow and cancels the ongoing action.
            strike.until = Time.time + 1.5f;
        }
        else if (strike != null)
        {
            strike.command = IntPtr.Zero;
            // A positively observed new command supersedes the old shot now.
            // Retain the short grace only across empty controller slots.
            if (cmd != null) strike.until = 0f;
        }
        return strike != null && Time.time < strike.until;
    }

    private float settleUntil, nextLog, nextScan;
    private bool lastActive;
    private readonly Dictionary<string,float> tracked = new Dictionary<string,float>();
    private readonly List<string> candidates = new List<string>();

    private readonly Dictionary<string,string> sampledSight = new Dictionary<string,string>();
    private readonly Dictionary<string,string> sentSight = new Dictionary<string,string>();
    private readonly Dictionary<string,bool> sampledDetecting = new Dictionary<string,bool>();
    private readonly Dictionary<string,float> guardSentAt = new Dictionary<string,float>();
    private readonly Dictionary<string,string> sampledTransition = new Dictionary<string,string>();
    private readonly Dictionary<string,string> sentTransition = new Dictionary<string,string>();
    private readonly List<string> page = new List<string>(4);
    private readonly Dictionary<string,(string levels,float until)> sightSamples = new Dictionary<string,(string,float)>();

    internal bool Visibility(string id, Entity target, out Detection.DetectionState state)
    {
        state=default;
        if (!sightSamples.TryGetValue(id,out var sample) || Time.realtimeSinceStartup>sample.until) return false;
        int index=-1;for(int i=0;i<crew.Count;i++)if(crew[i]==target){index=i;break;}
        if(index<0 || index>=sample.levels.Length)return false;
        char code=sample.levels[index];
        if (code=='x') return false;
        state=(Detection.DetectionState)(code-'0');
        return state==Detection.DetectionState.NotVisible || state>=Detection.DetectionState.Visible;
    }

    [HarmonyPatch(typeof(Detection),"determineVisibility")]
    internal static class ObserverVisibility
    {
        private static void Postfix(Detection __instance,TrackedObject __0,ref Detection.DetectionState __result)
        {
            if (NativeInput.Probe!=null && __0!=null)
                NativeInput.Probe.SyncSight(__instance.character,__0.objRef,ref __result);
        }
    }

    private static TrackedObject Tracked(Detection sensor, MiCharacter actor)
    {
        var entries=sensor?.m_liObjectsInRange;
        if (entries==null) return null;
        for(int i=0,count=entries.Count;i<count;i++)
        {
            var entry=entries[i];
            if(entry!=null && entry.objRef==actor) return entry;
        }
        return null;
    }

    private string SeenCrew(Detection sensor,MiCharacter a,MiCharacter b)
    {
        if(crew.Count==0)SetCrew(a,b,null);
        var levels=new char[crew.Count];Array.Fill(levels,sensor==null?'x':'0');
        var entries=sensor?.m_liObjectsInRange;
        if(entries!=null)for(int i=0,count=entries.Count;i<count;i++)
        {
            var entry=entries[i];if(entry==null)continue;
            for(int n=0;n<crew.Count;n++)if(entry.objRef==crew[n]){levels[n]=(char)('0'+(int)entry.state);break;}
        }
        return new string(levels);
    }

    private bool DetectingCrew(MiCharacter guard,string levels)
    {
        if(guard.bIsCivilian())return false;
        foreach(char level in levels)
            if(level!='x'&&level-'0'>=(int)Detection.DetectionState.PreDetected)return true;
        var focus=guard.controllerAI?.m_detectionOrientation?.m_objFocus?.objRef;
        if(focus==null)return false;
        for(int n=0;n<crew.Count&&n<levels.Length;n++)
            if(crew[n]!=null&&focus==crew[n]&&levels[n]!='x'&&levels[n]-'0'>=(int)Detection.DetectionState.Visible)return true;
        return false;
    }

    private static void ApplySeen(Detection sensor, MiCharacter actor, char code)
    {
        if(sensor==null || actor==null || code=='x') return;
        var state=(Detection.DetectionState)(code-'0');
        var entry=Tracked(sensor,actor);
        if(entry==null && state>=Detection.DetectionState.Visible)
            entry=sensor.addObjectToTrack(actor);
        if(entry!=null && entry.state!=state) sensor.updateObjState(entry,state);
    }

    private void ApplyDetectionFeedback(MiCharacter guard,string token)
    {
        var parts=token.Split(':');if(parts.Length!=4)return;
        var sensor=guard.controllerAI?.m_detection;
        var sight=guard.controllerAI?.m_detectionOrientation;
        if(sensor==null||sight==null)return;
        float range=float.Parse(parts[2],CultureInfo.InvariantCulture);
        int index=int.Parse(parts[3],CultureInfo.InvariantCulture);
        // Only replay an explicitly identified crew focus. A non-crew focus
        // (corpse, coin, etc.) is left to its existing interaction replay.
        if(index>=0&&index<crew.Count&&crew[index]!=null)
        {
            var entry=Tracked(sensor,crew[index])??sensor.addObjectToTrack(crew[index]);
            if(entry!=null&&sight.m_objFocus!=entry)sight.setFocusObject(entry.eType,entry);
        }
        else if(index==-1&&sight.m_objFocus!=null)
        {
            foreach(var actor in crew)if(actor!=null&&sight.m_objFocus.objRef==actor)
            {sight.endFocus();break;}
        }
        // This is a distance, not a guessed percentage. Native updateViewCone
        // reads it directly for its yellow fill; do not run a second fill clock.
        sensor.m_fDetectionRange=range;
        sensor.updateViewCone();
    }

    private string Target(AIHandler ai, MiCharacter a, MiCharacter b)
    {
        var target = ai.transTarget;
        if(target==a)return "A";if(target==b)return "B";
        foreach(var actor in crew)if(target==actor)return GuestMove.Identity(actor);
        return "-";
    }

    internal string Write(Dictionary<string,MiCharacter> guards, MiCharacter a, MiCharacter b, string alarm, int budget,string house="-")
    {
        if(crew.Count==0)SetCrew(a,b,null);
        string header = " P " + Health(a) + " " + Health(b) + " " + house;
        try { return WritePage(guards,a,b,alarm,budget,header); }
        catch (Exception ex)
        {
            if (Time.realtimeSinceStartup >= nextLog)
            { nextLog=Time.realtimeSinceStartup+2f; MoveProbePlugin.ProbeLog.LogWarning("Pursuit snapshot: "+ex.GetType().Name); }
            return header;
        }
    }

    private string WritePage(Dictionary<string,MiCharacter> guards,MiCharacter a,MiCharacter b,string alarm,int budget,string header)
    {
        bool active = alarm.StartsWith("Alarm",StringComparison.Ordinal);
        if (active) settleUntil = Time.time + 6f;
        if (Time.realtimeSinceStartup >= nextScan || active != lastActive)
        {
            nextScan = Time.realtimeSinceStartup + 0.12f;
            lastActive = active;
            candidates.Clear();
            foreach (var entry in guards)
            {
                var c = entry.Value;
                if (c == null) continue;
                var ai = c.controllerAI?.m_ai;
                if (ai == null || c.health == null || c.health.eHealthState != MiCharacterHealth.HealthState.Alive) continue;
                var position = c.transform.position;
                bool near = NearCrew(position,a,b);
                // A guard can engage before the mission-level alarm changes.
                bool engaged = ai.eAlertState == AIHandler.AlertState.Alert || ai.eAlertState == AIHandler.AlertState.Suspicious;
                bool striking = ObserveStrike(entry.Key,ObservedCommand(c),a,b);
                if (ai.alarmSquad != null || near ||
                    engaged && (near || Target(ai,a,b) != "-") || striking)
                    tracked[entry.Key] = Time.time + 6f;
                if (tracked.ContainsKey(entry.Key))
                {
                    candidates.Add(entry.Key);
                    var sensor=c.controllerAI.m_detection;
                    sampledSight[entry.Key]=SeenCrew(sensor,a,b);
                    sampledDetecting[entry.Key]=DetectingCrew(c,sampledSight[entry.Key]);
                    sampledTransition[entry.Key]=$"{ai.eAlertState}/{Target(ai,a,b)}/{ReadIcons(c)}/{c.movementType.eMovementStyle}/{c.movementType.eMovementModifier}/{striking}";
                }
            }
            candidates.Sort(StringComparer.Ordinal);
        }
        if (candidates.Count == 0) return header;
        var wire = new StringBuilder(header);
        cursor %= candidates.Count;
        page.Clear();
        // Put one rotating record first even when the packet fits fewer than
        // four guards. Advancing by the planned page starved its clipped tail.
        page.Add(candidates[cursor]);
        onWire.Clear();
        // One sustained detection slot keeps an already observing enemy fresh,
        // rather than prioritizing only its first state transition. The first
        // ordinary rotation slot remains first even with a one-record budget.
        string observer=null;float observerSentAt=float.NegativeInfinity;
        foreach(string id in candidates)
        {
            if(page.Contains(id)||!sampledDetecting.TryGetValue(id,out bool detecting)||!detecting)continue;
            float sent=guardSentAt.TryGetValue(id,out float at)?at:float.NegativeInfinity;
            if(observer==null||sent<observerSentAt){observer=id;observerSentAt=sent;}
        }
        if(observer!=null)page.Add(observer);
        // Keep one changed-state slot, or the original two when no observer
        // needs refreshing. The fourth slot remains ordinary fair rotation.
        foreach (var id in candidates)
        {
            if(page.Count>=3)break;
            bool changed=sentTransition.TryGetValue(id,out var transition) && transition!=sampledTransition[id];
            if (!page.Contains(id) && (changed || sampledSight[id].Trim('0').Length!=0 && (!sentSight.TryGetValue(id,out var previous) || previous!=sampledSight[id])))
                page.Add(id);
        }
        for(int n=1;n<candidates.Count && page.Count<4;n++)
        {
            var id=candidates[(cursor+n)%candidates.Count];
            if(!page.Contains(id)) page.Add(id);
        }
        foreach (var id in page)
        {
            if (!guards.TryGetValue(id,out var c) || c == null || c.health == null ||
                c.health.eHealthState != MiCharacterHealth.HealthState.Alive || c.controllerAI?.m_ai == null)
            continue;
            var ai = c.controllerAI.m_ai;
            var cmd = ObservedCommand(c);
            ObserveStrike(id,cmd,a,b);
            bool travelling=c.movement?.target!=null && !c.movement.isStoppedOrInactive;
            var goal = c.transform.position;
            string action = "-", victim = "-";
            int serial = 0;
            if (strikes.TryGetValue(id,out var strike) && Time.time < strike.until &&
                (!travelling || Offensive(cmd?.m_skill?.eSkillType)))
            { action = strike.kind; victim = strike.victim; serial = strike.serial; }
            else if ((travelling || active || ai.alarmSquad != null || ai.eAlertState != AIHandler.AlertState.Idle || Waypoint(id,out _)) &&
                (travelling || Search(CommandKind(cmd)) || cmd == null && Waypoint(id,out _)))
            {
                // The host controller has already resolved its random search destination.
                var proposed = c.movement?.target?.v3Target ?? cmd?.m_target?.v3Target ?? c.controller.v3CurrentTargetPosition;
                if (c.movement?.target==null && (cmd == null || cmd.m_skill?.eSkillType is Skill.SkillType.Investigate or Skill.SkillType.InvestigateBadFortune) &&
                    Waypoint(id,out var randomPoint)) proposed=randomPoint;
                if (Vector3.Distance(goal,proposed) <= 50f && NavMesh.SamplePosition(proposed,out var nav,1f,-1))
                { action = "Search"; goal = nav.position; }
            }
            string record = string.Format(CultureInfo.InvariantCulture," {0} {1} {2} {3} {4:F1}",
                id,ai.eAlertState,Target(ai,a,b),NavPoint(c.transform.position),c.transform.eulerAngles.y) + $" {Math.Max(0,c.health.iHealth)} {NavPoint(goal)} {action} {serial} {victim}";
            var controller=c?.controllerAI;
                if(controller==null)continue; // Controller changed during capture.
                var sight = controller.m_detectionOrientation;
            var sensor=c.controllerAI.m_detection;
            record += " "+(sight==null?"x":sight.qDetectionRot.eulerAngles.y.ToString("R",CultureInfo.InvariantCulture))+":"+
                NativeDetectionProfile.Capture(sensor?.m_dataSwitcher?.m_dataInstanceCurrent);
            int focus=-1;
            if(sight?.m_objFocus?.objRef!=null)
            {focus=-2;for(int n=0;n<crew.Count;n++)if(sight.m_objFocus.objRef==crew[n]){focus=n;break;}}
            record+=":"+(sensor==null?"0":sensor.fDetectionRangeCurrent.ToString("R",CultureInfo.InvariantCulture))+":"+focus.ToString(CultureInfo.InvariantCulture);
            string visibility=SeenCrew(sensor,a,b);
            record+=" "+visibility;
            var movement=c.movementType;
            record+=$" {(int)movement.eMovementStyle} {(int)movement.eMovementModifier} {(travelling ? 1 : 0)} {ReadIcons(c)}";
            if (wire.Length + record.Length > budget) continue;
            wire.Append(record);
            if ((active || ai.eAlertState!=AIHandler.AlertState.Idle) &&
                (!hostLogAt.TryGetValue(id,out float loggedAt) || Time.realtimeSinceStartup>=loggedAt))
            {
                hostLogAt[id]=Time.realtimeSinceStartup+1f;
                MoveProbePlugin.ProbeLog.LogInfo($"Pursuit host guard={id} alert={ai.eAlertState}/{Target(ai,a,b)} pos={NavPoint(c.transform.position)} goal={NavPoint(goal)} action={action} travel={travelling} gait={movement.eMovementStyle}/{movement.eMovementModifier} icons={ReadIcons(c)} command={DescribeCommand(cmd)} speed={c.movement.fMaxSpeed:F2} mult={c.movement.fSpeedMultiplier:F2} realSpeed={c.movement.m_fMaxSpeedReal:F2} driver={c.movement.eMovementDriver} moveType={c.movementType.eMovementType}");
            }
            onWire.Add(id);
            guardSentAt[id]=Time.realtimeSinceStartup;
            sentSight[id]=visibility;
            sentTransition[id]=sampledTransition[id];
            if (tracked.TryGetValue(id,out float expires) && Time.time >= expires && Time.time >= settleUntil)
                tracked.Remove(id);
        }
        // Advance past records actually transmitted. A one-record budget is
        // fair; a four-record budget does not repeat three guards next frame.
        int advance=1;
        while (advance<candidates.Count && onWire.Contains(candidates[(cursor+advance)%candidates.Count])) advance++;
        cursor=(cursor+advance)%candidates.Count;
        return wire.ToString();
    }

    internal void ApplyCrewHealth(string[] combat,MiCharacter a,MiCharacter b)
    {
        ReconcileHealth(a,int.Parse(combat[20],CultureInfo.InvariantCulture),combat[21]);
        if(b!=a)ReconcileHealth(b,int.Parse(combat[22],CultureInfo.InvariantCulture),combat[23]);
    }

    private void ApplySightPose(MiCharacter c,string id,string token,float bodyYaw)
    {
                var controller=c?.controllerAI;
                if(controller==null)return; // Possession replaces the AI controller.
                var sight = controller.m_detectionOrientation;
                var sightParts=token.Split(':');
                if(sightParts.Length>=2&&sightParts[1]!="-"&&controller.m_detection!=null)
                {
                    if(!sightProfiles.TryGetValue(id,out var cached)||cached.token!=sightParts[1])
                    {
                        if(NativeDetectionProfile.TryBuild(sightParts[1],out var profile))
                            sightProfiles[id]=cached=(sightParts[1],profile);
                    }
                    if(cached.data!=null)controller.m_detection.applyData(cached.data,true);
                }
                if (sight != null && sightParts[0] != "x")
                {
                    float angle=float.Parse(sightParts[0],CultureInfo.InvariantCulture);
                    float now=Time.realtimeSinceStartup;
                    sightDirections.TryGetValue(sight.Pointer,out var previous);
                    bool fresh=previous!=null&&now<=previous.until;
                    sightDirections[sight.Pointer]=new SightSample{owner=this,actor=c,angle=angle,bodyYaw=bodyYaw,
                        fromAngle=fresh?previous.Angle(now):angle,fromBody=fresh?previous.Body(now):bodyYaw,
                        range=sightParts.Length==4?float.Parse(sightParts[2],CultureInfo.InvariantCulture):null,
                        at=now,duration=fresh?Mathf.Clamp(now-previous.at,.025f,.15f):.025f,until=now+.55f};
                    RestoreSight(sight);
                }
    }
    internal void Apply(string[] combat, Dictionary<string,MiCharacter> guards, MiCharacter a, MiCharacter b,
        string protectedA, string protectedB, HashSet<string> protectedExtra=null)
    {
        if(crew.Count==0)SetCrew(a,b,null);
        ApplyCrewHealth(combat,a,b);
        if (!GuardAuthority.Available) { ClearDetectionView();authority.Release(); return; }
        for (int i=25; i<combat.Length; i+=20)
        {
            string id = combat[i];
            if (!guards.TryGetValue(id,out var c) || c == null)
            {
                if (Time.realtimeSinceStartup >= nextLog) { nextLog=Time.realtimeSinceStartup+1f; MoveProbePlugin.ProbeLog.LogInfo($"Pursuit unmatched guard={id}"); }
                continue;
            }
            if((EffectProtection?.Invoke(c)==true||id==protectedA||id==protectedB||protectedExtra?.Contains(id)==true) && c.health?.eHealthState==MiCharacterHealth.HealthState.Alive)
            {try{ApplySightPose(c,id,combat[i+14],float.Parse(combat[i+6],CultureInfo.InvariantCulture));}catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Protected sight: "+ex.GetType().Name);}}
            if (id == protectedA || id == protectedB || protectedExtra?.Contains(id)==true ||
                EffectProtection?.Invoke(c)==true ||
                c.health == null || c.health.eHealthState != MiCharacterHealth.HealthState.Alive) {
                ForgetDetectionView(id,false);
                authority.Release(id); continue;
            }
            var ai = c.controllerAI?.m_ai;
            if (ai == null) continue;
            string stage = "pose";
            try
            {
                var alert = (AIHandler.AlertState)Enum.Parse(typeof(AIHandler.AlertState),combat[i+1]);
                string target = combat[i+2], localTarget = Target(ai,a,b);
                string localAlert = ai.eAlertState.ToString();
                var pos = CrewSync.ReadPosition(combat,i+3);
                float gap = Vector3.Distance(c.transform.position,pos);
                bool engaged = alert == AIHandler.AlertState.Alert || alert == AIHandler.AlertState.Suspicious;
                float yaw=float.Parse(combat[i+6],CultureInfo.InvariantCulture);
                string action = combat[i+11];
                float now=Time.realtimeSinceStartup;
                if (!replicas.TryGetValue(id,out var replica)) replicas[id]=replica=new Replica();
                if(!replica.observed || Vector3.Distance(c.transform.position,replica.progressPoint)>0.12f)
                { replica.progressPoint=c.transform.position; replica.progressAt=now; }
                float interval=now-replica.seenAt;
                var previousHost=replica.host;
                bool fresh=replica.observed && interval < 0.9f;
                bool hostMoving=fresh && Vector3.Distance(pos,replica.host)>0.06f;
                bool localMoving=fresh && Vector3.Distance(c.transform.position,replica.local)>0.06f;
                if (action=="Search") replica.searchUntil=now+2f;
                // Maintain nearby patrols before an alarm. Waiting until the
                // alert transition allowed a local patrol to be 14m away when
                // it received the host's first pursuit destination.
                bool nearby=NearCrew(pos,a,b);
                bool mirror=nearby || action!="-" || engaged || target!="-" || now<replica.searchUntil;
                bool alreadyOwned=authority.Owns(id,c);
                if(mirror)
                {
                    if(!authority.Acquire(id,c)) continue;
                    if(!alreadyOwned) { replica.forceOrder=true; replica.nextOrderAt=0f; }
                }
                else authority.Release(id);
                icons.Record(c,int.Parse(combat[i+19],CultureInfo.InvariantCulture));
                // A searching guard walks to the host route. Limit instant
                // relocation to initial placement or a genuine host teleport.
                bool discontinuity = !replica.observed || fresh && Vector3.Distance(pos,replica.host)>10f;
                if (gap > 12f && discontinuity && action=="-" && !engaged && target=="-" &&
                    combat[12]=="None" && ai.alarmSquad==null && now>=replica.searchUntil &&
                    NavMesh.SamplePosition(pos,out var hit,1f,-1))
                {
                    c.warp(hit.position,Quaternion.Euler(0,yaw,0));
                    MoveProbePlugin.ProbeLog.LogInfo($"Guard placement guard={id} gap={gap:F1}");
                    gap=Vector3.Distance(c.transform.position,pos);
                }
                else if (!hostMoving && !localMoving && gap<0.6f &&
                    Mathf.Abs(Mathf.DeltaAngle(c.transform.eulerAngles.y,yaw)) > 8f && c.navigation != null)
                    c.navigation.setOrientation(Quaternion.Euler(0,yaw,0));
                replica.host=pos; replica.local=c.transform.position; replica.seenAt=now; replica.observed=true;
                ApplySightPose(c,id,combat[i+14],float.Parse(combat[i+6],CultureInfo.InvariantCulture));
                stage = "sight";
                sightSamples[id]=(combat[i+15],Time.realtimeSinceStartup+0.55f);
                for(int n=0;n<crew.Count;n++)ApplySeen(c.controllerAI.m_detection,crew[n],combat[i+15][n]);
                ApplyDetectionFeedback(c,combat[i+14]);
                var sensor=c.controllerAI.m_detection;
                if(sensor!=null)
                {
                    bool detecting=false;
                    foreach(char level in combat[i+15])if(level!='x'&&level-'0'>=(int)Detection.DetectionState.PreDetected)detecting=true;
                    var feedback=combat[i+14].Split(':');
                    if(feedback.Length==4&&int.TryParse(feedback[3],out int focus)&&focus>=0&&focus<combat[i+15].Length&&combat[i+15][focus]>='3')detecting=true;
                    // Civilian observation still reaches the authoritative AI,
                    // but must not take over the player's enemy-view display.
                    detecting&=!c.bIsCivilian();
                    if(detecting)
                    {
                        sensor.setViewConeActive(true);
                        float until=Time.realtimeSinceStartup+.55f;
                        detectionViews[id]=(c,until);
                        if(detectedId==id)detectedViewUntil=until;
                        if(detectedView==null)RestoreDetectionView();
                    }
                    else ForgetDetectionView(id,true);
                    sensor.updateViewCone();
                }
                stage = "alert";
                if (ai.eAlertState != alert) ai.setAlertState(alert);
                stage = "movement type";
                var hostStyle=(MiCharacterMovementType.MovementStyle)int.Parse(combat[i+16],CultureInfo.InvariantCulture);
                var hostModifier=(MiCharacterMovementType.MovementModifier)int.Parse(combat[i+17],CultureInfo.InvariantCulture);
                if(mirror && (c.movementType.eMovementStyle!=hostStyle || c.movementType.eMovementModifier!=hostModifier))
                    ai.setMovementType(hostStyle,hostModifier);
                // GoTo can outlive a walk/run change on the same destination.
                // Update its modifiers even when Follow keeps the existing path.
                if(mirror) ApplyGoToGait(c);
                stage = "target";
                if (target != localTarget)
                {
                    if (target != "-")
                    {
                        var targetActor=ResolveCrew(target,a,b);
                        if(targetActor==null)throw new InvalidOperationException("Missing confirmed crew target");
                        ai.setTarget(targetActor,new Il2CppSystem.Nullable<Vector3>(targetActor.transform.position));
                    }
                    else if (localTarget != "-") ai.setTarget(null,new Il2CppSystem.Nullable<Vector3>(CrewSync.ReadPosition(combat,i+8)));
                }
                stage = "health";
                ReconcileHealth(c,int.Parse(combat[i+7],CultureInfo.InvariantCulture),"Alive");
                stage = "head markers";
                ApplyIcons(c,int.Parse(combat[i+19],CultureInfo.InvariantCulture));
                stage = "order";
                if (action == "Search")
                {
                    var goal = CrewSync.ReadPosition(combat,i+8);
                    // Once the host's native movement driver is active, its
                    // destination is confirmed. Replaying that whole leg with
                    // its actual gait avoids the stop/start chase of trailing
                    // position samples, especially when reinforcements run.
                    Vector3 follow;
                    if(combat[i+18]=="1")
                    {
                        if(!replica.legGoal.HasValue || Vector3.Distance(replica.legGoal.Value,goal)>0.35f)
                            replica.nextOrderAt=0f;
                        replica.legGoal=goal; replica.lead=null; replica.forwardSamples=0;
                        follow=goal;
                    }
                    else follow = SearchDestination(c,replica,previousHost,pos,goal,gap,now,interval,
                        fresh && target=="-" && alert!=AIHandler.AlertState.Alert);
                    SetWaypoint(id,follow,2f);
                    Follow(c,replica,follow,now,localMoving);
                }
                else
                {
                    replica.legGoal=null; replica.lead=null; replica.forwardSamples=0;
                    // The host may spend several snapshots changing its search
                    // command between legs. Keep a guest random roll in that
                    // interval at the host's last observed point.
                    if (action=="-" && now<replica.searchUntil) SetWaypoint(id,pos,0.6f);
                    else waypoints.Remove(id);
                    if (action=="-" && mirror)
                        Follow(c,replica,pos,now,localMoving);
                    else replica.issued=null;
                }
                if (action != "-" && action != "Search")
                {
                    int serial = int.Parse(combat[i+12],CultureInfo.InvariantCulture);
                    var targetCrew = ResolveCrew(combat[i+13],a,b);
                    if(targetCrew==null)throw new InvalidOperationException("Missing confirmed attack victim");
                    var kind = (Skill.SkillType)Enum.Parse(typeof(Skill.SkillType),action);
                    if ((!played.TryGetValue(id,out int old) || serial!=old) && targetCrew.health.eHealthState==MiCharacterHealth.HealthState.Alive)
                    {
                        var running=c.controller.skillCommandProcess;
                        if (!(running?.m_skill?.eSkillType==kind && running.m_target?.m_entityTarget!=null &&
                            running.m_target.m_entityTarget.bAsCharacter(out var current) && current==targetCrew))
                        {
                            if(!IssueGuard(c,kind,new MiCharacterTarget(targetCrew,null))) continue;
                        }
                        played[id]=serial;
                        MoveProbePlugin.ProbeLog.LogInfo($"Pursuit attack guard={id} action={action} seq={serial} target={combat[i+13]}");
                    }
                }
                if ((engaged || gap>1f || localTarget != target || localAlert != combat[i+1]) &&
                    (!guestLogAt.TryGetValue(id,out float loggedAt) || now>=loggedAt))
                {
                    guestLogAt[id]=now+1f;
                    MoveProbePlugin.ProbeLog.LogInfo($"Pursuit guard={id} host={alert}/{target} localBefore={localAlert}/{localTarget} localAfter={ai.eAlertState}/{Target(ai,a,b)} gap={gap:F1} sightCrew={combat[i+15]} action={action} goal={combat[i+8]},{combat[i+9]},{combat[i+10]} authority={authority.Owns(id,c)}/{ai.bAIIsBlocked()} input={DescribeCommand(c.controller.skillCommand)} process={DescribeCommand(c.controller.skillCommandProcess)} moving={localMoving} travel={combat[i+18]} gait={hostStyle}/{hostModifier}->{c.movementType.eMovementStyle}/{c.movementType.eMovementModifier} icons={combat[i+19]}->{ReadIcons(c)} speed={c.movement.fMaxSpeed:F2} mult={c.movement.fSpeedMultiplier:F2} realSpeed={c.movement.m_fMaxSpeedReal:F2} driver={c.movement.eMovementDriver} moveType={c.movementType.eMovementType} ladder={InteractionSync.Climbing(c)}");
                }
            }
            catch (Exception ex)
            {
                if (Time.realtimeSinceStartup >= nextLog)
                { nextLog=Time.realtimeSinceStartup+2f; MoveProbePlugin.ProbeLog.LogWarning($"Pursuit guard {id} stage={stage}: {ex}"); }
            }
        }
    }

    private static Vector3 SearchDestination(MiCharacter guard,Replica replica,Vector3 previousHost,Vector3 host,
        Vector3 goal,float gap,float now,float interval,bool canLead)
    {
        bool sameLeg=replica.legGoal.HasValue && Vector3.Distance(replica.legGoal.Value,goal)<0.35f;
        if (!sameLeg) replica.nextOrderAt=0f;
        replica.legGoal=goal;
        var step=host-previousHost;
        var direction=(goal-host).normalized;
        float moved=Vector3.Distance(previousHost,host);
        bool aligned=sameLeg && canLead && gap<1.75f && interval>0f && interval<=0.65f;
        if (aligned && moved>0.06f && Vector3.Dot(direction,step.normalized)>0.8f)
        {
            replica.forwardSamples++;
            replica.advancedAt=now;
            // Let an existing short, verified corridor run. Tiny changes in
            // packet arrival times must not turn an already-ahead guard back.
            if (replica.lead.HasValue && Vector3.Dot(replica.lead.Value-host,direction)>0.4f &&
                Vector3.Distance(replica.lead.Value,guard.transform.position)>0.45f &&
                Vector3.Dot(replica.lead.Value-guard.transform.position,direction)>0f) return replica.lead.Value;
            if (replica.forwardSamples>=2 && guard.navigation!=null)
            {
                float lead=Math.Min(1.5f,Math.Max(0.55f,moved/interval*0.45f));
                var candidate=Vector3.MoveTowards(host,goal,lead);
                int agent=guard.navigation.agentType, mask=guard.navigation.areaMask;
                // The shipping IL2CPP setter for NavMeshQueryFilter.agentTypeID
                // is an unconditional throw. Call the retained typed natives.
                if (NavMesh.SamplePositionFilter(host,out var from,0.35f,agent,mask) &&
                    NavMesh.SamplePositionFilter(candidate,out var to,0.35f,agent,mask) && from.mask==to.mask &&
                    Vector3.Distance(host,to.position)<=1.5f && !NavMesh.RaycastFilter(from.position,to.position,out _,agent,mask) &&
                    NavMesh.SamplePositionFilter(guard.transform.position,out var local,0.35f,agent,mask) && local.mask==from.mask &&
                    !NavMesh.RaycastFilter(local.position,to.position,out _,agent,mask))
                { replica.lead=to.position; return to.position; }
            }
        }
        else if (aligned && moved<=0.06f && replica.lead.HasValue && now-replica.advancedAt<0.16f)
            return replica.lead.Value;
        else replica.forwardSamples=0;
        if (replica.lead.HasValue) replica.nextOrderAt=0f;
        replica.lead=null;
        return Vector3.Distance(host,goal)<0.45f ? goal : host;
    }

    private static void Follow(MiCharacter guard,Replica replica,Vector3 point,float now,bool moving)
    {
        if(InteractionSync.Climbing(guard)) return;
        if (now<replica.nextOrderAt) return;
        float remaining=Vector3.Distance(guard.transform.position,point);
        var current=guard.controller.v3CurrentTargetPosition;
        var command=guard.controller.skillCommandProcess ?? guard.controller.skillCommandActive ?? guard.controller.skillCommand;
        // A destination matching numerically does not mean the right command
        // is running: an old Investigate/Gun can keep choosing points/firing.
        bool replace=replica.forceOrder || command!=null && CommandKind(command)!=Skill.SkillType.GoTo;
        var native=guard.movement;
        bool onPath=native?.target!=null && !native.isStoppedOrInactive &&
            Vector3.Distance(native.target.v3Target,point)<0.5f;
        if(!replace && onPath && now-replica.progressAt<1.2f) return;
        if (!replace && Vector3.Distance(current,point)<0.5f && (remaining<0.5f || moving)) return;
        if (!replace && remaining<0.25f && !moving &&
            (!replica.issued.HasValue || Vector3.Distance(replica.issued.Value,point)<0.1f)) return;
        // Do not continually reset an accepted path or race a native search
        // that has already chosen the same point.
        replica.nextOrderAt=now+0.32f;
        if (guard.navigation==null) return;
        if (!NavMesh.SamplePositionFilter(point,out var nav,0.8f,guard.navigation.agentType,guard.navigation.areaMask) ||
            Math.Abs(nav.position.y-point.y)>0.45f) return;
        if(!IssueGuard(guard,Skill.SkillType.GoTo,new MiCharacterTarget(nav.position))) return;
        replica.forceOrder=false;
        replica.issued=nav.position;
        replica.progressAt=now;
    }

    // Process dispatch runs under our AI decision lease. Check the retained
    // native command and target, allowing normalized default locomotion.
    private static bool IssueGuard(MiCharacter guard,Skill.SkillType kind,MiCharacterTarget target)
    {
        var ai=guard.controllerAI?.m_ai;
        if(ai==null || InteractionSync.Climbing(guard)) return false;
        // setSkillExtern(process=true) does not consume the old input slot.
        // A retained patrol/Investigate order can resume after our GoTo ends,
        // changing alert state and creating a fresh local route under the lease.
        if(guard.controller.skillCommand!=null) guard.controller.skillCommand=null;
        bool accepted=ai.setSkillExtern(kind,target,NPCClique.SpreadType.None,false,true,null,null);
        var process=guard.controller.skillCommandProcess;
        accepted=accepted && CommandKind(process)==kind && process.m_target!=null &&
            (target.m_entityTarget!=null ? process.m_target.m_entityTarget!=null &&
                process.m_target.m_entityTarget.Pointer==target.m_entityTarget.Pointer :
                process.m_target.m_entityTarget==null && Vector3.Distance(process.m_target.v3Target,target.v3Target)<0.2f);
        if(accepted && kind==Skill.SkillType.GoTo) ApplyGoToGait(guard);
        if(!accepted) MoveProbePlugin.ProbeLog.LogInfo("Guard replay not accepted id="+Melee.GuardId(guard)+" skill="+kind+" process="+DescribeCommand(process));
        return accepted;
    }

    private static void ApplyGoToGait(MiCharacter guard)
    {
        var process=guard.controller.skillCommandProcess;
        if(CommandKind(process)!=Skill.SkillType.GoTo || InteractionSync.Climbing(guard)) return;
        process.setModifier(SkillCommand.ExecutionModifier.Run,guard.movementType.eMovementStyle==MiCharacterMovementType.MovementStyle.Run);
        process.setModifier(SkillCommand.ExecutionModifier.WasCrawling,guard.movementType.eMovementStyle==MiCharacterMovementType.MovementStyle.Crouch);
    }

    internal void ReconcileHealth(MiCharacter actor,int hp,string expected)
    {
        if (actor == null || actor.health == null) return;
        var health = actor.health;
        // Even Alive can still retain the native Knockout process after an
        // earlier health-only correction. Run the cannon wake lifecycle first.
        try { if(hp>0&&CannonStateSync.ReconcileWake(actor,expected))return; }
        catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Cannon wake sync: "+ex.GetType().Name);return;}
        string local = health.eHealthState.ToString();
        int wanted = Math.Min(hp,health.iHealthMax);
        if (expected == local && (local != "Alive" || wanted == health.iHealth)) return;
        int id=actor.GetInstanceID();
        bool lethal=expected=="Dead" || expected=="Dying" || expected=="PreDying" || expected=="Exterminated";
        // A previous non-lethal wound must not delay a newly lethal snapshot.
        // Use real time: game-over pause freezes Time.time.
        if ((!lethal || local!="Alive") && healthRetry.TryGetValue(id,out float next) && Time.realtimeSinceStartup<next) return;
        healthRetry[id]=Time.realtimeSinceStartup+0.6f;
        try
        {
            applyingHealth=true;
            if (expected=="Alive" && wanted>0 && local=="Alive")
            {
                int delta=wanted-health.iHealth;
                if (delta>0) MiScriptLibraryProject.CharacterHeal(actor,delta);
                else if (delta<0) MiScriptLibraryProject.CharacterDamage(actor,-delta);
            }
            else if (expected=="Alive" && wanted>0 && local=="Dead") MiScriptLibraryProject.CharacterRevive(actor);
            else if (lethal && (local=="Alive" || local=="Knockout")) MiScriptLibraryProject.CharacterKill(actor);
            else if (expected=="Knockout" && local=="Alive") MiScriptLibraryProject.CharacterKnockout(actor);
            else if (expected=="Alive" && local=="Knockout") health.comeTo();
            else return;
            MoveProbePlugin.ProbeLog.LogInfo($"Host health {actor.name}={hp}/{expected}, local result={health.iHealth}/{health.eHealthState}");
        }
        catch(Exception ex) { MoveProbePlugin.ProbeLog.LogWarning($"Health sync {id}: {ex.GetType().Name}"); }
        finally { applyingHealth=false; }
    }
}
