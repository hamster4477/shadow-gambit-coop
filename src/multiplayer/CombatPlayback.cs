using System;
using UnityEngine;
using UnityEngine.AI;
using Mimimi.MiScript;

internal static class AttackRetreat
{
    internal static bool PairedTransition(MiCharacterHealth.HealthState state)
        => state is MiCharacterHealth.HealthState.PreDying or MiCharacterHealth.HealthState.Dying or
            MiCharacterHealth.HealthState.PreKnockout or MiCharacterHealth.HealthState.InProcessOfBeingKnockedOut;

    internal static bool CanReplace(bool approaching, bool canMove, bool nativeLocked)
        => approaching || canMove && !nativeLocked;

    internal static bool MissedNativeStart(float now, float until, float distance, float limit,
        bool started, MiCharacterHealth.HealthState targetState)
        => !started && targetState == MiCharacterHealth.HealthState.Alive && (now >= until || distance > limit);
}

internal sealed class HostClickHistory
{
    private readonly (int sequence,float sent,Vector3 crew)[] views = new (int,float,Vector3)[16];
    internal void Record(int sequence,float now,Vector3 crew) => views[sequence % views.Length] = (sequence,now,crew);
    internal void Clear() => Array.Clear(views,0,views.Length);
    internal float ValidUntil(int sequence,Vector3 crew,Vector3 victim,Vector3 clickPoint)
    {
        if (sequence <= 0) return -1f;
        var view = views[sequence % views.Length];
        if (view.sequence != sequence || Vector3.Distance(view.crew,crew) > 1.2f ||
            Vector3.Distance(view.crew,clickPoint) > 4.65f || Vector3.Distance(victim,clickPoint) > 1f) return -1f;
        return view.sent + 0.24f;
    }
}

internal sealed class AttackCue
{
    private int serial;
    private string skill, id;
    private Vector3 point;
    private float until;
    private float observeAfter;
    private MiCharacter previous;
    private Skill.SkillType previousSkill;

    internal void Issue(MiCharacter victim, Skill.SkillType kind)
    {
        skill = kind == Skill.SkillType.Kill ? "L" : kind == Skill.SkillType.Knockout ? "K" : kind.ToString();
        if (victim == null || victim.guid.isEmpty() || Melee.Decode(skill) == Skill.SkillType.None || InteractionSync.Supported(kind)) return;
        serial = serial == int.MaxValue ? 1 : serial+1;
        id = Melee.GuardId(victim);
        point = victim.transform.position;
        until = Time.time + 1.4f;
        MoveProbePlugin.ProbeLog.LogInfo($"Host attack cue #{serial} {skill} guard {id}");
    }

    private IntPtr pointProcess;
    internal bool ObservePoint(MiCharacter actor)
    {
        // Execution is recorded by SkillThrow.execute after it saves its own
        // command. Controller process slots can disappear before the projectile.
        var cmd = actor?.controller?.skillCommandProcess;
        return cmd?.m_skill != null && Melee.Point(cmd.m_skill.eSkillType);
    }

    internal bool ObservePointCommand(SkillCommand cmd)
    {
        if (cmd?.m_skill == null || !Melee.Point(cmd.m_skill.eSkillType) || InteractionSync.Supported(cmd.m_skill.eSkillType) || cmd.m_target == null)
        { pointProcess=IntPtr.Zero; return false; }
        if (cmd.Pointer != pointProcess || skill != cmd.m_skill.eSkillType.ToString() || Vector3.Distance(point,cmd.m_target.v3Target)>0.1f)
        {
            pointProcess=cmd.Pointer;
            skill=cmd.m_skill.eSkillType.ToString(); id="0"; point=cmd.m_target.v3Target;
            serial=serial==int.MaxValue ? 1 : serial+1; until=Time.time+3f;
            MoveProbePlugin.ProbeLog.LogInfo($"Host point execution #{serial} {skill} at {CrewSync.Position(point)}");
        }
        return true;
    }

    // A acts through the native controller. Sample each game frame, including
    // the process command while its attack animation is already in progress.
    internal void Observe(MiCharacter actor)
    {
        if(actor?.controller==null || actor.health?.eHealthState!=MiCharacterHealth.HealthState.Alive) { Clear(); return; }
        if (Time.time < observeAfter) return;
        try
        {
            if (ObservePoint(actor)) { previous=null; return; }
            var cmd = actor.controller.skillCommandProcess;
            if (!Target(actor, cmd, out var victim))
            {
                cmd = actor.controllerPlayer?.skillCommand;
                if (!Target(actor, cmd, out victim)) { previous = null; return; }
                // A selected target alone is not proof that a queued order
                // survived cancellation. Wait for a process or an actual impact.
                if (victim.health.eHealthState == MiCharacterHealth.HealthState.Alive) return;
            }
            var kind = cmd.m_skill.eSkillType;
            if (previous != victim || previousSkill != kind)
            {
                previous = victim; previousSkill = kind;
                Issue(victim, kind);
            }
            else if (victim.health.eHealthState is MiCharacterHealth.HealthState.Dying or
                MiCharacterHealth.HealthState.InProcessOfBeingKnockedOut)
                until = Time.time + 0.5f;
        }
        catch (Exception ex)
        {
            observeAfter = Time.time + 2f;
            MoveProbePlugin.ProbeLog.LogWarning($"Host attack observation: {ex.GetType().Name}");
        }
    }

    private static bool Target(MiCharacter actor, SkillCommand cmd, out MiCharacter victim)
    {
        victim = null;
        if (cmd?.m_skill == null || cmd.m_target?.m_entityTarget == null) return false;
        var kind = cmd.m_skill.eSkillType;
        if (InteractionSync.Supported(kind) || !Melee.Contact(kind) && Melee.Decode(kind.ToString()) == Skill.SkillType.None) return false;
        return cmd.m_target.m_entityTarget.bAsCharacter(out victim) && victim != null && victim.bControllerAI() &&
            !victim.guid.isEmpty() && (!Melee.Contact(kind) || Vector3.Distance(actor.transform.position,victim.transform.position) <= 4f);
    }

    internal string Wire(bool stillExecuting = false) => id != null && (Time.time < until || stillExecuting) ?
        $"{serial} {skill} {id} {CrewSync.Position(point)}" : "0 - 0 0 0 0";

    internal void Clear() { id = null; until = 0f; }
}

internal sealed class AttackPlayback
{
    internal void Reset(MiCharacter actor){Finish(actor);lastSerial=null;}
    private string lastSerial;
    private float protectUntil;
    private MiCharacter victim;
    private Vector3 contactPoint;
    private Vector3 startPosition;
    private Skill.SkillType skill;
    internal string ProtectedId { get; private set; }
    internal bool PlayingContact => victim != null && Melee.Contact(skill);

    internal bool Apply(string[] cue, int offset, MiCharacter actor, Vector3 hostPosition, bool released = false, bool allowNewContact = true,bool controlledActor=false)
    {
        if(actor?.controller==null || actor.controllerPlayer==null || actor.health?.eHealthState!=MiCharacterHealth.HealthState.Alive)
        { Finish(actor); return false; }
        var serial = cue[offset];
        if (serial != "0" && serial != lastSerial)
        {
            var pointKind=Melee.Decode(cue[offset+1]);
            if ((offset == 6 || controlledActor) && Melee.Contact(pointKind))
            {
                // B's host can keep broadcasting an impact cue briefly after
                // releasing B. Do not restart its local approach at that point.
                if (released) { lastSerial = serial; Finish(actor); return false; }
                if (!allowNewContact) return false;
            }
            Finish(actor);
            if (Melee.Point(pointKind))
            {
                if (!allowNewContact) return false;
                lastSerial=serial;
                if (actor.health.eHealthState!=MiCharacterHealth.HealthState.Alive || actor.controller.getSkill(pointKind)==null) return false;
                skill=pointKind; protectUntil=Time.time+3f;
                MiScriptLibraryProject.CharacterExecuteSkill(actor,pointKind,CrewSync.ReadPosition(cue,offset+3));
                MoveProbePlugin.ProbeLog.LogInfo($"Guest point playback #{serial} {pointKind}");
                return true;
            }
            var target = Melee.Resolve(cue[offset+2]);
            if (target == null) return false;
            var kind = Melee.Decode(cue[offset+1]);
            var point = CrewSync.ReadPosition(cue,offset+3);
            if (actor.health == null || actor.health.eHealthState != MiCharacterHealth.HealthState.Alive ||
                target.health == null || target.health.eHealthState != MiCharacterHealth.HealthState.Alive ||
                actor.controller.getSkill(kind) == null) { lastSerial = serial; return false; }
            if (Melee.Contact(kind))
            {
                // Let the native paired animation align adjacent actors. A stale
                // cue must never warp either participant into a knife strike.
                if (Vector3.Distance(hostPosition, point) > 5f ||
                    Vector3.Distance(actor.transform.position, hostPosition) > 1.25f ||
                    Vector3.Distance(target.transform.position, point) > 1.25f ||
                    Vector3.Distance(actor.transform.position, target.transform.position) > 2.5f) return false;
            }
            else if (!Align(target, point) || !Align(actor, hostPosition)) return false;
            lastSerial = serial;
            contactPoint = point;
            startPosition = hostPosition;
            skill = kind;
            victim = target;
            ProtectedId = cue[offset+2];
            protectUntil = Time.time + 5f;
            try
            {
                Melee.Execute(actor,target,kind);
                MoveProbePlugin.ProbeLog.LogInfo($"Guest played attack #{serial} {kind} guard {ProtectedId}");
            }
            catch (Exception ex)
            {
                protectUntil = 0f;
                MoveProbePlugin.ProbeLog.LogWarning($"Guest attack playback: {ex.GetType().Name}");
            }
        }
        if (Melee.Point(skill) && Time.time<protectUntil &&
            (CoroWait.bIsRunning(actor.controller.getSkill(skill)?.TryCast<SkillThrow>()?.m_cwThrow) || actor.controller?.skillCommandProcess?.m_skill?.eSkillType==skill ||
             actor.controller?.skillCommandActive?.m_skill?.eSkillType==skill ||
             actor.controllerPlayer?.skillCommand?.m_skill?.eSkillType==skill)) return true;
        bool departed = serial == "0" && Melee.Contact(skill) && Vector3.Distance(hostPosition,contactPoint) > 5f;
        bool hostMovedOn = offset == 6 && Melee.Contact(skill) ? released || serial == "0" :
            released && (serial == "0" || Vector3.Distance(hostPosition,startPosition) > 0.65f);
        if (victim != null && !departed && !hostMovedOn)
        {
            var state = victim.health.eHealthState;
            bool targetingVictim = TargetsVictim(actor.controllerPlayer.skillCommand);
            bool processing = TargetsVictim(actor.controller.skillCommandProcess, allowMissingTarget:true) ||
                TargetsVictim(actor.controller.skillCommandActive, allowMissingTarget:true);
            bool inProgress = state is MiCharacterHealth.HealthState.Dying or MiCharacterHealth.HealthState.PreDying
                or MiCharacterHealth.HealthState.InProcessOfBeingKnockedOut or MiCharacterHealth.HealthState.PreKnockout;
            if (inProgress && Time.time < protectUntil + 8f && (offset != 6 || targetingVictim || processing)) return true;
            if (Time.time < protectUntil && (state == MiCharacterHealth.HealthState.Alive && targetingVictim || processing)) return true;
        }
        Finish(actor);
        return false;
    }

    private void Finish(MiCharacter actor)
    {
        if (victim != null && victim.health.eHealthState == MiCharacterHealth.HealthState.Alive)
        {
            var cmd = actor?.controllerPlayer?.skillCommand;
            if (TargetsVictim(cmd)) actor.controllerPlayer.cancelSkill();
        }
        ProtectedId = null;
        victim = null;
    }

    private bool TargetsVictim(SkillCommand cmd, bool allowMissingTarget = false)
    {
        if (cmd?.m_skill?.eSkillType != skill) return false;
        var entity = cmd.m_target?.m_entityTarget;
        return entity == null ? allowMissingTarget : entity.bAsCharacter(out var current) && current == victim;
    }

    private static bool Align(MiCharacter actor, Vector3 point)
    {
        if (Vector3.Distance(actor.transform.position,point) <= 0.65f) return true;
        if (!NavMesh.SamplePosition(point,out var hit,1f,-1)) return false;
        actor.warp(hit.position,actor.transform.rotation);
        return true;
    }
}

internal sealed class MissionAlarmSync
{
    [ThreadStatic] internal static bool ApplyingNative;
    private AlarmSystem system;
    private float lookup, nextAttempt, nextKeepAlive;
    private string lastHost, lastLocal;
    private bool alarmIssued;
    internal string Status => lastHost == null || lastHost == "None" && lastLocal == "None" ? null :
        $"Host alarm: {lastHost}  |  Local: {lastLocal}";

    private AlarmSystem Find()
    {
        if (system == null && Time.realtimeSinceStartup >= lookup)
        {
            lookup = Time.realtimeSinceStartup + 2f;
            system = UnityEngine.Object.FindObjectOfType<AlarmSystem>();
        }
        return system;
    }

    internal string Wire(Vector3 fallback)
    {
        try { return ReadWire(fallback); }
        catch (Exception) { return "Unknown 0 0 0 0 0 0"; }
    }

    private string ReadWire(Vector3 fallback)
    {
        if (Find() == null) return "Unknown 0 0 0 0 0 0";
        var state = system.eAlarmState;
        if (state == AlarmSystem.AlarmState.None) return "None 0 0 0 0 0 0";
        var squad = system.squadLastActive;
        var info = squad?.m_targetInfo;
        var target = info != null ? info.v3TargetPosAdaptive : squad != null ? squad.v3TargetPos : fallback;
        var source = info != null ? info.v3SourcePos : squad != null ? squad.v3CenterPos : target;
        return $"{state} {CrewSync.Position(target)} {CrewSync.Position(source)}";
    }

    internal void Apply(string[] cue)
    {
        string host = cue[12];
        if (host == "Unknown" || Find() == null) return;
        if (host == "None") alarmIssued=false;
        string local = system.eAlarmState.ToString();
        if(host!="None" && host!="AlarmBellPrepare" && local!="None" && local!="AlarmBellPrepare") alarmIssued=true;
        if (host != lastHost || local != lastLocal)
        {
            MoveProbePlugin.ProbeLog.LogInfo($"Mission alarm host={host} local={local}");
            if (host != lastHost || local != lastLocal && host == "None")
                nextAttempt = Time.time + (host == "None" ? 0.75f : 0f);
            lastHost = host; lastLocal = local;
        }
        // Replica AI must not end the local warning early while the host is
        // still alarmed. Keep existing native squads alive without spawning.
        if(host!="None" && host!="AlarmBellPrepare" && Time.time>=nextKeepAlive)
        {
            nextKeepAlive=Time.time+0.25f;
            var squads=system.liActiveSquads;
            if(squads!=null) for(int i=0;i<squads.Count;i++) squads[i]?.resetAlarmTime();
        }
        if (host == local || Time.time < nextAttempt) return;
        nextAttempt = Time.time + 0.25f;
        try
        {
            ApplyingNative=true;
            if (host == "None") MiScriptLibraryProject.CancelAlarm();
            else if (!alarmIssued && (local == "None" || local == "AlarmBellPrepare") && host != "AlarmBellPrepare")
            {
                MiScriptLibraryProject.Alarm(CrewSync.ReadPosition(cue,13), CrewSync.ReadPosition(cue,16));
                alarmIssued=true;
            }
            // Native squad membership may disappear under replica AI control.
            // Re-firing Alarm does not guarantee its mission state survives.
            // The shipping property setter changes state and dispatches the
            // existing native state-change event; it is not a Harmony hook.
            // Keep reinforcement creation in its separately deduplicated path.
            var wanted=(AlarmSystem.AlarmState)Enum.Parse(typeof(AlarmSystem.AlarmState),host);
            if(system.eAlarmState!=wanted) system.eAlarmState=wanted;
            lastLocal=system.eAlarmState.ToString();
        }
        catch (Exception ex) { MoveProbePlugin.ProbeLog.LogWarning($"Mission alarm apply: {ex.GetType().Name}"); }
        finally { ApplyingNative=false; }
    }
}
