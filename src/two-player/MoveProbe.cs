using System;

using System.Globalization;

using System.Collections.Generic;

using System.Threading.Tasks;

using BepInEx;

using BepInEx.Logging;

using BepInEx.Unity.IL2CPP;

using Mimimi.MiScript;

using UnityEngine;

using UnityEngine.AI;


[BepInPlugin("local.shadowgambit.moveprobe", "Coop Move Probe", "0.8.42")]

public sealed class MoveProbePlugin : BasePlugin

{

    internal static ManualLogSource ProbeLog;

    internal static BepInEx.Configuration.ConfigEntry<string> LastPair, LastGuestRoom;

    internal static bool LegacyKeys;

    internal static bool DirectGroundClicks;
    internal static bool LiveGuestLaunch;

    private static bool guestMarkerHooksAttempted;
    internal static bool OwnershipHooksReady;
    internal static void EnsureGuestMarkerHooks()
    {
        if(guestMarkerHooksAttempted)return;
        guestMarkerHooksAttempted=true;
        try
        {
            var markers=new HarmonyLib.Harmony("local.shadowgambit.guardicons");
            markers.PatchAll(typeof(GuardIcons.Show));
            markers.PatchAll(typeof(GuardIcons.Hide));
            markers.PatchAll(typeof(NativeInput.SyncDestination));
            ProbeLog.LogInfo("Guest sync waypoint destination filter installed.");
            ProbeLog.LogInfo("Guest-only host-confirmed guard head marker hooks installed.");
        }
        catch(Exception ex) { ProbeLog.LogWarning("Guard head marker hooks unavailable: "+ex); }
    }


    public override void Load()

    {

        ProbeLog = Log;

        // Local developer smoke check: install real IL2CPP detours, then quit
        // before creating a room, loading a profile or binding input handlers.
        // This is deliberately independent of the remembered launch role.
        if(Array.IndexOf(Environment.GetCommandLineArgs(),"--sgc-verify-skill-hooks")>=0)
        {
            bool installed=NativeSkillHooks.Install();
            Log.LogInfo("Native skill hook smoke result: "+(installed?"PASS":"FAIL"));
            Application.Quit(installed?0:21);
            return;
        }

        if(LaunchMode.SinglePlayer(System.IO.Path.Combine(Paths.GameRootPath,"ShadowGambitCoop","session-next","runtime","control","launch-mode.json"),Environment.GetCommandLineArgs()))
        {
            Log.LogInfo("Single-player launch: co-op initialization skipped.");
            return;
        }

        if(!NativeSkillHooks.Install())return;

        // A host may switch to chat while its friend loads. Unity must keep
        // publishing the held checkpoint and servicing the join handshake.
        Application.runInBackground=true;
        Log.LogInfo("Co-op background updates enabled for checkpoint handoff.");

        try
        {
            var ownershipHooks=new HarmonyLib.Harmony("local.shadowgambit.crewownership");
            ownershipHooks.PatchAll(typeof(HostCrewSelection.Selectable));
            ownershipHooks.PatchAll(typeof(HostCrewSelection.MouseSelectable));
            ownershipHooks.PatchAll(typeof(HostCrewSelection.Commands));
            ownershipHooks.PatchAll(typeof(HostCrewSelection.SlowCommands));
            OwnershipHooksReady=true;
        }
        catch(Exception ex){Log.LogError("Crew ownership input hooks unavailable: "+ex);}

        Log.LogInfo("S19 multicrew preparation development build: native validation, not published release.");
        LobbySelectionHooks.Install();
        SessionWorld.Install();
        GuestWorldMap.Install();
        ShipCrewSession.Install();
        MovementLineLoadRepair.Install();
        FootprintSync.Install();
        CoopWaitLock.InstallSaveFilters();

        GuardPursuit.EffectProtection=actor=>TimeFreezeSync.Protected(actor)||BlindSync.Protected(actor)||FrozenSync.Protected(actor)||FrozenDuelSync.Protected(actor)||CannonStateSync.Held(actor)||FishingPullSync.Held(actor);
        GuardOutcomes.GeneratedActor=actor=>SkillObjectRegistry.Active.Key(actor)!=null;
        GuardAuthority.VerifyNativeLocks();

        LastPair = Config.Bind("Connection", "LastPair", "", "Local remembered co-op pairing. F11 forgets this pairing.");
        LastGuestRoom=Config.Bind("Connection","LastGuestRoom","","Last guest room for explicit F6 reconnect from main menu; retained after F11.");
        var remembered=LastPair.Value.Split(' ');
        if(remembered.Length==3&&remembered[0]=="G"&&GuestMove.Room(remembered[2])!=null)LastGuestRoom.Value=remembered[2];
        var launchGeneration=Config.Bind("Connection","LastLobbyLaunch","","Consumed guest lobby launch generation; preserves explicit disconnect across Steam restart.");
        var lobbyRoom=LaunchMode.GuestLobby(System.IO.Path.Combine(Paths.GameRootPath,"ShadowGambitCoop","session-next","runtime","control","launch-mode.json"),GuestMove.Room,out var lobbyGeneration);
        MoveProbe.HostLaunch=LaunchMode.Host(System.IO.Path.Combine(Paths.GameRootPath,"ShadowGambitCoop","session-next","runtime","control","launch-mode.json"));
        string launchPair=LaunchMode.PairingForLaunch(MoveProbe.HostLaunch,LastPair.Value);
        if(launchPair!=LastPair.Value){LastPair.Value=launchPair;Log.LogInfo("Host launch: cleared stale guest pairing; remembered guest address retained.");}
        LiveGuestLaunch=lobbyRoom!=null;
        Log.LogInfo("Launch role: "+(MoveProbe.HostLaunch?"host":LiveGuestLaunch?"guest":"manual")+"; remembered pairing: "+(LastPair.Value.StartsWith("G ",StringComparison.Ordinal)?"guest":LastPair.Value=="H"?"host":"none"));
        // A guest may exit before any actor or checkpoint exists. Preserve
        // startup/room failures in the same automatic exit report as gameplay.
        if(LiveGuestLaunch)GuestExitCollection.Arm(Paths.GameRootPath);
        if(LiveGuestLaunch&&launchGeneration.Value!=lobbyGeneration)
        {
            LastPair.Value="G 1 "+lobbyRoom.AbsoluteUri;
            LastGuestRoom.Value=lobbyRoom.AbsoluteUri;
            launchGeneration.Value=lobbyGeneration;
        }

        GuardDiagnostics.Enabled = Config.Bind("Diagnostics", "GuardObservation", false, "Read-only guard inventory and paired snapshot logging; requires trace-guards.flag.").Value && System.IO.File.Exists(System.IO.Path.Combine(BepInEx.Paths.GameRootPath,"ShadowGambitCoop","trace-guards.flag"));

        Log.LogInfo("Guard observation: " + (GuardDiagnostics.Enabled ? "enabled (read-only)" : "disabled"));

        LegacyKeys = Config.Bind("Input", "LegacyGuestHotkeys", false, "Use the 0.5 diagnostic keys instead of forwarding native commands.").Value;

        DirectGroundClicks = !LegacyKeys;

        NativeInput.Probe = AddComponent<MoveProbe>();
        CoopPings.Initialize(Config);

        try { var hooks=new HarmonyLib.Harmony("local.shadowgambit.interactions");hooks.PatchAll(typeof(InteractionSync.Executed));hooks.PatchAll(typeof(InteractionSync.LadderContinuation)); }

        catch(Exception ex) { Log.LogWarning("Native interaction completion hook unavailable: "+ex.GetType().Name); }

        try { new HarmonyLib.Harmony("local.shadowgambit.gait").PatchAll(typeof(NativeGait.CommandSpeed)); }

        catch (Exception ex) { Log.LogWarning("Native command modifier hook unavailable: " + ex.GetType().Name); }

        try { new HarmonyLib.Harmony("local.shadowgambit.health").PatchAll(typeof(GuardPursuit.ConfirmedDamage)); }

        catch (Exception ex) { Log.LogWarning("Using snapshot health corrections only: " + ex.GetType().Name); }

        try { new HarmonyLib.Harmony("local.shadowgambit.trapresults").PatchAll(typeof(TrapObjectSync.HostImpact)); }
        catch(Exception ex) { Log.LogWarning("Trap authority hook unavailable: "+ex.GetType().Name); }
        try
        {
            var ghostHooks=new HarmonyLib.Harmony("local.shadowgambit.ghostresults");
            ghostHooks.PatchAll(typeof(GhostObjectSync.AddFollower));ghostHooks.PatchAll(typeof(GhostObjectSync.RemoveFollower));
        }
        catch(Exception ex){Log.LogWarning("Ghost follower authority hook unavailable: "+ex.GetType().Name);}

        try { new HarmonyLib.Harmony("local.shadowgambit.guardprocess").PatchAll(typeof(GuardPursuit.ProcessStarted)); }

        catch (Exception ex) { Log.LogWarning("Polling guard attack processes: " + ex.GetType().Name); }

        try
        {
            var visuals=new HarmonyLib.Harmony("local.shadowgambit.pointandpreview");
            visuals.PatchAll(typeof(NativeInput.PointExecuted));
            visuals.PatchAll(typeof(NativeInput.PreviewCompleted));
            visuals.PatchAll(typeof(NativeInput.RetiredAiming));
            Log.LogInfo("Point execution and melee preview lifecycle hooks installed.");
        }
        catch(Exception ex) { Log.LogWarning("Point/preview lifecycle hook unavailable: "+ex); }

        // Native per-guard ownership replaces unsafe broad AI command interception.

        Log.LogInfo("Guard authority: native per-instance decision leases; no AI command setter hooks.");

        try { new HarmonyLib.Harmony("local.shadowgambit.searchpoints").PatchAll(typeof(GuardPursuit.RandomSearch)); }

        catch (Exception ex) { Log.LogWarning("Using resolved search command positions: "+ex.GetType().Name); }

        try { new HarmonyLib.Harmony("local.shadowgambit.noisepreview").PatchAll(typeof(NativeInput.MeleePreview)); }

        catch (Exception ex) { Log.LogWarning("Melee preview hook unavailable: " + ex.GetType().Name); }

        // signalAlarm has IL2CPP value-type parameters. Do not detour it:

        // 0.8.23 crashed in coreclr when a live mission raised an alarm.

        Log.LogInfo("Alarm safety: native AlarmSystem remains unpatched; snapshot correction only.");

        try { var sightHooks=new HarmonyLib.Harmony("local.shadowgambit.sight");sightHooks.PatchAll(typeof(GuardPursuit.ObserverVisibility));sightHooks.PatchAll(typeof(GuardPursuit.SightDirection)); }

        catch (Exception ex) { Log.LogWarning("Using snapshot visibility correction only: "+ex.GetType().Name); }

        if (!LegacyKeys)

        {

            var patcher = new HarmonyLib.Harmony("local.shadowgambit.nativeinput");

            try

            {

                patcher.PatchAll(typeof(NativeInput.MouseCommand));

                patcher.PatchAll(typeof(NativeInput.MouseUpdate));
                patcher.PatchAll(typeof(NativeInput.PlayerUpdate));
                patcher.PatchAll(typeof(NativeInput.UiSkillInput));
                patcher.PatchAll(typeof(NativeInput.InstantSkillInput));
                patcher.PatchAll(typeof(NativeInput.TeleportCommand));
                patcher.PatchAll(typeof(NativeInput.TeleportExecute));
                NativeInput.InputScopeReady=true;

                patcher.PatchAll(typeof(NativeInput.Stance));

                patcher.PatchAll(typeof(NativeInput.GuestOrder));

                try { patcher.PatchAll(typeof(NativeInput.CommittedCancel)); }

                catch (Exception ex) { Log.LogWarning("Guest committed-melee input guard unavailable: " + ex.GetType().Name); }

                try { patcher.PatchAll(typeof(NativeInput.MouseGroundFacing)); }

                catch (Exception ex) { Log.LogWarning("Guest foliage facing guard unavailable: " + ex.GetType().Name); }

                try { patcher.PatchAll(typeof(NativeInput.DispatchedCommand)); }

                catch (Exception ex) { Log.LogWarning("Optional dispatch observer unavailable: " + ex.GetType().Name); }

            }

            catch (Exception ex)

            {

                LegacyKeys = true;

                Log.LogError($"Native input hooks unavailable; using legacy test keys: {ex}");

            }

        }

        Log.LogInfo("Host F7 resumes hosting. Guest F6 reconnects. F11 disconnects. F8 is native quickload. Input: " + (LegacyKeys ? "legacy keys" : "native commands"));

    }

}


public sealed class MoveProbe : MonoBehaviour

{

    public MoveProbe(IntPtr pointer) : base(pointer) { }


    private HostCrewSelection hostSelection;
    private bool executingHostFrame;
    private bool localShipSelection;
    internal bool PeerSelectionBlocked(MiSelectable selectable)=>!localShipSelection&&sessionHost.HasRecentPeer&&receiver!=null&&hostSelection?.Blocked(selectable)==true || sync?.SelectionBlocked(selectable)==true;
    internal bool HostCommandAllowed(MiCharacter actor,SkillCommand command=null)=>PlanningSync.Executing||localShipSelection||!sessionHost.HasRecentPeer||receiver==null||executingHostFrame||hostSelection?.IsGuest(actor)!=true||NativeInput.AutomaticCommand(actor);
    private readonly Dictionary<IntPtr,HostCrewOrders> hostOrders=new Dictionary<IntPtr,HostCrewOrders>();
    private HostCrewOrders RemoteOrders => crewB==null || HostSpectator ? null : OrdersFor(crewB);
    private HostCrewOrders OrdersFor(MiCharacter actor)
    {
        if(actor==null)throw new ArgumentNullException(nameof(actor));
        if(!hostOrders.TryGetValue(actor.Pointer,out var orders))
        {
            orders=new HostCrewOrders(actor,()=>receiver!=null && crewA!=null,
                ()=>hostWait.WorldHeld,Report,victim=>watchedGuard=victim);
            hostOrders.Add(actor.Pointer,orders);
        }
        return orders;
    }
    private void ResetHostOrders()
    {
        foreach(var orders in hostOrders.Values)orders.Reset();
        hostOrders.Clear();
    }
    private bool IssueMove(string source,Vector3 target,string gait=null,bool retreat=false) =>
        RemoteOrders?.IssueMove(source,target,gait,retreat)==true;

    private MiCharacter crewB;

    private MiCharacter crewA;
    private bool HostSpectator=>crewA!=null&&crewA==crewB;

    private Vector3 destination;


    private string status;

    private float statusUntil;

    private LoopbackMove receiver;

    private CrewSync sync;
    private readonly SessionBootstrap sessionBootstrap=new SessionBootstrap();
    private readonly SessionIndexRefresh sessionIndexRefresh=new SessionIndexRefresh();
    private readonly SessionLiveImport sessionLiveImport=new SessionLiveImport();
    private readonly SessionSceneFollow sessionSceneFollow=new SessionSceneFollow();
    private readonly GuestWorldMap guestWorldMap=new GuestWorldMap();
    private readonly GuestMissionPreparation guestMissionPreparation=new GuestMissionPreparation();
    private readonly SessionHost sessionHost=new SessionHost();
    private readonly LobbyHost lobbyHost=new LobbyHost();
    private readonly LobbyGuest lobbyGuest=new LobbyGuest();
    private string checkpointFrameGeneration;

    private string hostSession;

    private int snapshotNumber;

    private MiCharacter watchedGuard;

    private Task<string> pending;
    private readonly GuestActorRequests actorRequests=new GuestActorRequests();

    private System.Threading.CancellationTokenSource contactRequest;

    private float orderSentAt;


    private AttackCue cueA = new AttackCue();

    private MissionAlarmSync hostAlarm = new MissionAlarmSync();

    private ReinforcementSync hostHouses=new ReinforcementSync();

    private GuardPursuit hostPursuit = new GuardPursuit();

    private GuardOutcomes hostOutcomes = new GuardOutcomes();

    private BodySync hostBodies = new BodySync();

    private InteractionSync hostInteractions=new InteractionSync();
    private readonly CrewRosterHost hostRoster=new CrewRosterHost();

    internal void InteractionExecuted(MiCharacter actor,SkillCommand command)

    {

        if(receiver!=null) { if(actor==crewA || actor==crewB) hostInteractions.Mark(actor==crewB&&!HostSpectator,command); else hostRoster.Executed(actor,command); }

        MimicNative.ObserveAction(actor,command,"native-execute");

        GuestPreview.Executed(actor,command);

    }


    private readonly GuestCommandQueue nativeQueue = new GuestCommandQueue();

    private string queuedSession;
    private readonly Dictionary<string,(int frame,string command)> nativeCaptured=new Dictionary<string,(int,string)>(StringComparer.Ordinal);

    private bool applyingHostState;

    private string pendingStance,pendingActor;
    private long pendingActorSequence;


    private MiGameInput clickSettings;

    private readonly Dictionary<IntPtr,GuestGestureState> guestGestures=new Dictionary<IntPtr,GuestGestureState>();
    private readonly GuestGestureState noGesture=new GuestGestureState();
    private GuestGestureState Gesture(MiCharacter actor)
    {
        if(actor==null)return noGesture;
        if(!guestGestures.TryGetValue(actor.Pointer,out var state))guestGestures.Add(actor.Pointer,state=new GuestGestureState());
        return state;
    }
    private int pressFrame=-1;

    private SkillCommand pressDisposal;
    private MiCharacter pressActor;
    private readonly List<MiCharacter> pressCrew=new List<MiCharacter>();

    private Vector3 pressPosition;

    private Vector3? retreatPressPoint;

    private string retreatPressSurface;

    private string pressSession;

    internal void ReserveTraversal(MiCharacter actor,SkillCommand command)

    {

        if (!(Input.GetMouseButtonDown(0) || Input.GetMouseButtonUp(0))) return;

        // A native mouse action owns this click even when its target has no

        // selectable collider (for example the landing point of a jump).

        if (command?.m_skill?.eSkillType is Skill.SkillType.UseJump or Skill.SkillType.UseJumpAcross or

            Skill.SkillType.UseJumpDown or Skill.SkillType.UseJumpAttack or Skill.SkillType.UseClimbEnter or

            Skill.SkillType.UseClimbExit or Skill.SkillType.UseClimbMove or Skill.SkillType.UseDoorEnter or

            Skill.SkillType.UseDoorExit or Skill.SkillType.UseDoorWalkthrough or Skill.SkillType.UseCarryBodyThrow or

            Skill.SkillType.UseCarryBodyDrop or Skill.SkillType.UseCarryTorchThrow or Skill.SkillType.UseCarryTorchDrop or

            Skill.SkillType.UseCarryObjectDrop) Gesture(actor).nonMoveFrame = Time.frameCount;

    }

    internal void ObserveDispatchedPoint(MiCharacter actor, SkillCommand cmd)

    {

        if (!ListensTo(actor) || cmd?.m_skill?.eSkillType != Skill.SkillType.GoTo || cmd.m_target == null ||

            !(Input.GetMouseButtonDown(0) || Input.GetMouseButtonUp(0))) return;

        // GoTo is the native decision, even when its endpoint is a person,
        // doorway or another usable. Do not reclassify it from the mouse hit.

        Gesture(actor).issuedPoint = cmd.m_target.v3Target;

        Gesture(actor).issuedPointFrame = Time.frameCount;

    }

    private int clickLogCount, groundBlockLogs, lastClickFrame = -1;

    internal void TraceMouse(MiCharacter actor)

    {

        if (!NativeGuest || clickLogCount >= 12 || lastClickFrame == Time.frameCount || !NativeInput.MouseEdge()) return;

        clickLogCount++;

        lastClickFrame = Time.frameCount;

        MoveProbePlugin.ProbeLog.LogInfo($"Guest mouse input: actor={GuestMove.Identity(actor)} proxy={ControlledCrew.Active.Find(actor)?.Source} owned={sync.Owns(actor)} ready={sync.CanSend} applying={applyingHostState}");

    }

    private readonly GuardDiagnostics guardDiagnostics = new GuardDiagnostics("host");


    internal bool GuestLoadPending=>GuestMissionPreparation.Active||sessionLiveImport.Busy||sessionBootstrap.Busy||
        (LaunchMode.RememberedGuest(HostLaunch,cachedHostIntent,MoveProbePlugin.LastPair.Value)&&
         SceneLookup.Find<MiMissionHandler>()?.currentMissionData==null);
    internal void ReportGuestLoadBlocked()=>Report("正在加入房主场景，请等待；接档期间不能手动继续或读档。F11 可取消联机。");
    internal bool NativeGuest => !TrainingSession.Suspended && !MoveProbePlugin.LegacyKeys && sync != null;
    internal bool NetworkSessionActive => receiver!=null||sync!=null;

    internal void SearchPoint(MiCharacter actor, ref Vector3 point)

    {

        if (actor == null || !actor.bControllerAI() || actor.guid.isEmpty()) return;

        string id=Melee.GuardId(actor);

        if (receiver != null) hostPursuit.SetWaypoint(id,point,8f);

        else if (sync != null && sync.CanSend && sync.PursuitPoint(id,out var confirmed)) point=confirmed;

    }

    internal void SyncSight(MiCharacter actor,Entity target,ref Detection.DetectionState state)

    { if (sync!=null) sync.SyncSight(actor,target,ref state); }

    internal void PlayerPointProcess(MiCharacter actor, SkillCommand command)
    {
        if(receiver==null) return;
        if(InteractionSync.Supported(command?.m_skill?.eSkillType??Skill.SkillType.None))
        { InteractionExecuted(actor,command); return; }
        if(actor==crewA) cueA.ObservePointCommand(command);
        else if(hostSelection?.IsGuest(actor)==true) OrdersFor(actor).Cue.ObservePointCommand(command);
        else hostRoster.Point(actor,command);
    }

    internal void GuardProcess(MiCharacter actor, SkillCommand command)

    {

        if (receiver != null && crewA != null && crewB != null && actor != null && actor.bControllerAI() && !actor.guid.isEmpty())

            hostPursuit.ObserveStrike(Melee.GuardId(actor),command,crewA,crewB);

    }

    internal bool MirroredCrew(MiCharacter actor) => sync != null && sync.Session != null && !sync.Stopped && actor != null && (sync.Owns(actor) || actor == sync.HostCrew || sync.MirrorsExtra(actor));

    internal IEnumerable<MiCharacter> PlanningRoster()=>sync!=null?sync.Roster:hostSelection?.PlanningActors??Array.Empty<MiCharacter>();
    internal bool PlanningGuestOwns(MiCharacter actor)=>sync!=null?sync.Owns(actor):hostSelection?.IsGuest(actor)==true;
    internal IEnumerable<MiCharacter> GuestOwnedCrew()
    {if(sync!=null)foreach(var actor in sync.Roster)if(sync.Owns(actor))yield return actor;}

    internal bool GuestOrderOwner(MiCharacter actor) => !applyingHostState && NativeGuest && sync.Owns(actor);

    internal bool GuestContactLocked(MiCharacter actor) => GuestOrderOwner(actor) && sync.CommittedContactFor(actor);

    internal bool UnassignedGuestPlayer(MiCharacter actor) => !applyingHostState && sync != null && actor != null &&

        actor.bControllerPlayer() && GuestCrewSelection.Source(actor)==actor && (!sync.Owns(actor) || sync.Session == null);

    internal bool ListensTo(MiCharacter actor) => !applyingHostState && NativeGuest && sync.CanSend && sync.Owns(actor) && sync.SelectionReady && !ChangingCrew;


    internal void CaptureCommand(MiCharacter actor, SkillCommand cmd, bool nativeDispatch=false)

    {

        if (cmd?.m_skill == null || !ListensTo(actor)) return;

        var kind = cmd.m_skill.eSkillType;

        var gesture=Gesture(actor);

        bool bodyClick=nativeDispatch || NativeInput.MouseEdge() || Input.anyKeyDown;

        if(InteractionSync.Supported(kind) && bodyClick)

        {

            gesture.nonMoveFrame=Time.frameCount;

            string descriptor=InteractionSync.Describe(cmd);

            if(descriptor!=null) {
                int press=Input.GetMouseButtonDown(0)?Time.frameCount:leftDownFrame;
                bool contextClick=NativeInput.MouseEdge()&&press>=0&&InteractionProtocol.Context(InteractionProtocol.Code(kind.ToString()));
                if(contextClick&&gesture.contextPress==press&&gesture.contextDescriptor==descriptor)return;
                if(TryCapture(actor,"USE "+descriptor))
                {
                    if(contextClick){gesture.contextPress=press;gesture.contextDescriptor=descriptor;}
                    GuestPreview.ShowDisposal(actor,cmd); GuestSkillPreview.Submitted(actor,cmd);
                }
            }

            else Report("Interaction target has no stable network identity.");

            return;

        }

        if(BodySync.Skill(kind) && bodyClick)

        {

            gesture.nonMoveFrame=Time.frameCount;

            if(kind==Skill.SkillType.UseCarryBodyThrow)

            { Report("Body throwing is not synchronized yet; use ordinary put-down."); return; }

            if(kind==Skill.SkillType.UseCarryBodyDrop)

            {

                // A plain put-down. Do not reinterpret a disposal target as

                // a drop at the actor's feet.

                if((cmd.m_target?.m_entityTarget==null || cmd.m_target.m_entityTarget==actor)) Capture(actor,"BODY D 0");

                else Report("Targeted body disposal is not synchronized yet; put it down on ground.");

            }

            else if(cmd.m_target?.m_entityTarget!=null && cmd.m_target.m_entityTarget.bAsCharacter(out var corpse) && BodySync.Corpse(corpse))

                Capture(actor,"BODY "+(cmd.hasModifier(SkillCommand.ExecutionModifier.Run) ? "PR " : "P ")+Melee.GuardId(corpse));

            else Report("Body input has no finished corpse target; state="+cmd.m_target?.m_entityTarget?.TryCast<MiCharacter>()?.health?.eHealthState);

            return;

        }

        if(cmd.m_target==null) return;

        if (kind == Skill.SkillType.GoTo)

        {

            if(bodyClick && cmd.m_target.m_entityTarget!=null && cmd.m_target.m_entityTarget.bAsCharacter(out var corpse) && BodySync.Corpse(corpse))

            {

                gesture.nonMoveFrame=Time.frameCount;

                Capture(actor,"BODY "+(cmd.hasModifier(SkillCommand.ExecutionModifier.Run) ? "MR " : "M ")+Melee.GuardId(corpse));

                return;

            }

            if (NativeInput.MouseEdge() && cmd.hasModifier(SkillCommand.ExecutionModifier.Run))

            {

                gesture.nativeRunFrame = Time.frameCount;

                gesture.nativeWasCrouched = cmd.hasModifier(SkillCommand.ExecutionModifier.WasCrawling);

            }

            // Ground clicks have a single producer in LateUpdate, independent of the mouse hooks.

            return;

        }

        string key = kind == Skill.SkillType.Kill ? "L" : kind == Skill.SkillType.Knockout ? "K" : kind.ToString();

        if (Melee.Decode(key) == Skill.SkillType.None || !bodyClick) return;

        var entity = cmd.m_target.m_entityTarget;

        if (Melee.Point(kind))

        {

            // A native ground-targeted skill may retain its floor/foliage entity.
            // The resolved world point remains the skill destination.
            gesture.nonMoveFrame=Time.frameCount; Capture(actor,key+" "+CrewSync.Position(cmd.m_target.v3Target));

            return;

        }

        if (entity == null || !entity.bAsCharacter(out var guard) || guard == null || guard.guid.isEmpty() || !guard.bControllerAI()) return;

        // A controller can re-submit its previous target on a new mouse edge.

        // A verified floor click well away from that guard belongs to withdrawal.

        if (Melee.Contact(kind) && sync.CanRedirectAttackFor(actor) && guard == gesture.lastMeleeGuard &&

            GuestGroundTarget.TryPick(actor,true,false,default,out var floor,out _,true) &&

            Vector3.Distance(floor,guard.transform.position) > 3f) return;

        gesture.nonMoveFrame = Time.frameCount;

        if (Melee.Contact(kind))

        {

            int press = Input.GetMouseButtonDown(0) ? Time.frameCount : leftDownFrame;

            bool fast = cmd.hasModifier(SkillCommand.ExecutionModifier.Run) || gesture.RepeatMelee(guard,kind,press,Time.realtimeSinceStartup,DoubleClickWindow());

            if (press>=0 && press==gesture.lastMeleePress && guard==gesture.lastMeleeGuard && kind==gesture.lastMeleeSkill &&

                (!fast || gesture.lastMeleeFast)) return;

            gesture.lastMeleeFast=fast; gesture.lastMeleeGuard=guard; gesture.lastMeleeSkill=kind; gesture.lastMeleePress=press; gesture.lastMeleeTime=Time.realtimeSinceStartup;

            if (fast) key+="R";

        }

        Capture(actor, $"HIT {key} {Melee.GuardId(guard)}", Melee.Contact(kind) ? guard : null);

    }


    internal void Capture(MiCharacter actor, string command, MiCharacter meleeVictim = null)
        => TryCapture(actor,command,meleeVictim);

    internal bool TryCapture(MiCharacter actor, string command, MiCharacter meleeVictim = null)

    {

        if (!ListensTo(actor) || Time.timeScale <= 0f || (MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting)) return false;

        string actorId=GuestMove.Identity(actor);
        if(actorId==null)return false;
        if (queuedSession != sync.Session){nativeQueue.Clear();nativeCaptured.Clear();}
        queuedSession = sync.Session;
        if(nativeCaptured.TryGetValue(actorId,out var captured) && captured.frame==Time.frameCount && captured.command==command)return true;


        string queued = meleeVictim == null ? command : sync.WithContactProof(actor,command, meleeVictim);

        if (!nativeQueue.Add(sync.Session, actorId, queued, Time.realtimeSinceStartup,revision:sync.OwnershipRevision)) { Report("Input queue busy."); return false; }

        nativeCaptured[actorId]=(Time.frameCount,command);
        sync.QueuedInput(actor,command);

        if (command.StartsWith("HIT ", StringComparison.Ordinal))

        {

            sync.AttackInput(actor,command);

            if(meleeVictim!=null) GuestPreview.Attack(actor,Melee.Decode(command.Split(' ')[1]),meleeVictim);

        }

        else GuestPreview.Cancel(actor);

        if (command.StartsWith("BODY ", StringComparison.Ordinal) || command.StartsWith("USE ",StringComparison.Ordinal) || command.StartsWith("MIMIC ",StringComparison.Ordinal) ||
            command.StartsWith("ThrowBush ",StringComparison.Ordinal) || command.StartsWith("ThrowNoise ",StringComparison.Ordinal)) sync.InteractionInput(actor);

        if (command == "CROUCH" || command == "WALK")

        {

            sync.ManualStance(actor,command == "CROUCH");

            Gesture(actor).previousClickTime = float.NegativeInfinity;

        }

        return true;
    }


    private Task<string> SendGuestOrder(string actor,string command,System.Threading.CancellationToken token,string hint=null,bool fast=false,long expectedRevision=0)
    {
        pendingActor=actor;
        return SendGuestOrderCore(actor,command,token,out pendingActorSequence,hint,fast,expectedRevision);
    }
    private Task<string> SendGuestOrderCore(string actor,string command,System.Threading.CancellationToken token,out long sequence,string hint=null,bool fast=false,long expectedRevision=0)
    {
        sequence=0;
        if(sync==null)return Task.FromResult("NOT_READY");
        string wrapped=sync.EncodeOrder(actor,command,expectedRevision);
        if(wrapped==null){Report($"Order not sent: expected ownership={expectedRevision}, current={sync.OwnershipRevision}, command={command}");return Task.FromResult("REJECTED");}
        if(SyncProtocol.Command(actor+" "+sync.Session+" "+wrapped,out _,out _,out sequence))
            sync.ExtraOrderSending(actor,sequence,command,nativeQueue.Contains(actor));
        return GuestMove.Send(sync.Room,actor,sync.Session,wrapped,token,hint,fast);
    }

    private void PumpNativeInput()

    {

        if (nativeQueue.Count == 0) return;

        if (sync == null || sync.Session != queuedSession)

        { nativeQueue.Clear(); Report("Queued input cleared: check connection and selected guest crew."); return; }

        if (!sync.CanSend || pendingSelection!=null || ChangingCrew) return;

        for(int sent=0;sent<64;sent++)
        {
        string command = nativeQueue.Take(sync.Session, Time.realtimeSinceStartup, out string queuedActor, out int expired, out int walkTag,out long queuedRevision,
            actor=>actor==sync.BoundIdentity?pending==null:actorRequests.Available(actor));

        if (expired > 0) Report("Expired queued guest commands; reissue the action.");

        if (command == null) return;
        var sender=CrewById(queuedActor);
        if(sender==null || !sync.Owns(sender)){nativeQueue.Release(queuedActor);Report("Queued crew is no longer assigned to this guest.");continue;}

        string stance = command == "WALK" || command == "CROUCH" ? command : null;

        if(sender==sync.BoundCrew)
        {
            pendingStance=stance;orderSentAt=Time.realtimeSinceStartup;
            pending=SendGuestOrder(queuedActor,command,sync.OrderToken,sync.TakeTicket(),fast:true,expectedRevision:queuedRevision);
        }
        else actorRequests.Start(queuedActor,()=>
        {
            var task=SendGuestOrderCore(queuedActor,command,sync.OrderToken,out long sequence,sync.TakeTicket(),fast:true,expectedRevision:queuedRevision);
            return new GuestActorRequests.Entry{Actor=queuedActor,Command=command,Sequence=sequence,Session=sync.Session,
                Epoch=sync.WaitGeneration,SentAt=Time.realtimeSinceStartup,Task=task};
        });

        if (command.Length > 3 && command[2] == ' ' && SyncProtocol.GroundMode(command.Substring(0,2)))

            sync.SendingGroundOrder(sender,walkTag,command);

        else if(command.StartsWith("BODY ") || command.StartsWith("USE ") || command.StartsWith("MIMIC ") || stance!=null) sync.SendingInteractionOrder(sender);

        MoveProbePlugin.ProbeLog.LogInfo("Native guest command: " + command);
        }
    }


    private readonly PeerPace peerPace = new PeerPace();

    private readonly CoopWaitLock hostWait = new CoopWaitLock();

    private float worldWaitTime, worldWaitReal;
    private bool hostWorldWasHeld;
    private readonly CrewWaitGroup hostCrewWaits=new CrewWaitGroup();

    private void ObserveGuestRead(string session,int sequence,char phase)

    {

        if (session == hostSession) { peerPace.Observe(sequence,phase,Time.realtimeSinceStartup); sessionHost.PeerSawCheckpoint(); }

    }

    private void TickHostPace()

    {

        bool previousWorld = hostWorldWasHeld, previousCrew = hostCrewWaits.AnyHeld;

        peerPace.Tick(Time.realtimeSinceStartup,crewA != null && crewB != null &&

            !hostWait.ExternalPause && !(MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting));

        hostWait.SetWorld(!(MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting) && (peerPace.World || peerPace.PeerPaused));

        hostCrewWaits.Update(syncedRoster,actor=>hostSelection?.IsGuest(actor)==true,peerPace.Crew,hostWait.WorldHeld,Time.time,
            actor=>OrdersFor(actor).Busy,(actor,elapsed)=>OrdersFor(actor).ResumeMovement(elapsed));
        foreach(var orders in hostOrders.Values)orders.MovementHeld=hostCrewWaits.Suspended(orders.Actor);

        bool whole = hostWait.WorldHeld;
        hostWorldWasHeld=whole;

        if (whole && !previousWorld)

        { worldWaitTime=Time.time; worldWaitReal=Time.realtimeSinceStartup; }

        if (!whole && previousWorld)
            foreach(var orders in hostOrders.Values)
                orders.ResumeWorld(Time.time-worldWaitTime,Time.realtimeSinceStartup-worldWaitReal,worldWaitReal);

        if (previousWorld != hostWait.WorldHeld || previousCrew != hostCrewWaits.AnyHeld)
            MoveProbePlugin.ProbeLog.LogInfo($"Peer pace: world={hostWait.WorldHeld} guestCrew={hostCrewWaits.AnyHeld} snapshot={snapshotNumber}");
    }

    private void CloseReceiver()

    {
        hostSelectionInput=null;

        if (receiver != null) hostWait.Release();

        peerPace.Reset();
        hostCrewWaits.Clear();hostWorldWasHeld=false;

        ResetHostOrders();
        hostRoster.Clear();
        hostSkillObjects.Clear();hostTraps.Clear();hostGhosts.Clear();hostResources.Clear();hostFreezes.Clear();hostBlinds.Clear();hostFrozen.Clear();hostDuels.Clear();hostDigs.Clear();hostMimics.Clear();hostControls.Clear();hostCharming.Clear();FishingItemSync.Clear();MimicChoice.Clear();hostCannons.Clear();hostFishing.Clear();hostTailBudget.Clear();

        receiver?.Dispose();

        receiver = null;
        hostSelection=null;
        ControlledCrew.Active.Clear();syncedRoster.Clear();

    }


    private void DisconnectGuest()=>DisconnectGuest(false);

    private void DisconnectGuest(bool preservePreparation)

    {

        if(!preservePreparation){guestWorldMap.Close();guestMissionPreparation.Close();}
        GuestPreview.Clear();

        contactRequest?.Cancel();

        contactRequest?.Dispose();

        contactRequest = null;

        requestedCrew = null;

        pendingClaim = null;
        pendingSelection=null;guestSelection.Clear();

        guestGestures.Clear();

        sync?.Dispose();
        actorRequests.Clear();

        sync = null;

        nativeQueue.Clear();

        pending = null;

        pendingStance = null;

        pressFrame = -1;

        retreatPressPoint = null;

    }


    private bool shutdownComplete;
    public void OnEnable() => shutdownComplete=false;
    private void Shutdown()
    {
        if(shutdownComplete)return;
        shutdownComplete=true;
        CoopPings.Shutdown();
        GameSpeedSync.Clear();
        StorySync.Close();
        void Clean(string name,Action action)
        { try{action();}catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Shutdown "+name+": "+ex);} }
        Clean("live import",()=>sessionLiveImport.Cancel(false));
        Clean("host lobby",()=>lobbyHost.Close());
        Clean("guest lobby",()=>lobbyGuest.Close());
        Clean("host transfer",()=>sessionHost.Cancel("Game component disabled"));
        Clean("bootstrap",()=>sessionBootstrap.Cancel(false));
        Clean("host",CloseReceiver);
        Clean("guest",DisconnectGuest);
    }
    public void OnDisable() => Shutdown();


    // Retain guest role across disconnects and the trip back to the main menu.

    public void OnApplicationQuit()

    {
        NativeInput.EndGame();
        Shutdown();
        MoveProbePlugin.ProbeLog.LogInfo("Game quit callback completed co-op cleanup.");

        try { GuestExitCollection.Collect(); }

        catch (Exception ex) { MoveProbePlugin.ProbeLog.LogWarning("Exit collection: " + ex.Message); }

    }


    private readonly CoverSync hostCoverResults=new CoverSync();
    private readonly SkillObjectSync hostSkillObjects=new();
    private readonly TrapObjectSync hostTraps=new();
    private readonly GhostObjectSync hostGhosts=new();
    private readonly SkillResourceSync hostResources=new();
    private readonly TimeFreezeSync hostFreezes=new();
    private readonly BlindSync hostBlinds=new();
    private readonly FrozenSync hostFrozen=new();
    private readonly FrozenDuelSync hostDuels=new();
    private readonly DigStateSync hostDigs=new();
    private readonly MimicStateSync hostMimics=new();
    private readonly ControlStateSync hostControls=new();
    private readonly CharmingGhostSync hostCharming=new();
    private readonly CannonStateSync hostCannons=new();
    private readonly FishingPullSync hostFishing=new();
    private readonly SnapshotTailBudget hostTailBudget=new(18);
    private FootprintSync hostFootprints=new FootprintSync();
    private float nextHostError;
    private readonly List<MiCharacter> syncedRoster=new();
    private void RefreshControlledCrew()
    {
        if(hostSelection==null)return;
        var originals=new List<MiCharacter>(hostSelection.OriginalActors);
        if(!ControlledCrew.Active.Sample(originals,GuestMove.RawIdentity))throw new InvalidOperationException("Ambiguous controlled crew sample");
        ControlledCrew.Active.Restrict(binding=>hostSelection.OwnerCode(ControlledCrew.Active.Source(binding.Source))!='U');
        var authority=new List<CrewOwnership.ControlledActor>();var actors=new Dictionary<string,MiCharacter>(StringComparer.Ordinal);
        syncedRoster.Clear();syncedRoster.AddRange(originals);
        foreach(var binding in ControlledCrew.Active.Bindings)
        {authority.Add(binding.Authority);actors.Add(binding.Id,binding.Actor);syncedRoster.Add(binding.Actor);}
        if(!hostSelection.SetControlled(authority,actors))throw new InvalidOperationException("Controlled crew authority rejected");
        // A canceled possession must not leave a managed run/attack retry on
        // the NPC after the original game restores its AI controller.
        var live=new HashSet<IntPtr>();foreach(var actor in syncedRoster)live.Add(actor.Pointer);
        foreach(var key in new List<IntPtr>(hostOrders.Keys))if(!live.Contains(key))hostOrders.Remove(key);
    }

    private string Snapshot()

    {
        using var performance=CoopPerformance.Measure(2);

        if (crewA == null || crewB == null) return "NOT_READY";
        RefreshControlledCrew();
        // Establish generated-entity aliases before encoding any skill target
        // in this frame; a first-use hook must not leak a local hierarchy key.
        var persistentObjects=hostSkillObjects.Capture(syncedRoster);
        var persistentTraps=hostTraps.Capture(syncedRoster);
        var persistentGhosts=hostGhosts.Capture(syncedRoster);
        var persistentResources=hostResources.Capture(syncedRoster);
        var persistentFreezes=hostFreezes.Capture(syncedRoster,hostOutcomes.Registry.Values);
        var persistentBlinds=hostBlinds.Capture(syncedRoster,hostOutcomes.Registry.Values);
        var persistentFrozen=hostFrozen.Capture(syncedRoster,hostOutcomes.Registry.Values);
        var persistentDuels=hostDuels.Capture(syncedRoster,hostOutcomes.Registry.Values);
        var persistentDigs=hostDigs.Capture(syncedRoster);
        var persistentCannons=hostCannons.Capture(syncedRoster,hostOutcomes.Registry.Values);
        var persistentFishing=hostFishing.Capture();

        string mode = (MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting) ? "SCENE" : hostWait.ExternalPause || peerPace.PeerPaused ? "WAIT" : hostWait.WorldHeld ? "HOLD" : "RUN";

        var orders=OrdersFor(crewB);
        bool restCrouch=orders.RestCrouch;

        ++snapshotNumber;

        peerPace.Publish(snapshotNumber,Time.realtimeSinceStartup,mode=="HOLD");

        orders.RecordFrame(snapshotNumber);

        string state = $"S18 {hostSession} {mode} {snapshotNumber} {CrewSync.Describe(crewA,surface:true)} {CrewSync.Describe(crewB,orders.Running)}{(restCrouch ? "C" : "W")}{CrewSync.Cover(crewB)}{GuestGroundTarget.SurfaceCode(crewB)}";

        if (watchedGuard != null && watchedGuard.controllerAI?.m_ai != null && watchedGuard.health != null)

        {

            state += $" G {Melee.GuardId(watchedGuard)} {CrewSync.Position(watchedGuard.transform.position)} {watchedGuard.controllerAI.m_ai.eAlertState} {watchedGuard.health.eHealthState}";

            guardDiagnostics.HostSample(snapshotNumber, watchedGuard);

        }

        string alarm = hostAlarm.Wire(crewB.transform.position);

        hostOutcomes.SetAlarmActive(alarm.StartsWith("Alarm",StringComparison.Ordinal));

        state += (orders.Busy ? " M 1 " : " M 0 ") + receiver.ControlTicket + orders.ContactWire + hostOutcomes.Write(8);
        string cues=$" X {cueA.Wire()} {orders.CueWire} {alarm}";

        string coverResults=hostCoverResults.Write(crewA,HostSpectator?null:crewB);
        string interactions=hostInteractions.Write(crewA,HostSpectator?null:crewB);

        state+=hostBodies.Write(crewA,HostSpectator?null:crewB,hostOutcomes.Registry,2048-state.Length-cues.Length-interactions.Length-coverResults.Length-100);

        state+=interactions;

        string routeInfo="";

        try { routeInfo=orders.HostRouteWire(); }

        catch(Exception ex) { if(snapshotNumber%50==0) MoveProbePlugin.ProbeLog.LogWarning("Native route read: "+ex.GetType().Name); }

        // The optional ordinary destination must leave space for the health

        // and alarm-house header. Guard paging budgets its own records below.

        if (state.Length+cues.Length+routeInfo.Length+coverResults.Length+96<=2048) state+=routeInfo;

        state+=cues;

        string house="-";

        try { house=hostHouses.Write(alarm); }

        catch(Exception ex) { if (snapshotNumber%50==0) MoveProbePlugin.ProbeLog.LogWarning($"Alarm house read: {ex.GetType().Name}"); }

        hostPursuit.SetCrew(crewA,crewB,syncedRoster);
        string core=state + hostPursuit.Write(hostOutcomes.Registry,crewA,crewB,alarm,2048-state.Length-coverResults.Length,house)+coverResults;
        string full=hostRoster.Capture(core,syncedRoster,crewA,crewB,hostSelection.OwnerCode,hostSelection.Revision,actor=>
        {
            if(!hostSelection.IsGuest(actor))return (null,null,false);
            var actorOrders=OrdersFor(actor);actorOrders.RecordFrame(snapshotNumber);
            return (actorOrders.OrderWire(),actorOrders.CueWire,actorOrders.Running);
        });
        var controlled=new List<ControlledCrewProtocol.Entry>();
        foreach(var binding in ControlledCrew.Active.Bindings)controlled.Add(binding.Manifest);
        full=ControlledCrewProtocol.Attach(full,controlled);
        // Complete per-family manifests rotate when their combined size exceeds
        // the transport bound. Crew/ownership remains present on every frame.
        void Offer(int family,string attached)=>hostTailBudget.Offer(family,full,attached);
        Offer(0,SkillObjectProtocol.Attach(full,persistentObjects));
        Offer(1,TrapProtocol.Attach(full,persistentTraps));
        Offer(2,GhostProtocol.Attach(full,persistentGhosts));
        Offer(3,ResourceProtocol.Attach(full,persistentResources));
        Offer(4,FreezeProtocol.Attach(full,persistentFreezes));
        Offer(5,BlindProtocol.Attach(full,persistentBlinds));
        Offer(6,FrozenProtocol.Attach(full,persistentFrozen));
        Offer(7,DuelProtocol.Attach(full,persistentDuels));
        Offer(8,DigProtocol.Attach(full,persistentDigs));
        Offer(9,CannonProtocol.Attach(full,persistentCannons));
        Offer(10,CannonLaunchProtocol.Attach(full,CannonLaunchSync.Capture()));
        hostTailBudget.OfferPage(11,"K3",CannonFlightSync.Capture(),CannonFlightProtocol.Row);
        hostTailBudget.OfferPage(12,"Y1",persistentFishing,FishingPullProtocol.Row);
        Offer(13,hostFootprints.Attach(full,syncedRoster));
        Offer(14,MimicProtocol.Attach(full,hostMimics.Capture(syncedRoster)));
        Offer(15,ControlProtocol.Attach(full,hostControls.CaptureRows(syncedRoster)));
        Offer(16,CharmingGhostProtocol.Attach(full,hostCharming.CaptureRows(syncedRoster)));
        Offer(17,FishingItemProtocol.Attach(full,FishingItemSync.Capture()));
        string shipTail=ShipRoutineSync.Capture();
        string result=hostTailBudget.Append(full,SnapshotTransport.MaxBytes-shipTail.Length);
        if(!CrewRosterProtocol.Read(result,out _,out _))throw new InvalidOperationException("Invalid budgeted whole-world snapshot");
        return result+shipTail;

    }


    private void WatchGuard()

    {

        if (crewB == null || receiver == null) { Report("按 F7 恢复房主联机。 / Press F7 to resume hosting."); return; }

        watchedGuard = null;

        float nearest = 30f;

        foreach (var actor in UnityEngine.Object.FindObjectsOfType<MiCharacter>())

        {

            if (!actor.bControllerAI() || actor.controllerAI?.m_ai == null) continue;

            float distance = Vector3.Distance(actor.transform.position, crewB.transform.position);

            if (distance < nearest) { watchedGuard = actor; nearest = distance; }

        }

        Report(watchedGuard == null ? "No nearby AI within 30 units." : $"Watching nearest AI: {watchedGuard.name}, {nearest:F1} units from B.");

    }


    private bool IssuePoint(string payload)

    {

        if (hostWait.WorldHeld || HostSpectator) return false;

        if (!SyncProtocol.Command(payload, out var fields,out long ownershipVersion,out long actorSequence)) return false;

        if (crewB == null || crewA == null || !crewB.bControllerPlayer()

            || Time.timeScale <= 0f || (MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting) || fields[1] != hostSession) return false;

        if (fields.Length == 3 && fields[2] == "CLAIM" && ownershipVersion==0)

        {

            RefreshRoster();

            var chosen = CrewById(fields[0]);

            if (chosen == null || !roster.Exists(c => c != null && c.GetInstanceID() == chosen.GetInstanceID()) || hostSelection?.CanGuestClaim(chosen)!=true || RemoteOrders?.Busy == true || chosen.health == null ||

                chosen.health.eHealthState != MiCharacterHealth.HealthState.Alive) return false;

            if (chosen != crewB) { RemoteOrders?.EndHostRun(true); crewB = chosen; ArmHost(crewA,hostSelection?.HostOwned()); }

            return true;

        }

        if(ownershipVersion>0)
        {
            if(hostSelection==null||!hostSelection.Admit(fields[1],ownershipVersion,fields[0],actorSequence))return false;
            if(fields[2]=="SELECT")
            {
                var group=fields[3].Split(',');
                if(!hostSelection.SelectGuestGroup(fields[1],ownershipVersion,group))return false;
                if(!Array.Exists(group,id=>id==GuestMove.Identity(crewB)))
                {
                    var next=hostSelection.Original(group[0]);
                    // A possessed body/ghost is selected locally, while its
                    // original crew remains the persistent network anchor.
                    if(next!=null){crewB=next;hostInteractions.RebindSecond();RefreshRoster();}
                }
                return true;
            }
            if(fields[2]=="RELEASE")return hostSelection.ReleaseGuest(fields[1],ownershipVersion,fields[3].Split(','),crewB,
                actor=>actor==null||OrdersFor(actor).Busy||InteractionSync.Climbing(actor)||actor.controller?.skillCommandProcess?.m_skill!=null);
            var actor=CrewById(fields[0]);
            if(actor==null||!hostSelection.IsGuest(actor))return false;
            var actorOrders=OrdersFor(actor);bool accepted=actorOrders.Execute(fields);
            if(accepted)actorOrders.Applied(actorSequence);return accepted;
        }
        if (fields[0] != GuestMove.Identity(crewB)) return false;
        return OrdersFor(crewB).Execute(fields);
    }

    private bool IssueRemoteMove() => !HostSpectator && IssueMove("Loopback", destination);


    private int leftDownFrame=-1;

    private void SendAttack(string key)

    {

        var actor = Leader();

        if (sync == null || !sync.CanSend || !sync.Owns(actor) || !sync.SelectionReady || ChangingCrew ||

            Time.timeScale <= 0f || (MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting))

        { Report("Select the bound guest crew in the running mission."); return; }

        var target = Melee.Pick();

        if (key == "J") key = Melee.Special(actor).ToString();

        var skill = Melee.Decode(key);

        if (!Melee.CanUse(actor, target, skill))

        { Report($"Attack unavailable ({skill}): aim at a live AI within skill range."); return; }

        string cmd = "HIT " + key + " " + Melee.GuardId(target);

        if (Melee.Contact(skill)) cmd = sync.WithContactProof(actor,cmd, target);

        queuedSession=sync.Session;
        if(!nativeQueue.Add(queuedSession,GuestMove.Identity(actor),cmd,Time.realtimeSinceStartup,revision:sync.OwnershipRevision))
        {Report("Input queue busy.");return;}

        sync.QueuedInput(actor,cmd);

        sync.AttackInput(actor,cmd);

        PumpNativeInput();

        Report("Sent " + cmd);

    }


    private void SendGuestPoint(bool stance, bool leftClick = false, int clickStarted = -1,MiCharacter groupActor=null)

    {

        if (ChangingCrew) return;

        if (sync == null) { Report("Copy the host room URL, then press F6."); return; }

        if (!sync.CanSend) { Report(sync.Status); return; }

        var actor = groupActor??Leader();

        var gesture=Gesture(actor);

        var id = GuestMove.Identity(actor);

        if (!sync.Owns(actor) || !sync.SelectionReady || id == null || (MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting) || Time.timeScale <= 0f)

        { Report("Select B in the running mission."); return; }

        string command;

        if (stance)

            command = sync.CrouchedFor(actor) ? "WALK" : "CROUCH";

        else

        {

            bool issuedThisClick = leftClick && clickStarted >= 0 && gesture.issuedPointFrame >= clickStarted;

            bool retreat = leftClick && sync.CanRedirectAttackFor(actor);

            Vector3 point;string detail,surface;bool picked;
            if(groupActor!=null)
            {
                // Native squad targets include the game's spacing/formation.
                // Never replace every member's destination with the leader's ray.
                point=gesture.issuedPoint;surface=GuestGroundTarget.DestinationSurface(actor,point);detail="native squad destination "+CrewSync.Position(point);
                picked=issuedThisClick;
            }
            else
            {
                var c=actor.controllerPlayer;var selected=c?.skillSelection;
                bool ordinary=selected==null || selected.Pointer==c.m_skillSelectionNull?.Pointer ||
                    selected.eSkillType==c.m_eSkillTypeNull || selected.eSkillType is Skill.SkillType.None or Skill.SkillType.GoTo;
                picked=GuestGroundTarget.TryPick(actor,leftClick,issuedThisClick,gesture.issuedPoint,out point,out detail,out surface,retreat,ordinary);
            }
            if (!picked)

            {

                if (!retreat || !retreatPressPoint.HasValue)

                { Report("Ground point rejected: " + detail); return; }

                // If the camera or an actor moved under the pointer during native input handling,

                // keep the floor verified at mouse-down.

                point = retreatPressPoint.Value;

                surface = retreatPressSurface;

                detail = "verified retreat press point " + CrewSync.Position(point);

            }

            string gait = leftClick ? ClickGait(actor, point, clickStarted) : null;

            command = (gait == null ? "" : gait + " ") + CrewSync.Position(point);

            if (gait != null && surface != null) command += " "+surface;

            queuedSession = sync.Session;

            GuestPreview.Cancel(actor);

            sync.WithdrawContact(actor);

            if(actor==sync.BoundCrew)contactRequest?.Cancel();

            if (!nativeQueue.Add(queuedSession, id, command, Time.realtimeSinceStartup, walkTag:leftClick ? Time.frameCount : 0,

                supersedeAttack: leftClick && retreat,revision:sync.OwnershipRevision))

            { Report("Input queue busy."); return; }

            sync.QueuedInput(actor,command);

            if (leftClick) sync.QueueGroundTurn(actor,point);

            Report((leftClick ? "Left ground click queued: " : "F10 ground point queued: ") + detail + " gait=" + gait);

            PumpNativeInput();

            return;

        }

        queuedSession=sync.Session;
        if(!nativeQueue.Add(queuedSession,id,command,Time.realtimeSinceStartup,revision:sync.OwnershipRevision))
        {Report("Input queue busy.");return;}

        sync.QueuedInput(actor,command);

        sync.ManualStance(actor,command == "CROUCH");

        PumpNativeInput();

        Report("Sent " + command);

    }


    private void FinishActorRequests()
    {
        foreach(var entry in actorRequests.Completed())
        {
            string result;
            try{result=entry.Task.GetAwaiter().GetResult();}
            catch(Exception ex)
            {
                if(sync!=null&&sync.Session==entry.Session&&sync.WaitGeneration==entry.Epoch)
                {sync.NetworkOrderCompleted(entry.Actor,entry.Sequence,false);Report("Guest request failed: "+GuestMove.Failure(ex));}
                continue;
            }
            if(sync==null||sync.Session!=entry.Session||sync.WaitGeneration!=entry.Epoch)continue;
            bool ticket=result.StartsWith("ACCEPTED ",StringComparison.Ordinal)&&SyncProtocol.Session(result.Substring(9));
            bool accepted=ticket||result=="ACCEPTED";
            sync.NetworkOrderCompleted(entry.Actor,entry.Sequence,accepted);
            if(ticket)sync.UseNextTicket(result.Substring(9));
            if(!accepted)
            {
                if(!nativeQueue.Contains(entry.Actor))GuestPreview.Cancel(CrewById(entry.Actor));
                Report("Host reply: "+result);
            }
            else MoveProbePlugin.ProbeLog.LogInfo($"Host accepted crew command. RTT={(Time.realtimeSinceStartup-entry.SentAt)*1000f:F0}ms");
        }

    }

    private void FinishGuestPoint()
    {
        FinishActorRequests();
        if (pending == null || !pending.IsCompleted) return;

        var task = pending;

        pending = null;

        bool cancelledContact = contactRequest?.IsCancellationRequested == true;

        contactRequest?.Dispose();

        contactRequest = null;

        var selection=pendingSelection;pendingSelection=null;

        var claim = pendingClaim;

        pendingClaim = null;

        string stance = pendingStance;

        pendingStance = null;

        string orderActor=pendingActor;long orderSequence=pendingActorSequence;pendingActor=null;pendingActorSequence=0;
        string reply;

        try { reply = task.GetAwaiter().GetResult(); }

        catch (OperationCanceledException) when (cancelledContact) { sync?.NetworkOrderCompleted(orderActor,orderSequence,false); return; }

        catch (Exception ex) { if(selection!=null)guestSelection.Reply(selection.Serial,false,Time.realtimeSinceStartup); sync?.NetworkOrderCompleted(orderActor,orderSequence,false); if (stance != null) sync?.StanceReply(orderActor,stance, false); Report("Guest request failed: " + GuestMove.Failure(ex)); return; }

        bool withTicket = reply.StartsWith("ACCEPTED ",StringComparison.Ordinal) && SyncProtocol.Session(reply.Substring(9));

        sync?.NetworkOrderCompleted(orderActor,orderSequence,withTicket || reply == "ACCEPTED");

        if (withTicket) { sync?.UseNextTicket(reply.Substring(9)); reply="ACCEPTED"; }

        if (stance != null) sync?.StanceReply(orderActor,stance, reply == "ACCEPTED");

        if(selection!=null)
        {
            guestSelection.Reply(selection.Serial,reply=="ACCEPTED",Time.realtimeSinceStartup);
            if(reply!="ACCEPTED")Report("Crew assignment not accepted; refreshing host ownership.");
            return;
        }

        if (claim != null)

        {

            if (reply == "ACCEPTED") ConnectGuest(sync.Room,claim);

            else Report("Crew unavailable. Finish the current action or choose a free crew member.");

            return;

        }

        if (reply != "ACCEPTED") { GuestPreview.Cancel(CrewById(orderActor)??sync?.BoundCrew); Report("Host reply: " + reply); return; }

        if (NativeGuest) MoveProbePlugin.ProbeLog.LogInfo($"Host accepted native command. RTT={(Time.realtimeSinceStartup-orderSentAt)*1000f:F0}ms");

        else Report("Host accepted the command.");

    }


    private bool IssueStep(string direction)

    {

        var camera = Camera.main;

        if (camera == null || crewB == null || HostSpectator) return false;

        var right = Vector3.ProjectOnPlane(camera.transform.right, Vector3.up).normalized;

        var up = Vector3.Cross(right, Vector3.up);

        Vector3 axis = direction switch { "up" => up, "down" => -up, "right" => right, _ => -right };

        var point = crewB.transform.position + axis * 3f;

        if (!NavMesh.SamplePosition(point, out var hit, 1.5f, -1))

        {

            Report("No nearby walkable surface. Try another direction.");

            return false;

        }

        return IssueMove("Phone " + direction, hit.position);

    }


    private static MiCharacter cachedLeader;

    private static MiCharacter Leader()

    {

        if (cachedLeader != null && cachedLeader.controllerPlayer?.bLeader == true) return cachedLeader;

        foreach (var actor in UnityEngine.Object.FindObjectsOfType<MiCharacter>())

            if (actor.bControllerPlayer(out var player) && player != null && player.bLeader)

                return cachedLeader=actor;

        return null;

    }


    private void Report(string message)

    {

        status = message;

        statusUntil = Time.realtimeSinceStartup + 6f;

        MoveProbePlugin.ProbeLog.LogInfo(message);

    }


    internal static bool HostLaunch;
    private bool hostResumeRequested,resumeHostAfterDisconnect;
    private bool GuestReconnectAllowed=>LaunchMode.CanReconnectGuest(HostLaunch,cachedHostIntent||resumeHostAfterDisconnect,receiver!=null,MoveProbePlugin.LastPair.Value);
    private void ResumeHost(){
        if(MoveProbePlugin.LiveGuestLaunch||sync!=null){Report("客机请用 F6 重连房主。 / Guests reconnect with F6.");return;}
        if(receiver!=null){Report("房主联机已开启。 / Hosting is already active.");return;}
        if(Time.realtimeSinceStartup<reconnectAfter)return;
        reconnectAfter=Time.realtimeSinceStartup+2f;
        if(sessionLiveImport.Busy||sessionBootstrap.Busy||sessionIndexRefresh.Busy||TrainingSession.Suspended||SaveProcessManager.bLoading||SaveProcessManager.bProcessing||SaveProcessManager.bAsyncWriteOperationRunning){Report("请等待当前加载或训练结束。 / Wait for loading or training to finish.");return;}
        System.IO.Directory.CreateDirectory(SessionHost.DirectoryPath);
        System.IO.File.WriteAllText(System.IO.Path.Combine(SessionHost.DirectoryPath,"host-start.txt"),DateTime.UtcNow.ToString("O"));
        Remember("H");cachedHostIntent=true;nextHostIntentCheck=0;nextRestore=0;resumeHostAfterDisconnect=false;
        Report("房主联机已恢复，等待场景就绪及客机重连。 / Hosting resumed; waiting for scene readiness and guests.");
    }
    private float reconnectAfter;
    private bool menuReconnectRequested;
    private bool InReconnectMenu()=>SceneLookup.Find<MiMissionHandler>()?.currentMissionData==null&&
        SceneLookup.Find<UIMainMenuGUI>()?.gameObject.activeInHierarchy==true;
    private void ReconnectFromMenu(){
        if(!InReconnectMenu()||!GuestReconnectAllowed)return;
        if(Time.realtimeSinceStartup<reconnectAfter)return;
        reconnectAfter=Time.realtimeSinceStartup+2f;
        if(sessionLiveImport.Busy||sessionBootstrap.Busy||sessionIndexRefresh.Busy||SaveProcessManager.bLoading||SaveProcessManager.bProcessing){Report("正在加载或接档，请等待完成；F11 可取消后再重连。");return;}
        var address=GuestMove.Room(MoveProbePlugin.LastGuestRoom.Value);
        if(address==null){Report("没有上次房间记录，请先从联机启动窗口加入一次。");return;}
        try{
            string path=System.IO.Path.Combine(SessionHost.DirectoryPath,"live-import.json");
            if(System.IO.File.Exists(path)){Report("已有重连请求，正在等待处理…");return;}
            sessionLiveImport.Cancel();sessionBootstrap.Cancel();sessionSceneFollow.Close();lobbyGuest.Close();lobbyHost.Close();sessionHost.Cancel();Remember("");
            DisconnectGuest();CloseReceiver();crewA=crewB=null;StorySync.Close();GameSpeedSync.Clear();
            Remember("G 1 "+address.AbsoluteUri);MoveProbePlugin.LiveGuestLaunch=true;
            GuestExitCollection.Arm(Paths.GameRootPath);
            string generation=Guid.NewGuid().ToString("N");
            System.IO.Directory.CreateDirectory(SessionHost.DirectoryPath);
            string temp=path+"."+generation+".new";
            System.IO.File.WriteAllText(temp,System.Text.Json.JsonSerializer.Serialize(new{schema=1,generation,operation="join-room",room=address.AbsoluteUri,returnToMenu=false}));
            System.IO.File.Move(temp,path);
            Report("正在重连上次房主并接收当前进度…");
        }catch(Exception ex){Report("重连未完成："+ex.Message);}
    }
    private readonly CoopInvite inviteUI=new();
    public void OnGUI()
    {
        if(shutdownComplete)return;
        if(resumeHostAfterDisconnect&&receiver==null&&
            GUI.Button(new Rect(12,116,330,34),"F7  恢复房主联机 / Resume hosting"))hostResumeRequested=true;
        if(InReconnectMenu()&&GuestReconnectAllowed&&GuestMove.Room(MoveProbePlugin.LastGuestRoom.Value)!=null&&
            GUI.Button(new Rect(12,116,330,34),"F6  重连上次房主 / Reconnect to last host"))menuReconnectRequested=true;
        string notice=MissionEntrySync.WaitingGuestEntry?
            "等待客机加载同一关卡，暂不能继续…\nWaiting for the guest to load this mission…":sessionHost.LoadingStage;
        if(notice==null&&receiver!=null&&hostWait.WorldHeld)
            notice="等待另一端，游戏已暂停。F11 断开联机\nWaiting for the other player. Game paused. F11 to disconnect.";
        if(notice==null&&!TrainingSession.Suspended)notice=lobbyGuest.ConnectionHint(sync?.ReceivingFrames==true);
        if(notice==null&&sync!=null&&!sync.ReceivingFrames&&!TrainingSession.Suspended)
            notice="正在连接并同步房主…\nConnecting and synchronizing with the host…";
        if(notice==null&&status!=null&&Time.realtimeSinceStartup<statusUntil&&
            (status.Contains("failed",StringComparison.OrdinalIgnoreCase)||status.Contains("失败")||status.Contains("未完成")))
            notice="联机操作未完成；可按 F11 断开，再在主菜单按 F6 重连。\nCo-op action failed. F11 disconnects; F6 reconnects from the main menu.";
        if(!TrainingSession.MapBlocked&&!PlanningSync.Pending)HostLoadingView.Draw(notice);
        TrainingSession.Draw();if(!TrainingSession.MapBlocked)PlanningSync.Draw();
        // Invitations are available while paused; gameplay has no ownership,
        // debug-status or duplicate character-selection panels.
        if(receiver!=null&&Time.timeScale<=0f)inviteUI.Draw();
    }


    private bool BlockGround(string reason)

    {

        if (groundBlockLogs++ < 15) MoveProbePlugin.ProbeLog.LogInfo("Ground click skipped: " + reason);

        return false;

    }


    private bool AllowsGroundClick()

    {

        var actor = Leader();

        if (actor == null || !sync.Owns(actor) || !sync.SelectionReady || Time.timeScale <= 0f || (MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting))

            return BlockGround("selection, pause, or cutscene");

        var controller = actor.controllerPlayer;

        if (controller == null) return BlockGround("guest controller missing");

        var player = controller.m_playerInput;

        if (player?.m_planningModeInput?.m_bActive == true) return BlockGround("planning mode");

        var mouse = player?.m_mouseInput;

        bool overUi = mouse != null ? mouse.bOverUI || mouse.bOverGuardHouseUI

            : UnityEngine.EventSystems.EventSystem.current?.IsPointerOverGameObject() == true;

        if (overUi) return BlockGround("pointer over UI");

        // Native targeting has already decided this click is GoTo, including
        // ground clicks while an enemy-only ranged skill remains selected.
        // Do not reinterpret that decision as an attempted ground cast.
        if(Gesture(actor).issuedPointFrame==Time.frameCount)return true;

        var aiming = player?.m_aiming;

        var selected = controller.skillSelection;

        var defaultSelection = controller.m_skillSelectionNull;

        bool defaultInstance = selected != null && defaultSelection != null && selected.Pointer == defaultSelection.Pointer;

        if (sync.HasMeleeInputFor(actor) && Melee.Contact(selected?.eSkillType ?? Skill.SkillType.None) &&

            (aiming?.bIsActive != true || Melee.Contact(aiming.m_playerSkill?.eSkillType ?? Skill.SkillType.None)))

            return true; // TryPick still rejects characters, interactables and unwalkable surfaces.

        if (!GroundClickPolicy.Allows(aiming?.bIsActive == true, selected?.eSkillType, controller.m_eSkillTypeNull, defaultInstance))

            return BlockGround($"selected skill={selected?.eSkillType} default={controller.m_eSkillTypeNull} aim={aiming?.m_playerSkill?.eSkillType}");

        return true;

    }


    private float DoubleClickWindow()

    {

        if (clickSettings == null) clickSettings = UnityEngine.Object.FindObjectOfType<MiGameInput>();

        return clickSettings != null && clickSettings.m_fDoubleClickTime > 0 ?

            Math.Min(clickSettings.m_fDoubleClickTime, 1f) : 0.35f;

    }


    private string ClickGait(MiCharacter actor,Vector3 point,int started)
    {
        var mouse=actor.controllerPlayer.m_playerInput?.m_mouseInput;
        bool gameDouble=DoubleAction(mouse?.m_actionButtonInputMove)||DoubleAction(mouse?.m_actionButtonInputDoSkill);
        return Gesture(actor).Gait(point,Input.mousePosition,started,Time.realtimeSinceStartup,DoubleClickWindow(),gameDouble,sync.PreferredCrouchFor(actor));
    }

    private static bool DoubleAction(ActionButtonInput input) =>

        input != null && ActionButtonInput.bIsDouble(input.eActionButtonState);


    private int DispatchGuestGround(int started)
    {
        int dispatched=0;
        foreach(var member in pressCrew)
        {
            if(!ListensTo(member))continue;
            var state=Gesture(member);
            if(state.nonMoveFrame>=started||state.issuedPointFrame<started)continue;
            SendGuestPoint(false,true,started,member);dispatched++;
        }
        return dispatched;
    }

    private void DispatchOrdinaryPress(int started)
    {
        if (!AllowsGroundClick()) return;
        bool interacted=false;
        foreach(var member in pressCrew)
        {
            if(!ListensTo(member))continue;
            // A native contextual action may commit on release/hold. Do not
            // issue an earlier fallback GoTo for this same interaction click.
            // Already captured native actions need no second submission.
            if(Gesture(member).nonMoveFrame>=started){interacted=true;continue;}
            var context=GuestDisposal.ForInteraction(member);
            if(context==null)continue;
            CaptureCommand(member,context,true);interacted=true;
        }
        if(interacted)return;
        if (pressCrew.Count > 1)
        {
            int dispatched = DispatchGuestGround(started);
            if (dispatched == 0) MoveProbePlugin.ProbeLog.LogInfo("Squad click had no native ground destinations; no leader-position fallback sent.");
            return;
        }
        var actor = Leader();
        var cache = actor?.controllerPlayer?.m_playerInput?.m_raycastCache;
        cache?.Recache();
        var body = BodySync.GroundClickTarget(actor, cache?.entity);
        if (body != null)
        {
            var gait = ClickGait(actor, body.transform.position, started);
            Capture(actor, "BODY " + (gait == "RC" || gait == "RW" ? "MR " : "M ") + Melee.GuardId(body));
            return;
        }
        SendGuestPoint(false, true, started);
    }

    private bool SamePressedCrew()
    {
        var selected=GuestCrewSelection.Members(pressActor?.controllerPlayer?.m_playerInput);
        if(selected==null||selected.Count!=pressCrew.Count)return false;
        for(int i=0;i<selected.Count;i++){var actor=selected[i];if(!pressCrew.Exists(c=>c==actor))return false;}
        return true;
    }

    public void LateUpdate()

    {
        if(shutdownComplete)return;
        if(CoopPings.ConsumesClick){pressFrame=-1;pressDisposal=null;return;}

        try
        {
            if(GuestWorldMap.NativeUiOwnsCrew||MiCutsceneHandler.bCutsceneRunning)
            {GuestPreview.Clear();return;}
            sync?.RestoreDetectionView();
            if(sync!=null && (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape)))
            {
                var selected=Leader()?.controllerPlayer?.m_playerInput?.lSelectedCharacter;
                if(selected!=null)for(int i=0;i<selected.Count;i++)if(sync.Owns(selected[i]))GuestPreview.Cancel(selected[i]);
            }
            MimicChoice.Tick();
            GuestPreview.Tick();
        }
        catch(Exception ex) { MoveProbePlugin.ProbeLog.LogWarning("Guest range preview: "+ex.GetType().Name); GuestPreview.Clear(); }

        // Read Unity input directly after the game's normal mouse update. Preserve

        // skill clicks and held disposal. Ordinary movement commits on mouse down,
        // so dragging or releasing elsewhere cannot change its destination.

        if (!Application.isFocused || !MoveProbePlugin.DirectGroundClicks || sync == null || !sync.CanSend)

        { pressFrame = -1; pressDisposal=null; return; }

        try

        {

            var gesture=Gesture(Leader());

            if (Input.GetMouseButtonDown(0))

            {

                leftDownFrame=Time.frameCount;
                pressActor=Leader();pressCrew.Clear();
                var pressedSelection=GuestCrewSelection.Members(sync.SelectionInput);
                if(pressedSelection!=null)for(int i=0;i<pressedSelection.Count;i++)if(sync.Owns(pressedSelection[i]))pressCrew.Add(pressedSelection[i]);

                if (gesture.lastMeleeGuard!=null && gesture.RepeatMelee(gesture.lastMeleeGuard,gesture.lastMeleeSkill,leftDownFrame,Time.realtimeSinceStartup,DoubleClickWindow()) &&

                    Leader()?.controllerPlayer?.m_playerInput?.m_mouseInput?.bOverUI == false && Melee.Pick()==gesture.lastMeleeGuard)

                {

                    gesture.lastMeleePress=leftDownFrame; gesture.lastMeleeFast=true; gesture.nonMoveFrame=leftDownFrame;

                    Capture(Leader(),"HIT "+(gesture.lastMeleeSkill==Skill.SkillType.Kill ? "LR " : "KR ")+Melee.GuardId(gesture.lastMeleeGuard),gesture.lastMeleeGuard);

                }

                pressDisposal=ListensTo(Leader()) ? GuestDisposal.ForClick(Leader()) : null;

                if(pressDisposal!=null) GuestPreview.ShowDisposal(Leader(),pressDisposal);

                pressFrame = pressDisposal!=null || AllowsGroundClick() ? Time.frameCount : -1;

                retreatPressPoint = null;

                if (pressFrame >= 0 && sync.CanRedirectAttackFor(Leader()) &&

                    GuestGroundTarget.TryPick(Leader(),true,false,default,out var floor,out _,out retreatPressSurface,true))

                    retreatPressPoint = floor;

                pressPosition = Input.mousePosition;

                pressSession = sync.Session;

                // Native mouse handling has already populated this frame's targets.
                // Commit ordinary movement now; only disposal retains release semantics.
                if (pressFrame >= 0 && pressDisposal == null)
                {
                    int pressStarted = pressFrame;
                    pressFrame = -1;
                    if (gesture.nonMoveFrame < pressStarted || pressCrew.Count > 1)
                        DispatchOrdinaryPress(pressStarted);
                    return;
                }

            }

            if (pressFrame < 0) return;

            if ((Input.mousePosition - pressPosition).sqrMagnitude > 64f || pressSession != sync.Session || Leader()!=pressActor || !SamePressedCrew())

            { pressFrame = -1; pressDisposal=null; GuestPreview.Clear(pressActor); return; }

            if (!Input.GetMouseButtonUp(0)) return;

            int started = pressFrame;

            pressFrame = -1;

            if(gesture.nonMoveFrame>=started&&pressCrew.Count<=1) { pressDisposal=null; return; }

            if(pressDisposal!=null)

            {

                var disposal=pressDisposal;pressDisposal=null;

                CaptureCommand(Leader(),disposal,true);

                MoveProbePlugin.ProbeLog.LogInfo("Single-click native disposal context forwarded.");

                return;

            }


        }

        catch (Exception ex) { pressFrame = -1; MoveProbePlugin.ProbeLog.LogWarning("Guest ground click: " + ex); }

    }


    private float nextRestore;

    private bool saveGuestPair;

    private static void Remember(string value)

    {

        var remembered=value.Split(' ');
        if(remembered.Length==3&&remembered[0]=="G"&&GuestMove.Room(remembered[2])!=null)MoveProbePlugin.LastGuestRoom.Value=remembered[2];
        if (MoveProbePlugin.LastPair.Value != value) MoveProbePlugin.LastPair.Value = value;
        if(value.Length==0)
        {
            foreach(string path in new[]{System.IO.Path.Combine(SessionHost.DirectoryPath,"host-start.txt"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ShadowGambitCoop","join-once.txt")})
                try{System.IO.File.Delete(path);}catch(Exception ex){MoveProbePlugin.ProbeLog.LogWarning("Launch intent cleanup: "+ex.GetType().Name);}
        }

    }


    private void ConnectGuest(Uri room, MiCharacter guest)

    {

        GuestExitCollection.Arm(Paths.GameRootPath);

        CloseReceiver();

        DisconnectGuest();

        MoveProbePlugin.EnsureGuestMarkerHooks();
        sync = new CrewSync(room, guest);

        // Use the managed instance that actually owns this connection in Update.

        bool sameInstance = ReferenceEquals(NativeInput.Probe, this);

        NativeInput.Probe = this;

        clickLogCount = 0;

        groundBlockLogs = 0;

        MoveProbePlugin.ProbeLog.LogInfo("Guest input attached to F6 instance; previous matched: " + sameInstance);

        Report("Connecting to host.");

        saveGuestPair = true;

    }


    private bool ArmHost(MiCharacter leader,IEnumerable<string> preserveHost=null,LobbyDeployment.Binding assignment=null)

    {

        crewA = leader;

        destination = crewA.transform.position;


        try

        {

            if(!MoveProbePlugin.OwnershipHooksReady)throw new InvalidOperationException("Crew ownership input hooks unavailable; host room cannot start safely.");
            receiver ??= new LoopbackMove();

            peerPace.Reset();

            hostWait.Release();
            hostCrewWaits.Clear();hostWorldWasHeld=false;

            ResetHostOrders();
            hostRoster.Clear();
            hostFootprints=new FootprintSync();
            OrdersFor(crewB);
            receiver.Arm();
            cueA = new AttackCue(); hostAlarm = new MissionAlarmSync();

            hostHouses = new ReinforcementSync();

            hostPursuit = new GuardPursuit();

            hostOutcomes = new GuardOutcomes();

            hostBodies = new BodySync();

            hostInteractions = new InteractionSync();

            InteractionSync.ResetRegistry();
            ControlledCrew.Active.Clear();syncedRoster.Clear();
            hostSkillObjects.Clear();hostTraps.Clear();hostGhosts.Clear();hostResources.Clear();hostFreezes.Clear();hostBlinds.Clear();hostFrozen.Clear();hostDuels.Clear();hostDigs.Clear();hostMimics.Clear();hostControls.Clear();hostCharming.Clear();FishingItemSync.Clear();MimicChoice.Clear();hostCannons.Clear();hostFishing.Clear();hostTailBudget.Clear();

            hostSession = Guid.NewGuid().ToString("N");
            RefreshRoster();
            hostSelection=new HostCrewSelection();
            hostSelection.Reset(hostSession,roster,crewA,crewB,preserveHost,assignment?.owners);
            hostSelection.Reconcile(crewA.controllerPlayer?.m_playerInput);


            snapshotNumber = 0;


            Remember("H");

            guardDiagnostics.Begin(hostSession);

            Report($"Target set at {destination}. Loopback ready; move A away.");
            return true;

        }

        catch (System.Net.Sockets.SocketException ex)

        {

            CloseReceiver();
            Report($"Target set. Loopback unavailable ({ex.SocketErrorCode}); F9 still works.");

        }
        catch(Exception ex)
        {
            CloseReceiver();MoveProbePlugin.ProbeLog.LogError(ex);Report("Host setup failed: "+ex.Message);
        }
        return false;

    }


    private static MiCharacterHandler rosterHandler;

    private static readonly List<MiCharacter> roster = new List<MiCharacter>();

    private static float refreshRosterAt;

    private static string rosterSource = "waiting";


    private static void RefreshRoster()

    {

        if(ShipCrewSession.Active)
        {
            roster.Clear();
            var h=ShipCrewSession.Host;var g=ShipCrewSession.Guest;
            if(h!=null&&h.bControllerPlayer())roster.Add(h);
            if(g!=null&&g!=h&&g.bControllerPlayer())roster.Add(g);
            rosterSource="confirmed ship controllers";return;
        }

        if (Time.realtimeSinceStartup < refreshRosterAt) return;

        refreshRosterAt = Time.realtimeSinceStartup + 1f;

        var previousFirst = roster.Count > 0 ? roster[0] : null;

        roster.Clear();

        if (rosterHandler == null) rosterHandler = SceneLookup.Find<MiCharacterHandler>();

        var members = rosterHandler?.m_liPlayerCharactersInMission;

        if (members != null)

        {

            for (int i = 0; i < members.Count; i++)

            {

                var c = members[i];

                if (c != null && c.bControllerPlayer() && !roster.Exists(actor=>actor==c)) roster.Add(c);

            }

        }

        rosterSource = "mission roster";

        // A published one-person roster is authoritative. Possessed enemies and
        // skill-created controllers are not a second deployed crew member.
        if (roster.Count >= 1) return;

        // Some intro/scripted missions expose the two live controllers before

        // publishing their full roster. Only infer a pair if it is unambiguous.

        var leader = Leader();

        if (leader == null) return;

        MiCharacter other = null;

        foreach (var c in UnityEngine.Object.FindObjectsOfType<MiCharacter>())

        {

            if (c == leader || !c.bControllerPlayer() || c.health == null ||

                c.health.eHealthState != MiCharacterHealth.HealthState.Alive) continue;

            if (other != null) return;

            other = c;

        }

        if (other == null) return;

        var first = roster.Count == 1 ? roster[0] : previousFirst;

        if (first != leader && first != other) first = leader;

        roster.Clear(); roster.Add(first); roster.Add(first == leader ? other : leader);

        rosterSource = "two active controllers";

    }


    private static readonly Dictionary<int,int> knownSlots = new Dictionary<int,int>();


    private static int SlotNumber(MiCharacter actor)

    {

        RefreshRoster();

        if (actor == null) return 0;

        int id = actor.GetInstanceID();

        for (int i=0; i<roster.Count; i++)

            if (roster[i] != null && roster[i].GetInstanceID() == id) return knownSlots[id] = i+1;

        return knownSlots.TryGetValue(id,out int slot) ? slot : 0;

    }


    private static string SlotLabel(MiCharacter actor)

    {

        int slot = SlotNumber(actor);

        return slot > 0 ? slot.ToString() : "?";

    }


    private static MiCharacter Slot(int index)

    {

        RefreshRoster();

        return index < roster.Count ? roster[index] : null;

    }


    internal static MiCharacter CrewById(string id)

    {

        var controlled=ControlledCrew.Active.Find(id);
        if(controlled!=null)return controlled.Actor;
        var source=ControlledCrew.Active.Source(id);
        if(source!=null)return source;

        MiCharacter found = null;

        foreach (var actor in UnityEngine.Object.FindObjectsOfType<MiCharacter>())

        {

            if (!actor.bControllerPlayer() || GuestMove.Identity(actor) != id) continue;

            if (found != null) return null;

            found = actor;

        }

        return found;

    }


    internal static void SelectAlone(MiCharacter actor)

    {

        var player = actor.controllerPlayer;

        var input = player.m_playerInput ?? Leader()?.controllerPlayer?.m_playerInput;

        if (input == null) return;

        // Rebinding the room must not replace the native shadow avatar with
        // its real actor: that tears down the local planning skill selection.
        if(input.m_planningModeInput?.m_bActive==true&&GuestCrewSelection.Source(input.leader)==actor)return;
        if (player.bLeader && input.iSelectedCharacterCount == 1) return;

        var options = new MiSelectable.SelectOptions(false);

        input.m_selectHandler.deselectAll(options);

        input.m_selectHandler.selectAndSetLeader(player.m_selectable, options);

    }


    private bool DefaultHost()

    {

        if(localShipSelection)refreshRosterAt=0;

        var first = Slot(0);

        var second = Slot(1);

        if (first == null || SaveProcessManager.bLoading) return false;
        if(second==null&&roster.Count==1)second=first;
        if(second==null)return false;

        if(ShipCrewSession.Active&&!localShipSelection)
        {first=ShipCrewSession.Host;second=ShipCrewSession.Guest??first;if(first==null||!first.bControllerPlayer()||!second.bControllerPlayer())return false;}

        // On the ship the native leader changes controllers; the previous
        // leader remains player-controlled briefly before returning to a routine.
        // With no peer present this transient pair is NOT a two-player roster.
        if(localShipSelection)
        {
            var leader=Leader();
            if(leader==null||!leader.bControllerPlayer())return false;
            first=second=leader;
            if(!roster.Exists(actor=>actor==leader))return false;
            roster.Clear();roster.Add(leader);
        }

        var departure=localShipSelection?null:lobbyHost.Departure;
        LobbyDeployment.Binding assignment=null;
        if(departure!=null)
        {
            var mission=SceneLookup.Find<MiMissionHandler>();
            // Some missions finish their objectives during the opening script
            // and start directly in Playing_Exit. Their deployed crew still
            // needs the first checkpoint frame while the join pause is held.
            if(SaveProcessManager.bLoading||SaveProcessManager.bProcessing||mission?.currentMissionData==null||
                mission.eCurrentMissionState is not (MiMissionState.Playing_Objectives or MiMissionState.Playing_Exit))return false;
            var live=new List<LobbyDeployment.Live>();
            foreach(var actor in roster)
            {
                if(actor==null||actor.m_characterData?.m_characterDefinition==null)return false;
                live.Add(new LobbyDeployment.Live{definition=LobbyHost.Identity(actor.m_characterData),actor=GuestMove.Identity(actor)});
            }
            assignment=departure.Bind(mission.currentMissionData.guid.iValue,live);
            if(assignment==null){Report("Waiting for the selected mission crew to finish loading.");return false;}
            first=roster.Find(c=>GuestMove.Identity(c)==assignment.host);
            second=roster.Find(c=>GuestMove.Identity(c)==assignment.guest);
        }
        else if(!localShipSelection&&!ShipCrewSession.Active&&receiver!=null&&crewA!=null&&crewB!=null&&hostSelection!=null)
        {
            // Checkpoint export rotates the session, not either player's squad.
            var owned=hostSelection.CopyOwners(roster);
            if(owned!=null&&owned.TryGetValue(GuestMove.Identity(crewA),out char aOwner)&&aOwner=='H'&&
                owned.TryGetValue(GuestMove.Identity(crewB),out char bOwner)&&bOwner=='G')
            {first=crewA;second=crewB;assignment=new LobbyDeployment.Binding{host=GuestMove.Identity(first),guest=GuestMove.Identity(second),owners=owned};}
        }

        knownSlots.Clear();

        knownSlots[first.GetInstanceID()] = roster.FindIndex(c=>c==first)+1; knownSlots[second.GetInstanceID()] = roster.FindIndex(c=>c==second)+1;

        MoveProbePlugin.ProbeLog.LogInfo($"Automatic host pair from {rosterSource}: host {first.name}, guest {second.name}");

        if(!localShipSelection&&Leader()!=null&&(receiver==null||crewA!=first||crewB!=second))SelectAlone(first);

        crewB = second;

        watchedGuard = null;

        bool armed=ArmHost(first,assignment:assignment);
        if(armed&&departure!=null)lobbyHost.ConsumeDeparture(departure);
        return armed;

    }


    private void RestorePair()

    {

        if (Time.realtimeSinceStartup < nextRestore) return;

        nextRestore = Time.realtimeSinceStartup + 2f;

        bool planningReconnect=PlanningSync.Pending||SceneLookup.Find<PlanningMode>()?.bIsActive==true;
        if (!SessionGuestBarrier.AwaitingFrame && (Time.timeScale <= 0f&&!planningReconnect || (MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting))) return;

        var file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ShadowGambitCoop","join-once.txt");

        if (System.IO.File.Exists(file))

        {

            try

            {

                var requested = GuestMove.Room(System.IO.File.ReadAllText(file));

                if (requested == null || DateTime.UtcNow-System.IO.File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1))

                    System.IO.File.Delete(file);

                else

                {

                    GuestExitCollection.Arm(Paths.GameRootPath);

                    var second = Slot(1) ?? Slot(0) ?? Leader();

                    if (second == null) return;

                    System.IO.File.Delete(file);

                    if (sync == null || sync.Room != requested) ConnectGuest(requested,second);

                    return;

                }

            }

            catch (System.IO.IOException) { return; }

            catch (UnauthorizedAccessException) { return; }

        }

        var hostIntent=System.IO.Path.Combine(SessionHost.DirectoryPath,"host-start.txt");
        if(System.IO.File.Exists(hostIntent))
        {
            if(DateTime.UtcNow-System.IO.File.GetLastWriteTimeUtc(hostIntent)>TimeSpan.FromHours(1))System.IO.File.Delete(hostIntent);
            else if(Slot(0)!=null)
            {
                DisconnectGuest();
                if(DefaultHost()){Remember("H");System.IO.File.Delete(hostIntent);return;}
            }
        }

        if (sync != null || receiver != null || MoveProbePlugin.LastPair.Value.Length == 0) return;

        var pair = MoveProbePlugin.LastPair.Value.Split(' ');

        if (pair[0] == "H") { DefaultHost(); return; }

        if (pair.Length != 3 || !LaunchMode.RememberedGuest(HostLaunch,cachedHostIntent,MoveProbePlugin.LastPair.Value)) return;

        var room = GuestMove.Room(pair[2]);
        // First join can observe preparation before any task exists. Never
        // attach gameplay to the guest's unrelated local checkpoint.
        if(MoveProbePlugin.LiveGuestLaunch&&!SessionGuestBarrier.AwaitingFrame&&
            !sessionSceneFollow.Matches(room,lobbyGuest.Current?.world))return;

        var actor = Slot(1) ?? Slot(0) ?? Leader();

        if (room != null && actor != null) ConnectGuest(room,actor);

    }


    private MiCharacter requestedCrew,pendingClaim;
    private readonly GuestCrewSelection guestSelection=new GuestCrewSelection();
    private GuestCrewSelection.Request pendingSelection;

    private bool ChangingCrew => requestedCrew != null || pendingClaim != null;
    private MiPlayerInput hostSelectionInput;
    internal void ObserveGuestInput(MiPlayerInput input)
    {
        sync?.ObserveInput(input);
        // Read the actual native input callback, including avatar clicks.
        // An unselected network anchor need not retain m_playerInput.
        if(receiver!=null&&input?.leader!=null&&hostSelection?.Original(GuestMove.Identity(input.leader))!=null)
            hostSelectionInput=input;
    }
    private string lastGuestSelection;

    private void CheckGuestSelection()

    {

        if (sync == null || !sync.CanSend || sync.BoundCrew == null || (MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting) || Time.timeScale <= 0f) return;

        var current = sync.BoundCrew;
        if(sync.OwnershipRevision>0)
        {
            var input=sync.SelectionInput;
            guestSelection.Context(sync.Room.AbsoluteUri+"|"+SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue);
            guestSelection.Observe(input,current,sync.OwnerCode,sync.KnownCrew,Time.realtimeSinceStartup);
            var members=new List<string>();
            var selected=GuestCrewSelection.Members(input);
            if(selected!=null)for(int i=0;i<selected.Count;i++)members.Add(GuestMove.Identity(selected[i])+":"+sync.OwnerCode(selected[i]));
            string signature=sync.OwnershipRevision+" "+string.Join(",",members);
            if(signature!=lastGuestSelection)
            {lastGuestSelection=signature;MoveProbePlugin.ProbeLog.LogInfo("Guest selection "+signature+" pending="+(pending!=null)+" actors="+actorRequests.Count);}
            if(pending!=null||actorRequests.Count>0)return;
            var request=guestSelection.Next(sync.OwnershipRevision,Time.realtimeSinceStartup,current,sync.Roster,sync.OwnerCode,
                actor=>InteractionSync.Climbing(actor)||sync.CommittedContactFor(actor)||actor.controller?.skillCommandProcess?.m_skill!=null,
                ControlledCrew.Active.Releasable);
            if(request==null)return;
            if(request.Release)foreach(string id in request.Actors)nativeQueue.Release(id);
            pendingSelection=request;orderSentAt=Time.realtimeSinceStartup;
            pending=SendGuestOrder(sync.BoundIdentity,request.Command,sync.OrderToken,sync.TakeTicket(),expectedRevision:request.Revision);
            Report(request.Release?"Releasing idle unselected crew.":"Requesting selected crew control.");
            return;
        }

        if (!current.controllerPlayer.bLeader || current.controllerPlayer.m_playerInput.iSelectedCharacterCount > 1)

        {

            var chosen = Leader();

            if (chosen == null) return;

            SelectAlone(current);

            requestedCrew = null;

            if (sync.ReservedByHost(chosen)) { Report("Host owns this crew member."); return; }

            if (chosen != current) requestedCrew = chosen;

        }

        if (requestedCrew == null || pending != null || actorRequests.Count>0) return;

        var desired = requestedCrew;

        requestedCrew = null;

        pendingClaim = desired;

        nativeQueue.Clear();

        orderSentAt = Time.realtimeSinceStartup;

        pending = SendGuestOrder(GuestMove.Identity(desired),"CLAIM",sync.OrderToken,sync.TakeTicket());

        Report("Requesting crew change.");

    }


    public void Update()
    {
        if(shutdownComplete)return;
        CoopPerformance.Frame();
        using var performance=CoopPerformance.Measure(0);
        SkillLifecycleTrace.Tick();
        SessionWorld.DiagnosticTick();EnemyRewind.Tick();
        bool previous=executingHostFrame;executingHostFrame=true;
        try{UpdateCore();}finally{executingHostFrame=previous;}
    }
    private float nextHostIntentCheck;
    private bool cachedHostIntent;
    private bool TickPreparation()
    {
        using var performance=CoopPerformance.Measure(1);
        string pairing=MoveProbePlugin.LastPair.Value;
        string intent=System.IO.Path.Combine(SessionHost.DirectoryPath,"host-start.txt");
        if(Time.realtimeSinceStartup>=nextHostIntentCheck){
            nextHostIntentCheck=Time.realtimeSinceStartup+.5f;
            cachedHostIntent=System.IO.File.Exists(intent)&&DateTime.UtcNow-System.IO.File.GetLastWriteTimeUtc(intent)<TimeSpan.FromHours(1);
        }
        bool hostIntent=cachedHostIntent;
        bool hosting=sync==null&&(receiver!=null||pairing=="H"||hostIntent);
        Uri room=sync?.Room;
        if(room==null&&!hosting){var fields=pairing.Split(' ');if(fields.Length==3&&fields[0]=="G")room=GuestMove.Room(fields[2]);}
        if(TrainingSession.Tick(hosting,room,Report,()=>{
            sessionLiveImport.Cancel();sessionBootstrap.Cancel();sessionBootstrap.ForgetJoinedWorld();sessionHost.Cancel();
            sessionSceneFollow.Close();lobbyGuest.Close();lobbyHost.Close();sessionHost.ObserveHosting(false);
            DisconnectGuest();CloseReceiver();crewA=crewB=null;
            CoopPings.Clear();PlanningSync.Clear();StorySync.Close();GameSpeedSync.Clear();ShipRoutineSync.Clear();ShipCrewSession.Close();
        },()=>{sessionSceneFollow.Close();nextRestore=0;}))
        {localShipSelection=true;return true;}
        sessionHost.ObserveHosting(hosting);
        if(hosting)ShipCrewSession.Tick(true,sessionHost.HasRecentPeer,null,false);
        lobbyHost.Tick(hosting);
        MissionEntrySync.TickHostEntry(hosting);
        lobbyGuest.Tick(hosting?null:room);
        SessionBootstrap.PreparationFrame=hosting?null:lobbyGuest.Current;
        ShipCrewSession.RequestSelection=hosting?null:lobbyGuest.SelectShip;
        int mission=SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue??0;
        bool transition=sessionLiveImport.Busy||sessionBootstrap.Busy||sessionIndexRefresh.Busy;
        GameSpeedSync.Observe(!hosting&&room!=null&&!transition?lobbyGuest.Current:null,mission);
        GuestMissionPreparation.RequestPick=hosting?null:lobbyGuest.SelectPrepared;
        bool preparing=guestMissionPreparation.Tick(hosting?null:room,lobbyGuest.Current,mission,
            sessionSceneFollow.HasWorld(room),transition,Report);
        if(preparing){CoopPings.Clear();if(sync!=null)DisconnectGuest(true);return true;} // Native preparation owns its UI; no stale gameplay replay.
        if(!hosting)ShipCrewSession.Tick(false,false,lobbyGuest.Current,!transition&&sessionSceneFollow.Matches(room,lobbyGuest.Current?.world));
        if(!UIWorldMap.bOpenStatic)ShipRoutineSync.Tick(hosting);
        StorySync.Tick(hosting,hosting?null:room,transition,lobbyGuest.Current,hosting||sessionSceneFollow.Matches(room,lobbyGuest.Current?.world));
        PlanningSync.Tick(hosting,room,hosting?SessionWorld.Current:lobbyGuest.Current?.world,!transition&&(hosting?receiver!=null&&sessionHost.HasRecentPeer:sync!=null&&!sync.Stopped&&sync.Session!=null&&sessionSceneFollow.Matches(room,lobbyGuest.Current?.world)));
        CoopPings.Tick(hosting,room,hosting?SessionWorld.Current:lobbyGuest.Current?.world,!transition&&(hosting?receiver!=null&&sessionHost.HasRecentPeer:sync!=null&&!sync.Stopped&&sync.Session!=null&&sessionSceneFollow.Matches(room,lobbyGuest.Current?.world)));
        bool queued=sessionSceneFollow.Tick(hosting?null:room,lobbyGuest.Current,mission,sync?.CanSend==true,
            sessionBootstrap.JoinedRoom,sessionBootstrap.JoinedWorld,transition,Report,()=>{
                if(sessionIndexRefresh.Busy||SaveProcessManager.bLoading||SaveProcessManager.bProcessing||SaveProcessManager.bAsyncWriteOperationRunning)return false;
                sessionLiveImport.Cancel();sessionBootstrap.Supersede();return true;
            },()=>{
                if(!GuestMissionPreparation.CanReturnToShip(room,mission)||
                   SaveProcessManager.bLoading||SaveProcessManager.bProcessing||SaveProcessManager.bAsyncWriteOperationRunning)return false;
                MoveProbePlugin.ProbeLog.LogInfo("Guest native HUB return from verified preparation mission="+mission);
                GuestMissionPreparation.BeginShipUiReturn(room);
                try{SessionBootstrap.ReturnToShip();}
                catch{GuestMissionPreparation.EndShipUiReturn();throw;}
                GuestMissionPreparation.ClearShipReturn();return true;
            },()=>{
                bool ready=!SaveProcessManager.bLoading&&!SaveProcessManager.bProcessing&&
                    SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue==ShipCrewSession.Mission&&
                    SceneLookup.Find<Mimimi.HUB.HUBLogic>()?.characters?.m_bRoutineSpawningProcessEnded==true;
                if(ready)GuestMissionPreparation.EndShipUiReturn();
                return ready;
            },nativeTransition:SaveProcessManager.bLoading||SaveProcessManager.bProcessing||SaveProcessManager.bAsyncWriteOperationRunning);
        guestWorldMap.Tick(!hosting&&room!=null,!transition&&sessionSceneFollow.Matches(room,lobbyGuest.Current?.world),lobbyGuest.Current,Report);
        return queued;
    }
    private void UpdateCore()

    {

        try

        {
            NativeUiEvidence.Tick();

            if (Application.isFocused && Input.GetKeyDown(KeyCode.F11))

            { resumeHostAfterDisconnect=resumeHostAfterDisconnect||receiver!=null||MoveProbePlugin.LastPair.Value=="H"||cachedHostIntent||HostLaunch;
              cachedHostIntent=false;string hostStart=System.IO.Path.Combine(SessionHost.DirectoryPath,"host-start.txt");if(System.IO.File.Exists(hostStart))System.IO.File.Delete(hostStart);
              CoopPings.Clear();PlanningSync.Clear();TrainingSession.Leave();sessionLiveImport.Cancel();sessionIndexRefresh.CancelPending();lobbyHost.Close();lobbyGuest.Close();sessionHost.Cancel(); sessionBootstrap.Cancel(); Remember(""); DisconnectGuest(); CloseReceiver(); crewA = crewB = null; GameSpeedSync.Clear(); StorySync.Close(); Report("Disconnected; pairing forgotten."); return; }

            if(hostResumeRequested||(Application.isFocused&&Input.GetKeyDown(KeyCode.F7))){hostResumeRequested=false;ResumeHost();return;}
            NativeSkillProbe.Tick();
            MissionEntryTrace.TickShip();
            localShipSelection=sync==null&&!sessionHost.HasRecentPeer&&
                SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue==-379511569;

            if(menuReconnectRequested||(Application.isFocused&&Input.GetKeyDown(KeyCode.F6)&&InReconnectMenu()&&GuestReconnectAllowed)){menuReconnectRequested=false;ReconnectFromMenu();return;}
            if(TickPreparation())return;
            // A HUB mission ID becomes visible before its characters/UI finish
            // initialization. Never unload that partially initialized scene.
            bool nativeSceneReady=SceneLookup.Find<MiMissionHandler>()?.currentMissionData?.guid.iValue!=ShipCrewSession.Mission||
                SceneLookup.Find<Mimimi.HUB.HUBLogic>()?.characters?.m_bRoutineSpawningProcessEnded==true;
            if(sessionLiveImport.Tick(()=>{DisconnectGuest();CloseReceiver();crewA=crewB=null;},Report,sessionIndexRefresh.Busy||sessionBootstrap.Busy,nativeSceneReady))return;
            MissionEntrySync.TickContinue();
            if(!sessionLiveImport.Busy&&sessionIndexRefresh.Tick(Report))return;
            if(sessionBootstrap.Tick(()=>{DisconnectGuest();CloseReceiver();crewA=crewB=null;},Report,()=>Slot(0)!=null))return;
            if(SessionGuestBarrier.TickFrameWait()){DisconnectGuest();Report("Join failed before first frame. Please rejoin.");return;}
            if(sessionHost.Tick(sync==null&&Slot(0)!=null&&(receiver!=null||MoveProbePlugin.LastPair.Value.StartsWith("H")),Report))
            {
                if(sessionHost.ServingCheckpoint)
                {
                    if(checkpointFrameGeneration!=sessionHost.Generation&&DefaultHost())checkpointFrameGeneration=sessionHost.Generation;
                    if(checkpointFrameGeneration==sessionHost.Generation)
                        receiver?.Poll(()=>false,_=>false,_=>false,Snapshot,ObserveGuestRead);
                }
                return;
            }

            // A native HUB transition can unload the mission without setting
            // SaveProcessManager.bLoading. Old crew wrappers survive briefly,
            // but must not be sampled or accept commands in that interval.
            // Lobby and checkpoint coordinators above remain active.
            if(SaveProcessManager.bLoading||SceneLookup.Find<MiMissionHandler>()?.currentMissionData==null)
            {
                if(receiver!=null){CloseReceiver();crewA=crewB=null;}
                return;
            }

            FinishGuestPoint();


            applyingHostState = true;

            int beforeWait = sync?.WaitGeneration ?? 0;

            try { sync?.Tick(); }

            finally { applyingHostState = false; }

            if (sync != null && beforeWait != sync.WaitGeneration)

            {

                nativeQueue.Clear();

                requestedCrew = null;

                pressFrame = -1;

            }

            if (sync?.Stopped == true) DisconnectGuest();

            if (saveGuestPair && sync != null && sync.CanSend)

            {

                Remember("G " + sync.BoundIdentity + " " + sync.Room.AbsoluteUri);

                saveGuestPair = false;

            }

            RestorePair();

            CheckGuestSelection();

            PumpNativeInput();

            // Wait out the complete mouse gesture; LateUpdate gets first say on

            // any release that could withdraw the still-cancellable attack.

            if (sync != null && pending == null && nativeQueue.Count == 0 && !ChangingCrew &&

                pressFrame < 0 && !NativeInput.MouseEdge() && !Input.GetMouseButton(0) && !Input.GetMouseButton(1) &&

                sync.Owns(Leader()) && Time.timeScale > 0f && !(MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting))

            {

                string confirm = sync.ContactReply();

                if (confirm != null)

                {

                    orderSentAt = Time.realtimeSinceStartup;

                    contactRequest = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(sync.OrderToken);

                    pending = SendGuestOrder(sync.BoundIdentity,confirm,contactRequest.Token,sync.TakeTicket(),fast:true);

                    MoveProbePlugin.ProbeLog.LogInfo("Guest confirmed visible melee contact " + confirm);

                }

            }

            if (receiver != null)

            {

                RefreshRoster();
                if(ShipCrewSession.Active&&!localShipSelection)
                {
                    if(crewA!=ShipCrewSession.Host||crewB!=(ShipCrewSession.Guest??ShipCrewSession.Host))DefaultHost();
                }
                else if(localShipSelection)
                {
                    var shipLeader=Leader();
                    if(shipLeader!=null&&(crewA!=shipLeader||!HostSpectator))DefaultHost();
                }
                else if(roster.Count==1&&(!HostSpectator||crewA!=roster[0]) || HostSpectator&&roster.Count>=2)DefaultHost();
                if (crewA == null || crewB == null) CloseReceiver();

                else

                {

                    try {

                        if(sessionHost.HasRecentPeer)RefreshControlledCrew();

                        // Deselection can detach the old anchor from input.
                        // Observe the native leader's input, not the last network anchor.
                        var input = hostSelectionInput ?? Leader()?.controllerPlayer?.m_playerInput ?? crewA.controllerPlayer?.m_playerInput;

                        var chosen = input?.leader;

                        // Keep the original primary snapshot anchors stable.
                        // Native squad selection is independent of room binding.
                        if(!localShipSelection&&sessionHost.HasRecentPeer&&!hostWait.WorldHeld&&!(MiCutsceneHandler.bCutsceneRunning || StorySync.Waiting))
                        {
                            hostSelection?.Reconcile(input);
                            // Ownership follows the selected squad. Native
                            // idle/GoTo process slots can remain populated and
                            // must not reserve a deselected shipmate forever.
                            var anchor=hostSelection?.FollowHostSelection(input,_=>false);
                            if(anchor!=null&&anchor!=crewA)
                            {crewA=anchor;cueA=new AttackCue();hostInteractions.RebindFirst();
                                MoveProbePlugin.ProbeLog.LogInfo("Host primary follows selected crew "+GuestMove.Identity(anchor));}
                        }

                        if (sessionHost.HasRecentPeer && !hostWait.WorldHeld)
                        {
                            cueA.Observe(crewA);RefreshRoster();
                            foreach(var actor in syncedRoster)if(hostSelection?.IsGuest(actor)==true)OrdersFor(actor).Cue.ObservePoint(actor);
                            hostRoster.Observe(syncedRoster,crewA,crewB);
                        }

                        receiver.Poll(IssueRemoteMove, IssueStep, IssuePoint, Snapshot,ObserveGuestRead); }

                    catch (System.Net.Sockets.SocketException ex) { CloseReceiver(); Report($"Loopback unavailable: {ex.SocketErrorCode}"); }

                    catch (Exception ex) { if(Time.realtimeSinceStartup>=nextHostError) { nextHostError=Time.realtimeSinceStartup+2f; MoveProbePlugin.ProbeLog.LogError(ex); Report("Host update failed; retrying without disconnecting."); } }

                }

            }

            if (receiver != null) TickHostPace(); // Drain applied-frame receipts before considering a pause.
            foreach(var orders in hostOrders.Values)
                if(!HostSpectator)orders.TickMovement(hostWait.WorldHeld,hostCrewWaits.Suspended(orders.Actor));

            if (hostWait.WorldHeld) return; // Network polling and F11 continue on unscaled frames.

            foreach(var orders in hostOrders.Values)orders.TickActions();

            if (!Application.isFocused) return;

            if (Input.GetKeyDown(KeyCode.F6)&&GuestReconnectAllowed)

            {

                if (pending != null) { Report("Waiting for host."); return; }

                var room = GuestMove.Room(GUIUtility.systemCopyBuffer ?? "");

                var guest = Slot(1) ?? Slot(0) ?? Leader();

                if (room == null || guest == null) { Report("Select guest crew and copy the complete host room URL first."); return; }

                ConnectGuest(room, guest);

                return;

            }

            if (sync != null && Input.GetKeyDown(KeyCode.F10)) { SendGuestPoint(false); return; }

            if (NativeGuest) return;

            if (sync != null && Input.GetKeyDown(KeyCode.LeftBracket)) { SendGuestPoint(true); return; }

            if (sync != null && Input.GetKeyDown(KeyCode.K)) { SendAttack("K"); return; }

            if (sync != null && Input.GetKeyDown(KeyCode.L)) { SendAttack("L"); return; }

            if (sync != null && Input.GetKeyDown(KeyCode.J)) { SendAttack("J"); return; }

            if (sync != null) return;

            if (Input.GetKey(KeyCode.LeftAlt) && (Input.GetKeyDown(KeyCode.K) || Input.GetKeyDown(KeyCode.L) || Input.GetKeyDown(KeyCode.J)))

            {

                var skill = Input.GetKeyDown(KeyCode.J) ? Melee.Special(crewB) :

                    Input.GetKeyDown(KeyCode.K) ? Skill.SkillType.Knockout : Skill.SkillType.Kill;

                RemoteOrders?.HostAttack(Melee.Pick(), skill);

                return;

            }

            if (Input.GetKeyDown(KeyCode.F4)) { WatchGuard(); return; }

            if(!Input.GetKeyDown(KeyCode.F9))return;
            if(MiCutsceneHandler.bCutsceneRunning||StorySync.Waiting||Time.timeScale<=0f)return;
            var leader=Leader();
            if(receiver==null||crewA==null||leader!=crewA){Report("F7 恢复房主联机。 / F7 resumes hosting.");return;}

            IssueMove("Hotkey", destination);

        }

        catch (Exception ex)

        {

            MoveProbePlugin.ProbeLog.LogError(ex);

            Report("Probe error; see BepInEx/LogOutput.log.");

            enabled = false;

        }

    }

}

