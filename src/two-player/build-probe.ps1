param([string]$GameDirectory=$env:SHADOW_GAMBIT_GAME_DIR, [switch]$Stage, [ValidatePattern("^[A-Za-z0-9._-]+[.]dll$")][string]$StageName="ShadowGambitCoop.MoveProbe.dll")

$ErrorActionPreference = 'Stop'
if (!$GameDirectory) { throw 'Pass -GameDirectory or set SHADOW_GAMBIT_GAME_DIR to your local game installation.' }
$game = [IO.Path]::GetFullPath($GameDirectory)
if (!(Test-Path -LiteralPath (Join-Path $game 'BepInEx/interop') -PathType Container)) { throw 'Generate the local BepInEx interop cache first. Game files are not included in this repository.' }
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
$refs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($folder in 'dotnet','BepInEx/core','BepInEx/interop') {
    foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $game $folder) -Filter '*.dll')) {
        try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) }
        catch [BadImageFormatException] { continue }
        $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
    }
}
$trees = foreach ($name in 'MoveProbe.cs','PingWire.cs','CoopPingVisual.cs','CoopPings.cs','CoopPerformance.cs','StoryObservation.cs','TrainingSession.cs','PlanningWire.cs','PlanningNative.cs','PlanningSync.cs','StoryReplayLedger.cs','GameSpeedSync.cs','SceneLookup.cs','SceneLookupSlot.cs','CoopInvite.cs','HostCrewSelection.cs','GuestCrewSelection.cs','session-next/CrewOwnership.cs','HostCrewOrders.cs','LaunchMode.cs','LobbyProtocol.cs','LobbyHost.cs','LobbySelectionHooks.cs','LobbyDeployment.cs','LobbyGuest.cs','SessionBootstrap.cs','SessionHost.cs','SessionGuestBarrier.cs','GuestExitCollection.cs','PeerPace.cs','CoopWaitLock.cs','CrewWaitGroup.cs','LoopbackMove.cs','GuestMove.cs','LanStateFeed.cs','SnapshotTransport.cs','CrewRosterProtocol.cs','ActorOrderReceipt.cs','GuestActorIntent.cs','GuestGestureState.cs','CrewOwnershipReplica.cs','CrewFollower.cs','CrewRoutePlayback.cs','CrewRosterSync.cs','CrewSync.cs','Melee.cs','GuestMouseSnapshot.cs','GuestLadderSnapshot.cs','NativeInput.cs','GuardDiagnostics.cs','SyncProtocol.cs','LoopbackProtocol.cs','GuestCommandQueue.cs','GuestActorRequests.cs','GuestOrderVersions.cs','GroundClickPolicy.cs','GuestGroundTarget.cs','CombatPlayback.cs','CoverSync.cs','FootprintSync.cs','FootprintProtocol.cs','ReinforcementSync.cs','GuardPursuit.cs','GuardIcons.cs','GuardAuthority.cs','GuardOutcomes.cs','NativeGait.cs','BodySync.cs','GuestDisposal.cs','GuestPreview.cs','InteractionProtocol.cs','InteractionSync.cs') {
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $name))
    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)
}
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CharmingGhostSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CharmingGhostPlayback.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CharmingGhostNative.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ControlPlayback.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ControlNative.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ControlStateSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'NativeSkillHooks.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'GuestSkillPreview.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'MimicChoice.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'MimicNative.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'MimicStateSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SessionIndexRefresh.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ControlledCrew.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SkillObjectSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SkillObjectRegistry.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'BaitObjectSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'TrapObjectSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'GhostObjectSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SkillResourceSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'TimeFreezeSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'EffectProcessClock.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'NativeDetectionProfile.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'BlindSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FrozenProcessClock.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FrozenSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FrozenDuelSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'DigStateSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CannonStateSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CannonLaunchSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CannonFlightSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CannonFlightProtocol.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'NativeSkillProbe.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ShipCrewLease.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ShipCrewSession.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'MovementLineLoadRepair.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HostLoadingView.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ShipRoutineProtocol.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ShipRoutineSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FishingPullSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FlightCastRecovery.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FishingPullProtocol.cs')))
foreach($name in 'SessionLiveImport.cs','SessionQuickReload.cs','SessionTaskContinue.cs','SessionSceneFollow.cs','SceneEpoch.cs','SessionWorld.cs','StorySync.cs','EnemyRewind.cs','MissionEntryTrace.cs','MissionEntrySync.cs','NativeUiEvidence.cs','GuestWorldMap.cs','GuestMissionPreparation.cs','session-next/GuestSavePrepare.cs','session-next/FrameworkJsonCompat.cs') {
    $trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot $name)))
}
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FishingItemSync.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FishingItemProtocol.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FlightPageBudget.cs')))
$trees += [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SkillLifecycleTrace.cs')))
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$options = $options.WithDeterministic($true)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('ShadowGambitCoop.MoveProbe', [Microsoft.CodeAnalysis.SyntaxTree[]]$trees, $refs, $options)
$memory = [IO.MemoryStream]::new()
try {
    $result = $compilation.Emit($memory)
    if (!$result.Success) { throw (($result.Diagnostics | ForEach-Object ToString) -join [Environment]::NewLine) }
    $output = Join-Path $game 'BepInEx/plugins/ShadowGambitCoop.MoveProbe.dll'
    if ($Stage) {
        $stageDirectory = Join-Path $PSScriptRoot 'staging'
        [void][IO.Directory]::CreateDirectory($stageDirectory)
        $output = Join-Path $stageDirectory $StageName
    }
    [IO.File]::WriteAllBytes("$output.new", $memory.ToArray())
    # Validate the emitted DLL before replacing either the staged or installed
    # build. Source-only adapter tests cannot detect a missing startup hook.
    & (Join-Path $PSScriptRoot 'verify-skill-hooks.ps1') -Plugin "$output.new" -GameDirectory $game
    [IO.File]::Move("$output.new", $output, $true)
    Get-Item -LiteralPath $output | Select-Object FullName,Length
} finally {
    $memory.Dispose()
}

