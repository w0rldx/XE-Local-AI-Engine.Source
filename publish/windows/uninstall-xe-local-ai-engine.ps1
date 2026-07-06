<#
.SYNOPSIS
    Remove XE Local AI Engine (Windows).

.DESCRIPTION
    What this does, in order:
      1. Stops any running XE Local AI Engine process and the llama-server / sd-server
         child runtimes THIS app spawned (matched strictly by executable path under the
         app's own per-user data directory - an unrelated llama-server/Ollama is never
         touched, mirroring the app's own StaleLlamaServerReaper).
      2. If a Velopack-managed install is detected, notes that Velopack/the OS uninstall
         owns the app binaries and points at it (this script does not delete a managed
         install tree - Velopack does).
      3. After an explicit confirmation, deletes ONLY the per-user data directory
         (%LOCALAPPDATA%\XE-Local-AI-Engine): node.sqlite, node.key, node-settings.json,
         hf-token.enc, the downloaded llama.cpp / stable-diffusion.cpp binaries, the
         GGUF/image models, and the AgentHome workspace.

    It NEVER deletes anything outside that exact directory. Portable-zip users: after
    running this, also delete the folder you unzipped the app into.

.PARAMETER Yes
    Non-interactive: skip the delete confirmation prompt.

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
$BinaryName  = 'XE-Local-AI-Engine.Client'   # running-process base name (no .exe)
$DataDirName = 'XE-Local-AI-Engine'          # per-user data directory name (no ".Client")
$ChildNames  = @('llama-server', 'sd-server')

# Per-user uninstall: an elevated session can resolve a different profile's LOCALAPPDATA.
if (-not $AllowAdmin) {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Error (("Do not run this uninstaller elevated. {0} stores its data under the " +
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

# --- 1. Stop running processes ------------------------------------------------------

# True when $Path is under $Root (or equal), case-insensitive, with a trailing-separator
# guard so a sibling-prefix directory can never match (mirrors StaleLlamaServerReaper).
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

$targets = New-Object System.Collections.Generic.List[object]

# The app process itself (matched by name - a single, specific binary name).
foreach ($p in @(Get-Process -Name $BinaryName -ErrorAction SilentlyContinue)) {
    $targets.Add($p)
}
# Child runtimes: only those whose executable lives under our own data dir.
foreach ($p in @(Get-Process -Name $ChildNames -ErrorAction SilentlyContinue)) {
    $path = $null
    try { $path = $p.Path } catch { $path = $null }
    if (Test-UnderDirectory -Path $path -Root $dataDir) {
        $targets.Add($p)
    }
}

if ($targets.Count -gt 0) {
    Write-Host ">> Running $AppName processes to stop:"
    foreach ($p in $targets) {
        $path = ''
        try { $path = $p.Path } catch { $path = '(path unavailable)' }
        Write-Host ("     pid {0}  {1}" -f $p.Id, $path)
    }
    if (-not $DryRun) {
        # Stop the app first: terminating it closes its Job Object (KILL_ON_JOB_CLOSE),
        # which reaps its own llama-server/sd-server children.
        foreach ($p in $targets) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { $null = $_ }
        }
        Start-Sleep -Seconds 1
        # Sweep any child-runtime stragglers still resident under our data dir.
        foreach ($p in @(Get-Process -Name $ChildNames -ErrorAction SilentlyContinue)) {
            $path = $null
            try { $path = $p.Path } catch { $path = $null }
            if (Test-UnderDirectory -Path $path -Root $dataDir) {
                try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { $null = $_ }
            }
        }
        Write-Host ">> Processes stopped."
    }
} else {
    Write-Host ">> No running $AppName processes found."
}
Write-Host ""

# --- 2. Velopack-managed install note ----------------------------------------------

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
    # A Velopack-managed install owns its own tree, and on the default Windows layout
    # that tree ($env:LOCALAPPDATA\XE-Local-AI-Engine) is ALSO where the app writes its
    # data. We must NOT brute-force Remove-Item a live managed install. Delegate to
    # Velopack's own uninstall and stop here. The portable/manual zip these scripts ship
    # for is NOT managed, so it never reaches this branch.
    Write-Host ">> A Velopack-managed install was detected at: $velopackRoot"
    $updateExe = Join-Path $velopackRoot 'Update.exe'
    if ((Test-Path -LiteralPath $updateExe) -and -not $DryRun) {
        $doUninstall = $Yes
        if (-not $Yes) {
            $reply = Read-Host 'Run Velopack uninstall now (removes the app + registration)? Type "y" to run'
            $doUninstall = ($reply -eq 'y' -or $reply -eq 'Y')
        }
        if ($doUninstall) {
            try {
                Write-Host ">> Running Velopack uninstall: $updateExe --uninstall"
                & $updateExe --uninstall
                Write-Host ">> Velopack uninstall invoked."
            } catch {
                Write-Host ">> Velopack uninstall could not be run automatically."
                Write-Host "   Uninstall the app from Windows 'Apps & features' instead."
            }
        }
    } else {
        Write-Host "   Uninstall the app from Windows 'Apps & features' (or run its"
        Write-Host "   Update.exe --uninstall)."
    }
    Write-Host ">> This script will not delete a managed install tree. Processes were"
    Write-Host "   already stopped above."
    exit 0
}

# --- 3. Delete the per-user data directory (portable / manual install) -------------

if ($KeepData) {
    Write-Host ">> -KeepData: leaving $dataDir in place. Done."
    exit 0
}
if (-not (Test-Path -LiteralPath $dataDir)) {
    Write-Host ">> No data directory at $dataDir - nothing left to remove. Done."
    exit 0
}
if ($DryRun) {
    Write-Host ">> Dry-run: would delete $dataDir (and everything under it)."
    exit 0
}

if (-not $Yes) {
    Write-Host "This will permanently delete your $AppName data:"
    Write-Host "  $dataDir"
    Write-Host "  (database, keys, settings, downloaded runtimes, and all models)."
    $reply = Read-Host 'Type "y" to delete, anything else to cancel'
    if ($reply -ne 'y' -and $reply -ne 'Y') {
        Write-Host ">> Cancelled. Nothing was deleted."
        exit 0
    }
}

# Guard: refuse to operate on an empty/blank path.
if ([string]::IsNullOrWhiteSpace($dataDir)) {
    Write-Error "Data directory path is unexpectedly empty; refusing to delete."
    exit 1
}
Remove-Item -LiteralPath $dataDir -Recurse -Force
Write-Host ">> Removed $dataDir."
Write-Host ">> $AppName data removed. If you used the portable zip, also delete the folder"
Write-Host "   you unzipped the app into."
