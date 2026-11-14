# Compatible with Windows PowerShell 5.1. File checks only: no game/network changes.

param(

    [ValidateSet('Check','Collect')][string]$Mode = 'Check',

    [ValidateSet('Host','Guest','Unknown')][string]$Role = 'Unknown',

    [string]$GameRoot = (Split-Path $PSScriptRoot),

    [switch]$OnExit,

    [string]$CopyDirectory

)

$ErrorActionPreference = 'Stop'

$GameRoot = [IO.Path]::GetFullPath($GameRoot)

$project = Join-Path $GameRoot 'ShadowGambitCoop'

$expectedPath = Join-Path $PSScriptRoot 'observation-build.json'

$issues = [Collections.Generic.List[string]]::new()



function Read-SharedText([string]$Path) {

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)

    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)

    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }

}



function File-Sha256([string]$Path) {

    $algorithm = [Security.Cryptography.SHA256]::Create()

    $stream = [IO.File]::OpenRead($Path)

    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }

    finally { $stream.Dispose(); $algorithm.Dispose() }

}



function Config-Value([string]$Text, [string]$Section, [string]$Key) {

    $current = ''

    foreach ($line in ($Text -split '\r?\n')) {

        $value = $line.Trim()

        if ($value -match '^\[(.+)\]$') { $current = $Matches[1]; continue }

        if ($current -eq $Section -and $value -match ('^' + [regex]::Escape($Key) + '\s*=\s*(true|false)\s*$')) {

            return $Matches[1].ToLowerInvariant()

        }

    }

    return 'default_or_unknown'

}



# Best-effort public-log redaction; review the archive before posting publicly.
function ConvertTo-CoopPublicLog {
    param([AllowNull()][string]$Text, [string]$FileName = '')
    if ($null -eq $Text) { return '' }
    if ($FileName -match '^(?:udp-endpoint|last-udp-address|last-address|last-network)\.txt$') {
        return '[NETWORK ENDPOINT REDACTED]'
    }
    $value = [regex]::Replace($Text, '(?i)\b(?:https?|tcp|udp)://[^\s"''<>]+', '[URL REDACTED]')
    # Preserve keys, quoting and line structure, including JSON diagnostics.
    $credentialPattern = '(?im)(?<prefix>(?<![\w-])["'']?(?:room|lobbyRoom|roomUrl|roomToken|sessionToken|access[_-]?token|refresh[_-]?token|auth[_-]?token|token|secret|inviteSecret|inviteCode|password|passwd|api[_-]?key|authorization|cookie|peer|client|steamId|accountId|username)["'']?\s*[:=]\s*)(?<value>"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*''|\[[A-Z ]*REDACTED\]|[^\s,;\}\]]+)'
    $value = [regex]::Replace($value, $credentialPattern, [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        $original = $match.Groups['value'].Value
        $quote = if ($original.StartsWith('"')) { '"' } elseif ($original.StartsWith("'")) { "'" } else { '' }
        return $match.Groups['prefix'].Value + $quote + '[REDACTED]' + $quote
    })
    $value = [regex]::Replace($value, '(?im)^(?<prefix>\s*(?:Authorization|Proxy-Authorization)\s*[:=]\s*)[^\r\n]+', '${prefix}[REDACTED]')
    # Any drive, slash direction or JSON-escaped backslash; leave DLL filenames.
    $value = [regex]::Replace($value, '(?i)(?<prefix>\b[A-Z]:(?:\\{1,2}|/)Users(?:\\{1,2}|/))[^\\/\r\n"<>]+', '${prefix}[USER REDACTED]')
    $value = [regex]::Replace($value, '(?i)(?<prefix>/(?:home|Users)/)[^/\r\n"<>]+', '${prefix}[USER REDACTED]')
    $value = [regex]::Replace($value, '(?i)\buser_steam_[0-9]{10,20}\b', 'user_steam_[ACCOUNT REDACTED]')
    $value = [regex]::Replace($value, '(?<![A-Za-z0-9])7656119[0-9]{10}(?![A-Za-z0-9])', '[STEAM ID REDACTED]')
    $value = [regex]::Replace($value, '(?i)(?<![A-Za-z0-9_.])(?:[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\.)+[A-Za-z0-9-]+:[0-9]{1,5}(?![0-9])', '[NETWORK ENDPOINT REDACTED]')
    $value = [regex]::Replace($value, '(?i)(?<prefix>\b(?:ip|ipv4|localAddress|remoteAddress|bindAddress|hostAddress)\s*["'']?\s*[:=]\s*["'']?)(?:[0-9]{1,3}\.){3}[0-9]{1,3}', '${prefix}[IP REDACTED]')
    return $value
}

$required = @('winhttp.dll','doorstop_config.ini','BepInEx/core/BepInEx.Unity.IL2CPP.dll','BepInEx/plugins/ShadowGambitCoop.MoveProbe.dll')

$missing = @($required | Where-Object { !(Test-Path -LiteralPath (Join-Path $GameRoot $_) -PathType Leaf) })

if ($missing.Count) { $issues.Add('Required files missing. Install the Full package first.') }

$plugin = Join-Path $GameRoot 'BepInEx/plugins/ShadowGambitCoop.MoveProbe.dll'

$pluginHash = if (Test-Path -LiteralPath $plugin -PathType Leaf) { File-Sha256 $plugin } else { $null }

$expected = if (Test-Path -LiteralPath $expectedPath) { Get-Content -LiteralPath $expectedPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }

$matchesExpected = $null

if ($expected -and $pluginHash) {

    $matchesExpected = $pluginHash -eq $expected.dllSha256

    if (!$matchesExpected) { $issues.Add('Installed DLL differs from the prepared build. This does not by itself mean corruption.') }

} else { $issues.Add('Cannot verify the installed DLL against the prepared build.') }

$doorstop = Join-Path $GameRoot 'doorstop_config.ini'

$loaderEnabled = 'unknown'

if (Test-Path -LiteralPath $doorstop) {

    $loaderEnabled = Config-Value (Read-SharedText $doorstop) 'General' 'enabled'

    if ($loaderEnabled -eq 'false') { $issues.Add('Doorstop loader is disabled.') }

}

$configPath = Join-Path $GameRoot 'BepInEx/config/local.shadowgambit.moveprobe.cfg'

$configText = if (Test-Path -LiteralPath $configPath) { Read-SharedText $configPath } else { '' }

$legacy = Config-Value $configText 'Input' 'LegacyGuestHotkeys'

$observation = Config-Value $configText 'Diagnostics' 'GuardObservation'

if ($legacy -eq 'true') { $issues.Add('Legacy guest hotkeys are enabled; normal-control testing will not use the default path.') }

if ($observation -eq 'false') { $issues.Add('Guard observation is disabled; no new guard evidence will be captured.') }

$candidates = @(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'BepInEx/plugins') -Filter '*Coop*.dll' -Recurse -File -ErrorAction SilentlyContinue)

if ($candidates.Count -gt 1) { $issues.Add('Multiple Coop-named plugin DLLs found. Check for duplicate plugin copies.') }



$logPath = Join-Path $GameRoot 'BepInEx/LogOutput.log'

$logText = if (Test-Path -LiteralPath $logPath) { Read-SharedText $logPath } else { '' }

$starts = [regex]::Matches($logText, 'Loading \[Coop Move Probe ([0-9.]+)\]')

$lastVersion = $null

$currentLog = $logText

if ($starts.Count) {

    $last = $starts[$starts.Count - 1]

    $lastVersion = $last.Groups[1].Value

    $currentLog = $logText.Substring($last.Index)

} else { $issues.Add('No plugin startup marker in the current log. Start the game, then check again.') }

if ($lastVersion -and $expected -and $lastVersion -ne $expected.version) {

    $issues.Add('Last logged startup uses a different version. The log may predate an update; restart and recheck.')

}

$markers = [ordered]@{

    nativeInput = $currentLog.Contains('Input: native commands')

    legacyOrFallback = ($currentLog.Contains('Input: legacy keys') -or $currentLog.Contains('Native input hooks unavailable'))

    inventory = $currentLog.Contains('"kind":"inventory_end"')

    hostGuardSamples = $currentLog.Contains('"kind":"guard_host"')

    guestGuardSamples = $currentLog.Contains('"kind":"guard_guest"')

    diagnosticFailure = $currentLog.Contains('Guard diagnostics stopped')

    diagnosticLimit = $currentLog.Contains('Guard diagnostics record limit reached')

}

if ($markers.diagnosticFailure) { $issues.Add('A guard capture failed. Preserve the log; a missing sample is not a pass.') }

if ($markers.diagnosticLimit) { $issues.Add('Observation record limit reached. Collect logs before restarting.') }

$python = Get-Command python -ErrorAction SilentlyContinue

$pythonCommandPresent = (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'python/python.exe')) -or $null -ne $python

if ($Role -eq 'Host' -and !$pythonCommandPresent) { $issues.Add('Host gateway needs Python 3; no python command was found. The guest does not need Python for gameplay.') }

$report = [ordered]@{

    schema = 1; collectedUtc = [DateTime]::UtcNow.ToString('o'); role = $Role

    expectedVersion = if ($expected) { $expected.version } else { $null }

    installedDllSha256 = $pluginHash; installedMatchesPreparedBuild = $matchesExpected

    missingFiles = $missing; loaderEnabled = $loaderEnabled

    settings = [ordered]@{ legacyGuestHotkeys = $legacy; guardObservation = $observation }

    coopNamedDllCount = $candidates.Count; lastLoggedVersion = $lastVersion

    lastLogWriteUtc = if (Test-Path -LiteralPath $logPath) { (Get-Item -LiteralPath $logPath).LastWriteTimeUtc.ToString('o') } else { $null }

    markers = $markers; pythonCommandPresent = $pythonCommandPresent; issues = $issues.ToArray()

    limits = @('File and last-log evidence only; not a live health check.',

        'No connectivity, firewall, save compatibility, or multiplayer correctness is tested.',

        'Default_or_unknown settings are not a confirmed runtime value.',

        'Python command presence is not proof that a working Python 3 interpreter is installed.')

}

Write-Host ('Expected build: {0}; last logged startup: {1}; DLL matches: {2}' -f $report.expectedVersion, $lastVersion, $matchesExpected)

if ($issues.Count) { foreach ($issue in $issues) { Write-Host ('CHECK: ' + $issue) } }

else { Write-Host 'File checks found no listed issue. Actual mission and connection tests are still required.' }

if ($Mode -eq 'Check') { $report | ConvertTo-Json -Depth 6; return }



# Each collection gets a unique directory. Never overwrite a previous test or delete files.

$collectionRoot = Join-Path $project 'test-results'

[void][IO.Directory]::CreateDirectory($collectionRoot)

$name = 'Coop-' + $Role + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)

$directory = Join-Path $collectionRoot $name

[void][IO.Directory]::CreateDirectory($directory)

$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $directory 'check.json') -Encoding UTF8

$selected = [Collections.Generic.List[string]]::new()

$keep = $false

foreach ($line in ($logText -split '\r?\n')) {

    if ($line -match '^\[[^\]]+\]') {

        $keep = $line -match 'Coop Move Probe|Il2CppException'

    }

    if ($keep -or $line -match 'Probe error') {

        $selected.Add([regex]::Replace($line, 'https?://\S+', '[URL REDACTED]'))

    }

}

[IO.File]::WriteAllLines((Join-Path $directory 'coop.log'), $selected.ToArray(), [Text.UTF8Encoding]::new($true))

# Bounded native UI evidence. All copied text is sanitized before archiving.

foreach ($evidenceName in @('scene-ui-evidence.jsonl','scene-ui-evidence.jsonl.previous','scene-components.jsonl','scene-components.jsonl.previous')) {

    $evidencePath=Join-Path $project $evidenceName

    if ((Test-Path -LiteralPath $evidencePath) -and (Get-Item -LiteralPath $evidencePath).Length -le 32MB) {

        $evidenceText = ConvertTo-CoopPublicLog -Text (Read-SharedText $evidencePath) -FileName $evidenceName
        [IO.File]::WriteAllText((Join-Path $directory $evidenceName), $evidenceText, [Text.UTF8Encoding]::new($true))

    }

}

# Compare only native code/metadata and UI bundles, after game exit. This is

# a host/guest equality check, not a substitute for Steam file verification.

$nativeReference=Join-Path $PSScriptRoot 'native-ui-reference.json'

if(Test-Path -LiteralPath $nativeReference){

    $nativeRows=@()

    foreach($entry in (Get-Content -LiteralPath $nativeReference -Raw -Encoding UTF8 | ConvertFrom-Json).files){

        $target=Join-Path $GameRoot $entry.path

        $actual=$null;$errorText=$null

        try{if(Test-Path -LiteralPath $target -PathType Leaf){$actual=File-Sha256 $target}}

        catch{$errorText=$_.Exception.GetType().Name}

        $nativeRows += [pscustomobject]@{path=$entry.path;matchesHost=($actual -eq $entry.sha256);expected=$entry.sha256;actual=$actual;error=$errorText}

    }

    [IO.File]::WriteAllText((Join-Path $directory 'native-ui-files.json'),(ConvertTo-Json -InputObject $nativeRows -Depth 4),[Text.UTF8Encoding]::new($true))

}

# Native load failures are reported in Unity Player.log, outside BepInEx.

$nativeLog = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData/LocalLow/Mimimi Games/ShadowGambit_TCC/Player.log'

if (Test-Path -LiteralPath $nativeLog) {

    # Native crashes may terminate without an Exception line. Keep a bounded

    # tail as well as the existing selected errors, with room URLs redacted.

    $nativeText=Read-SharedText $nativeLog

    $nativeTail=@(($nativeText -split '\r?\n') | Select-Object -Last 2000)

    $tailText=[regex]::Replace(($nativeTail -join "`r`n"),'(?:https?|tcp)://[^\s''"]+','[URL REDACTED]')

    if($tailText.Length -gt 1048576){$tailText=$tailText.Substring($tailText.Length-1048576)}

    [IO.File]::WriteAllText((Join-Path $directory 'native-player-tail.log'),$tailText,[Text.UTF8Encoding]::new($true))

    $nativeErrors = [Collections.Generic.List[string]]::new()

    $remaining = 0

    foreach ($line in ((Read-SharedText $nativeLog) -split '\r?\n')) {

        if ($line -match 'Exception:|GameObject contains a component|#### (Finished loading|Load) Level:|Character might be broken|FailureLevelLoadErrors') { $remaining = 38 }

        if ($remaining -gt 0) {

            $nativeErrors.Add([regex]::Replace($line, 'https?://[^\s''"]+', '[URL REDACTED]'))

            $remaining--

        }

    }

    [IO.File]::WriteAllLines((Join-Path $directory 'native-load-errors.log'), $nativeErrors.ToArray(), [Text.UTF8Encoding]::new($true))

}

# Event 1000 identifies the native fault module/offset, unlike a managed log.

# Export only selected diagnostic fields, not process memory or full events.

try {

    $faults = @()

    foreach ($event in @(Get-WinEvent -FilterHashtable @{LogName='Application';Id=1000;StartTime=(Get-Date).AddHours(-3)} -MaxEvents 128 -ErrorAction SilentlyContinue)) {

        $xml=[xml]$event.ToXml();$fields=@{}

        foreach ($data in $xml.Event.EventData.Data) {

            if ($data.Name -in @('AppName','AppVersion','ModuleName','ModuleVersion','ExceptionCode','FaultingOffset','ProcessId')) { $fields[[string]$data.Name]=[string]$data.InnerText }

        }

        if ($fields.AppName -ieq 'ShadowGambit_TCC.exe') { $fields.timeUtc=$event.TimeCreated.ToUniversalTime().ToString('o');$faults += $fields }

    }

    [IO.File]::WriteAllText((Join-Path $directory 'native-exit-events.json'),(ConvertTo-Json -InputObject $faults -Depth 4),[Text.UTF8Encoding]::new($true))

} catch {

    [IO.File]::WriteAllText((Join-Path $directory 'native-exit-events.json'),'{"status":"event log unavailable"}',[Text.UTF8Encoding]::new($true))

}

# Include the exit code written by the Steam parent after the process ended.

# Collect selected diagnostic text, then redact it at the final export boundary.

$launchDirectory = Join-Path $project 'session-next/runtime/steam-launch'

if (Test-Path -LiteralPath $launchDirectory) {

    $launchFiles = @(Get-ChildItem -LiteralPath $launchDirectory -Filter 'launch-progress-*.log' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 3)

    $lastError = Join-Path $launchDirectory 'last-error.txt'

    if (Test-Path -LiteralPath $lastError) { $launchFiles += Get-Item -LiteralPath $lastError }

    $processCheck = Join-Path $launchDirectory 'last-game-process.json'

    if (Test-Path -LiteralPath $processCheck) { $launchFiles += Get-Item -LiteralPath $processCheck }

    foreach ($endpointName in @('udp-endpoint.txt', 'last-udp-address.txt')) {

        $endpointPath = Join-Path $launchDirectory $endpointName

        if (Test-Path -LiteralPath $endpointPath) { $launchFiles += Get-Item -LiteralPath $endpointPath }

    }

    foreach ($file in $launchFiles) {

        $contents = Read-SharedText $file.FullName

        $contents = [regex]::Replace($contents, 'https?://[^\s''"]+', '[URL REDACTED]')

        [IO.File]::WriteAllText((Join-Path $directory $file.Name), $contents, [Text.UTF8Encoding]::new($true))

    }

}

@('Write visual observations here:','A patrol:','B move / stance:', 'C guest knockout:',

    'D host knockout:', 'E alarm:', 'F reload:', '',

    'Logs were captured once. A final in-progress line may be incomplete.',

    'No save files or full configuration files are intentionally collected.', 'Sensitive log text is redacted on a best-effort basis. Review this ZIP before public sharing.', 'Do not attach crash memory dumps or original private logs to public GitHub issues.') |

    Set-Content -LiteralPath (Join-Path $directory 'notes.txt') -Encoding UTF8

# Sanitize EVERY collected text artifact, including copied JSONL evidence.
# No raw diagnostic copy may bypass this final export boundary.
foreach ($publicFile in @(Get-ChildItem -LiteralPath $directory -Recurse -File)) {
    if ($publicFile.Name -notmatch '\.(?:json|jsonl|log|txt)(?:\.previous)?$') {
        throw ('Unexpected diagnostic file type; refusing public export: ' + $publicFile.Name)
    }
    $publicText = ConvertTo-CoopPublicLog -Text (Read-SharedText $publicFile.FullName) -FileName $publicFile.Name
    [IO.File]::WriteAllText($publicFile.FullName, $publicText, [Text.UTF8Encoding]::new($true))
}
$zip = $directory + '.zip'

Add-Type -AssemblyName System.IO.Compression.FileSystem

[IO.Compression.ZipFile]::CreateFromDirectory($directory, $zip + '.partial')

[IO.File]::Move($zip + '.partial', $zip)

Write-Host ('Result bundle: ' + $zip)

if ($OnExit -and $Role -eq 'Guest') {

    # Resolve redirected Downloads folders as well as the standard profile path.

    try {

        $downloads = $CopyDirectory

        if (!$downloads) {

            $folders = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders')

            try { $downloads = if ($folders) { $folders.GetValue('{374DE290-123F-4565-9164-39C4925E467B}') } } finally { if ($folders) { $folders.Dispose() } }

        }

        if (!$downloads) { $downloads = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads' }

        $downloads = [Environment]::ExpandEnvironmentVariables($downloads)

        if ([IO.Directory]::Exists($downloads)) {

            $dest = Join-Path $downloads ($name + '.zip')

            [IO.File]::Copy($zip, $dest + '.partial')

            [IO.File]::Move($dest + '.partial', $dest)

        }

    } catch { Write-Warning ('Downloads copy unavailable. Local result: ' + $zip) }

}

Write-Host 'Keep both Host and Guest bundles. Collect before restarting the game.'

