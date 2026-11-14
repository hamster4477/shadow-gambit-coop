$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$checks = 0
function Assert-LogCheck([bool]$Condition, [string]$Label) {
    if (!$Condition) { throw ('Log redaction check failed: ' + $Label) }
    $script:checks++
}
$firstFunction = $null
foreach ($variant in @('two-player','multiplayer')) {
    $sourcePath = Join-Path $repository ('src/' + $variant + '/test-support.ps1')
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$errors)
    Assert-LogCheck ($errors.Count -eq 0) 'production collector parses'
    $function = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ConvertTo-CoopPublicLog'}, $true)
    Assert-LogCheck ($null -ne $function) 'production sanitizer exists'
    if ($firstFunction) { Assert-LogCheck ($firstFunction -ceq $function.Extent.Text) 'variants share the exact sanitizer' }
    else { $firstFunction = $function.Extent.Text }
    # Evaluate only the extracted function, never the diagnostic collector.
    . ([ScriptBlock]::Create($function.Extent.Text))
    foreach ($scheme in @('http','https','tcp','udp')) {
        $actual = ConvertTo-CoopPublicLog ('connect ' + $scheme + '://fixture.example.test:12345/private-fixture?token=fixture')
        Assert-LogCheck ($actual -eq 'connect [URL REDACTED]') ('room URI ' + $scheme)
    }
    foreach ($name in @('udp-endpoint.txt','last-udp-address.txt','last-address.txt','last-network.txt')) {
        Assert-LogCheck ((ConvertTo-CoopPublicLog "fixture.example.test:12345`r`n192.0.2.15:23456" $name) -eq '[NETWORK ENDPOINT REDACTED]') 'endpoint file content'
    }
    $native = 'phase=ready DLL=GameAssembly.dll version=1.0.0.0 code=0xC0000005 generation=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa sha256=' + ('A'*64)
    Assert-LogCheck ((ConvertTo-CoopPublicLog $native) -ceq $native) 'useful native diagnostics remain'
    Assert-LogCheck ((ConvertTo-CoopPublicLog 'fault C:\Users\Fixture Person\AppData\Local\GameAssembly.dll') -eq 'fault C:\Users\[USER REDACTED]\AppData\Local\GameAssembly.dll') 'Windows home path with spaces'
    Assert-LogCheck ((ConvertTo-CoopPublicLog 'fault D:/Users/fixture-user/AppData/Local/UnityPlayer.dll') -eq 'fault D:/Users/[USER REDACTED]/AppData/Local/UnityPlayer.dll') 'redirected user drive'
    Assert-LogCheck ((ConvertTo-CoopPublicLog '/home/fixture-user/game/Player.log') -eq '/home/[USER REDACTED]/game/Player.log') 'Unix home path'
    Assert-LogCheck ((ConvertTo-CoopPublicLog 'user_steam_76561190000000000/profileData.save') -eq 'user_steam_[ACCOUNT REDACTED]/profileData.save') 'Steam profile directory'
    Assert-LogCheck ((ConvertTo-CoopPublicLog 'account 76561190000000000 failed') -eq 'account [STEAM ID REDACTED] failed') 'Steam64 identity'
    Assert-LogCheck ((ConvertTo-CoopPublicLog 'connect fixture.example.test:23456 failed') -eq 'connect [NETWORK ENDPOINT REDACTED] failed') 'bare DNS endpoint'
    Assert-LogCheck ((ConvertTo-CoopPublicLog 'connect 192.0.2.15:23456 failed') -eq 'connect [NETWORK ENDPOINT REDACTED] failed') 'bare IPv4 endpoint'
    Assert-LogCheck ((ConvertTo-CoopPublicLog 'remoteAddress=192.0.2.15 ready') -eq 'remoteAddress=[IP REDACTED] ready') 'labeled address'
    foreach ($key in @('room','lobbyRoom','roomUrl','token','access_token','refresh-token','authToken','secret','inviteCode','password','api_key','peer','client','username','steamId')) {
        $text = '{"' + $key + '":"fixture-sensitive-value","phase":"ready"}'
        $parsed = ConvertTo-CoopPublicLog $text | ConvertFrom-Json
        Assert-LogCheck ($parsed.$key -eq '[REDACTED]' -and $parsed.phase -eq 'ready') ('JSON key ' + $key)
    }
    Assert-LogCheck ((ConvertTo-CoopPublicLog "Authorization: Bearer fixture-sensitive-value`r`nphase=ready") -eq "Authorization: [REDACTED]`r`nphase=ready") 'authorization header'
    $json = '{"path":"C:\\Users\\fixture-user\\AppData\\user_steam_76561190000000000\\profileData.save","token":"fixture\"value","phase":"loaded"}'
    $clean = ConvertTo-CoopPublicLog $json
    $parsed = $clean | ConvertFrom-Json
    Assert-LogCheck ($parsed.path -eq 'C:\Users\[USER REDACTED]\AppData\user_steam_[ACCOUNT REDACTED]\profileData.save') 'JSON path stays valid'
    Assert-LogCheck ($parsed.token -eq '[REDACTED]' -and $parsed.phase -eq 'loaded') 'escaped secret stays valid JSON'
    foreach ($sample in @($json,$native,'room=tcp://fixture.example.test:23456/private-fixture','token=fixture-sensitive-value','remoteAddress=192.0.2.15','C:\Users\Fixture Person\AppData\GameAssembly.dll')) {
        $once = ConvertTo-CoopPublicLog $sample
        Assert-LogCheck ((ConvertTo-CoopPublicLog $once) -ceq $once) 'redaction is idempotent'
    }
    # Every gathered text artifact is rewritten before CreateFromDirectory.
    $production = [IO.File]::ReadAllText($sourcePath)
    $boundary = $production.IndexOf('foreach ($publicFile in @(Get-ChildItem -LiteralPath $directory -Recurse -File))')
    $archive = $production.IndexOf('[IO.Compression.ZipFile]::CreateFromDirectory')
    Assert-LogCheck ($boundary -ge 0 -and $archive -gt $boundary) 'final sanitizer precedes archive creation'
    Assert-LogCheck ($production.Substring($boundary,$archive-$boundary).Contains('ConvertTo-CoopPublicLog -Text (Read-SharedText $publicFile.FullName)')) 'final boundary applies actual sanitizer'
    Assert-LogCheck ($production.Contains('Review this ZIP before public sharing.')) 'public review instruction included'
    Assert-LogCheck (!$production.Contains('Copy-Item -LiteralPath $evidencePath')) 'no raw evidence-copy shortcut'
    # Run the exact final production boundary against a temporary nested archive.
    # Only synthetic documentation addresses and synthetic identities are used.
    $reader = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Read-SharedText'}, $true)
    . ([ScriptBlock]::Create($reader.Extent.Text))
    $exportBoundary = $ast.Find({param($node) $node -is [Management.Automation.Language.ForEachStatementAst] -and $node.Variable.VariablePath.UserPath -eq 'publicFile'}, $true)
    Assert-LogCheck ($null -ne $exportBoundary) 'production archive boundary found'
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('coop-redaction-' + [Guid]::NewGuid().ToString('N'))
    $directory = Join-Path $temporaryRoot 'logs'
    $nested = Join-Path $directory 'nested'
    [void][IO.Directory]::CreateDirectory($nested)
    try {
        [IO.File]::WriteAllText((Join-Path $directory 'coop.log'), 'phase=ready room=tcp://fixture.example.test:23456/private-fixture')
        [IO.File]::WriteAllText((Join-Path $nested 'scene-ui-evidence.jsonl.previous'), $json)
        [IO.File]::WriteAllText((Join-Path $directory 'udp-endpoint.txt'), 'fixture.example.test:23456')
        . ([ScriptBlock]::Create($exportBoundary.Extent.Text))
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archivePath = Join-Path $temporaryRoot 'synthetic.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($directory, $archivePath)
        $opened = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            Assert-LogCheck ($opened.Entries.Count -eq 3) 'all synthetic artifacts archived'
            foreach ($entry in $opened.Entries) {
                $stream = [IO.StreamReader]::new($entry.Open())
                try { $entryText = $stream.ReadToEnd() } finally { $stream.Dispose() }
                Assert-LogCheck ($entryText -notmatch 'fixture\.example|fixture-user|76561190000000000|private-fixture') 'archive content sanitized including nested JSONL'
                if ($entry.Name -eq 'scene-ui-evidence.jsonl.previous') {
                    Assert-LogCheck (($entryText | ConvertFrom-Json).phase -eq 'loaded') 'archive JSONL remains readable'
                }
            }
        } finally { $opened.Dispose() }
        $unexpected = Join-Path $directory 'memory.dmp'
        [IO.File]::WriteAllText($unexpected, 'synthetic data')
        $blocked = $false
        try { . ([ScriptBlock]::Create($exportBoundary.Extent.Text)) } catch { $blocked = $true }
        Assert-LogCheck $blocked 'unknown artifact extension blocks export'
    } finally {
        # Delete only the verified, newly created temporary fixture boundary.
        $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
        $temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (!$resolvedTemporary.StartsWith($temporaryParent, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $resolvedTemporary) -notmatch '^coop-redaction-[a-f0-9]{32}$') { throw 'Unexpected fixture cleanup boundary' }
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}
Write-Output ('PASS public diagnostic redaction: ' + $checks + ' checks; exact exported production functions, no game/network access.')
