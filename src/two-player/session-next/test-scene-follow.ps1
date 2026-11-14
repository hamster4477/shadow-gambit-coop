$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
Add-Type -Path @((Join-Path $root 'SceneEpoch.cs'),(Join-Path $root 'SessionSceneFollow.cs'),(Join-Path $root 'LobbyProtocol.cs'),(Join-Path $PSScriptRoot 'SceneFollowTests.cs')) -CompilerOptions '/nowarn:0649,0414'
"Scene follow checks passed: $([SceneFollowTests]::Run((Join-Path $PSScriptRoot 'runtime/scene-follow-tests'))). Production epoch/policy/mailbox; native time simulated."
