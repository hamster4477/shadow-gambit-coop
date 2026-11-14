$ErrorActionPreference='Stop'
$base=Split-Path $PSScriptRoot
Add-Type -Path @((Join-Path $base 'SessionLiveImport.cs'),(Join-Path $PSScriptRoot 'GuestSavePrepare.cs'),(Join-Path $PSScriptRoot 'FrameworkJsonCompat.cs'),(Join-Path $PSScriptRoot 'LiveImportTests.cs'))
$root=Join-Path $PSScriptRoot ('runtime/live-import-test-'+[Guid]::NewGuid().ToString('N'))
$share=[LiveImportTests]::CreateSyntheticShare((Join-Path $root 'synthetic-share'))
"Live import checks passed: $([LiveImportTests]::Run($root,$share)). Production installer/coordinator; native API stubs and synthetic save bytes."
