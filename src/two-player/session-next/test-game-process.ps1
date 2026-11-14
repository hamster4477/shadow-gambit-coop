$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot) 'steam-launch-common.ps1')
Add-Type -TypeDefinition @'
using System;
public class GameProcessFixture:IDisposable {
 public int Id=22852,Error;public bool Exited,Disposed;public string Path="",StartTime="";
 public void Refresh(){if(Error!=0)throw new System.ComponentModel.Win32Exception(Error);}
 public bool HasExited {get{return Exited;}}
 public void Dispose(){Disposed=true;}
}
'@
$script:samples=@();$script:readCount=0
$script:census=@();$script:censusCount=0
function Get-CoopProcessCensus([int]$ProcessId){if($script:census.Count){$index=[Math]::Min($script:censusCount,$script:census.Count-1);$script:censusCount++;return $script:census[$index]}}
function Set-Census($values){$script:census=@($values);$script:censusCount=0}
function Get-Process {param($Name,$ErrorAction) $script:readCount++; if($script:samples.Count){$script:samples[[Math]::Min($script:readCount-1,$script:samples.Count-1)]} }
function Set-Samples($values){$script:samples=@($values);$script:readCount=0}
$checks=0
function Check($value,$why){if(!$value){throw $why};$script:checks++}
$gone=New-Object GameProcessFixture;$gone.Exited=$true
Set-Samples @($gone);Check (@(Get-CoopGameProcess).Count -eq 0 -and $gone.Disposed) 'Exited listed process must not block'
$live=New-Object GameProcessFixture
Set-Samples @($live);$result=@(Get-CoopGameProcess);Check ($result.Count -eq 1 -and $result[0].State -eq 'running') 'Invisible live process still blocks'
$denied=New-Object GameProcessFixture;$denied.Error=5
Set-Samples @($denied);$result=@(Get-CoopGameProcess);Check ($result.Count -eq 1 -and $result[0].State -eq 'unknown' -and $result[0].Error -match '5') 'Unreadable process must be diagnosed, not treated as exited'
$vanished=New-Object GameProcessFixture;$vanished.Error=87
Set-Samples @($vanished);Check (@(Get-CoopGameProcess).Count -eq 0) 'Process gone between enumeration and query is safe to retry'
$root=Join-Path $PSScriptRoot ('runtime/process-check-'+[Guid]::NewGuid().ToString('N'))
Set-Samples @($live,$gone);Assert-CoopGameStopped $root -WaitMilliseconds 1000
Check ($script:readCount -eq 2) 'Exiting process should finish without another user launch'
$record=Get-Content (Join-Path $root 'session-next/runtime/steam-launch/last-game-process.json') -Raw|ConvertFrom-Json
Check ($record.Count -eq 2 -and @($record[-1].processes).Count -eq 0) 'Capture transition evidence'
Set-Samples @($denied);$message='';try{Assert-CoopGameStopped $root -WaitMilliseconds 0}catch{$message=$_.Exception.Message}
Check ($message -match '22852' -and $message -match 'unknown') 'Blocked dialog must identify actual candidate and uncertainty'
Set-Samples @();Assert-CoopGameStopped $root -WaitMilliseconds 0
Check ($script:readCount -eq 1) 'No game should proceed immediately'
$empty=[pscustomobject]@{ProcessId=22852;Name='ShadowGambit_TCC.exe';CreationDate=[datetime]'2026-11-10T21:31:15';ThreadCount=0;HandleCount=0}
Set-Census @($empty);Set-Samples @($live)
Check (@(Get-CoopGameProcess).Count -eq 0 -and $script:censusCount -eq 2) 'Same residual with no threads or handles is confirmed twice and does not block'
Set-Census @($empty);Set-Samples @($live);Assert-CoopGameStopped $root -WaitMilliseconds 0
$record=Get-Content (Join-Path $root 'session-next/runtime/steam-launch/last-game-process.json') -Raw|ConvertFrom-Json
Check ($record[0].processes[0].State -eq 'exited-residual') 'Residual evidence survives successful launch preflight'
$active=$empty.PSObject.Copy();$active.ThreadCount=1
Set-Census @($empty,$active);Set-Samples @($live)
Check (@(Get-CoopGameProcess).Count -eq 1) 'A process gaining threads during census still blocks'
$reused=$empty.PSObject.Copy();$reused.CreationDate=$empty.CreationDate.AddSeconds(1)
Set-Census @($empty,$reused);Set-Samples @($live)
Check (@(Get-CoopGameProcess).Count -eq 1) 'PID reuse cannot inherit a prior residual verdict'
$unknown=$empty.PSObject.Copy();$unknown.ThreadCount=$null
Set-Census @($unknown);Set-Samples @($live)
Check (@(Get-CoopGameProcess).Count -eq 1) 'Missing thread count is not zero threads'
Set-Census @();Set-Samples @($denied)
Check (@(Get-CoopGameProcess).Count -eq 1) 'Access denial without independent evidence still blocks'
"Game process checks passed: $checks; production launcher policy with process lifecycle fixtures."
