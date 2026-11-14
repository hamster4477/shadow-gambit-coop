param(
    [string]$GameDirectory=(Split-Path -Parent $PSScriptRoot),
    [string]$OfflineCache='',
    [switch]$SkipPython,
    [switch]$Force,
    [switch]$DryRun
)
$ErrorActionPreference='Stop'
# One-time dependency installer. Downloads only the pinned official builds below,
# verifies their SHA-256 before touching anything, and never runs what it downloads.
# It does not read or write saves, and it does not start the game.

$BepInExUrl='https://builds.bepinex.dev/projects/bepinex_be/682/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.682%2B910844a.zip'
$BepInExSha='087B337D512949A5A8E16B5C085BCEECEAB699ACF1CE9DCF8938735243A6572C'
$BepInExName='BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.682+910844a.zip'

$PythonUrl='https://www.python.org/ftp/python/3.13.9/python-3.13.9-embed-amd64.zip'
$PythonSha='91D828C2DA3A029B41699E918674A0CB379C02CF20DAB9C501306885F837402A'
$PythonName='python-3.13.9-embed-amd64.zip'

# The broker scripts import from the co-op folder and from session-next.
$PythonPathFile=@('python313.zip','.','..','../session-next')

$game=[IO.Path]::GetFullPath($GameDirectory)
$coop=Join-Path $game 'ShadowGambitCoop'
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot=Join-Path $coop ("backups/dependencies-" + $stamp)
$stage=Join-Path $backupRoot 'stage'
$downloadDir=if($OfflineCache){[IO.Path]::GetFullPath($OfflineCache)}else{Join-Path $backupRoot 'downloads'}
$moved=New-Object System.Collections.ArrayList

function Say([string]$Text){Write-Host $Text}

function Test-IsGameRunning {
    return @(Get-Process -Name 'ShadowGambit_TCC' -ErrorAction SilentlyContinue).Count -gt 0
}

function Assert-ArchiveSafe([string]$Path){
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($Path)
    try{
        foreach($entry in $zip.Entries){
            $name=$entry.FullName
            if($name.StartsWith('/') -or $name.StartsWith('\') -or $name -match '^[A-Za-z]:' -or ($name -split '/') -contains '..'){
                throw ("不安全的压缩包路径 / Unsafe archive entry: " + $name)
            }
        }
    } finally { $zip.Dispose() }
}

function Get-VerifiedArchive([string]$Url,[string]$Name,[string]$Expected){
    if(!(Test-Path -LiteralPath $downloadDir)){[void][IO.Directory]::CreateDirectory($downloadDir)}
    $target=Join-Path $downloadDir $Name
    if(Test-Path -LiteralPath $target -PathType Leaf){
        Say ("  使用已有文件 / using existing file: " + $Name)
    } elseif($OfflineCache){
        throw ("离线目录中没有该文件 / Missing from the offline folder: " + (Join-Path $OfflineCache $Name))
    } else {
        Say ("  下载 / downloading: " + $Url)
        try{[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12}catch{}
        $old=$ProgressPreference;$ProgressPreference='SilentlyContinue'
        try{Invoke-WebRequest -Uri $Url -OutFile $target -UseBasicParsing -TimeoutSec 1800}
        finally{$ProgressPreference=$old}
    }
    $actual=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if($actual -ne $Expected){
        Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        throw ("哈希校验失败，已删除下载文件 / SHA-256 mismatch, download discarded.`n  expected " + $Expected + "`n  actual   " + $actual)
    }
    Say ("  校验通过 / verified: " + $actual)
    Assert-ArchiveSafe $target
    return $target
}

function Backup-Existing([string]$Path){
    if(!(Test-Path -LiteralPath $Path)){return}
    $relative=$Path.Substring($game.Length).TrimStart('\')
    $saved=Join-Path $backupRoot ('existing/' + $relative)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved))
    Move-Item -LiteralPath $Path -Destination $saved -Force
    [void]$moved.Add([pscustomobject]@{Saved=$saved;Target=$Path})
    Say ("  已备份 / backed up: " + $relative)
}

function Restore-Moved {
    foreach($item in $moved){
        if((Test-Path -LiteralPath $item.Saved) -and !(Test-Path -LiteralPath $item.Target)){
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($item.Target))
            Move-Item -LiteralPath $item.Saved -Destination $item.Target -Force
        }
    }
}

# ---------------------------------------------------------------- plan
Say '依赖安装 / Dependency installer'
Say ('游戏目录 / Game directory: ' + $game)
if(!(Test-Path -LiteralPath (Join-Path $game 'ShadowGambit_TCC.exe') -PathType Leaf)){
    throw '未找到 ShadowGambit_TCC.exe，目录可能不对 / ShadowGambit_TCC.exe not found; wrong folder?'
}
$needBepInEx=-not (Test-Path -LiteralPath (Join-Path $game 'BepInEx/core/BepInEx.Core.dll') -PathType Leaf)
$pyTarget=Join-Path $coop 'python/python.exe'
$needPython=(-not $SkipPython) -and (-not (Test-Path -LiteralPath $pyTarget -PathType Leaf))
if($Force){$needBepInEx=$true; $needPython=(-not $SkipPython)}

Say ''
Say '计划 / Plan:'
Say ('  BepInEx 6.0.0-be.682 IL2CPP win-x64 : ' + $(if($needBepInEx){'安装 / install'}else{'已存在，跳过 / present, skip'}) + ('  <' + $BepInExName + '>'))
Say ('  CPython 3.13.9 embeddable           : ' + $(if($SkipPython){'跳过 / skipped'}elseif($needPython){'安装 / install'}else{'已存在，跳过 / present, skip'}) + ('  <' + $PythonName + '>'))
Say ('  下载位置 / download location        : ' + $downloadDir)
Say ('  备份位置 / backup location          : ' + $backupRoot)
Say '  不会修改存档，也不会合并任何游戏文件。/ Saves are not touched and no game files are merged.'
Say '  首次启动时 BepInEx 仍会自行下载 Unity 基础库，需要联网。/ BepInEx still downloads Unity base libraries on first launch; that needs network.'

if($DryRun){Say '';Say 'DryRun：未做任何改动 / DryRun: nothing was changed.';exit 0}
if(-not ($needBepInEx -or $needPython)){Say '';Say '依赖已齐全，无需安装。/ Nothing to install.';exit 0}
if(Test-IsGameRunning){
    Say ''
    throw '游戏正在运行，请先完全退出。/ The game is running; close it completely first.'
}

if(!(Test-Path -LiteralPath $stage)){[void][IO.Directory]::CreateDirectory($stage)}
Add-Type -AssemblyName System.IO.Compression.FileSystem

try{
    if($needBepInEx){
        Say ''
        Say '[1/2] BepInEx'
        $zip=Get-VerifiedArchive $BepInExUrl $BepInExName $BepInExSha
        $extract=Join-Path $stage 'bepinex'
        Say '  解压 / extracting'
        [IO.Compression.ZipFile]::ExtractToDirectory($zip,$extract)
        foreach($file in @('winhttp.dll','doorstop_config.ini','.doorstop_version')){
            $from=Join-Path $extract $file
            if(!(Test-Path -LiteralPath $from)){throw ("压缩包缺少 / Archive is missing: " + $file)}
        }
        if(!(Test-Path -LiteralPath (Join-Path $extract 'BepInEx/core/BepInEx.Core.dll'))){throw '压缩包缺少 BepInEx/core / Archive is missing BepInEx/core'}

        foreach($file in @('winhttp.dll','doorstop_config.ini','.doorstop_version','changelog.txt')){
            $from=Join-Path $extract $file
            if(!(Test-Path -LiteralPath $from)){continue}
            $to=Join-Path $game $file
            Backup-Existing $to
            Move-Item -LiteralPath $from -Destination $to -Force
        }
        $existingBepInEx=Join-Path $game 'BepInEx'
        $newBepInEx=Join-Path $extract 'BepInEx'
        if(Test-Path -LiteralPath $existingBepInEx){
            # Keep the player's config and installed plugins; take the new binaries.
            $keep=Join-Path $stage 'keep'
            [void][IO.Directory]::CreateDirectory($keep)
            $preserved=New-Object System.Collections.ArrayList
            foreach($name in @('config','plugins','interop','unity-libs','cache','chainloader')){
                $path=Join-Path $existingBepInEx $name
                if(!(Test-Path -LiteralPath $path)){continue}
                Move-Item -LiteralPath $path -Destination (Join-Path $keep $name) -Force
                [void]$preserved.Add($name)
            }
            $oldBepInEx=Join-Path $backupRoot 'existing/BepInEx'
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($oldBepInEx))
            Move-Item -LiteralPath $existingBepInEx -Destination $oldBepInEx -Force
            if(Test-Path -LiteralPath $existingBepInEx){
                throw '无法移开旧 BepInEx 目录，已停止 / Could not move the existing BepInEx folder aside'
            }
            Move-Item -LiteralPath $newBepInEx -Destination $existingBepInEx -Force
            foreach($name in $preserved){
                $from=Join-Path $keep $name
                $dest=Join-Path $existingBepInEx $name
                if(Test-Path -LiteralPath $dest){Remove-Item -LiteralPath $dest -Recurse -Force}
                Move-Item -LiteralPath $from -Destination $dest -Force
            }
            Say ('  保留了原有 / preserved: ' + (@($preserved) -join ', '))
        } else {
            Move-Item -LiteralPath $newBepInEx -Destination $existingBepInEx -Force
        }
        if(!(Test-Path -LiteralPath (Join-Path $game 'BepInEx/core/BepInEx.Core.dll'))){
            throw '安装后找不到 BepInEx/core/BepInEx.Core.dll / Post-install check failed'
        }
        $dotnetNew=Join-Path $extract 'dotnet'
        if(Test-Path -LiteralPath $dotnetNew){
            $dotnetExisting=Join-Path $game 'dotnet'
            Backup-Existing $dotnetExisting
            Move-Item -LiteralPath $dotnetNew -Destination $dotnetExisting -Force
        }
        Say '  BepInEx 安装完成 / BepInEx installed'
    }

    if($needPython){
        Say ''
        Say '[2/2] Python runtime'
        $zip=Get-VerifiedArchive $PythonUrl $PythonName $PythonSha
        $extract=Join-Path $stage 'python'
        Say '  解压 / extracting'
        [IO.Compression.ZipFile]::ExtractToDirectory($zip,$extract)
        if(!(Test-Path -LiteralPath (Join-Path $extract 'python.exe'))){throw '压缩包缺少 python.exe / Archive is missing python.exe'}
        # The embedded runtime ships a minimal search path; the brokers need these entries.
        [IO.File]::WriteAllLines((Join-Path $extract 'python313._pth'),$PythonPathFile,(New-Object Text.UTF8Encoding($false)))
        if(!(Test-Path -LiteralPath $coop)){[void][IO.Directory]::CreateDirectory($coop)}
        $pyDir=Join-Path $coop 'python'
        if(Test-Path -LiteralPath $pyDir){Backup-Existing $pyDir}
        Move-Item -LiteralPath $extract -Destination $pyDir -Force
        Say '  Python 安装完成 / Python installed'
    }

    Say ''
    Say '安装完成，正在复查 / Installed; re-checking'
    & (Join-Path $PSScriptRoot 'Check-Environment.ps1') -GameDirectory $game
    $code=$LASTEXITCODE
    Say ''
    Say ('备份保留在 / backups kept in: ' + $backupRoot)
    Say '备份确认无误后可删除。/ You may delete it once things work.'
    exit $code
}
catch{
    Say ''
    Say ('失败 / Failed: ' + $_.Exception.Message)
    Say '正在回退已移动的文件 / rolling back moved files'
    try{Restore-Moved}catch{Write-Warning ('回退失败 / Rollback failed: ' + $_.Exception.Message)}
    Say ('完整目录保留在 / staging kept at: ' + $backupRoot)
    exit 1
}
