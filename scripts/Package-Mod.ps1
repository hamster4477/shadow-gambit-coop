param(
    [ValidateSet('two-player','multiplayer')][string]$Variant='two-player',
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$Python='python'
)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Build.ps1') -Variant $Variant -GameDirectory $GameDirectory
& $Python (Join-Path $PSScriptRoot 'package_mod.py') --variant $Variant
if($LASTEXITCODE -ne 0){throw 'Mod-only packaging failed.'}
