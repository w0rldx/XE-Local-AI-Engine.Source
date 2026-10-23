<#
.SYNOPSIS
    Remove XE Local AI Engine (Windows).

.DESCRIPTION
    What this does, in order:
      1. Stops the desktop shell and launcher first, then any running XE Local AI Engine
         process, then the llama-server / sd-server / whisper-server child runtimes and
         the trainer's Python interpreter THIS app spawned (matched strictly by executable path under the
         app's own per-user data directory - an unrelated llama-server/Ollama is never
         touched, mirroring the app's own StaleProcessReaper).
      2. If a Velopack-managed install is detected, notes that Velopack/the OS uninstall
         owns the app binaries and points at it (this script does not delete a managed
         install tree - Velopack does).
      3. After an explicit confirmation, deletes ONLY the per-user data directory
         (%LOCALAPPDATA%\XE-Local-AI-Engine): node.sqlite, node.key, node-settings.json,
         hf-token.enc, the downloaded llama.cpp / stable-diffusion.cpp binaries, the
         GGUF/image models, the AgentHome workspace, python\ (the shared uv, the
         provisioned Python interpreters and uv's package cache), training-runtime\
         and compute-runtime\ (the package venvs, multi-gigabyte for training), and
         training\ (downloaded base checkpoints, datasets and run artifacts).

    It NEVER deletes anything outside that exact directory. When the directory holds
    node.key, a second, typed confirmation is required. Portable-zip users: after
    running this, also delete the folder you unzipped the app into.

.PARAMETER Yes
    Skip the first delete confirmation. A data directory that holds node.key still asks
    for the typed second confirmation, so it is never deleted without one.

.PARAMETER DryRun
    Show what would happen; stop and delete nothing.

.PARAMETER KeepData
    Stop processes only; keep the per-user data directory.

.PARAMETER AllowAdmin
    Permit running from an elevated (Administrator) session. By default this is refused,
    because the data directory is per-user and an elevated session may resolve a
    different user's %LOCALAPPDATA%.

.EXAMPLE
    .\uninstall-xe-local-ai-engine.ps1
.EXAMPLE
    .\uninstall-xe-local-ai-engine.ps1 -Yes
.EXAMPLE
    .\uninstall-xe-local-ai-engine.ps1 -DryRun
#>
#Requires -Version 5.1
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'Interactive uninstaller CLI: Write-Host is the intended console output channel.')]
[CmdletBinding()]
param(
    [switch] $Yes,
    [switch] $DryRun,
    [switch] $KeepData,
    [switch] $AllowAdmin
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$AppName     = 'XE Local AI Engine'
$DataDirName = 'XE-Local-AI-Engine'          # per-user data directory name (no ".Client")

# Per-user uninstall: an elevated session can resolve a different profile's LOCALAPPDATA.
if (-not $AllowAdmin) {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host ((">> Do not run this uninstaller elevated. {0} stores its data under the " +
            "current user's %LOCALAPPDATA%; an Administrator session may target the wrong " +
            "profile. Re-run it in a normal (non-elevated) window, or pass -AllowAdmin.") -f $AppName)
        exit 1
    }
}

$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$dataDir      = Join-Path $localAppData $DataDirName

Write-Host ">> $AppName uninstaller"
Write-Host "   Data directory: $dataDir"
if ($DryRun) { Write-Host "   (dry-run - nothing will be stopped or deleted)" }
Write-Host ""

# 1. Stop running processes

# True when $Path is under $Root (or equal), case-insensitive, with a trailing-separator
# guard so a sibling-prefix directory can never match (mirrors StaleProcessReaper).
function Test-UnderDirectory {
    param([string] $Path, [string] $Root)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    try {
        $full     = [IO.Path]::GetFullPath($Path)
        $rootFull = [IO.Path]::GetFullPath($Root)
    } catch { return $false }
    $sep = [IO.Path]::DirectorySeparatorChar
    if (-not $rootFull.EndsWith($sep)) { $rootFull += $sep }
    return $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)
}

# Every process to stop, in stop order. The desktop shell and the launcher come first: the shell holds
# desktop-shell.lock and its WebView2 profile inside the data dir, so a live shell makes the delete abort
# half-way. Then the app process, then WebView2, then the child runtimes and the trainer's Python interpreter.
# Nothing is matched by name alone: the shell, launcher and app only when their executable lives in this install
# (the data dir, the folder this script ships in, or the Velopack root), so a same-named host of another
# install or checkout keeps running; the runtimes and Python only when they live under our own data dir.
# The launcher starts the shell and the app as "dotnet.exe <dll>" (WindowsLauncherApplication,
# DesktopEngineSession.CreateStartInfo), so those are found through Win32_Process by the dll path on their
# command line; WebView2 by the data dir on its command line. A failed CIM query throws: the caller must stop.
function Get-XEUninstallTarget {
    param([Parameter(Mandatory)][string] $DataDirectory, [string[]] $InstallDirectory = @())
    $ownedNames = @('llama-server', 'sd-server', 'whisper-server', 'python', 'pythonw')
    $appRoots = @($DataDirectory) + @($InstallDirectory | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    $rows = @(Get-CimInstance -ClassName Win32_Process -Filter "Name='dotnet.exe' OR Name='msedgewebview2.exe'" -ErrorAction Stop)
    $hosts = @{ Desktop = @(); Client = @() }
    $webViews = @()
    $dataDirPattern = '(?i)' + [regex]::Escape([IO.Path]::GetFullPath($DataDirectory).TrimEnd('\', '/')) + '([\\/"\s]|$)'
    foreach ($row in $rows) {
        $commandLine = "$($row.CommandLine)"
        $entry = [pscustomobject]@{ Id = [int]$row.ProcessId; Name = $row.Name; Path = $row.ExecutablePath }
        if ($row.Name -ieq 'dotnet.exe') {
            $match = [regex]::Match($commandLine, '(?i)"([^"]*XE-Local-AI-Engine\.(Desktop|Client)\.dll)"|(\S*XE-Local-AI-Engine\.(Desktop|Client)\.dll)')
            if (-not $match.Success) { continue }
            $dll = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[3].Value }
            $kind = if ($match.Groups[2].Success) { $match.Groups[2].Value } else { $match.Groups[4].Value }
            if (@($appRoots | Where-Object { Test-UnderDirectory -Path $dll -Root $_ }).Count -gt 0) {
                # The display path names the dll: "dotnet.exe" alone would not say which host it is.
                $entry.Path = "$($row.ExecutablePath) $dll"
                if ($kind -ieq 'Desktop') { $hosts.Desktop += $entry } else { $hosts.Client += $entry }
            }
        } elseif ($commandLine -match $dataDirPattern) {
            $webViews += $entry
        }
    }

    $found = New-Object System.Collections.Generic.List[object]
    # Apphost first, then the dotnet-hosted dll, per stage: shell, launcher, app, WebView2.
    $stages = @(
        @{ Name = 'XE-Local-AI-Engine.Desktop'; Hosted = $hosts.Desktop }
        @{ Name = 'XE-Local-AI-Engine.WindowsLauncher'; Hosted = @() }
        @{ Name = 'XE-Local-AI-Engine.Client'; Hosted = $hosts.Client }
    )
    foreach ($stage in $stages) {
        foreach ($p in @(Get-Process -Name $stage.Name -ErrorAction SilentlyContinue)) {
            $path = $null
            try { $path = $p.Path } catch { $path = $null }
            if (@($appRoots | Where-Object { Test-UnderDirectory -Path $path -Root $_ }).Count -gt 0) {
                $found.Add($p)
            }
        }
        foreach ($p in $stage.Hosted) { $found.Add($p) }
    }
    foreach ($p in $webViews) { $found.Add($p) }
    foreach ($p in @(Get-Process -Name $ownedNames -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { $path = $null }
        if (Test-UnderDirectory -Path $path -Root $DataDirectory) {
            $found.Add($p)
        }
    }
    return $found.ToArray()
}

# The portable zip ships this script beside the app; a Velopack install lives in the "-app" root.
$installDirs = @($PSScriptRoot, (Join-Path $localAppData "$DataDirName-app"))

# Without a full process list a live shell or engine could be missed, and the delete would run under it.
function Get-XETargetOrExit {
    try {
        return @(Get-XEUninstallTarget -DataDirectory $dataDir -InstallDirectory $installDirs)
    } catch {
        Write-Host ">> Could not enumerate processes: $($_.Exception.Message)"
        Write-Host "   Nothing was deleted."
        exit 1
    }
}

$targets = @(Get-XETargetOrExit)

if ($targets.Count -gt 0) {
    Write-Host ">> Running $AppName processes to stop:"
    foreach ($p in $targets) {
        $path = ''
        try { $path = $p.Path } catch { $path = '(path unavailable)' }
        Write-Host ("     pid {0}  {1}" -f $p.Id, $path)
    }
    if (-not $DryRun) {
        # In list order: the shell first, then the app, whose Job Object (KILL_ON_JOB_CLOSE)
        # reaps its own child runtimes as it goes.
        foreach ($p in $targets) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { $null = $_ }
        }
        Start-Sleep -Seconds 1
        # Sweep any stragglers (a relaunched app, a child that outlived its job).
        foreach ($p in @(Get-XETargetOrExit)) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { $null = $_ }
        }
        # Deleting under a live process aborts half-way, so a survivor stops the run before step 3.
        $survivors = @(Get-XETargetOrExit)
        if ($survivors.Count -gt 0) {
            Write-Host ">> Could not stop: pid $(@($survivors | ForEach-Object { $_.Id }) -join ', ')."
            Write-Host "   Quit the app (Quit XE in the tray menu) or end these processes, then run this again."
            Write-Host "   Nothing was deleted."
            exit 1
        }
        Write-Host ">> Processes stopped."
    }
} else {
    Write-Host ">> No running $AppName processes found."
}
Write-Host ""

# 2. Velopack-managed install note

# Velopack installs per-user under %LOCALAPPDATA%\<PackId> with a "current\" dir and an
# "Update.exe" helper. Detect and point at it; do NOT delete it here.
$velopackRoot = $null
foreach ($candidate in @((Join-Path $localAppData $DataDirName), (Join-Path $localAppData "$DataDirName-app"))) {
    if ((Test-Path (Join-Path $candidate 'current')) -or (Test-Path (Join-Path $candidate 'Update.exe'))) {
        $velopackRoot = $candidate
        break
    }
}
if ($velopackRoot) {
    # Releases ship no Setup.exe, so a managed tree here is an old install, or a portable copy extracted into the
    # data folder. Its uninstaller can take the data folder with it, past every confirmation below, so this script
    # never runs it and deletes nothing; the user removes that layout by hand.
    Write-Host ">> A Velopack-managed install was detected at: $velopackRoot"
    Write-Host "   This script does not remove a managed install and deleted nothing. Remove it from"
    Write-Host "   Windows 'Apps & features', or delete the folders by hand (see the user guide)."
    exit 0
}

# 3. Delete the per-user data directory (portable / manual install)

if ($KeepData) {
    Write-Host ">> -KeepData: leaving $dataDir in place. Done."
    exit 0
}
if (-not (Test-Path -LiteralPath $dataDir)) {
    Write-Host ">> No data directory at $dataDir - nothing left to remove. Done."
    exit 0
}
# Follows every symlink or junction along the path, so two paths can be compared by where they really are.
function Resolve-XEPhysicalPath {
    param([Parameter(Mandatory)][string] $Path, [int] $Depth = 0)
    if ($Depth -gt 32) { throw "Too many links while resolving '$Path'." }
    $full = [IO.Path]::GetFullPath($Path)
    $current = [IO.Path]::GetPathRoot($full)
    $parts = $full.Substring($current.Length).Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)
    foreach ($part in $parts) {
        $current = Join-Path $current $part
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and $item.LinkType -and $item.Target) {
            $target = @($item.Target)[0]
            if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path (Split-Path -Parent $current) $target }
            $current = Resolve-XEPhysicalPath -Path $target -Depth ($Depth + 1)
        }
    }
    return $current
}

# Why deleting $DataDirectory recursively is unsafe, or $null: a malformed or drive-root path, a link at the root
# (the delete would land on its target), or an app folder inside it (the delete would take the app and this script).
function Get-XERemovalBlocker {
    param([string] $DataDirectory, [string] $InstallDirectory)
    if ([string]::IsNullOrWhiteSpace($DataDirectory) -or $DataDirectory -notmatch '^([A-Za-z]:[\\/]|[\\/])') {
        return "the data directory path '$DataDirectory' is empty or not absolute"
    }
    $full = [IO.Path]::GetFullPath($DataDirectory)
    if ($full.TrimEnd('\', '/') -eq [IO.Path]::GetPathRoot($full).TrimEnd('\', '/')) {
        return "the data directory path '$DataDirectory' is a drive root"
    }
    $item = Get-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
    if ($null -eq $item -or -not $item.PSIsContainer) {
        return "'$DataDirectory' is not a folder"
    }
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        return "'$DataDirectory' is a link (junction or symbolic link), not the real data folder"
    }
    if (-not [string]::IsNullOrWhiteSpace($InstallDirectory)) {
        $physicalData = Resolve-XEPhysicalPath -Path $full
        $physicalInstall = Resolve-XEPhysicalPath -Path $InstallDirectory
        if ($physicalInstall.TrimEnd('\', '/') -eq $physicalData.TrimEnd('\', '/') -or
            (Test-UnderDirectory -Path $physicalInstall -Root $physicalData)) {
            return "the app folder '$InstallDirectory' lies inside the data folder"
        }
    }
    return $null
}

$blocker = Get-XERemovalBlocker -DataDirectory $dataDir -InstallDirectory $PSScriptRoot
if ($blocker) {
    Write-Host ">> Refusing to delete the data folder: $blocker."
    Write-Host "   Nothing was deleted. Remove the folders by hand (see the user guide)."
    exit 1
}
if ($DryRun) {
    Write-Host ">> Dry-run: would delete $dataDir (and everything under it)."
    exit 0
}

# Asks before the data directory goes: once unless -Yes, then once more, typed, when it holds node.key, because
# without that key the database and every encrypted credential stay unreadable, even restored from a backup.
function Confirm-XEDataRemoval {
    param([Parameter(Mandatory)][string] $DataDirectory, [string] $Name = 'XE Local AI Engine', [switch] $Yes)
    if (-not $Yes) {
        Write-Host "This will permanently delete your $Name data:"
        Write-Host "  $DataDirectory"
        Write-Host "  (database, keys, settings, downloaded runtimes, and all models)."
        $reply = Read-Host 'Type "y" to delete, anything else to cancel'
        if ($reply -ne 'y') { return $false }
    }
    if (Test-Path -LiteralPath (Join-Path $DataDirectory 'node.key')) {
        Write-Host "The folder holds node.key, the key to your database and saved credentials."
        Write-Host "Without it no backup of this data can be opened again."
        $reply = Read-Host 'Type "delete key" to delete it too, anything else to cancel'
        if ($reply -ne 'delete key') { return $false }
    }
    return $true
}

# The engine pid from ready.json (DesktopReadyPublisher.Publish) when that process is still alive, or $null.
# Independent of the process scan above: a host the scan cannot recognise still blocks the delete. A process
# that started after StartedAtUtc only reuses the pid. A missing or unreadable ready.json proves nothing either way.
function Get-XERunningEnginePid {
    param([Parameter(Mandatory)][string] $DataDirectory)
    try {
        $ready = Get-Content -LiteralPath (Join-Path $DataDirectory 'ready.json') -Raw -ErrorAction Stop | ConvertFrom-Json
        $enginePid = [int]$ready.PSObject.Properties['pid'].Value
    } catch { return $null }
    if ($enginePid -le 0) { return $null }
    $process = Get-Process -Id $enginePid -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    try {
        $value = $ready.PSObject.Properties['startedAtUtc'].Value
        $startedAt = if ($value -is [DateTime]) { [DateTimeOffset]$value } else { [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture) }
        if ($process.StartTime.ToUniversalTime() -gt $startedAt.UtcDateTime) { return $null }
    } catch { $null = $_ }
    return $enginePid
}

$enginePid = Get-XERunningEnginePid -DataDirectory $dataDir
if ($null -ne $enginePid) {
    Write-Host ">> Engine pid $enginePid still running. Nothing was deleted."
    Write-Host "   Quit the app (Quit XE in the tray menu) or end that process, then run this again."
    exit 1
}

if (-not (Confirm-XEDataRemoval -DataDirectory $dataDir -Name $AppName -Yes:$Yes)) {
    Write-Host ">> Cancelled. Nothing was deleted."
    exit 0
}

Remove-Item -LiteralPath $dataDir -Recurse -Force
Write-Host ">> Removed $dataDir."
Write-Host ">> $AppName data removed. If you used the portable zip, also delete the folder"
Write-Host "   you unzipped the app into."
# The Velopack portable zip ships this script in its "current" folder; the extracted folder is the parent.
if ((Split-Path -Leaf $PSScriptRoot) -eq 'current') {
    Write-Host "   That is: $(Split-Path -Parent $PSScriptRoot)"
}
exit 0
