using System;
using System.Globalization;
using Mimimi.MiScript;
using UnityEngine;
using UnityEngine.AI;

// One controller per live actor. Native calls always use this immutable actor;
// another crew's selection, run, approach or retreat cannot replace its state.
internal sealed class HostCrewOrders
{
    internal readonly MiCharacter Actor;
    internal readonly AttackCue Cue = new AttackCue();
    private Vector3? afterClimb;
    private string afterClimbGait;
    private readonly Func<bool> hostReady, worldHeld;
    private readonly Action<string> Report;
    private readonly Action<MiCharacter> watchGuard;
    internal HostCrewOrders(MiCharacter actor, Func<bool> hostReady, Func<bool> worldHeld,
        Action<string> report, Action<MiCharacter> watchGuard)
    {
        Actor=actor ?? throw new ArgumentNullException(nameof(actor));
        this.hostReady=hostReady;this.worldHeld=worldHeld;Report=report;this.watchGuard=watchGuard;
    }
    private bool Ready => Actor!=null && hostReady();
    internal long AppliedSequence{get;private set;}
    internal void Applied(long sequence){if(sequence>AppliedSequence)AppliedSequence=sequence;}
    internal string OrderWire()
    {
        string route=HostRouteWire();
        bool hasRoute=route.StartsWith(" N ",StringComparison.Ordinal);
        return " O "+AppliedSequence.ToString(CultureInfo.InvariantCulture)+" "+(RestCrouch?"C":"W")+" "+(Busy?"1":"0")+
            (hasRoute?" 1 "+route.Substring(3):" 0 0 0 0")+" "+(hasRoute?GuestGroundTarget.DestinationSurface(Actor,lastNativeGoal)??"-":"-");
    }
    internal bool MovementHeld{get;set;}
    internal bool Busy => attackTarget!=null;
    internal bool Running => (hostRunning || approaching && approachRun!=null) && NativeGait.CanRun(Actor);
    private bool ContinueApproachRun => Ready && NativeGait.CanRun(Actor) &&
        Actor.movement?.isStoppedOrInactive==false && (Running || NativeGait.Read(Actor)=="R");
    internal bool RestCrouch => attackTarget!=null ? attackReturnCrouch : hostRunning ? hostReturnCrouch :
        crouchAfterAction || NativeGait.Read(Actor)=="C";
    internal string ContactWire => awaitingGuestContact && approaching && attackTarget!=null && contactGate.Current>0
        ? $" {contactGate.Current} {Melee.GuardId(attackTarget)}" :
          attackTarget==null && Time.realtimeSinceStartup<showMissUntil ? " 0 !" : " 0 -";
    internal string CueWire => Cue.Wire(attackTarget!=null && !approaching);
    internal void RecordFrame(int sequence) => hostViews.Record(sequence,Time.realtimeSinceStartup,Actor.transform.position);
    internal void ResumeWorld(float gameElapsed,float realElapsed,float pausedAt)
    {
        if(attackTarget==null)return;
        attackDeadline+=gameElapsed;nextApproachCheck+=gameElapsed;approachMovedAt+=gameElapsed;
        if(nativeStartUntil>pausedAt)nativeStartUntil+=realElapsed;
    }
    internal void ResumeMovement(float elapsed)
    {
        if(!float.IsFinite(elapsed)||elapsed<=0f)return;
        if(hostRunning)
        {
            hostRunDeadline+=Math.Min(elapsed,Math.Max(0f,Time.time-hostRunStarted));
            runLastMoved=Math.Min(Time.time,runLastMoved+elapsed);
        }
        if(retreatToVerify.HasValue)
        {
            float overlap=Math.Min(elapsed,Math.Max(0f,Time.time-retreatStarted));
            verifyRetreatAt+=overlap;verifyRetreatUntil+=overlap;
        }
    }
    internal void Reset()
    {
        AppliedSequence=0;MovementHeld=false;EndHostRun(true);
        afterClimb=null;
        if(Actor!=null && approachRun!=null)NativeGait.CancelRunModifiers(Actor,approachPoint);
        retreatToVerify=null;attackTarget=null;afterAttack=null;approaching=false;approachRun=null;
        confirmedRouteGoal=null;crouchAfterAction=false;awaitingGuestContact=false;observeAt=0;
        contactGate.Clear();hostViews.Clear();Cue.Clear();showMissUntil=0;
    }
    internal void TickMovement(bool worldPaused,bool crewPaused)
    {
        if(!Ready)return;
        if(afterClimb.HasValue&&!worldPaused&&!crewPaused&&!InteractionSync.Climbing(Actor))
        {var goal=afterClimb.Value;var gait=afterClimbGait;afterClimb=null;if(!IssueMove("After ladder exit",goal,gait)){afterClimb=goal;afterClimbGait=gait;}}
        if(!worldPaused && !crewPaused)UpdateHostRun();
        if(crouchAfterAction && !worldPaused && !crewPaused && !hostRunning && attackTarget==null &&
            Time.timeScale>0f && !MiCutsceneHandler.bCutsceneRunning && Actor.movementType!=null &&
            Actor.health?.eHealthState==MiCharacterHealth.HealthState.Alive && Actor.movementType.bCanCrouch)
        {
            if(CrewSync.SetStance(Actor,true) && Time.time>=keepCrouchUntil)crouchAfterAction=false;
        }
    }
    internal void TickActions()
    {
        if(!Ready || worldHeld())return;
        UpdateAttack();if(!MovementHeld)VerifyRetreat();
        if(observeAt>0 && Time.realtimeSinceStartup>=observeAt)
        {
            observeAt=0;
            Report($"Crew #{Actor.GetInstanceID()} at {Actor.transform.position}; distance to target {Vector3.Distance(Actor.transform.position,observedTarget):F1}");
        }
    }

    private Vector3 observedTarget;
    private float observeAt;
    private float attackDeadline;
    private bool strikeCommitted;
    private MiCharacter attackTarget;
    private Skill.SkillType activeAttack;
    private bool approaching;
    private Vector3 approachPoint;
    private float nextApproachCheck;
    private Vector3? afterAttack;
    private string afterAttackGait;
    private Vector3? retreatToVerify;
    private string retreatGait;
    private float verifyRetreatAt, verifyRetreatUntil, retreatStarted;
    private Vector3 retreatOrigin;
    private MiCharacterHealth.HealthState observedHealth;
    private bool hostRunning, hostReturnCrouch;
    private Vector3 hostRunTarget;
    private float hostRunDeadline, hostRunStarted;
    private float nextRunSample;
    private Vector3 lastRunPosition;
    private float runLastMoved;
    private bool crouchAfterAction;
    private float keepCrouchUntil;
    private Vector3? confirmedRouteGoal;
    private Vector3 lastNativeGoal;
    private float routeGrace;
    private IntPtr confirmedRouteTargetPointer;
    private string approachRun;
    private bool attackReturnCrouch;
    private Vector3 lastApproachPosition;
    private float approachMovedAt;
    private readonly MeleeContactGate contactGate = new MeleeContactGate();
    private bool awaitingGuestContact;
    private readonly HostClickHistory hostViews = new HostClickHistory();
    private float clickProofUntil, showMissUntil;
    private float nativeStartUntil, nativeContactLimit;
    private MiCharacter missedGuard;
    private Vector3 clickProofGuard;

    internal void EndHostRun(bool restore)

    {

        if (!hostRunning) return;

        hostRunning = false;

        if (Actor == null || Actor.health == null || Actor.health.eHealthState != MiCharacterHealth.HealthState.Alive) return;

        NativeGait.CancelRunModifiers(Actor, hostRunTarget);

        if (restore)

        {

            if (hostReturnCrouch) RestoreCrouch();

            else CrewSync.SetStance(Actor, false);

        }

    }

    private void RestoreCrouch()

    {

        // Paired animations may briefly stand the actor up after movement unlocks.

        crouchAfterAction = true;

        keepCrouchUntil = Time.time + 0.65f;

        CrewSync.SetStance(Actor, true);

    }

    private void UpdateHostRun()

    {

        if (!hostRunning) return;

        if (Actor == null || Actor.health == null || Actor.health.eHealthState != MiCharacterHealth.HealthState.Alive ||

            MiCutsceneHandler.bCutsceneRunning) { EndHostRun(false); return; }

        if (Time.timeScale <= 0f) return;

        var position = Actor.transform.position;

        if (Vector3.Distance(position, lastRunPosition) >= 0.12f)

        { lastRunPosition = position; runLastMoved = Time.time; }

        if (Vector3.Distance(position, hostRunTarget) <= 0.45f || Time.time >= hostRunDeadline || Time.time - runLastMoved > 3f)

        { EndHostRun(true); return; }

        // The pending slot can still contain the previous order, or be empty

        // after the game consumes it. Only arrival, a stall, or a replacement

        // order ends this run. Traversal restrictions still govern its speed.

        bool matched = NativeGait.MaintainRun(Actor, hostRunTarget);

        if (Time.realtimeSinceStartup >= nextRunSample)

        {

            nextRunSample = Time.realtimeSinceStartup + 2f;

            MoveProbePlugin.ProbeLog.LogInfo($"Host run sample: style={NativeGait.Read(Actor)} fast={Actor.movementType.bIsInFastMovement} matched={matched} remaining={Vector3.Distance(position,hostRunTarget):F2}");

        }

    }

    internal string HostRouteWire()

    {

        if (!confirmedRouteGoal.HasValue || attackTarget!=null || Actor.health?.eHealthState!=MiCharacterHealth.HealthState.Alive) return "";

        var wanted=confirmedRouteGoal.Value;

        var queued=Actor.controllerPlayer.skillCommand;

        var active=Actor.controller.skillCommandProcess;

        var movement=Actor.movement;

        var target=movement?.target;

        // A native stop or a new entity interaction supersedes a leftover

        // queued GoTo. Keep the final waypoint if the host actually arrived.

        if (target?.m_entityTarget!=null || movement?.isStoppedOrInactive==true &&

            Vector3.Distance(GuestGroundTarget.NavigationPosition(Actor),wanted)>0.55f) return "";

        bool ordinaryTarget=target!=null && target.m_entityTarget==null;

        bool match=ordinaryTarget && (target.Pointer==confirmedRouteTargetPointer && confirmedRouteTargetPointer!=IntPtr.Zero ||
            Vector3.Distance(GuestGroundTarget.NavigationPoint(Actor,target.v3Target),wanted)<=0.8f);

        Vector3 point=match ? GuestGroundTarget.NavigationPoint(Actor,target.v3Target) : wanted;

        // The game consumes the input slot on dispatch. Either the running

        // GoTo or the controller's resulting world target can verify the path.

        if (!match && WorldGoTo(queued,wanted,out var fromInput)) { point=fromInput; match=true; }

        if (!match && WorldGoTo(active,wanted,out var fromProcess)) { point=fromProcess; match=true; }

        bool busy=DifferentOrder(queued,wanted) || DifferentOrder(active,wanted);

        if (busy)

        {

            if (Time.realtimeSinceStartup>routeGrace) confirmedRouteGoal=null;

            return "";

        }

        // The native path may end before the requested black-marker point.
        // Publish the actual completed path endpoint, not the rejected raw goal.
        if (match && ordinaryTarget && movement!=null && movement.bPathIsStale()) return "";
        if (match && ordinaryTarget && movement!=null &&
            movement.bGetLastPathPosition(out var reachable)) point=reachable;
        if (match) { lastNativeGoal=point; routeGrace=Time.realtimeSinceStartup+0.20f; }

        else if (ordinaryTarget || Time.realtimeSinceStartup>routeGrace) return "";

        return " N "+CrewSync.Position(lastNativeGoal);

    }

    private bool WorldGoTo(SkillCommand cmd,Vector3 wanted,out Vector3 point)

    {

        point=default;

        if (cmd?.m_skill?.eSkillType!=Skill.SkillType.GoTo || cmd.m_target==null || cmd.m_target.m_entityTarget!=null) return false;

        point=GuestGroundTarget.NavigationPoint(Actor,cmd.m_target.v3Target);

        return cmd.m_target.Pointer==confirmedRouteTargetPointer && confirmedRouteTargetPointer!=IntPtr.Zero || Vector3.Distance(point,wanted)<=0.8f;

    }

    private bool HostStance(bool crouch)

    {

        if(Actor==null) return false;

        if(attackTarget!=null)

        {

            RefreshStrike();

            if(!AttackRetreat.CanReplace(approaching,Actor.controllerPlayer.bCanMove,RetreatLocked())) return false;

        }

        if(!NativeGait.StopForStance(Actor,crouch)) return false;

        EndHostRun(false);confirmedRouteGoal=null;retreatToVerify=null;

        approachRun=null;approaching=false;attackTarget=null;afterAttack=null;

        awaitingGuestContact=false;contactGate.Clear();Cue.Clear();crouchAfterAction=false;

        return true;

    }

    internal bool IssueMove(string source, Vector3 target, string gait = null, bool retreat = false)

    {

        if (worldHeld()) return false;

        if (!Ready || !Actor.bControllerPlayer()

            || MiCutsceneHandler.bCutsceneRunning || Time.timeScale <= 0f)

        {

            Report("Host crew command unavailable in this mission state.");

            return false;

        }

        if (retreatToVerify.HasValue && attackTarget == null && gait == retreatGait &&

            Vector3.Distance(target, retreatToVerify.Value) <= 0.12f)

            return true; // Keep verifying the first departure when the same withdrawal is clicked repeatedly.

        MoveProbePlugin.ProbeLog.LogInfo($"Before GoTo: crew #{Actor.GetInstanceID()} at {Actor.transform.position}, target {target}");
        if(InteractionSync.Climbing(Actor))
        {
            if(!InteractionSync.ExitToward(Actor,target))return false;
            afterClimb=target;afterClimbGait=gait;confirmedRouteGoal=null;
            EndHostRun(false);return true;
        }
        afterClimb=null;

        retreat |= retreatToVerify.HasValue;

        retreatToVerify = null;

        if (attackTarget != null)

        {

            retreat = true;

            RefreshStrike();

            bool alive = attackTarget.health.eHealthState == MiCharacterHealth.HealthState.Alive;

            if (!AttackRetreat.CanReplace(approaching, Actor.controllerPlayer.bCanMove, RetreatLocked()))

            {

                afterAttack = target;

                afterAttackGait = gait;

                Report("Retreat received: use latest point when crew can move.");

                return true;

            }

            // A new walk supersedes the approach. If the original targeted

            // command was already posted, cancel it before replacing it.

            if (!approaching && !strikeCommitted && alive) Actor.controllerPlayer.cancelSkill();

            if (approaching && approachRun != null) NativeGait.CancelRunModifiers(Actor, approachPoint);

            approachRun = null;

            approaching = false;

            attackTarget = null;

            afterAttack = null;

            if (!strikeCommitted) Cue.Clear();

            Report(strikeCommitted ? "Host released crew to retreat after strike." : "Host cancelled attack before impact; retreating.");

        }

        if (gait != null && Actor.controllerPlayer.bCanMove) gait = NativeGait.AvailableTravel(Actor,gait);

        if (gait != null && NativeGait.Running(gait) && !NativeGait.CanRun(Actor))

        { Report("Run rejected: crew cannot run in current state."); return false; }

        bool run = NativeGait.Running(gait);

        string travel = run ? "RW" : gait;

        bool samePath = gait != null && !retreat && confirmedRouteGoal.HasValue &&

            Vector3.Distance(confirmedRouteGoal.Value,target) <= 0.3f &&

            (Actor.movement?.isStoppedOrInactive == false || Time.realtimeSinceStartup <= routeGrace) &&

            NativeGait.ChangePathSpeed(Actor,confirmedRouteGoal.Value,travel);

        if (samePath) target=confirmedRouteGoal.Value;

        if (samePath && hostRunning && run)

        {

            hostReturnCrouch = NativeGait.ReturnsCrouched(gait);

            return true;

        }

        // Keep the old running process at speed while Unity switches paths.

        // Clearing its run modifier here introduces a one-frame crouch on turns.

        if (!samePath && !run) EndHostRun(gait == null);

        if (gait != null) crouchAfterAction = false;

        if (gait == null) MiScriptLibraryProject.CharacterExecuteSkill(Actor, Skill.SkillType.GoTo, target);

        else

        {

            if (!samePath && !NativeGait.Execute(Actor, target, travel)) return false;

            hostRunning = run;

            hostRunTarget = target;

            hostReturnCrouch = NativeGait.ReturnsCrouched(gait);

            hostRunStarted = Time.time;
            hostRunDeadline = Time.time + 25f;

            runLastMoved = Time.time;

            lastRunPosition = Actor.transform.position;

        }

        if(!samePath) confirmedRouteTargetPointer=(Actor.controllerPlayer.skillCommand?.m_target ?? Actor.controller.skillCommandProcess?.m_target)?.Pointer ?? IntPtr.Zero;
        observedTarget = target;

        confirmedRouteGoal=lastNativeGoal=target;

        routeGrace=Time.realtimeSinceStartup+0.20f;

        if (retreat)

        {

            retreatToVerify = target;

            retreatGait = gait;

            retreatOrigin = Actor.transform.position;

            retreatStarted = Time.time;
            verifyRetreatAt = Time.time + 0.2f;

            verifyRetreatUntil = Time.time + 2.5f;

        }

        observeAt = Time.realtimeSinceStartup + 3f;

        Report($"{source} GoTo sent to crew #{Actor.GetInstanceID()}, gait={gait ?? "default"}");

        return true;

    }

    private void VerifyRetreat()

    {

        if (!retreatToVerify.HasValue || Time.time < verifyRetreatAt) return;

        Vector3 point = retreatToVerify.Value;

        if (!Ready || attackTarget != null || MiCutsceneHandler.bCutsceneRunning ||

            Actor.health.eHealthState != MiCharacterHealth.HealthState.Alive ||

            Vector3.Distance(Actor.transform.position,point) < 0.55f)

        { retreatToVerify = null; return; }

        var cmd = Actor.controllerPlayer.skillCommand;

        bool correctOrder = cmd?.m_skill?.eSkillType == Skill.SkillType.GoTo && cmd.m_target != null &&

            Vector3.Distance(cmd.m_target.v3Target,point) < 0.6f;

        // The native controller can accept GoTo while the paired animation

        // still pins the actor at contact. Verify actual departure as well as the order.

        if (correctOrder && Vector3.Distance(Actor.transform.position,retreatOrigin) > 0.25f)

        { retreatToVerify = null; return; }

        if (cmd?.m_skill != null && cmd.m_skill.eSkillType != Skill.SkillType.GoTo && cmd.m_skill.eSkillType != activeAttack)

        { retreatToVerify = null; return; }

        if (Time.time >= verifyRetreatUntil)

        { retreatToVerify = null; Report("Retreat not taken by native controller; click ground again."); return; }

        verifyRetreatAt = Time.time + 0.4f;

        if (correctOrder && Time.time < verifyRetreatUntil - 1.5f) return;

        if (!Actor.controllerPlayer.bCanMove) return;

        if (retreatGait == null) MiScriptLibraryProject.CharacterExecuteSkill(Actor, Skill.SkillType.GoTo, point);

        else NativeGait.Execute(Actor, point, NativeGait.Running(retreatGait) ? "RW" : retreatGait);

    }

    private void SendApproach()

    {

        if (approachRun!=null && NativeGait.CanRun(Actor)) NativeGait.Execute(Actor,approachPoint,"RW");

        else MiScriptLibraryProject.CharacterExecuteSkill(Actor,Skill.SkillType.GoTo,approachPoint);

    }

    internal bool HostAttack(MiCharacter victim, Skill.SkillType skill, bool run=false, bool guestOrder=false,

        float clickUntil=-1f, Vector3 clickGuard=default)

    {

        if (guestOrder && run && victim != null && victim == missedGuard && skill == activeAttack &&

            Time.realtimeSinceStartup < showMissUntil - 1f) return false;

        if (Ready && victim != null && victim == attackTarget && skill == activeAttack)

        {

            if (approaching && clickUntil > clickProofUntil) { clickProofUntil = clickUntil; clickProofGuard = clickGuard; }

            if (run && approaching && approachRun==null && NativeGait.CanRun(Actor))

            {

                approachRun=attackReturnCrouch ? "RC" : "RW";

                SendApproach(); Report("Host upgraded melee approach to run.");

            }

            return true;

        }

        if (!Ready || Time.timeScale <= 0f ||

            MiCutsceneHandler.bCutsceneRunning || !Melee.CanUse(Actor, victim, skill, true))

        {

            Report($"Attack rejected ({skill}): check target, range, pause and selected host crew.");

            return false;

        }

        float distance = Vector3.Distance(Actor.transform.position, victim.transform.position);

        bool needsApproach = Melee.Contact(skill) && distance > 1.5f;

        NavMeshHit approachHit = default;

        if (needsApproach && !NavMesh.SamplePosition(victim.transform.position, out approachHit, 1.5f, -1))

        { Report("Attack rejected: no walkable approach to target."); return false; }

        if(attackTarget!=null)
        {
            RefreshStrike();
            if(!AttackRetreat.CanReplace(approaching,Actor.controllerPlayer.bCanMove,RetreatLocked()))
            {Report("Attack deferred: current strike is locked; target was not replaced.");return false;}
            if(!approaching&&!strikeCommitted)Actor.controllerPlayer.cancelSkill();
            if(approachRun!=null)NativeGait.CancelRunModifiers(Actor,approachPoint);
            attackTarget=null;approaching=false;approachRun=null;afterAttack=null;
        }

        watchGuard(victim);

        contactGate.Clear();

        clickProofUntil = clickUntil;

        nativeStartUntil = 0f;

        clickProofGuard = clickGuard;

        showMissUntil = 0f;

        missedGuard = null;

        awaitingGuestContact = guestOrder && Melee.Contact(skill);

        retreatToVerify = null;

        attackReturnCrouch = hostRunning ? hostReturnCrouch : crouchAfterAction || NativeGait.Read(Actor) == "C";

        EndHostRun(true);

        attackTarget = victim;

        confirmedRouteGoal=null;

        activeAttack = skill;

        strikeCommitted = false;

        Cue.Clear();

        afterAttack = null;

        observedHealth = victim.health.eHealthState;

        attackDeadline = Time.time + (Melee.Contact(skill) ? 24f : 6f);

        approachRun = run && needsApproach && NativeGait.CanRun(Actor) ? (attackReturnCrouch ? "RC" : "RW") : null;

        approaching = needsApproach || awaitingGuestContact;

        lastApproachPosition=Actor.transform.position;

        approachMovedAt=Time.time;

        if (approaching)

        {

            approachPoint = needsApproach ? approachHit.position : victim.transform.position;

            if (needsApproach) SendApproach();

            nextApproachCheck = Time.time + 0.4f;

        }

        else { Melee.Execute(Actor, victim, skill); RefreshStrike(); }

        Report($"Host accepted {skill} on guard {Melee.GuardId(victim)}, distance={distance:F2}, approach={approaching}.");

        return true;

    }

    private void UpdateApproach()

    {

        if (!approaching || attackTarget == null || Time.timeScale <= 0f || MiCutsceneHandler.bCutsceneRunning ||

            attackTarget.health.eHealthState != MiCharacterHealth.HealthState.Alive || Time.time >= attackDeadline ||

            Actor == null || Actor.health.eHealthState != MiCharacterHealth.HealthState.Alive) return;

        if (approachRun!=null && NativeGait.CanRun(Actor)) NativeGait.Apply(Actor,"R");

        var position = attackTarget.transform.position;

        var current = Actor.transform.position;

        if (Vector3.Distance(current,lastApproachPosition)>0.1f)

        { lastApproachPosition=current; approachMovedAt=Time.time; }

        float distance=Vector3.Distance(current,position);

        float now = Time.realtimeSinceStartup;

        // The host owns admission and processes new/cancel orders before this

        // update. Do not cancel a valid attack because a visual contact ACK

        // reaches us one network frame after the host arrives at the guard.

        // If the guard's collider prevents a closer approach, let the original

        // melee skill complete the final alignment once the actor has stopped closing.

        bool stalledNearGuard = distance <= (awaitingGuestContact ? 2.5f : 3.5f) && Time.time-approachMovedAt>0.6f;

        if ((distance <= 1.5f || stalledNearGuard) &&

            Melee.CanUse(Actor, attackTarget, activeAttack))

        {

            if (awaitingGuestContact)

            {

                contactGate.Clear();

                // The native skill still has to align the pair and turn. The

                // former 180 ms window revoked valid stationary-target kills.

                // This is local animation startup, never a guest-ACK wait;

                // retreat input remains serviced before UpdateAttack.

                nativeStartUntil = now + 1.5f;

                nativeContactLimit = Math.Min(2.75f, distance + 0.45f);

                Report("Host melee range reached; striking without guest contact ACK.");

            }

            approaching = false;

            if (approachRun!=null) { NativeGait.CancelRunModifiers(Actor,approachPoint); approachRun=null; }

            Melee.Execute(Actor, attackTarget, activeAttack);

            RefreshStrike();

            Report($"Host reached guard {Melee.GuardId(attackTarget)}; issued {activeAttack}.");

        }

        else

        {

            if (Time.time < nextApproachCheck) return;

            nextApproachCheck = Time.time + 0.4f;

            if (Vector3.Distance(position, approachPoint) > 0.8f && NavMesh.SamplePosition(position, out var hit, 1.5f, -1))

            {

                approachPoint = hit.position;

                SendApproach();

            }

        }

    }

    private bool InNativeAttack()

    {

        var process = Actor.controller.skillCommandProcess;

        if (process?.m_skill?.eSkillType != activeAttack) return false;

        var entity = process.m_target?.m_entityTarget;

        return entity == null || entity.bAsCharacter(out var victim) && victim == attackTarget;

    }

    private bool RetreatLocked()

    {

        if (approaching) return false;

        var health = attackTarget.health.eHealthState;

        if (Melee.Contact(activeAttack))

        {

            // bCanMove may already be true the frame the victim starts dying.

            // Keep the paired animation authoritative until both actors unlock.

            if (AttackRetreat.PairedTransition(health)) return true;

            if (InPairedProcess()) return true;

        }

        return health == MiCharacterHealth.HealthState.Alive && InNativeAttack();

    }

    private bool InPairedProcess()

    {

        var type = activeAttack == Skill.SkillType.Kill ? MiCharacterProcess.Type.Kill : MiCharacterProcess.Type.Knockout;

        return Actor.processes != null && Actor.processes.bIsExecuting(type);

    }

    private void RefreshStrike()

    {

        if (strikeCommitted || approaching || attackTarget == null) return;

        if (Melee.Contact(activeAttack) && attackTarget.health.eHealthState == MiCharacterHealth.HealthState.Alive &&

            !InNativeAttack() && !InPairedProcess()) return;

        strikeCommitted = true;

        Cue.Issue(attackTarget, activeAttack);

    }

    private void UpdateAttack()

    {

        if (attackTarget == null || Actor == null) return;

        UpdateApproach();

        var health = attackTarget.health.eHealthState;

        bool landed = health == MiCharacterHealth.HealthState.Knockout || health == MiCharacterHealth.HealthState.Dead ||

            health == MiCharacterHealth.HealthState.Exterminated;

        RefreshStrike();

        bool canLeave = Actor.controllerPlayer.bCanMove;

        bool locked = RetreatLocked();

        bool missedNativeStart = nativeStartUntil > 0f && !approaching &&

            AttackRetreat.MissedNativeStart(Time.realtimeSinceStartup, nativeStartUntil,

                Vector3.Distance(Actor.transform.position,attackTarget.transform.position), nativeContactLimit,

                strikeCommitted || locked, health);

        if (strikeCommitted) nativeStartUntil = 0f;

        if (missedNativeStart)

        {

            // Also revoke the submitted native order while a later retreat is

            // waiting for movement to unlock. Preserve that retreat below.

            Actor.controllerPlayer.cancelSkill();

            nativeStartUntil = 0f;

            attackDeadline = Time.time;

            showMissUntil = Time.realtimeSinceStartup + 1.5f;

            Report("Melee skipped: native contact did not begin in reach.");

        }

        bool timedOut = Time.time >= attackDeadline;

        bool release = afterAttack.HasValue && AttackRetreat.CanReplace(approaching, canLeave, locked);

        bool actorUnavailable = Actor.health.eHealthState != MiCharacterHealth.HealthState.Alive;

        bool finished = actorUnavailable || release || landed && canLeave && !locked ||

            timedOut && !locked && (!afterAttack.HasValue || canLeave);

        if (health != observedHealth || finished)

            Report($"Host melee observation: guard {Melee.GuardId(attackTarget)} health={health}");

        observedHealth = health;

        if (finished)

        {

            if (timedOut && awaitingGuestContact && (approaching || missedNativeStart) && !actorUnavailable && canLeave && !afterAttack.HasValue)

                MiScriptLibraryProject.CharacterExecuteSkill(Actor,Skill.SkillType.GoTo,Actor.transform.position);

            if (approachRun != null) NativeGait.CancelRunModifiers(Actor, approachPoint);

            approachRun = null;

            if (!approaching && !missedNativeStart && health == MiCharacterHealth.HealthState.Alive && (afterAttack.HasValue || !strikeCommitted))

                Actor.controllerPlayer.cancelSkill();

            attackTarget = null;

            approaching = false;

            var next = afterAttack;

            afterAttack = null;

            if (attackReturnCrouch && Actor.health.eHealthState == MiCharacterHealth.HealthState.Alive)

                RestoreCrouch();

            if (next.HasValue && canLeave && !actorUnavailable)

            {

                if (health == MiCharacterHealth.HealthState.Alive) Cue.Clear();

                IssueMove("After attack", next.Value, afterAttackGait, true);

            }

            else if (next.HasValue) Report("Crew cannot move; reissue the retreat when available.");

        }

    }

    private bool DifferentOrder(SkillCommand cmd,Vector3 wanted) =>

        cmd?.m_skill!=null && cmd.m_skill.eSkillType!=Skill.SkillType.None && !WorldGoTo(cmd,wanted,out _);

    internal static int ContinueInteractionFlags(int flags,bool running,bool restCrouch)
        => running ? flags|1|(restCrouch?2:0) : flags;
    internal bool Execute(string[] fields)
    {
        if(!Ready || worldHeld() || Actor.health?.eHealthState!=MiCharacterHealth.HealthState.Alive) return false;
        if(fields.Length==4 && fields[2]=="MIMIC")
        {
            if(attackTarget!=null||!SyncProtocol.MimicForm(fields[3]))return false;
            bool accepted=MimicNative.Apply(Actor,int.Parse(fields[3],CultureInfo.InvariantCulture));
            if(accepted){EndHostRun(false);confirmedRouteGoal=null;retreatToVerify=null;crouchAfterAction=false;}
            return accepted;
        }
        if(fields.Length==9 && fields[2]=="USE")

        {

            if(attackTarget!=null) return false;

            // A second single click changes the destination/action, not the
            // gait already chosen by the preceding double click.
            var forwarded=(string[])fields.Clone();
            int flags=int.Parse(forwarded[8],CultureInfo.InvariantCulture);
            forwarded[8]=ContinueInteractionFlags(flags,ContinueApproachRun,RestCrouch).ToString(CultureInfo.InvariantCulture);
            bool accepted=InteractionSync.Issue(Actor,forwarded,3);

            if(accepted) { afterClimb=null;EndHostRun(false);confirmedRouteGoal=null;retreatToVerify=null;crouchAfterAction=false; }

            return accepted;

        }

        if(fields.Length==5 && fields[2]=="BODY")

        {

            if(attackTarget!=null) return false;

            var body=fields[3]=="D" ? null : Melee.Resolve(fields[4]);

            bool run=fields[3]=="MR" || fields[3]=="PR" || fields[3]!="D"&&ContinueApproachRun;

            if(fields[3]=="M" || fields[3]=="MR")

                return BodySync.Corpse(body) && body.m_carryable?.charCarryiedBy==null &&

                    GuestGroundTarget.ValidDestination(body.transform.position) &&

                    IssueMove("Guest body position",body.transform.position,

                        run ? (NativeGait.Read(Actor)=="C" ? "RC":"RW") : NativeGait.Read(Actor)=="C" ? "GC":"GW");

            if(fields[3]!="D" && !BodySync.CanPickup(Actor,body)) return false;

            EndHostRun(true); confirmedRouteGoal=null; retreatToVerify=null; crouchAfterAction=false;

            bool accepted=fields[3]=="D" ? BodySync.IssueDrop(Actor) : BodySync.IssuePickup(Actor,body,run);

            Report("Host body order "+fields[3]+" "+fields[4]+": "+accepted);

            return accepted;

        }

        if (fields.Length == 4 && fields[2] == "READY")

            return awaitingGuestContact && attackTarget != null && approaching &&

                contactGate.Confirm(int.Parse(fields[3], CultureInfo.InvariantCulture),Time.realtimeSinceStartup);

        bool taggedSurface = fields.Length == 7;

        string gait = fields.Length == 6 || taggedSurface ? fields[2] : null;

        int first = gait == null ? 2 : 3;

        {

            if ((fields.Length == 5 || fields.Length == 9) && fields[2] == "HIT")

            {

                var victim = Melee.Resolve(fields[4]);

                float validUntil = -1f;

                Vector3 guardAtClick = default;

                if (fields.Length == 9 && victim != null)

                {

                    int seq = int.Parse(fields[5],CultureInfo.InvariantCulture);

                    guardAtClick = CrewSync.ReadPosition(fields,6);

                    validUntil = hostViews.ValidUntil(seq,Actor.transform.position,victim.transform.position,guardAtClick);

                }

                return HostAttack(victim, Melee.Decode(fields[3]), fields[3]=="KR" || fields[3]=="LR", guestOrder:true,

                    clickUntil:validUntil, clickGuard:guardAtClick);

            }

            if (fields.Length == 3 && (fields[2] == "CROUCH" || fields[2] == "WALK"))

            {

                bool accepted = HostStance(fields[2] == "CROUCH");

                Report($"Guest stance {fields[2]}: {accepted}");

                return accepted;

            }

        }

        if (fields.Length != first + 3 + (taggedSurface ? 1 : 0)) return false;

        var target = new Vector3();

        for (int i = 0; i < 3; i++)

        {

            if (!float.TryParse(fields[i+first], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)

                || !float.IsFinite(v)) return false;

            target[i] = v;

        }

        if (!GuestGroundTarget.ValidDestination(target)) return false;

        // Ground input is an intention, not proof that its exact point is
        // reachable. Let the same native GoTo used by the host resolve partial
        // paths and nearby endpoints, with this actor's own navigation rules.
        if(gait==null || SyncProtocol.GroundMode(gait))
        {
            if(taggedSurface && GuestGroundTarget.SurfaceFloor(Actor,fields[6],target,out var sameSurface)) target=sameSurface;
            return IssueMove("Guest native ground",target,gait);
        }

        if (!NavMesh.SamplePosition(target, out var hit, 1.5f, -1)) return false;

        if (SyncProtocol.PointSkill(gait))

        {

            var skill = Melee.Decode(gait);

            var ability = Actor.controller.getSkill(skill);

            if (attackTarget != null || ability == null || Actor.health.eHealthState != MiCharacterHealth.HealthState.Alive ||

                ability.fRange>0 && Vector3.Distance(Actor.transform.position,hit.position)>ability.fRange+0.5f) return false;

            retreatToVerify=null; EndHostRun(true);

            confirmedRouteGoal=null; crouchAfterAction=false;
            MiScriptLibraryProject.CharacterExecuteSkill(Actor,skill,hit.position);

            return true;

        }

        var goal = hit.position;

        if (Vector3.Distance(goal,target) > 0.025f && HidingTrigger.bPointInHiding(target) &&

            !HidingTrigger.bPointInHiding(goal) && !GuestGroundTarget.HidingFloor(target,out goal)) return false;

        return IssueMove("Guest point", goal, gait);

    }
}
