$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$checks=0
foreach($variant in 'two-player','multiplayer'){
    $path=Join-Path $repo "src/$variant/session-next/prepare-guest.ps1"
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw "Invalid PowerShell: $path"}
    $assignment=$ast.Find({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$steamId'},$true)
    if(!$assignment){throw 'Missing SteamID64 conversion'}
    # Execute the production arithmetic only; no Steam registry, saves or game.
    # The fixed base describes public Steam ID encoding, not a personal ID.
    $convert=[scriptblock]::Create($assignment.Right.Extent.Text)
    foreach($case in @(@(0,[uint64]76561197960265728),@(42,[uint64]76561197960265770),@([uint32]::MaxValue,[uint64]76561202255233023))){
        $active=[uint64]$case[0]
        if((& $convert) -ne $case[1]){throw "$variant SteamID64 conversion failed for synthetic account ID $active"}
        $checks++
    }
}
"Steam account conversion checks passed: $checks; public constants and synthetic IDs only."
