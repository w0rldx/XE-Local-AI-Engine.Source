<#
.SYNOPSIS
    Install-type-aware uninstaller for the XE-Local-AI-Engine Windows host agent.

.DESCRIPTION
    Removes only what the product created — processes, the managed WSL distro
    (xe-engine-runtime), manifest-owned Docker artifacts (external mode), the
    host-agent data root, binaries, and shortcuts. It never touches the WSL
    feature/platform, any non-managed distro, or any container/network/volume
    that is not declared in the runtime manifest.

    Default behavior prints a full inventory of what will be removed and then
    requires a typed 'yes' confirmation before deleting anything. Pass -Force to
    skip the prompt (MSI / automation). Pass -WhatIf for a non-destructive
    dry-run.

.PARAMETER Mode
    auto (default) | managed | external. 'auto' resolves to the runtimeMode read
    from the runtime manifest (if present), otherwise defaults to 'managed' with
    a printed assumption note.

.PARAMETER Force
    Skip the typed confirmation gate. Intended for MSI / automation.

.PARAMETER KeepModels
    Keep pulled models. In managed mode models live inside the distro, so this
    flag has no separate effect (a note is printed). In external mode it keeps
    the owned 'ollama-models' volume.

.PARAMETER KeepData
    Keep the host-agent data root (config, logs, runtime files, secrets) and the
    managed distro. In managed mode -KeepData implies -KeepModels.

.EXAMPLE
    pwsh -File uninstall-host-agent.ps1 -WhatIf
    Dry-run: print the inventory and exit without removing anything.

.EXAMPLE
    pwsh -File uninstall-host-agent.ps1 -Force
    Unattended full purge (managed) for the MSI uninstall hook.
#>
# Write-Host is intentional: this is an interactive, operator-facing uninstaller
# whose inventory/confirmation UX must render to the console (not the pipeline).
# $Force is consumed via script scope inside Confirm-Teardown.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Force')]
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('auto', 'managed', 'external')]
    [string] $Mode = 'auto',

    [switch] $Force,

    [switch] $KeepModels,

    [switch] $KeepData,

    [string] $InstallDirectory = "$env:ProgramFiles\XE-Local-AI-Engine",

    [string] $ShortcutDirectory = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\XE-Local-AI-Engine",

    [string] $DesktopDirectory = ([Environment]::GetFolderPath('CommonDesktopDirectory')),

    [string] $ManifestPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- Fixed product-owned constants (verified against plan §4 anchors) --------
$ProgramDataBase = if ([string]::IsNullOrWhiteSpace($env:ProgramData)) { '' } else { $env:ProgramData }
$ProgramDataRoot = if ([string]::IsNullOrWhiteSpace($ProgramDataBase)) { '' } else { Join-Path $ProgramDataBase 'XE-Local-AI-Engine' }
$HostAgentDataRoot = if ([string]::IsNullOrWhiteSpace($ProgramDataRoot)) { '' } else { Join-Path $ProgramDataRoot 'host-agent' }
$RuntimeMetadataPath = if ([string]::IsNullOrWhiteSpace($HostAgentDataRoot)) { '' } else { Join-Path $HostAgentDataRoot 'runtime.json' }
$DistroName = 'xe-engine-runtime'
$ModelsVolumeName = 'ollama-models'
$ProcessNames = @(
    'XE-Local-AI-Engine.Tray',
    'XE-Local-AI-Engine.HostAgent.Windows'
)
# Built defensively: skip any base whose directory could not be resolved (an
# empty base would otherwise make Join-Path throw before the teardown begins).
$ShortcutPaths = @()
foreach ($base in @($ShortcutDirectory, $DesktopDirectory)) {
    if ([string]::IsNullOrWhiteSpace($base)) {
        continue
    }

    $ShortcutPaths += (Join-Path $base 'XE-Local-AI-Engine.lnk')
    $ShortcutPaths += (Join-Path $base 'XE-Local-AI-Engine — Log Mode.lnk')
}

# --- Result accounting -------------------------------------------------------
$script:Removed = [System.Collections.Generic.List[string]]::new()
$script:Kept = [System.Collections.Generic.List[string]]::new()
$script:Absent = [System.Collections.Generic.List[string]]::new()
$script:Errors = [System.Collections.Generic.List[string]]::new()

function Write-Evidence {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    Write-Host "[uninstall-host-agent] $Message"
}

function Test-CommandPresent {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    return [bool](Get-Command -Name $Name -ErrorAction SilentlyContinue)
}

# --- Mode resolution ---------------------------------------------------------
function Resolve-ManifestPath {
    param(
        [string] $Explicit
    )

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        return $Explicit
    }

    if ([string]::IsNullOrWhiteSpace($HostAgentDataRoot)) {
        return ''
    }

    $candidates = @(
        (Join-Path $HostAgentDataRoot 'manifest.yaml'),
        (Join-Path $HostAgentDataRoot 'manifest.yml'),
        (Join-Path $HostAgentDataRoot 'manifest.json')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return ''
}

function Get-RuntimeModeFromFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    # Extracts only the runtimeMode value from a manifest (YAML, each key on its
    # own line) or runtime.json (compact single-line JSON). The pattern is NOT
    # anchored to line-start so it matches both formats. It is deliberately
    # narrow — only a quoted/unquoted identifier follows the key — so it cannot
    # accidentally capture secret values or long strings.
    $match = Select-String -LiteralPath $Path -Pattern '"?runtimeMode"?\s*:\s*"?([A-Za-z]+)"?' -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($null -eq $match) {
        return ''
    }

    return $match.Matches[0].Groups[1].Value.Trim().ToLowerInvariant()
}

function Resolve-Mode {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Requested,

        [string] $ManifestFile
    )

    # Resolution order (spec §7.3):
    #   1. explicit -Mode
    #   2. manifest runtimeMode
    #   3. runtime.json runtimeMode
    #   4. platform default (managed)

    if ($Requested -ne 'auto') {
        Write-Evidence "Mode: $Requested (explicit -Mode)"
        return $Requested
    }

    # 2. manifest
    if (-not [string]::IsNullOrWhiteSpace($ManifestFile) -and (Test-Path -LiteralPath $ManifestFile -PathType Leaf)) {
        $manifestMode = Get-RuntimeModeFromFile -Path $ManifestFile

        if ($manifestMode -eq 'external') {
            Write-Evidence "Mode: external (runtimeMode from manifest '$ManifestFile')"
            return 'external'
        }

        if ($manifestMode -eq 'managed' -or $manifestMode -eq 'native') {
            # 'native' is a Linux runtime mode; on Windows the managed teardown
            # path (WSL distro) is the correct shape.
            Write-Evidence "Mode: managed (runtimeMode '$manifestMode' from manifest '$ManifestFile')"
            return 'managed'
        }
    }

    # 3. runtime.json (present when the agent has been run at least once,
    #    even if the manifest was later removed).
    if (-not [string]::IsNullOrWhiteSpace($RuntimeMetadataPath) -and (Test-Path -LiteralPath $RuntimeMetadataPath -PathType Leaf)) {
        $jsonMode = Get-RuntimeModeFromFile -Path $RuntimeMetadataPath

        if ($jsonMode -eq 'external') {
            Write-Evidence "Mode: external (runtimeMode from runtime.json '$RuntimeMetadataPath')"
            return 'external'
        }

        if ($jsonMode -eq 'managed' -or $jsonMode -eq 'native') {
            Write-Evidence "Mode: managed (runtimeMode '$jsonMode' from runtime.json '$RuntimeMetadataPath')"
            return 'managed'
        }
    }

    # 4. platform default
    Write-Evidence "Mode: managed (assumed — no runtimeMode found in manifest or runtime.json; pass -Mode to override)"
    return 'managed'
}

# --- Inventory ---------------------------------------------------------------
function Test-DistroPresent {
    if (-not (Test-CommandPresent -Name 'wsl.exe')) {
        return $false
    }

    try {
        # wsl.exe --list --quiet emits UTF-16 LE. PowerShell decodes the bytes
        # using [Console]::OutputEncoding, which defaults to the OEM codepage and
        # will mojibake if it isn't set to Unicode. Capture raw bytes and decode
        # explicitly so the check is encoding-independent on any host.
        $prevEncoding = [Console]::OutputEncoding
        try {
            [Console]::OutputEncoding = [System.Text.Encoding]::Unicode
            $rawLines = @(& wsl.exe --list --quiet 2>$null)
        }
        finally {
            [Console]::OutputEncoding = $prevEncoding
        }
    }
    catch {
        return $false
    }

    if ($rawLines.Count -eq 0) {
        return $false
    }

    # Belt-and-suspenders: strip any residual NUL bytes that slip through when
    # the encoding isn't perfectly aligned, then normalise whitespace.
    $names = $rawLines |
        ForEach-Object { ($_ -replace "`0", '').Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    return ($names -contains $DistroName)
}

function Get-ManifestOwnedDockerTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    # Fail-closed: derive the kill-list strictly from manifest-declared names.
    # No wildcard 'docker ps'/'prune' is ever used (plan §3 / §10 invariant).
    $containers = [System.Collections.Generic.List[string]]::new()
    $networks = [System.Collections.Generic.List[string]]::new()
    $volumes = [System.Collections.Generic.List[string]]::new()

    $content = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop

    # Container names: anchor to list-item entries (`- name:`) so we only
    # capture containers[].name, not any other `name:` key (e.g. image name
    # fields, environment keys, etc.). This mirrors ContainerOwnership.Owns().
    foreach ($m in [regex]::Matches($content, '(?m)^\s+-\s+name\s*:\s*"?([A-Za-z0-9._-]+)"?\s*$')) {
        $value = $m.Groups[1].Value.Trim()
        if (-not [string]::IsNullOrWhiteSpace($value) -and -not $containers.Contains($value)) {
            $containers.Add($value)
        }
    }

    foreach ($m in [regex]::Matches($content, '(?m)^\s*"?network"?\s*:\s*"?([A-Za-z0-9._-]+)"?')) {
        $value = $m.Groups[1].Value.Trim()
        if (-not [string]::IsNullOrWhiteSpace($value) -and -not $networks.Contains($value)) {
            $networks.Add($value)
        }
    }

    # Only named Docker volumes (no path / no slash) are owned, mounted volumes.
    # Bind mounts (source begins with '/' or a drive) are host paths, not volumes.
    foreach ($m in [regex]::Matches($content, '(?m)^\s*-?\s*"?source"?\s*:\s*"?([A-Za-z0-9][A-Za-z0-9._-]*)"?\s*$')) {
        $value = $m.Groups[1].Value.Trim()
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }
        if ($value -match '[\\/]' -or $value -match '^[A-Za-z]:') {
            continue
        }
        if (-not $volumes.Contains($value)) {
            $volumes.Add($value)
        }
    }

    return [pscustomobject]@{
        Containers = $containers
        Networks   = $networks
        Volumes    = $volumes
    }
}

# --- Removal helpers (each is best-effort + ShouldProcess-gated) -------------
function Remove-ProductPath {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        # Guard: never act on an unset/empty path.
        $script:Errors.Add("$Label (refused: empty path)")
        Write-Evidence "ERROR: refusing to remove '$Label' — resolved path was empty"
        return
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        $script:Absent.Add("$Label ($Path)")
        Write-Evidence "absent : $Label ($Path)"
        return
    }

    if ($PSCmdlet.ShouldProcess($Path, "Remove $Label")) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            $script:Removed.Add("$Label ($Path)")
            Write-Evidence "removed: $Label ($Path)"
        }
        catch {
            $script:Errors.Add("$Label ($Path): $($_.Exception.Message)")
            Write-Evidence "ERROR  : $Label ($Path): $($_.Exception.Message)"
        }
    }
}

function Stop-ProductProcess {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $processes = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)

    if ($processes.Count -eq 0) {
        $script:Absent.Add("process $Name")
        Write-Evidence "absent : process $Name (not running)"
        return
    }

    if ($PSCmdlet.ShouldProcess($Name, 'Stop process')) {
        try {
            $processes | Stop-Process -Force -ErrorAction Stop
            $script:Removed.Add("process $Name")
            Write-Evidence "stopped: process $Name ($($processes.Count) instance(s))"
        }
        catch {
            $script:Errors.Add("process ${Name}: $($_.Exception.Message)")
            Write-Evidence "ERROR  : process ${Name}: $($_.Exception.Message)"
        }
    }
}

function Remove-ManagedDistro {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param()

    if ($KeepData) {
        $script:Kept.Add("WSL distro $DistroName (-KeepData)")
        Write-Evidence "keep   : WSL distro $DistroName (-KeepData)"
        return
    }

    if (-not (Test-CommandPresent -Name 'wsl.exe')) {
        $script:Absent.Add("WSL distro $DistroName (wsl.exe unavailable)")
        Write-Evidence "absent : wsl.exe not available — cannot manage distro $DistroName"
        return
    }

    if (-not (Test-DistroPresent)) {
        $script:Absent.Add("WSL distro $DistroName")
        Write-Evidence "absent : WSL distro $DistroName (not registered)"
        return
    }

    if ($PSCmdlet.ShouldProcess($DistroName, 'Terminate + unregister WSL distro (deletes its VHDX = all in-distro data)')) {
        try {
            & wsl.exe --terminate $DistroName 2>$null | Out-Null
        }
        catch {
            Write-Evidence "note   : --terminate $DistroName returned a non-fatal error; continuing to --unregister"
        }

        try {
            & wsl.exe --unregister $DistroName
            if ($LASTEXITCODE -ne 0) {
                throw "wsl.exe --unregister exited with code $LASTEXITCODE"
            }
            $script:Removed.Add("WSL distro $DistroName (unregistered)")
            Write-Evidence "removed: WSL distro $DistroName (unregistered — VHDX/data deleted)"
        }
        catch {
            $script:Errors.Add("WSL distro ${DistroName}: $($_.Exception.Message)")
            Write-Evidence "ERROR  : wsl.exe --unregister $DistroName failed: $($_.Exception.Message)"
            Write-Evidence "         Manual remediation: run 'wsl --unregister $DistroName' once wsl.exe is permitted."
        }
    }
}

function Invoke-DockerScopedRemoval {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject] $Targets
    )

    if (-not (Test-CommandPresent -Name 'docker')) {
        $script:Absent.Add('docker CLI (external teardown skipped)')
        Write-Evidence 'absent : docker CLI not available — cannot remove owned containers/network/volumes'
        Write-Evidence "         Manual remediation: remove containers $($Targets.Containers -join ', '), network $($Targets.Networks -join ', '), and (unless keeping models) volume(s) $($Targets.Volumes -join ', ')."
        return
    }

    # Order matters: containers -> network -> volumes (volume rm fails while a
    # container still references it). Strictly manifest-scoped; never wildcards.
    foreach ($container in $Targets.Containers) {
        if ($PSCmdlet.ShouldProcess($container, 'docker rm -f (owned container)')) {
            try {
                & docker rm -f $container 2>$null | Out-Null
                $script:Removed.Add("container $container")
                Write-Evidence "removed: container $container"
            }
            catch {
                $script:Errors.Add("container ${container}: $($_.Exception.Message)")
                Write-Evidence "ERROR  : docker rm -f $container failed: $($_.Exception.Message)"
            }
        }
    }

    foreach ($network in $Targets.Networks) {
        if ($PSCmdlet.ShouldProcess($network, 'docker network rm (owned network)')) {
            try {
                & docker network rm $network 2>$null | Out-Null
                $script:Removed.Add("network $network")
                Write-Evidence "removed: network $network"
            }
            catch {
                $script:Errors.Add("network ${network}: $($_.Exception.Message)")
                Write-Evidence "ERROR  : docker network rm $network failed: $($_.Exception.Message)"
            }
        }
    }

    foreach ($volume in $Targets.Volumes) {
        if ($KeepModels -and $volume -eq $ModelsVolumeName) {
            $script:Kept.Add("volume $volume (-KeepModels)")
            Write-Evidence "keep   : volume $volume (-KeepModels)"
            continue
        }

        if ($PSCmdlet.ShouldProcess($volume, 'docker volume rm (owned volume)')) {
            try {
                & docker volume rm $volume 2>$null | Out-Null
                $script:Removed.Add("volume $volume")
                Write-Evidence "removed: volume $volume"
            }
            catch {
                $script:Errors.Add("volume ${volume}: $($_.Exception.Message)")
                Write-Evidence "ERROR  : docker volume rm $volume failed: $($_.Exception.Message)"
            }
        }
    }
}

# --- Inventory printer -------------------------------------------------------
function Show-Inventory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ResolvedMode,

        [string] $ManifestFile,

        [pscustomobject] $DockerTargets
    )

    Write-Host ''
    Write-Host '==================== UNINSTALL INVENTORY ===================='
    Write-Evidence "Resolved mode      : $ResolvedMode"
    Write-Evidence "Install directory  : $InstallDirectory"
    Write-Evidence "Host-agent data    : $HostAgentDataRoot"
    Write-Evidence "KeepModels         : $($KeepModels.IsPresent)"
    Write-Evidence "KeepData           : $($KeepData.IsPresent)"
    Write-Host '------------------------------------------------------------'

    Write-Host 'Processes to stop:'
    foreach ($name in $ProcessNames) {
        $running = @(Get-Process -Name $name -ErrorAction SilentlyContinue).Count
        $state = if ($running -gt 0) { "[remove] running ($running)" } else { '[absent] not running' }
        Write-Host "  - ${name}: $state"
    }

    if ($ResolvedMode -eq 'managed') {
        Write-Host 'WSL distro:'
        if ($KeepData) {
            Write-Host "  - ${DistroName}: [keep:KeepData]"
        }
        else {
            $present = Test-DistroPresent
            $state = if ($present) { '[remove] terminate + unregister (deletes VHDX/data)' } else { '[absent] not registered' }
            Write-Host "  - ${DistroName}: $state"
        }

        if ($KeepModels -and -not $KeepData) {
            Write-Host '  note: -KeepModels has no separate effect in managed mode (models live inside the distro).'
        }
        if ($KeepData) {
            Write-Host '  note: -KeepData keeps the distro, so models are retained (implies -KeepModels).'
        }
    }
    elseif ($ResolvedMode -eq 'external') {
        Write-Host 'Docker artifacts (manifest-scoped only):'
        if ([string]::IsNullOrWhiteSpace($ManifestFile)) {
            Write-Host '  - [skip] no manifest found — refusing Docker teardown (cannot prove ownership).'
            Write-Host '           Pass -ManifestPath <file> to scope the Docker removal, or remove containers manually.'
        }
        else {
            Write-Host "  (ownership source: $ManifestFile)"
            foreach ($c in $DockerTargets.Containers) { Write-Host "  - container ${c}: [remove]" }
            foreach ($n in $DockerTargets.Networks) { Write-Host "  - network ${n}: [remove]" }
            foreach ($v in $DockerTargets.Volumes) {
                if ($KeepModels -and $v -eq $ModelsVolumeName) {
                    Write-Host "  - volume ${v}: [keep:KeepModels]"
                }
                else {
                    Write-Host "  - volume ${v}: [remove]"
                }
            }
            if ($DockerTargets.Containers.Count -eq 0 -and $DockerTargets.Networks.Count -eq 0 -and $DockerTargets.Volumes.Count -eq 0) {
                Write-Host '  - (no owned Docker artifacts declared in manifest)'
            }
        }
        Write-Host '  note: non-owned containers/networks/volumes are NOT touched.'
    }

    Write-Host 'Data root:'
    if ($KeepData) {
        Write-Host "  - ${HostAgentDataRoot}: [keep:KeepData]"
    }
    else {
        $dataPresent = (-not [string]::IsNullOrWhiteSpace($HostAgentDataRoot)) -and (Test-Path -LiteralPath $HostAgentDataRoot)
        $state = if ($dataPresent) { '[remove] logs, runtime.json, desired-state.json, secrets, wsl/, rootfs/' } else { '[absent]' }
        Write-Host "  - ${HostAgentDataRoot}: $state"
    }

    Write-Host 'Binaries:'
    $binPresent = (-not [string]::IsNullOrWhiteSpace($InstallDirectory)) -and (Test-Path -LiteralPath $InstallDirectory)
    $binState = if ($binPresent) { '[remove]' } else { '[absent]' }
    Write-Host "  - ${InstallDirectory}: $binState"

    Write-Host 'Shortcuts:'
    foreach ($shortcut in $ShortcutPaths) {
        $state = if (Test-Path -LiteralPath $shortcut) { '[remove]' } else { '[absent]' }
        Write-Host "  - ${shortcut}: $state"
    }

    Write-Host 'Never touched: the WSL feature/platform, the WSL kernel, any other distro, non-owned Docker artifacts.'
    Write-Host '============================================================'
    Write-Host ''
}

# --- Confirmation gate -------------------------------------------------------
function Confirm-Teardown {
    if ($Force) {
        Write-Evidence 'Confirmation: skipped (-Force).'
        return $true
    }

    # -WhatIf already short-circuits every ShouldProcess call below, so a -WhatIf
    # run reaches the gate, declines, and removes nothing.
    if ($WhatIfPreference) {
        Write-Evidence 'Dry-run (-WhatIf): no confirmation requested; nothing will be removed.'
        return $true
    }

    Write-Host "Type 'yes' to permanently remove the items listed above. Anything else aborts." -ForegroundColor Yellow
    $answer = Read-Host 'Confirm'

    if ($answer -ne 'yes') {
        Write-Evidence "Aborted: confirmation was '$answer' (expected 'yes'). Nothing was removed."
        return $false
    }

    return $true
}

# --- Final summary -----------------------------------------------------------
function Show-Summary {
    Write-Host ''
    Write-Host '==================== UNINSTALL SUMMARY ====================='
    Write-Evidence "Removed: $($script:Removed.Count)"
    foreach ($item in $script:Removed) { Write-Host "  removed: $item" }
    Write-Evidence "Kept   : $($script:Kept.Count)"
    foreach ($item in $script:Kept) { Write-Host "  kept   : $item" }
    Write-Evidence "Absent : $($script:Absent.Count)"
    foreach ($item in $script:Absent) { Write-Host "  absent : $item" }
    Write-Evidence "Errors : $($script:Errors.Count)"
    foreach ($item in $script:Errors) { Write-Host "  ERROR  : $item" }
    Write-Host '============================================================'
}

# --- Main --------------------------------------------------------------------
Write-Evidence "Started: $(Get-Date -Format o)"

$manifestFile = Resolve-ManifestPath -Explicit $ManifestPath
$resolvedMode = Resolve-Mode -Requested $Mode -ManifestFile $manifestFile

if (-not [string]::IsNullOrWhiteSpace($RuntimeMetadataPath) -and (Test-Path -LiteralPath $RuntimeMetadataPath -PathType Leaf)) {
    Write-Evidence "runtime.json present at $RuntimeMetadataPath (install detected)."
}
else {
    Write-Evidence "runtime.json absent at $RuntimeMetadataPath (may already be uninstalled)."
}

$dockerTargets = $null
if ($resolvedMode -eq 'external' -and -not [string]::IsNullOrWhiteSpace($manifestFile)) {
    $dockerTargets = Get-ManifestOwnedDockerTarget -Path $manifestFile
}

Show-Inventory -ResolvedMode $resolvedMode -ManifestFile $manifestFile -DockerTargets $dockerTargets

if (-not (Confirm-Teardown)) {
    exit 2
}

# --- Teardown (safe order) ---------------------------------------------------
# 1. Stop processes first so nothing holds the distro / files open.
foreach ($name in $ProcessNames) {
    Stop-ProductProcess -Name $name
}

# 2. Mode-specific runtime teardown.
if ($resolvedMode -eq 'managed') {
    Remove-ManagedDistro

    if ($KeepModels -and -not $KeepData) {
        Write-Evidence 'note   : -KeepModels has no separate effect in managed mode (models live inside the distro).'
    }
}
elseif ($resolvedMode -eq 'external') {
    if ([string]::IsNullOrWhiteSpace($manifestFile)) {
        Write-Evidence 'note   : external mode but no manifest found — Docker teardown skipped (cannot prove ownership). Remove owned containers manually.'
        $script:Kept.Add('Docker artifacts (no manifest — ownership unprovable)')
    }
    else {
        Invoke-DockerScopedRemoval -Targets $dockerTargets
    }
}

# 3. Data root (config, logs, runtime files, secrets, wsl/, rootfs/).
if ($KeepData) {
    $script:Kept.Add("data root $HostAgentDataRoot (-KeepData)")
    Write-Evidence "keep   : data root $HostAgentDataRoot (-KeepData)"
}
else {
    Remove-ProductPath -Path $HostAgentDataRoot -Label 'host-agent data root'
}

# 4. Binaries.
Remove-ProductPath -Path $InstallDirectory -Label 'install directory (binaries)'

# 5. Shortcuts.
foreach ($shortcut in $ShortcutPaths) {
    Remove-ProductPath -Path $shortcut -Label 'shortcut'
}

Show-Summary
Write-Evidence "Completed: $(Get-Date -Format o)"

if ($script:Errors.Count -gt 0) {
    exit 1
}

exit 0
