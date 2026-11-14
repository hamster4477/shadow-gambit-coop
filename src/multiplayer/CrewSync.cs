using System;
using System.Collections.Generic;

using System.Globalization;

using System.Threading.Tasks;

using System.Threading;

using Mimimi.MiScript;

using UnityEngine;

using UnityEngine.AI;



// Orders use reliable HTTP. State transport can drop superseded samples;

// only Tick applies them to native game objects.

internal sealed class CrewSync : IDisposable

{

    internal readonly Uri Room;

    private MiCharacter guestCrew;
    private float nextApplyError;

    internal MiCharacter BoundCrew => guestCrew;
    internal bool Spectating {get;private set;}

    internal MiCharacter HostCrew => hostCrew;

    private bool initialSelection = true;

    private MiCharacter hostCrew;

    private Task<string> read;

    private readonly LanStateFeed lan;

    private int httpRead,sampleRead;

    private float httpStarted;

    private bool usingLan;

    private int timingSamples,routeSamples;

    private float lastAppliedAt,nextTimingLog,largestGap;

    private double slowestApplyMs;

    private float nextRead, lastGood = -100f, lastLog;

    private bool ready, stopped;

    private MiCharacter guard;

    private string guardKey, guardText;

    private Vector3 guardPosition;

    private float nextGuardSearch;

    private readonly CoverSync coverResults=new CoverSync();
    private readonly FootprintSync footprints=new FootprintSync();
    private readonly CrewRosterGuest extraCrew=new CrewRosterGuest();
    private readonly SkillObjectSync skillObjects=new();
    private readonly TrapObjectSync traps=new();
    private readonly GhostObjectSync ghosts=new();
    private readonly SkillResourceSync resources=new();
    private readonly TimeFreezeSync freezes=new();
    private readonly BlindSync blinds=new();
    private readonly FrozenSync frozen=new();
    private readonly FrozenDuelSync duels=new();
    private readonly DigStateSync digs=new();
    private readonly MimicStateSync mimics=new();
    private readonly ControlPlayback controls=new();
    private readonly CharmingGhostPlayback charming=new();
    private readonly CannonStateSync cannons=new();
    private readonly FishingPullSync fishing=new();
    private List<MiCharacter> controlledSources;
    private string[][] controlledRows;
    private ControlledCrewProtocol.Entry[] controlledManifest;
    private HashSet<string> controlledDeferred;
    private readonly HashSet<IntPtr> selectedControlProxies=new();
    private void RefreshControlledBindings()
    {
        if(controlledSources==null)return;
        // Native control/spawn coroutines may finish between two network
        // samples. Resolve them before local selection filtering, without
        // replaying any cached movement or skill a second time.
        if(!ControlledCrew.Active.Sample(controlledSources,GuestMove.RawIdentity))ControlledCrew.Active.Clear();
        ControlledCrew.Active.Restrict(controlledManifest);
        extraCrew.Prepare(controlledRows,controlledDeferred);
        // Native possession can select its proxy before the host manifest has
        // arrived. Selection filtering then removes that not-yet-known actor.
        // Restore the handoff once both ownership and the local proxy agree.
        var liveControlProxies=new HashSet<IntPtr>();
        foreach(var binding in ControlledCrew.Active.Bindings)liveControlProxies.Add(binding.Actor.Pointer);
        selectedControlProxies.IntersectWith(liveControlProxies);
        foreach(var binding in ControlledCrew.Active.Bindings)
        {
            if(!Owns(binding.Caster)||binding.Kind is not ("O" or "C" or "M")||
                !Owns(binding.Actor)||selectedControlProxies.Contains(binding.Actor.Pointer))continue;
            var leader=SelectionInput?.leader;
            if(leader==null||leader==binding.Caster)
            {MoveProbe.SelectAlone(binding.Actor);selectedControlProxies.Add(binding.Actor.Pointer);}
            else if(leader==binding.Actor)selectedControlProxies.Add(binding.Actor.Pointer);
        }

        pursuit.SetCrew(hostCrew,guestCrew,Array.ConvertAll(controlledRows,row=>MoveProbe.CrewById(row[0])),preserveMissing:true);
    }
    internal bool MirrorsExtra(MiCharacter actor)=>extraCrew.Mirrors(actor);
    private readonly AttackPlayback playbackA = new AttackPlayback(), playbackB = new AttackPlayback();

    private readonly MissionAlarmSync missionAlarm = new MissionAlarmSync();

    private readonly ReinforcementSync houses=new ReinforcementSync();

    private readonly GuardPursuit pursuit = new GuardPursuit();
    internal void RestoreDetectionView(){if(CanSend&&!Stopped)pursuit.RestoreDetectionView();}

    internal bool PursuitPoint(string id,out Vector3 point) => pursuit.Waypoint(id,out point);

    internal void SyncSight(MiCharacter guard,Entity target,ref Detection.DetectionState state)

    {

        if (stopped || Session==null || guard==null || target==null || guard.guid.isEmpty()) return;
        // Replica animation can leave a different local trail. Only the host
        // decides whether footprints were discovered; its guard search route,
        // alert and combat results are replayed independently of this query.
        if(target.TryCast<FootprintBase>()!=null)
        { state=Detection.DetectionState.NotVisible; return; }
        if(!target.bAsCharacter(out var observed) || target!=guestCrew && target!=hostCrew && !extraCrew.Mirrors(observed)) return;

        if (pursuit.Visibility(Melee.GuardId(guard),target,out var seen)) state=seen;

    }

    private readonly GuardOutcomes guardOutcomes = new GuardOutcomes();

    private int readNumber;

    private int confirmedReadNumber;

    private readonly GuestPace pace = new GuestPace();

    private readonly CoopWaitLock paceLock = new CoopWaitLock();
    private readonly CrewWaitGroup crewWaits=new CrewWaitGroup();

    private CancellationTokenSource orders = new CancellationTokenSource();

    internal CancellationToken OrderToken => orders.Token;

    internal int WaitGeneration { get; private set; }

    private bool holdingInputs;
    private float hostPauseSeen=float.NegativeInfinity;
    private bool hostPaused;
    private bool hostCutscene;

    private void ApplyWait()

    {

        if(hostPaused && Time.realtimeSinceStartup-hostPauseSeen>3f)hostPaused=false;
        paceLock.SetWorld(pace.World || hostPaused);

        crewWaits.Update(Roster,Owns,pace.Crew,paceLock.WorldHeld,Time.realtimeSinceStartup,
            actor=>actor==guestCrew?HostAttacking:extraCrew.Busy(actor),
            (actor,elapsed)=>{if(actor==guestCrew)route.ResumeWait(elapsed,Time.realtimeSinceStartup);else extraCrew.ResumeWait(actor,elapsed,Time.realtimeSinceStartup);});

        if (pace.World && !holdingInputs)

        {

            orders.Cancel(); orders.Dispose();

            orders=new CancellationTokenSource();

            WaitGeneration++;

            turn.Clear();

        }

        holdingInputs=pace.World;

    }

    private string receipt;

    private float readStarted;

    private int ticketBarrier;

    private int moveReadBarrier;

    private bool moveInFlight;

    private int sentWalkTag;

    private readonly ConfirmedTurn turn = new ConfirmedTurn();

    private readonly CrewRoutePlayback routePlayback=new CrewRoutePlayback();

    private AcceptedGroundRoute route=>routePlayback.Route;

    private int latestGroundTag;

    private Vector3 sentGoal;

    private string sentSurface;

    private float lastRouteViewStarted;

    private Vector3 travelDirection;

    private float lastTravel = float.NegativeInfinity;

    private readonly CrewFollower followA=new CrewFollower(),followB=new CrewFollower();



    internal void QueueGroundTurn(Vector3 point)

    {

        latestGroundTag = Time.frameCount;

        // A later user gesture supersedes an old ACK, even before the second

        // click of a run is classified and transmitted.

        if (guestCrew == null || HostAttacking || PlayingCommittedContact || !lastConfirmedB.HasValue) return;

        if (route.Active)

        {

            if (Vector3.Distance(point,route.Goal)<=0.25f || AcceptedGroundRoute.Continuing(guestCrew.transform.position,route.Goal,point))

            { turn.Clear(); return; }

            StopRoute();

        }

        float now = Time.realtimeSinceStartup;

        if (!turn.Begin(Time.frameCount, guestCrew.transform.position, point, travelDirection, now, lastTravel)) return;

        // Only stop the GoTo this guest issued to follow a snapshot. Keep

        // native interactions and paired animations owned by the game.

        if (followB.Last.HasValue && guestCrew.health?.eHealthState == MiCharacterHealth.HealthState.Alive)

        {

            var input = guestCrew.controllerPlayer.skillCommand;

            var process = guestCrew.controller.skillCommandProcess;

            bool sameInput = FollowsPoint(input,followB.Last) || FollowsPoint(input,followB.Previous);

            bool sameProcess = FollowsPoint(process,followB.Last) || FollowsPoint(process,followB.Previous);

            if ((sameInput || input?.m_skill == null) && (sameProcess || process?.m_skill == null) && (sameInput || sameProcess))

                guestCrew.controllerPlayer.cancelSkill();

        }

        followB.Previous = followB.Last = null;

        followB.RunWaypoint = null;

        MoveProbePlugin.ProbeLog.LogInfo($"Guest holding turn {Time.frameCount} at snapshot {order.LastSequence}");

    }



    private static bool FollowsPoint(SkillCommand cmd,Vector3? point) => point.HasValue && cmd?.m_skill?.eSkillType == Skill.SkillType.GoTo &&

        cmd.m_target != null && cmd.m_target.m_entityTarget == null && Vector3.Distance(cmd.m_target.v3Target,point.Value)<0.18f;



    private void StopRoute(bool clear=true)=>routePlayback.Stop(guestCrew,clear,PlayingCommittedContact);

    internal void SendingGroundOrder(int tag,string command)

    {

        moveInFlight = true; sentWalkTag = tag;

        var parts=command.Split(' ');

        sentGoal=ReadPosition(parts,1);

        sentSurface=parts.Length==5 ? parts[4] : null;

    }

    internal void SendingInteractionOrder() { moveInFlight=true;sentWalkTag=0; }

    private string ticketHint;

    private void ClearTicket() { ticketHint = null; ticketBarrier = readNumber; }

    internal void OrderCompleted(bool accepted=true)

    {

        if (moveInFlight)

        {

            moveInFlight = false;

            moveReadBarrier = readNumber;

            turn.Reply(sentWalkTag,accepted,Time.realtimeSinceStartup,readNumber);

            if (sentWalkTag!=0 && sentWalkTag==latestGroundTag)

            {

                if (accepted && lastConfirmedB.HasValue)

                {

                    // The host accepted this destination for B. Mirror the

                    // route only while subsequent host movement confirms it.

                    routePlayback.Begin(sentGoal,sentSurface,lastConfirmedB.Value,Time.realtimeSinceStartup);

                }

                else StopRoute();

            }

        }

        ClearTicket();

        nextRead = 0f;

    }

    internal void UseNextTicket(string hint) { ticketHint=hint; ticketBarrier=readNumber; }

    internal string TakeTicket() { string hint = ticketHint; ClearTicket(); return hint; }

    internal bool RedirectingRun { get; private set; }

    internal bool HostAttacking { get; private set; }

    internal bool PlayingCommittedContact => CanSend && HostAttacking && (playbackB.PlayingContact ||

        hostPlayingContact || contactChallenge > 0 && sentContactChallenge == contactChallenge);

    private int contactChallenge;

    private int sentContactChallenge;

    private int sentContactSequence;

    private bool hostPlayingContact;

    private string contactGuard, intendedGuard;

    private MiCharacter contactVictim;

    private Vector3 contactPosition;

    private Vector3? contactVictimPosition;

    private float nextContactReply;

    internal void WithdrawContact() { intendedGuard = null; }

    // Capture the visible geometry together with the click. An order waiting

    // in the send queue must never acquire newer evidence of melee contact.

    internal string WithContactProof(string hit, MiCharacter victim)

    {

        if (victim == null || !CanSend || moveInFlight || confirmedReadNumber <= moveReadBarrier || !lastConfirmedB.HasValue ||

            order.LastSequence <= 0 || Time.realtimeSinceStartup > contactReplyExpires ||

            guestCrew?.health == null || guestCrew.health.eHealthState != MiCharacterHealth.HealthState.Alive ||

            victim.health == null || victim.health.eHealthState != MiCharacterHealth.HealthState.Alive)

            return hit;

        var local = guestCrew.transform.position;

        var point = victim.transform.position;

        if (!(Vector3.Distance(local, lastConfirmedB.Value) <= 0.95f) ||

            !(Vector3.Distance(local, point) <= 3.65f)) return hit;

        if (guardText != null && guardKey == Melee.GuardId(victim) &&

            !(Vector3.Distance(guardPosition, point) <= 1.15f)) return hit;

        string proof = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2:F1} {3:F1} {4:F1}",

            hit, order.LastSequence, point.x, point.y, point.z);

        // Every payload also carries its 64-character crew identity, session

        // and two separating spaces. Oversize coordinates use the normal gate.

        return 64 + Session.Length + 2 + proof.Length <= 152 ? proof : hit;

    }

    internal string ContactReply()

    {

        if (!CanSend || contactChallenge == 0 || contactGuard != intendedGuard || sentContactSequence == order.LastSequence ||

            Time.realtimeSinceStartup < nextContactReply || Time.realtimeSinceStartup-lastGood > 0.25f ||

            Time.realtimeSinceStartup > contactReplyExpires ||

            Vector3.Distance(guestCrew.transform.position,contactPosition) > 0.95f) return null;

        if (contactVictim == null) contactVictim = Melee.Resolve(contactGuard);

        nextContactReply = Time.realtimeSinceStartup + 0.075f;

        if (contactVictim?.health == null || contactVictim.health.eHealthState != MiCharacterHealth.HealthState.Alive ||

            guestCrew.health.eHealthState != MiCharacterHealth.HealthState.Alive ||

            Vector3.Distance(guestCrew.transform.position,contactVictim.transform.position) > 3.65f ||

            contactVictimPosition.HasValue && Vector3.Distance(contactVictim.transform.position,contactVictimPosition.Value)>1.15f) return null;

        sentContactChallenge = contactChallenge;

        sentContactSequence = order.LastSequence;

        return "READY " + contactChallenge.ToString(CultureInfo.InvariantCulture);

    }

    private float lastAttackInput = float.NegativeInfinity;

    private float contactReplyExpires;

    internal bool HasMeleeInput { get; private set; }

    internal void AttackInput(string command)

    {

        latestGroundTag=0; StopRoute();

        turn.Clear();

        lastAttackInput = Time.realtimeSinceStartup;

        if (command.StartsWith("HIT K ", StringComparison.Ordinal) || command.StartsWith("HIT L ", StringComparison.Ordinal) || command.StartsWith("HIT LR ") || command.StartsWith("HIT KR "))

        {

            HasMeleeInput = true;

            intendedGuard = GuestActorIntent.ContactTarget(command);

        }

        else WithdrawContact();

    }

    internal bool CanRedirectAttack => HostAttacking || Time.realtimeSinceStartup - lastAttackInput < 2f;

    private readonly BodySync bodySync=new BodySync();

    private readonly InteractionSync interactions=new InteractionSync();

    internal void InteractionInput() { latestGroundTag=0; StopRoute(); turn.Clear(); WithdrawContact(); }

    private string manualStyle;

    private int manualReadBarrier;

    private float manualDeadline;

    internal bool PreferredCrouch { get; private set; }

    private readonly GuardDiagnostics guardDiagnostics = new GuardDiagnostics("guest");

    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();

    private readonly SnapshotOrder order = new SnapshotOrder();

    internal CancellationToken Token => lifetime.Token;

    internal string Session { get; private set; }

    internal string Status { get; private set; } = "Connecting";

    internal bool Crouched { get; private set; }

    internal bool Stopped => stopped;

    internal string BoundIdentity => GuestMove.Identity(guestCrew);

    internal bool ReceivingFrames => ready && !stopped && Time.realtimeSinceStartup - lastGood < 2f;
    internal bool CanSend => !GuestWorldMap.NativeUiOwnsCrew && ReceivingFrames && !pace.World;



    internal CrewSync(Uri room, MiCharacter guest)

    {

        InteractionSync.ResetRegistry();

        Room = room;

        lan=LanStateFeed.Open(room,tunnelEndpoint:LanStateFeed.TunnelEndpoint(room,System.IO.Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","session-next","runtime","steam-launch","udp-endpoint.txt")));

        guestCrew = guest;

    }



    internal static string Position(Vector3 p) =>

        string.Format(CultureInfo.InvariantCulture, "{0:R} {1:R} {2:R}", p.x, p.y, p.z);



    internal static string Describe(MiCharacter crew,bool running=false,bool surface=false) =>

        $"{GuestMove.Identity(crew)} {Position(GuestGroundTarget.NavigationPosition(crew))} {(running ? "R" : NativeGait.Read(crew))}"+(surface?GuestGroundTarget.SurfaceCode(crew).ToString():"");



    private readonly CrewOwnershipReplica crewOwners=new CrewOwnershipReplica();
    private readonly GuestOrderVersions orderVersions=new GuestOrderVersions();
    internal long OwnershipRevision=>crewOwners.Revision;
    internal string TransportLabel=>usingLan?"UDP":"TCP";
    private MiPlayerInput selectionInput;
    internal MiPlayerInput SelectionInput=>selectionInput??guestCrew?.controllerPlayer?.m_playerInput??hostCrew?.controllerPlayer?.m_playerInput;
    internal void ObserveInput(MiPlayerInput input)
    {
        if(input?.leader!=null && KnownCrew(input.leader))selectionInput=input;
    }
    internal string EncodeOrder(string actor,string command,long expectedRevision=0)=>
        Spectating&&command?.StartsWith("SELECT ",StringComparison.Ordinal)!=true?null:
            orderVersions.Encode(Session,actor,command,crewOwners.Revision,expectedRevision);
    internal void ExtraOrderSending(string actor,long sequence,string command,bool queued)=>extraCrew.Sending(actor,sequence,readNumber,Time.realtimeSinceStartup,command,queued);
    internal void ExtraOrderCompleted(string actor,long sequence,bool accepted){extraCrew.Complete(actor,sequence,accepted,readNumber);nextRead=0f;}
    internal void NetworkOrderCompleted(string actor,long sequence,bool accepted)
    {
        if(actor==null||actor==BoundIdentity)OrderCompleted(accepted);
        else ExtraOrderCompleted(actor,sequence,accepted);
    }
    internal bool ReservedByHost(MiCharacter actor)=>actor!=null && (actor==hostCrew || crewOwners.Owner(GuestMove.Identity(actor))=='H');
    internal bool SelectionBlocked(MiSelectable selectable)
    {
        if(stopped||Session==null||selectable==null)return false;
        if(hostCrew?.controllerPlayer?.m_selectable==selectable)return true;
        foreach(var actor in extraCrew.Actors)if(actor?.controllerPlayer?.m_selectable==selectable)return ReservedByHost(actor);
        return false;
    }
    internal IEnumerable<MiCharacter> Roster
    {get{if(hostCrew!=null)yield return hostCrew;if(guestCrew!=null&&guestCrew!=hostCrew)yield return guestCrew;foreach(var actor in extraCrew.Actors)yield return actor;}}
    internal char OwnerCode(MiCharacter actor)=>actor==null?'U':actor==hostCrew?'H':actor==guestCrew&&!Spectating?'G':crewOwners.Owner(GuestMove.Identity(actor));
    internal bool KnownCrew(MiCharacter actor)=>actor!=null&&(actor==guestCrew||actor==hostCrew||extraCrew.Mirrors(actor));
    internal bool Owns(MiCharacter crew)=>crew!=null&&!Spectating&&(crew==guestCrew || extraCrew.Mirrors(crew)&&OwnerCode(crew)=='G');
    internal bool SelectionReady=>OwnershipRevision==0 || GuestCrewSelection.Ready(SelectionInput,OwnerCode);

    internal bool CommittedContactFor(MiCharacter actor)=>actor==guestCrew?PlayingCommittedContact:extraCrew.PlayingContact(actor);
    internal bool CanRedirectAttackFor(MiCharacter actor)=>actor==guestCrew?CanRedirectAttack:extraCrew.CanRedirect(actor,Time.realtimeSinceStartup);
    internal bool HasMeleeInputFor(MiCharacter actor)=>actor==guestCrew?HasMeleeInput:extraCrew.HasMelee(actor);
    internal bool PreferredCrouchFor(MiCharacter actor)=>actor==guestCrew?PreferredCrouch:extraCrew.PreferredCrouch(actor);
    internal bool CrouchedFor(MiCharacter actor)=>actor==guestCrew?Crouched:extraCrew.Crouched(actor);
    internal void QueuedInput(MiCharacter actor,string command)
    {if(actor!=guestCrew)extraCrew.Queued(actor,command,Time.realtimeSinceStartup);}
    internal void AttackInput(MiCharacter actor,string command){if(actor==guestCrew)AttackInput(command);}
    internal void InteractionInput(MiCharacter actor){if(actor==guestCrew)InteractionInput();}
    internal void ManualStance(MiCharacter actor,bool crouch){if(actor==guestCrew)ManualStance(crouch);}
    internal void StanceReply(string actor,string requested,bool accepted){if(actor==BoundIdentity)StanceReply(requested,accepted);}
    internal void WithdrawContact(MiCharacter actor){if(actor==guestCrew)WithdrawContact();else extraCrew.Withdraw(actor);}
    internal void QueueGroundTurn(MiCharacter actor,Vector3 point){if(actor==guestCrew)QueueGroundTurn(point);}
    internal void SendingGroundOrder(MiCharacter actor,int tag,string command){if(actor==guestCrew)SendingGroundOrder(tag,command);}
    internal void SendingInteractionOrder(MiCharacter actor){if(actor==guestCrew)SendingInteractionOrder();}
    internal string WithContactProof(MiCharacter actor,string hit,MiCharacter victim)
    {
        if(actor==guestCrew)return WithContactProof(hit,victim);
        if(!CanSend || victim==null || order.LastSequence<=0 ||
            actor?.health?.eHealthState!=MiCharacterHealth.HealthState.Alive ||
            victim.health?.eHealthState!=MiCharacterHealth.HealthState.Alive ||
            !extraCrew.Confirmed(actor,Time.realtimeSinceStartup,out var host))return hit;
        var local=actor.transform.position;var target=victim.transform.position;
        if(Vector3.Distance(local,host)>0.95f || Vector3.Distance(local,target)>3.65f)return hit;
        string proof=string.Format(CultureInfo.InvariantCulture,"{0} {1} {2:F1} {3:F1} {4:F1}",hit,order.LastSequence,target.x,target.y,target.z);
        return 64+Session.Length+2+proof.Length<=152?proof:hit;
    }

    internal void ManualStance(bool crouch)

    {

        latestGroundTag=0; StopRoute();

        NativeGait.StopForStance(guestCrew,crouch);

        followB.Previous=followB.Last=null;followB.RunWaypoint=null;

        turn.Clear();

        PreferredCrouch = crouch;

        RedirectingRun = false;

        manualStyle = crouch ? "C" : "W";

        manualReadBarrier = int.MaxValue;

        manualDeadline = Time.realtimeSinceStartup + 6f;

    }

    internal void StanceReply(string requested, bool accepted)

    {

        if (manualStyle != (requested == "CROUCH" ? "C" : "W")) return;

        if (accepted) manualReadBarrier = readNumber;

        else manualStyle = null;

    }

    public void Dispose()

    {

        if (stopped) return;

        stopped = true;
        InteractionSync.ResetRegistry();
        footprints.Release();ShipRoutineSync.Clear();

        lan?.Dispose();

        crewWaits.Clear();
        paceLock.Release();

        pursuit.Release();
        extraCrew.Clear();
        skillObjects.Clear();traps.Clear();ghosts.Clear();resources.Clear();freezes.Clear();blinds.Clear();frozen.Clear();duels.Clear();digs.Clear();mimics.Clear();controls.Clear();charming.Clear();FishingItemSync.Clear();MimicChoice.Clear();cannons.Clear();fishing.Clear();
        ControlledCrew.Active.Clear();selectedControlProxies.Clear();
        controlledSources=null;controlledRows=null;controlledManifest=null;controlledDeferred=null;

        orders.Cancel(); orders.Dispose();

        ready = false;

        lifetime.Cancel();

        if (Session != null && order.LastSequence > 0) _ = GuestMove.Leave(Room,Session,order.LastSequence);

        // In-flight operations hold this token; cancellation is immediate. Keep the source

        // alive until those references are collected, rather than disposing under a send.

    }



    private void Note(string message)

    {

        if (Status != message) MoveProbePlugin.ProbeLog.LogInfo("Guest sync: " + message);

        Status = message;

    }



    internal void Tick()

    {

        if (stopped) return;

        if (guestCrew == null) { Dispose(); Note("Waiting for saved crew."); return; }

        if(!GuestWorldMap.NativeUiOwnsCrew)RefreshControlledBindings();

        if(!GuestWorldMap.NativeUiOwnsCrew&&!hostCutscene&&!(MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting))pursuit.Tick();

        float now=Time.realtimeSinceStartup;

        var sample=lan?.Drain(now) ?? default;

        bool live=lan?.Live(now)==true;

        if (usingLan!=live)

        {

            usingLan=live;

            MoveProbePlugin.ProbeLog.LogInfo(live ? "State transport: UDP (LAN/tunnel)." : "State transport: HTTP/TCP fallback.");

        }

        if (read != null && read.IsCompleted)

        {

            var completed = read;

            read = null;

            nextRead=httpStarted+0.065f;

            try

            {

                string body=completed.GetAwaiter().GetResult();

                if (!live && httpRead>sample.Read)

                    sample=new LanStateFeed.Sample { Read=httpRead, Started=httpStarted, Body=body };

            }

            catch (Exception ex)

            {

                if (!live)

                {

                    ready=false;

                    string reason=GuestMove.Failure(ex);

                    Note(reason=="HTTP 404" ? "Room changed. Copy the new room URL and press F6." : "Retrying room: "+reason);

                    nextRead=now+0.35f;

                }

            }

        }

        if (sample.Read>sampleRead)

        {

            sampleRead=sample.Read;

            readStarted=sample.Started;

            bool previouslyReady=ready;

            ready=false;

            var tick=System.Diagnostics.Stopwatch.GetTimestamp();

            try { Apply(sample.Body,previouslyReady); }

            catch(Exception ex) {
                Note("State apply failed: "+GuestMove.Failure(ex));
                if(Time.realtimeSinceStartup>=nextApplyError){nextApplyError=Time.realtimeSinceStartup+10f;MoveProbePlugin.ProbeLog.LogWarning("State apply stack: "+ex);}
            }

            if (ready && confirmedReadNumber==sample.Read)

            {

                double ms=(System.Diagnostics.Stopwatch.GetTimestamp()-tick)*1000d/System.Diagnostics.Stopwatch.Frequency;

                slowestApplyMs=Math.Max(slowestApplyMs,ms);

                if (lastAppliedAt>0f) largestGap=Math.Max(largestGap,now-lastAppliedAt);

                lastAppliedAt=now;

                timingSamples++;

                if (route.Issued) routeSamples++;

                if (now>=nextTimingLog)

                {

                    MoveProbePlugin.ProbeLog.LogInfo($"State delivery {(live ? "UDP" : "HTTP")} samples={timingSamples} nativeRoute={routeSamples} longestGapMs={largestGap*1000f:F0} maxApplyMs={slowestApplyMs:F1}");

                    nextTimingLog=now+5f; timingSamples=routeSamples=0; largestGap=0f; slowestApplyMs=0;

                }

            }

            if (!ready && !pace.World) nextRead=now+0.35f;

        }

        if (stopped) return;

        // Handle newly arrived samples before evaluating the slow-frame timers.

        if (paceLock.ExternalPause || (MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting) || hostCutscene) pace.Reset();

        else pace.Tick(now);

        if(!GuestWorldMap.NativeUiOwnsCrew)ApplyWait();

        if (!GuestWorldMap.NativeUiOwnsCrew&&!hostCutscene && !(MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting) && route.Issued && (now-lastGood>0.45f || now-lastRouteViewStarted>0.85f || pace.World || pace.Crew || paceLock.ExternalPause))

            StopRoute(clear:false);

        if(!GuestWorldMap.NativeUiOwnsCrew&&!hostCutscene&&!(MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting))extraCrew.Tick(now,pace.World || pace.Crew || paceLock.ExternalPause);

        if (pace.World) Note("Network wait: mission paused. F11 disconnect.");

        lan?.Send(now,receipt,ref readNumber);

        if (!live && read==null && now>=nextRead)

        {

            httpRead=++readNumber;

            httpStarted=now;

            read = GuestMove.State(Room, Token,receipt);

        }

    }



    private void Apply(string body,bool previouslyReady)

    {

        if(!ShipRoutineProtocol.Split(body,out body,out var shipRows)){Note("Invalid ship routine snapshot.");return;}
        string[][] extra=Array.Empty<string[]>();long ownerRevision=0;string ownerVector=null;
        ControlledCrewProtocol.Entry[] controlled=Array.Empty<ControlledCrewProtocol.Entry>();
        string[][] objects=Array.Empty<string[]>();
        TrapProtocol.Group[] trapGroups=Array.Empty<TrapProtocol.Group>();
        string[][] ghostRows=Array.Empty<string[]>();
        ResourceProtocol.Group[] resourceGroups=Array.Empty<ResourceProtocol.Group>();
        string[][] freezeRows=null;
        string[][] blindRows=null;
        string[][] frozenRows=null;
        string[][] duelRows=null;
        string[][] digRows=null,cannonRows=null,fishingRows=null,launchRows=null,cannonFlightRows=null,mimicRows=null,controlRows=null,charmingRows=null,itemRows=null;
        FootprintProtocol.Page footprintPage=null;
        if(body!=null && body.StartsWith("S19 ",StringComparison.Ordinal))
        {
            if(!CrewRosterProtocol.Read(body,out var core,out extra,out ownerRevision,out ownerVector,out controlled,out objects,out trapGroups,out ghostRows,out resourceGroups,out freezeRows,out blindRows,out frozenRows,out duelRows,out digRows,out cannonRows,out fishingRows,out launchRows,out cannonFlightRows,out mimicRows) ||
                !FishingItemProtocol.Split(body,out var withoutItems,out itemRows) || !CharmingGhostProtocol.Split(withoutItems,out var withoutCharming,out charmingRows) || !ControlProtocol.Split(withoutCharming,out var withoutControl,out controlRows) || !MimicProtocol.Split(withoutControl,out var withoutMimic,out _) || !FootprintProtocol.Split(withoutMimic,out _,out footprintPage)) { Note("Invalid whole-crew snapshot.");return; }
            body=core;
        }

        bool spectatorFrame=ownerVector!=null&&ownerVector.Length>=2&&ownerVector[1]=='-';
        if (!SyncProtocol.Snapshot(body, out var f, out int sequence, out bool attacking, out var outcomes, out var ticket, out var combat, out int challenge, out string victimId,out var routePoint,out var bodies,out var interactionData,out var bushes,Array.ConvertAll(extra,row=>row[0]),spectatorFrame))

        { Note(body == "NOT_READY" || body == "NO_REPLY" ? "Host not ready. Bind crew with F7/F8." : "Invalid or incompatible host snapshot."); return; }

        if (f[0] != "S18") { Note("Update both game plugins to the same current version."); return; }

        if (Session != null && Session != f[1])

        { Dispose(); Note("Host rebound crew. Restoring pairing."); return; }

        if (Session==f[1] && sequence<=order.LastSequence)

        { ready=previouslyReady; return; }

        // Native map fade toggles the gameplay HUD and its selection children.
        // Keep transport/receipts alive, but do not reconnect controllers,
        // change selection, replay skills or rewrite health under that UI.
        if(GuestWorldMap.NativeUiOwnsCrew)
        {
            Session=f[1];receipt=Session+" "+f[3]+" I";
            hostPaused=f[2]=="WAIT";hostPauseSeen=Time.realtimeSinceStartup;
            pace.Reset();paceLock.SetWorld(hostPaused);
            return;
        }

        Spectating=spectatorFrame;
        if (f[9] != GuestMove.Identity(guestCrew))

        {

            var assigned = MoveProbe.CrewById(f[9]);

            if (assigned == null) { Note("Waiting for matching mission and guest crew."); return; }

            StopRoute();turn.Clear();WithdrawContact();
            interactions.Forget(guestCrew);playbackB.Reset(guestCrew);followB.Reset();
            lastConfirmedB=null;latestGroundTag=0;RedirectingRun=false;
            guestCrew = assigned;
            // Changing the network primary must not collapse a native box
            // selection into one actor or reuse the previous actor's path.
            initialSelection = initialSelection || SelectionInput?.lSelectedCharacter?.Count==0;

        }

        if (hostCrew == null || GuestMove.Identity(hostCrew) != f[4])

        {

            interactions.Forget(hostCrew);
            playbackA.Reset(hostCrew);
            hostCrew = null;
            followA.Reset();

            int matches = 0;

            foreach (var c in UnityEngine.Object.FindObjectsOfType<MiCharacter>())

                if ((Spectating || c != guestCrew) && c.bControllerPlayer() && GuestMove.Identity(c) == f[4])

                { hostCrew = c; matches++; }

            if (matches > 1) { hostCrew = null; Note("Host crew identity is ambiguous."); return; }

        }

        if (hostCrew == null) { Note("Host crew missing in this mission."); return; }
        // Ownership is authoritative for every slot, including former A.

        var ownerIds=new string[extra.Length+2];ownerIds[0]=f[4];ownerIds[1]=f[9];
        for(int i=0;i<extra.Length;i++)ownerIds[i+2]=extra[i][0];
        if(Spectating)
        {
            var realIds=new List<string>{f[4]};foreach(var row in extra)realIds.Add(row[0]);
            ownerIds=realIds.ToArray();ownerVector=ownerVector.Remove(1,1);
        }
        if(!crewOwners.CanApply(ownerRevision,ownerIds,ownerVector,Spectating))
        {Note("Ignored stale or conflicting crew ownership.");return;}

        var proxies=new HashSet<string>(StringComparer.Ordinal);
        foreach(var entry in controlled)proxies.Add(entry.Id);
        var originals=new List<MiCharacter>{hostCrew};if(!Spectating)originals.Add(guestCrew);
        foreach(var row in extra)if(!proxies.Contains(row[0]))
        {
            var actor=MoveProbe.CrewById(row[0]);
            if(actor==null){Note("Waiting for every ship/mission crew member, including DLC crew.");return;}
            originals.Add(actor);
        }
        if(!ControlledCrew.Active.Sample(originals,GuestMove.RawIdentity))
        {Note("Waiting for unambiguous native controlled actors.");return;}
        // A validated complete host manifest explicitly retires its former
        // possession. Restore native controllers before discarding that alias.
        controls.ReconcileManifest(controlledManifest,controlled);
        ControlledCrew.Active.Restrict(controlled);
        // Apply the complete ownership vector, but defer only explicitly
        // announced skill-created rows. The caster must still replay the
        // native spawn/control action before those rows can resolve locally.
        if(!extraCrew.Prepare(extra,proxies)){Note("Waiting for matching crew.");return;}

        if (!order.Accept(f[1], sequence)) { Note("Ignored stale or duplicate snapshot."); return; }

        bool ownerChanged=crewOwners.Revision!=ownerRevision;
        crewOwners.Apply(ownerRevision,ownerIds,ownerVector,Spectating);
        // Loading restores the host's saved local selection too. Filter it
        // on confirmed frames even when paused/spectating, before input can
        // acquire a stale host leader. Never alter native cutscene selection.
        if(!SaveProcessManager.bLoading&&!MiCutsceneHandler.bCutsceneRunning)
            GuestCrewSelection.RemoveHostSelection(SelectionInput,OwnerCode);
        controlledSources=originals;controlledRows=extra;controlledManifest=controlled;controlledDeferred=proxies;
        if(ownerChanged)MoveProbePlugin.ProbeLog.LogInfo($"Guest ownership revision={ownerRevision} owners={ownerVector} actors={string.Join(",",ownerIds)}");
        // Preserve holes for not-yet-spawned proxies. Compacting the list would
        // apply the missing ghost's detection digit to the next crew member.
        pursuit.SetCrew(hostCrew,guestCrew,Array.ConvertAll(extra,row=>MoveProbe.CrewById(row[0])),preserveMissing:true);

        if (initialSelection)

        {

            if(!Spectating&&SelectionInput?.lSelectedCharacter?.Count>0)MoveProbe.SelectAlone(guestCrew); initialSelection = false; lastConfirmedB = null;
            followB.Reset();

            lastTravel = float.NegativeInfinity; turn.Clear(); followB.Previous = followB.Last = followB.RunWaypoint = null;

            route.Clear(); latestGroundTag=0;

        }

        Session = f[1];
        footprints.Apply(footprintPage,Roster,Time.realtimeSinceStartup-readStarted);
        SessionGuestBarrier.ReceivedFrame();

        guardDiagnostics.Begin(Session);

        string receiptPrefix = Session + " " + f[3] + " ";
        // Cutscene-owned actors are not ordinary playable actors. Do not
        // overwrite scripted death/poses or convert native timeline pauses
        // into a remote player pause. Keep acknowledgements alive for reload.
        hostCutscene=f[2]=="SCENE";
        if(hostCutscene)ShipRoutineSync.Clear();else ShipRoutineSync.Apply(shipRows);
        if ((MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting) || hostCutscene)
        {
            hostPaused=false;pace.Reset();ApplyWait();
            receipt=receiptPrefix+"I";
            Note("Native cutscene in progress.");return;
        }

        // Death can pause the native mission. Apply confirmed health before
        // the WAIT/HOLD gates, so the final lethal sample is never discarded.
        ghosts.Apply(ghostRows,pursuit.ReconcileHealth);
        resources.Apply(resourceGroups,Session,sequence);
        pursuit.ApplyCrewHealth(combat,hostCrew,guestCrew);
        extraCrew.ApplyHealth(pursuit);
        freezes.Apply(freezeRows,Roster,guardOutcomes.Registry.Values);
        blinds.Apply(blindRows,Roster,guardOutcomes.Registry.Values);
        frozen.Apply(frozenRows,Roster,guardOutcomes.Registry.Values);
        duels.Apply(duelRows,Roster,guardOutcomes.Registry.Values);
        charming.Apply(charmingRows,Time.realtimeSinceStartup,MoveProbe.CrewById);
        controls.Apply(controlRows,Time.realtimeSinceStartup,MoveProbe.CrewById,Melee.Resolve);
        mimics.Apply(mimicRows,MoveProbe.CrewById,Time.realtimeSinceStartup);
        CannonLaunchSync.Apply(launchRows);
        CannonFlightSync.Apply(cannonFlightRows);
        cannons.Apply(cannonRows);
        fishing.Apply(fishingRows);FishingItemSync.Apply(itemRows);
        // Confirmed process end must not wait behind the network pause gate:
        // duel completion itself depends on seeing the authoritative death/KO.
        // Restrict this early path to completed frozen/duel targets; ordinary
        // melee results retain their existing animation protection below.
        var completedEffects=Array.FindAll(outcomes,result=>(result[0]=='K'||result[0]=='D')&&
            (frozen.ReadyForOutcome("e"+result.Substring(1))||duels.ReadyForOutcome("e"+result.Substring(1))));
        guardOutcomes.Apply(completedEffects);
        foreach(var result in completedEffects)if(guardOutcomes.IsApplied(result))
        {string key="e"+result.Substring(1);frozen.OutcomeApplied(key);duels.OutcomeApplied(key);}

        bool localPaused = paceLock.ExternalPause || (MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting);
        bool reportPlayerPause=localPaused&&!UIWorldMap.bOpenStatic;
        hostPaused=f[2]=="WAIT";hostPauseSeen=Time.realtimeSinceStartup;

        pace.Observe(localPaused ? "WAIT" : f[2],readStarted,Time.realtimeSinceStartup);

        ApplyWait();

        if (f[2] == "HOLD" || pace.World)

        {

            receipt = receiptPrefix + (reportPlayerPause ? "P" : paceLock.WorldHeld ? "H" : "I");

            Note("Network wait: mission paused. F11 disconnect.");

            return;

        }

        if (localPaused || f[2] == "WAIT")

        { receipt = receiptPrefix + (reportPlayerPause ? "P" : "I"); Note("Mission paused by a player. Close the pause menu to resume."); return; }

        if (Time.timeScale <= 0f)

        { receipt = receiptPrefix + "I"; Note("Waiting for both missions to resume."); return; }

        Vector3 a = ReadPosition(f, 5), b = ReadPosition(f, 10);

        string travel = f[13].Substring(0,1);

        string rest = f[13].Substring(1,1);

        char hostCover = f[13][2], guestCover = Cover(guestCrew);

        if (hostCover != lastHostCover || guestCover != lastGuestCover)

        {

            MoveProbePlugin.ProbeLog.LogInfo($"B cover at snapshot {sequence}: host={hostCover} guest={guestCover} (o=outside i=inside h=hidden x=unknown)");

            lastHostCover=hostCover; lastGuestCover=guestCover;

        }

        Crouched = travel == "C";

        float errorA = Vector3.Distance(GuestGroundTarget.NavigationPosition(hostCrew),a);

        bool usingA=interactions.Apply(interactionData,0,hostCrew,a,true);

        if (!usingA && !playbackA.Apply(combat,0,hostCrew,a)) followA.Follow(hostCrew,a,f[8].Substring(0,1),hostSurface:f[8][1]);

        float errorB=0f;
        bool usingB=false,freshMoveState=false;
        if(!Spectating)
        {
        var localB = GuestGroundTarget.NavigationPosition(guestCrew);

        errorB = Vector3.Distance(localB, b);

        if (manualStyle != null && (Time.realtimeSinceStartup >= manualDeadline ||

            sampleRead > manualReadBarrier && rest == manualStyle)) manualStyle = null;

        RedirectingRun = !attacking && travel == "R";

        HostAttacking = attacking;

        hostPlayingContact = combat[6] != "0" && (combat[7] == "L" || combat[7] == "K");

        if (manualStyle == null) PreferredCrouch = rest == "C";

        bool relocate = !lastConfirmedB.HasValue || !attacking && combat[6] == "0" &&

            Vector3.Distance(lastConfirmedB.Value,b) > 8f;

        freshMoveState = !moveInFlight && sampleRead > moveReadBarrier && manualStyle==null;

        var segment = b - (lastConfirmedB ?? b);

        float now = Time.realtimeSinceStartup;

        if (attacking || relocate) { StopRoute(); turn.Clear(); lastTravel = float.NegativeInfinity; followB.Previous = followB.Last = null; }

        else if (segment.sqrMagnitude > 0.0004f) { travelDirection = segment.normalized; lastTravel = now; }

        bool holdTurn = turn.Hold(b, localB, segment, now, sampleRead);

        usingB=interactions.Apply(interactionData,8,guestCrew,b,freshMoveState);

        bool playing=usingB || playbackB.Apply(combat,6,guestCrew,b,!attacking,freshMoveState);

        bool replayedRoute=!playing && freshMoveState && !attacking && route.Active &&

            ReplayRoute(b,localB,travel,now,now-readStarted,routePoint==null ? (Vector3?)null : ReadPosition(routePoint,0));

        if (!playing && !replayedRoute && freshMoveState && !holdTurn)

        {

            // When B has already crossed the latest sample along the host's path,

            // turning back for a few centimetres creates a visible double turn.

            bool justPassed = segment.sqrMagnitude > 0.0004f && errorB < 0.55f &&

                Vector3.Dot(segment.normalized, localB-b) > errorB * 0.8f;

            bool edgeMismatch = hostCover!='x' && guestCover!='x' && (hostCover=='o') != (guestCover=='o') &&

                segment.sqrMagnitude <= 0.0004f;

            if (!justPassed) followB.Follow(guestCrew, b, travel, true, relocate, segment, edgeMismatch, hostCover!='o',f[13][3]);

            else NativeGait.Apply(guestCrew, travel);

        }

        lastConfirmedB = b;
        }

        extraCrew.ApplyActions(sampleRead,Time.realtimeSinceStartup,readStarted,pace.Crew);
        digs.Apply(digRows);
        RefreshControlledBindings();
        var effectA=InteractionSync.ProtectedTarget(hostCrew);if(effectA!=null)extraCrew.Protected.Add(effectA);
        var effectB=InteractionSync.ProtectedTarget(guestCrew);if(effectB!=null)extraCrew.Protected.Add(effectB);
        // Possession outlives its cast animation. While native control is
        // materializing (or remains active), ordinary NPC AI/body correction
        // must not reclaim that target between network samples.
        foreach(var entry in controlled)if(entry.Kind is "C" or "O")extraCrew.Protected.Add(entry.Target);
        cannons.Protect(extraCrew.Protected);CannonFlightSync.Protect(extraCrew.Protected);fishing.Protect(extraCrew.Protected);
        guardOutcomes.SetAlarmActive(combat[12].StartsWith("Alarm",StringComparison.Ordinal));

        guardOutcomes.Apply(outcomes, playbackA.ProtectedId, playbackB.ProtectedId,extraCrew.Protected);

        if(!usingA && !usingB && !interactions.ProtectBodies(interactionData,0,hostCrew) && !interactions.ProtectBodies(interactionData,8,guestCrew))

            bodySync.Apply(bodies,guardOutcomes.Registry,hostCrew,Spectating?null:guestCrew,playbackA.ProtectedId,playbackB.ProtectedId,freshMoveState,extraCrew.Protected);
        extraCrew.ApplyResults(guardOutcomes.Registry,playbackA.ProtectedId,playbackB.ProtectedId);
        skillObjects.Apply(objects,Roster);
        traps.Apply(trapGroups,new List<MiCharacter>(Roster));

        coverResults.Apply(bushes,hostCrew,Spectating?null:guestCrew);
        missionAlarm.Apply(combat);

        try { houses.Apply(combat[24],combat[12]); }

        catch(Exception ex) { if(sequence%50==0) MoveProbePlugin.ProbeLog.LogWarning($"Alarm house apply: {ex.GetType().Name}"); }

        pursuit.Apply(combat,guardOutcomes.Registry,hostCrew,guestCrew,playbackA.ProtectedId,playbackB.ProtectedId,extraCrew.Protected);

        ready = true;

        confirmedReadNumber = sampleRead;

        receipt = receiptPrefix + "R";

        if (challenge != contactChallenge || contactGuard != victimId)

        { contactVictim = null; nextContactReply = 0f; }

        contactChallenge = challenge;

        contactReplyExpires = readStarted + 0.22f;

        contactGuard = victimId;

        contactPosition = b;

        contactVictimPosition = f.Length == 21 && f[15] == victimId ? ReadPosition(f,16) : (Vector3?)null;

        if (sampleRead > ticketBarrier) ticketHint = ticket;

        lastGood = Time.realtimeSinceStartup;

        UpdateGuard(f);

        Note(Spectating ? "Spectating: host controls the only crew member." : victimId == "!" ? "Melee skipped: positions out of sync." : attacking && combat[6] != "0" ? "Host striking. Movement waits." :

            challenge != 0 ? "Confirming melee range with host." : MoveProbePlugin.LegacyKeys ? "Following host. F10 move / [ stance / F11 disconnect" :

            "Following host. Use normal game controls / F11 disconnect");

        if (lastGood >= lastLog + 5f)

        {

            string movement = attacking ? "host attack" : "confirmed host position";

            MoveProbePlugin.ProbeLog.LogInfo($"Snapshot {f[3]}: guest errors before correction A={errorA:F2} B={errorB:F2} mode={movement} stateRTT={(lastGood-readStarted)*1000f:F0}ms");

            if(f[13][3]=='s' || f[13][3]=='d' || GuestGroundTarget.SurfaceCode(guestCrew)=='s' || GuestGroundTarget.SurfaceCode(guestCrew)=='d')

                MoveProbePlugin.ProbeLog.LogInfo($"Water sample {f[3]}: hostSurface={f[13][3]} guestSurface={GuestGroundTarget.SurfaceCode(guestCrew)} host={Position(b)} guestNav={Position(GuestGroundTarget.NavigationPosition(guestCrew))} guestRaw={Position(guestCrew.transform.position)}");

            lastLog = lastGood;

        }

    }



    private void UpdateGuard(string[] f)

    {

        guardText = null;

        if (f.Length != 21 || f[14] != "G") return;

        guardPosition = ReadPosition(f, 16);

        if (guardKey != f[15]) { guard = null; guardKey = f[15]; nextGuardSearch = 0; }

        if (guard == null && Time.realtimeSinceStartup >= nextGuardSearch)

        {

            nextGuardSearch = Time.realtimeSinceStartup + 5f;

            // Do not silently pick the first enemy when IDs are ambiguous.

            guard = Melee.Resolve(guardKey);

        }

        guardText = $"Host guard: {f[19]} / {f[20]}";

        if (guard != null && guard.controllerAI?.m_ai != null && guard.health != null)

            guardText += $" | Local: {guard.controllerAI.m_ai.eAlertState} / {guard.health.eHealthState} | gap {Vector3.Distance(guard.transform.position, guardPosition):F1}";

        guardDiagnostics.GuestSample(f, guard);

    }



    internal void DrawGuard()

    {

        if (CanSend && missionAlarm.Status != null) GUI.Box(new Rect(12,116,520,28),missionAlarm.Status);

        if (!CanSend || guardText == null) return;

        GUI.Box(new Rect(12, 84, 720, 28), guardText);

        var camera = Camera.main;

        if (camera == null) return;

        var screen = camera.WorldToScreenPoint(guardPosition + Vector3.up * 2f);

        if (screen.z > 0) GUI.Box(new Rect(screen.x - 48, Screen.height - screen.y - 14, 96, 28), "Host guard");

    }



    internal static Vector3 ReadPosition(string[] f, int start)

    {

        Vector3 p = default;

        for (int i = 0; i < 3; i++)

        {

            float v = float.Parse(f[start+i], CultureInfo.InvariantCulture);

            if (!float.IsFinite(v)) throw new FormatException();

            p[i] = v;

        }

        return p;

    }



    internal static bool SetStance(MiCharacter crew, bool crouch)

    {

        return NativeGait.Apply(crew, crouch ? "C" : "W");

    }



    private Vector3? lastConfirmedB;

    private bool ReplayRoute(Vector3 host,Vector3 local,string style,float now,float snapshotAge,Vector3? actual)
    {
        lastRouteViewStarted=readStarted;
        bool replayed=routePlayback.Replay(guestCrew,followB,host,local,style,now,snapshotAge,actual,pace.Crew);
        if(replayed)turn.Clear();
        return replayed;
    }

    private char lastHostCover, lastGuestCover;



    private static float nextCoverRead;

    internal static char Cover(MiCharacter crew)

    {

        if (Time.realtimeSinceStartup < nextCoverRead) return 'x';

        try

        {

            var sensor = crew.controllerPlayer?.m_hidingTriggerPlayer;

            if (sensor == null) return 'x';

            // Position reconciliation may cross a hiding trigger without the
            // native exit callback. Only repair a provably outside guest sensor;
            // never infer absence from a host position while the guest is inside.
            if(NativeInput.Probe?.NativeGuest==true&&!sensor.bInHidingSpotCollider()&&!sensor.bInHidingSpotLogic()&&
                (sensor.m_bInHiding||sensor.m_bHidden))
            {
                sensor.exitLast();sensor.forceUpdateStateExternal();sensor.updateRenderHiding();
            }

            return !sensor.bInHidingSpotCollider() ? 'o' : sensor.m_bHidden ? 'h' : 'i';

        }

        catch (Exception ex)

        {

            nextCoverRead = Time.realtimeSinceStartup + 5f;

            MoveProbePlugin.ProbeLog.LogWarning("Cover sensor unavailable: " + ex.GetType().Name);

            return 'x';

        }

    }







}



internal static class ConfirmedRunFollow

{

    internal static bool Keep(Vector3 issued, Vector3 confirmed, Vector3 heading, Vector3 step)

    {

        // A stationary host supplies the final endpoint. A turn replaces the

        // path immediately. A straight run uses longer confirmed segments so

        // the native controller does not restart acceleration every packet.

        return step.sqrMagnitude > 0.0004f && Vector3.Dot(heading, step.normalized) > 0.7f &&

            Vector3.Distance(issued, confirmed) < 0.75f;

    }

}



// Intent comes only from an acknowledged order. Position samples confirm

// that the host is moving and bound the guest's lead. Never steer it back to

// the previous host position merely because it reached that sample earlier.

internal sealed class AcceptedGroundRoute

{

    internal enum Action { FollowSnapshot, Wait, Move }

    internal Vector3 Goal { get; private set; }

    internal string Surface { get; private set; }

    internal bool Active { get; private set; }

    internal bool Issued;

    private Vector3 origin, lastProgressPos;

    private Vector3 requested;

    private Vector3 lastLocal;

    private float started, lastProgress, divergedAt, lastGoalAt;

    private float localMovingSince;

    private bool observed, ahead;



    internal void Clear() { Active=false; Issued=false; }

    internal void ResumeWait(float elapsed,float now)
    {
        if(!Active||!float.IsFinite(elapsed)||!float.IsFinite(now)||elapsed<=0f)return;
        // Samples and replacement routes may arrive after the pause began.
        // Only compensate the overlap; no observation can move into the future.
        started=Math.Min(now,started+elapsed);lastProgress=Math.Min(now,lastProgress+elapsed);
        lastGoalAt=Math.Min(now,lastGoalAt+elapsed);
        if(divergedAt>=0f)divergedAt=Math.Min(now,divergedAt+elapsed);
        if(localMovingSince>=0f)localMovingSince=Math.Min(now,localMovingSince+elapsed);
    }

    internal static bool Continuing(Vector3 from,Vector3 previous,Vector3 next)

    {

        var oldLeg=previous-from; var newLeg=next-from;

        // A target at our feet can be a deliberate stop. An oblique turn or

        // reversal must not continue down the previous leg while awaiting ACK.

        return oldLeg.sqrMagnitude>0.0625f && newLeg.sqrMagnitude>0.36f &&

            Vector3.Dot(oldLeg.normalized,newLeg.normalized)>=0.8f;

    }

    internal void Begin(Vector3 target,string surface,Vector3 host,float now)

    {

        // Repeated clicks or the second half of a double click do not stop a

        // working controller. The next snapshot carries the accepted gait.

        if (Active && Vector3.Distance(requested,target)<0.1f && Surface==surface) return;

        bool rolling=Active && Issued && observed && !ahead && now-lastProgress<0.23f && Continuing(host,Goal,target);

        Active=true; Issued=observed=rolling; ahead=false;

        Goal=requested=target; Surface=surface; origin=lastProgressPos=host; lastGoalAt=now;

        started=lastProgress=now; divergedAt=localMovingSince=-1f;

    }



    internal bool UpdateGoal(Vector3? actual,float now)

    {

        if (!actual.HasValue) return false;

        if (Vector3.Distance(requested,actual.Value)>1.8f)

        { lastGoalAt=float.NegativeInfinity; return false; }

        Goal=actual.Value; lastGoalAt=now; return true;

    }

    internal bool AwaitingGoal(float now) => now-lastGoalAt<=0.25f;



    internal Action Decide(Vector3 host,Vector3 local,float now,float age)

    {

        if (!Active || now-started>25f) return Action.FollowSnapshot;

        if (!Issued || age>0.50f) localMovingSince=-1f;

        if (age>0.50f) return Action.Wait;

        float hostLeft=Vector3.Distance(host,Goal), localLeft=Vector3.Distance(local,Goal);

        float gap=Vector3.Distance(host,local);

        bool arrived=hostLeft<0.45f;

        if (arrived && localLeft<0.45f) return Action.FollowSnapshot;

        if (Vector3.Distance(lastProgressPos,host)>0.055f)

        { lastProgress=now; lastProgressPos=host; }

        if (!observed)

        {

            bool progressed=hostLeft < Vector3.Distance(origin,Goal)-0.045f;

            bool detour=now-started>0.30f && Vector3.Distance(host,origin)>0.18f && now-lastProgress<0.12f;

            observed=arrived || progressed || detour;

        }

        if (!arrived && now-lastProgress>0.9f) return Action.FollowSnapshot;

        if (!observed || (!arrived && now-lastProgress>0.23f)) return Action.Wait;

        // Divergent routes or a newer external command eventually hand back

        // to normal authoritative reconciliation. A lagging guest that is

        // closing on the same final destination may finish the accepted path.

        bool lateral=gap>1.25f && Math.Abs(hostLeft-localLeft)<gap*0.5f;
        if ((gap>4f || lateral) && !arrived)

        {

            if (divergedAt<0f) divergedAt=now;

            if (now-divergedAt>(lateral?0.20f:0.70f)) return Action.FollowSnapshot;

        }

        else divergedAt=-1f;

        if (hostLeft-localLeft>0.95f) ahead=true;

        else if (hostLeft-localLeft<0.30f) ahead=false;

        if (ahead) return Action.Wait;

        if (Issued && localLeft>0.45f)

        {

            if (localMovingSince<0f || Vector3.Distance(lastLocal,local)>0.07f)

            { lastLocal=local; localMovingSince=now; }

            if (now-localMovingSince>1.25f) return Action.FollowSnapshot;

        }

        return Action.Move;

    }

}



// On a reversal the host may be several samples ahead on the old path.

// Replaying those old-side positions makes B turn toward the old target

// and immediately about-face a second time. Hold only ordinary locomotion.

internal sealed class ConfirmedTurn

{

    private int tag, readBarrier;

    private bool accepted;

    private float deadline;

    private Vector3 goal, heading;



    internal void Clear() { tag = 0; accepted = false; }

    internal bool Begin(int nextTag,Vector3 local,Vector3 target,Vector3 lastHeading,float now,float lastTravel)

    {

        var desired = target-local;

        var direction = desired.normalized;

        bool continuing = tag != 0 && now < deadline && Vector3.Dot(direction,heading)>0.8f;

        if (nextTag == 0 || desired.sqrMagnitude < 0.09f || !continuing &&

            (now-lastTravel>0.35f || Vector3.Dot(direction,lastHeading)>-0.35f))

        { Clear(); return false; }

        tag = nextTag;

        goal = target;

        heading = direction;

        accepted = false;

        deadline = now + 2f;

        return true;

    }



    internal void Reply(int sent,bool ok,float now,int read)

    {

        if (tag == 0 || tag != sent) return;

        if (!ok) { Clear(); return; }

        accepted = true;

        readBarrier = read;

        deadline = now + 0.75f;

    }



    internal bool Hold(Vector3 host,Vector3 local,Vector3 step,float now,int read)

    {

        if (tag == 0) return false;

        if (now >= deadline) { Clear(); return false; }

        if (!accepted || read <= readBarrier) return true;

        var delta = host-local;

        // Only resume when the confirmed point is on the new side of B.

        // Curved/blocked navigation resumes normal following at the deadline.

        bool passed = step.sqrMagnitude>0.0004f && Vector3.Dot(step.normalized,heading)>0.2f &&

            Vector3.Dot(delta,heading)>0.05f;

        if (!passed && Vector3.Distance(host,goal)>0.18f) return true;

        Clear();

        return false;

    }

}

