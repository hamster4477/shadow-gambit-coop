$ErrorActionPreference='Stop'
$source=[IO.File]::ReadAllText((Join-Path (Split-Path $PSScriptRoot) 'SnapshotTransport.cs'))
$fixture=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SnapshotAssemblyTests.cs'))
Add-Type -TypeDefinition ($source+"`n"+$fixture)
"Snapshot fragment checks passed: $([SnapshotAssemblyTests]::Run())."
