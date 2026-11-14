param(
    [ValidateSet('two-player','multiplayer')][string]$Variant='two-player',
    [Parameter(Mandatory=$true)][string]$GameDirectory
)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'Run this script with PowerShell 7 (pwsh).'}
$repo=Split-Path $PSScriptRoot
$source=Join-Path $repo "src/$Variant"
$output=Join-Path $repo "artifacts/build/$Variant"
$gameRoot=[IO.Path]::GetFullPath($GameDirectory)
foreach($dependency in 'dotnet','BepInEx/core','BepInEx/interop'){
    if(!(Test-Path -LiteralPath (Join-Path $gameRoot $dependency) -PathType Container)){
        throw "Missing local dependency: $dependency. Install the matching framework and generate local bindings first."
    }
}
[void][IO.Directory]::CreateDirectory($output)
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $output -Recurse -Force
& pwsh -NoProfile -File (Join-Path $output 'build-probe.ps1') -GameDirectory $gameRoot -Stage -StageName 'ShadowGambitCoop.MoveProbe.dll'
if($LASTEXITCODE -ne 0){throw 'Plugin build or native hook audit failed.'}
& pwsh -NoProfile -File (Join-Path $output 'build-steam-launcher.ps1')
if($LASTEXITCODE -ne 0){throw 'Launcher build failed.'}
Write-Output "Built $Variant into $output. The installed game was not changed."
