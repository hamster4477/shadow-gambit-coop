$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$source="using System;`nusing System.Collections.Generic;`nusing System.Linq;`nusing HarmonyLib;`n"
foreach($path in @((Join-Path $PSScriptRoot 'CrewOwnership.cs'),(Join-Path $root 'HostCrewSelection.cs'))){$source+=([IO.File]::ReadAllText($path)-replace '(?m)^using [^;]+;\r?\n','')+"`n"}
$source+=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'MultiOwnershipTests.cs'))
Add-Type -TypeDefinition $source -IgnoreWarnings -WarningAction SilentlyContinue
"Multiplayer ownership checks passed: $([MultiOwnershipTests]::Run()). Production ledger and host adapter; native selection is simulated."
