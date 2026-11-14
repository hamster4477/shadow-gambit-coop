function Get-CoopSha256([string]$Path) {
    # Windows PowerShell launched by another runtime may not auto-load the
    # Get-FileHash module. Use the Framework primitive on the startup path.
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','') }
    finally { $hash.Dispose(); $stream.Dispose() }
}

function Get-CoopProcessCensus([int]$ProcessId) {
    try { Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId="+$ProcessId) -ErrorAction Stop }
    catch { return $null }
}

function Test-CoopEmptyProcessRecord($Record, [int]$ProcessId) {
    return ($null -ne $Record -and $Record.ProcessId -eq $ProcessId -and
        $Record.Name -ieq 'ShadowGambit_TCC.exe' -and $null -ne $Record.CreationDate -and
        $null -ne $Record.ThreadCount -and $Record.ThreadCount -eq 0 -and
        $null -ne $Record.HandleCount -and $Record.HandleCount -eq 0)
}

function Get-CoopGameProcess([switch]$IncludeResidual) {
    foreach ($candidate in @(Get-Process -Name ShadowGambit_TCC -ErrorAction SilentlyContinue)) {
        if ($null -eq $candidate) { continue }
        $processId=$candidate.Id
        $state='unknown';$why='';$path='';$started=''
        try {
            $candidate.Refresh()
            if ($candidate.HasExited) { continue }
            $state='running'
            try { $path=$candidate.Path } catch { }
            try { $started=$candidate.StartTime.ToString('O') } catch { }
        } catch {
            $base=$_.Exception.GetBaseException()
            if ($base -is [ArgumentException] -or ($base -is [ComponentModel.Win32Exception] -and $base.NativeErrorCode -in @(87,1168))) { continue }
            $why=$base.GetType().Name
            if ($base -is [ComponentModel.Win32Exception]) { $why+=' '+$base.NativeErrorCode }
        } finally { if($candidate -is [IDisposable]){$candidate.Dispose()} }
        $threads=$null;$handles=$null
        if ([string]::IsNullOrEmpty($path) -and [string]::IsNullOrEmpty($started)) {
            $first=Get-CoopProcessCensus $processId
            if ($null -ne $first) {$threads=$first.ThreadCount;$handles=$first.HandleCount}
            if (Test-CoopEmptyProcessRecord $first $processId) {
                # Windows may retain an exited process object while external
                # references remain. Never infer this from a missing window or
                # access denial alone; confirm the same creation identity twice.
                Start-Sleep -Milliseconds 150
                $second=Get-CoopProcessCensus $processId
                if ((Test-CoopEmptyProcessRecord $second $processId) -and $second.CreationDate -eq $first.CreationDate) {
                    $state='exited-residual';$started=[string]$first.CreationDate
                    if (!$IncludeResidual) { continue }
                }
            }
        }
        [pscustomobject]@{Id=$processId;State=$state;Path=$path;StartTime=$started;Error=$why;ThreadCount=$threads;HandleCount=$handles}
    }
}

function Assert-CoopGameStopped([string]$Project, [int]$WaitMilliseconds=6000) {
    $runtime=Join-Path $Project 'session-next/runtime/steam-launch'
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $history=@()
    do {
        $observed=@(Get-CoopGameProcess -IncludeResidual)
        $remaining=@($observed|Where-Object {$_.State -ne 'exited-residual'})
        $history+=@{utc=[DateTime]::UtcNow.ToString('O');processes=$observed}
        if (!$remaining.Count) { break }
        if ($timer.ElapsedMilliseconds -ge $WaitMilliseconds) { break }
        Start-Sleep -Milliseconds 300
    } while ($true)
    [void][IO.Directory]::CreateDirectory($runtime)
    [IO.File]::WriteAllText((Join-Path $runtime 'last-game-process.json'),(ConvertTo-Json -InputObject $history -Depth 5),[Text.UTF8Encoding]::new($true))
    if ($remaining.Count) {
        $details=($remaining|ForEach-Object { 'PID '+$_.Id+' / '+$_.State+$(if($_.Error){' / '+$_.Error}) }) -join '; '
        throw ('游戏进程检查尚未通过 / Game process check did not pass: '+$details+'。已等待退出；诊断已保存 / Waited for exit; diagnostics: session-next/runtime/steam-launch/last-game-process.json。')
    }
}

function Test-CoopNetworkIPv4([string]$Value) {
    $ip=$null
    if (![Net.IPAddress]::TryParse($Value,[ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork') { return $false }
    $b=$ip.GetAddressBytes()
    return ($b[0] -eq 10 -or $b[0] -in @(25,26) -or ($b[0] -eq 100 -and $b[1] -ge 64 -and $b[1] -le 127) -or ($b[0] -eq 192 -and $b[1] -eq 168) -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31))
}

function Get-CoopNetworkOptions {
    [pscustomobject]@{Key='lan';Label='热点 / 局域网（自动） | Hotspot / LAN (automatic)'}
    foreach($item in @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.AddressState -eq 'Preferred' -and (Test-CoopNetworkIPv4 $_.IPAddress) } | Sort-Object InterfaceAlias,IPAddress)) {
        $label=if($item.InterfaceAlias -ieq 'natpierce'){'皎月连 / NatPierce'}else{[string]$item.InterfaceAlias}
        [pscustomobject]@{Key=('adapter:'+$item.InterfaceAlias);Label=($label+' — '+$item.IPAddress)}
    }
}

function Get-CoopNetworkBinding([string]$PreferenceFile) {
    $mode = if (Test-Path -LiteralPath $PreferenceFile) { [IO.File]::ReadAllText($PreferenceFile).Trim() } else { 'lan' }
    if ($mode -in @('lan','')) { return '' }
    $alias=if($mode -eq 'natpierce'){'natpierce'}elseif($mode.StartsWith('adapter:')){$mode.Substring(8)}else{throw '保存的联机网络无效，请重新选择。 / Saved network is invalid. Select a network again.'}
    $addresses = @(Get-NetIPAddress -InterfaceAlias $alias -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.AddressState -eq 'Preferred' -and (Test-CoopNetworkIPv4 $_.IPAddress) })
    if ($addresses.Count -ne 1) { throw ('所选联机网络不可用或地址不唯一 / Selected network is unavailable or has multiple addresses: '+$alias+'。请先连接组网工具，再重新选择网络。 / Connect your virtual LAN tool, then select its network again.') }
    return [string]$addresses[0].IPAddress
}

function Get-CoopDirectDiscoveryTargets([Net.IPAddress]$Address, [int]$Prefix, [string]$InterfaceAlias) {
    # Bounded fallback only for known overlay adapters on a /24 subnet.
    if ($InterfaceAlias -notmatch '(?i)natpierce|zerotier|radmin|tailscale|hamachi|easy.?tier|tinc|softether' -or $Prefix -ne 24) { return }
    $bytes = $Address.GetAddressBytes()
    if ($bytes.Length -ne 4 -or !(Test-CoopNetworkIPv4 $Address.IPAddressToString)) { return }
    for ($last=1; $last -le 254; $last++) {
        if ($last -ne $bytes[3]) { '{0}.{1}.{2}.{3}' -f $bytes[0],$bytes[1],$bytes[2],$last }
    }
}

function Write-CoopLaunchProgress([string]$Directory, [string]$Stage) {
    # Never include room addresses, Steam arguments or account details.
    try {
        [void][IO.Directory]::CreateDirectory($Directory)
        $line = [DateTime]::Now.ToString('s') + ' ' + $Stage + [Environment]::NewLine
        [IO.File]::AppendAllText([IO.Path]::Combine($Directory,('launch-progress-'+$PID+'.log')),$line,[Text.UTF8Encoding]::new($false))
    } catch { }
}

function ConvertTo-SteamProcessArgument([AllowEmptyString()][string]$Value) {
    # Quote for CommandLineToArgvW / the Windows C runtime, without a shell.
    $quoted = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $quoted = [regex]::Replace($quoted, '(\\+)$', '$1$1')
    return '"' + $quoted + '"'
}

function Get-CoopSteamOption([string]$Launcher) {
    return '"' + (Join-Path $env:WINDIR 'System32/wscript.exe') + '" "' + $Launcher + '" %command%'
}

function Write-CoopLaunchMode([string]$Project, [ValidateSet('Solo','Host','Guest')][string]$Mode, [string]$LobbyRoom) {
    $control = Join-Path $Project 'session-next/runtime/control'
    [void][IO.Directory]::CreateDirectory($control)
    $path = Join-Path $control 'launch-mode.json'
    $generation = [Guid]::NewGuid().ToString('N')
    $pending = $path + '.' + $generation + '.new'
    try {
        $record = @{schema=1;generation=$generation;mode=$Mode.ToLowerInvariant()}
        if ($LobbyRoom) {
            $roomUri = $null
            if ($Mode -ne 'Guest' -or $LobbyRoom -notmatch '\Ahttps?://[^/?#\\\s%]+/[A-Za-z0-9_-]{1,128}\z' -or
                ![Uri]::TryCreate($LobbyRoom,[UriKind]::Absolute,[ref]$roomUri) -or $roomUri.UserInfo -or $roomUri.Port -lt 1) { throw 'Invalid guest lobby address.' }
            $record.lobbyRoom = $roomUri.AbsoluteUri
        }
        $value = $record | ConvertTo-Json -Compress
        if ([Text.Encoding]::UTF8.GetByteCount($value) -gt 2048) { throw 'Launch record too large.' }
        # This function marks an explicit new launch, not Steam's replacement
        # game process. Retire an old guest handoff even if it never reached
        # session-load.json; otherwise a fresh Host launch can consume it and
        # lock its menu or import the previous host's checkpoint. Preserve
        # explicitly scheduled local verification requests used by diagnostics.
        $oldImport = Join-Path $control 'live-import.json'
        if ([IO.File]::Exists($oldImport) -and (Get-Item -LiteralPath $oldImport).Length -le 2048) {
            $oldRequest = $null
            try { $oldRequest = [IO.File]::ReadAllText($oldImport) | ConvertFrom-Json } catch { }
            if ($oldRequest -and $oldRequest.operation -in @('join-room','return-menu')) {
                [IO.File]::Move($oldImport,($oldImport+'.retired-launch-'+$generation))
            }
        }
        [IO.File]::WriteAllText($pending,$value,[Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($path)) { [IO.File]::Replace($pending,$path,[System.Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($pending,$path) }
    } finally { if ([IO.File]::Exists($pending)) { [IO.File]::Delete($pending) } }
}

function Read-CoopSteamArguments([string]$ArgumentsFile, [string]$Directory) {
    $path = [IO.Path]::GetFullPath($ArgumentsFile)
    $dir = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($path) -ine $dir -or [IO.Path]::GetFileName($path) -notmatch '^launch-[A-Za-z0-9_.-]+\.json$') {
        throw 'Invalid Steam argument file location.'
    }
    $file = Get-Item -LiteralPath $path
    if ($file.Length -gt 32768 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid Steam argument file.' }
    $raw = [IO.File]::ReadAllText($path, [Text.Encoding]::Unicode)
    if (!$raw.TrimStart().StartsWith('[')) { throw 'Steam arguments must be an array.' }
    $items = ConvertFrom-Json -InputObject $raw
    if ($items -isnot [array] -or !$items.Count -or $items.Count -gt 64) { throw 'Invalid Steam argument count.' }
    $total = 0
    foreach ($item in $items) {
        if ($item -isnot [string] -or $item.IndexOfAny([char[]]@([char]0,[char]10,[char]13)) -ge 0) { throw 'Invalid Steam argument.' }
        $total += $item.Length
    }
    if ($total -gt 8192) { throw 'Steam arguments are too long.' }
    return ,$items
}

function Set-CoopQuietConsole([string]$GameDirectory) {
    $path = Join-Path $GameDirectory 'BepInEx/config/BepInEx.cfg'
    $original = if ([IO.File]::Exists($path)) { [IO.File]::ReadAllText($path) } else { '' }
    $text = $original
    foreach ($setting in @(@('Logging.Console','false'), @('Logging.Disk','true'))) {
        $section = [regex]::Match($text, '(?ms)^\[' + [regex]::Escape($setting[0]) + '\][^\r\n]*\r?\n.*?(?=^\[|\z)')
        if ($section.Success) {
            $body = $section.Value
            if ($body -match '(?m)^\s*Enabled\s*=') {
                $body = [regex]::Replace($body, '(?m)^\s*Enabled\s*=[^\r\n]*', ('Enabled = ' + $setting[1]))
            } else { $body = $body.TrimEnd() + "`r`nEnabled = " + $setting[1] + "`r`n" }
            $text = $text.Substring(0,$section.Index) + $body + $text.Substring($section.Index + $section.Length)
        } else { $text = $text.TrimEnd() + "`r`n[" + $setting[0] + "]`r`nEnabled = " + $setting[1] + "`r`n" }
    }
    if ($text -ne $original) {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        if ([IO.File]::Exists($path) -and ![IO.File]::Exists($path+'.before-coop-console')) { [IO.File]::Copy($path,$path+'.before-coop-console') }
        [IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($true))
    }
}

function Invoke-CoopSteamGame([string]$Executable, [string[]]$GameArguments, [string]$WorkingDirectory) {
    Set-CoopQuietConsole $WorkingDirectory
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.Arguments = (@($GameArguments | ForEach-Object { ConvertTo-SteamProcessArgument $_ }) -join ' ')
    $process = [Diagnostics.Process]::Start($info)
    try { $process.WaitForExit(); return $process.ExitCode } finally { $process.Dispose() }
}
