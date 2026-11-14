$ErrorActionPreference='Stop'
$base=Split-Path $PSScriptRoot
Add-Type -Path @((Join-Path $base 'EnemyRewind.cs'),(Join-Path $PSScriptRoot 'EnemyRewindTests.cs')) -CompilerOptions '/nowarn:0414'
"Enemy rewind context checks passed: $([EnemyRewindTests]::Run()). Production hooks/state handling; native callbacks simulated."
