$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler was not found.' }
$output = Join-Path $PSScriptRoot 'SteamLauncherHost.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll (('/out:' + $output)) (Join-Path $PSScriptRoot 'SteamLauncherHost.cs')
if ($LASTEXITCODE -ne 0) { throw 'Steam launcher compilation failed.' }
$bytes = [IO.File]::ReadAllBytes($output)
$pe = [BitConverter]::ToInt32($bytes,0x3c)
if ([BitConverter]::ToUInt16($bytes,$pe+24+68) -ne 2) { throw 'Launcher must use the Windows GUI subsystem.' }
Write-Output 'PASS SteamLauncherHost.exe built with GUI subsystem (no console window)'
