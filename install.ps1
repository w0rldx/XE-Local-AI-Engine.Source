#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Pre,
    [string]$InstallDir,
    [switch]$Yes,
    [string]$GitHubToken,
    [switch]$DryRun,
    [switch]$Setup,
    [switch]$Start,
    [switch]$Autostart,
    [switch]$NoAutostart,
    [switch]$InstallSkill,
    [string]$GitHubApiBase,
    [string]$DownloadBase,
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Repository = 'w0rldx/XE-Local-AI-Engine.Source'
$script:DefaultApiBase = 'https://api.github.com'
$script:DefaultDownloadBase = 'https://github.com'
$script:OwnershipMarker = '.xe-local-ai-engine-install'
$script:OwnershipValue = 'XE_LOCAL_AI_ENGINE_INSTALL=1'
$script:AutostartOwnershipMarker = '.xe-local-ai-engine-autostart'
$script:AutostartOwnershipValue = 'XE_LOCAL_AI_ENGINE_AUTOSTART=1'
# These script parameters are consumed through Invoke-XEInstaller's script-scope closure. Make
# that use explicit for static analysis while retaining pipe-to-iex compatibility.
$null = $Version, $Pre, $InstallDir, $Yes, $GitHubToken, $DryRun, $Setup, $Start,
    $Autostart, $NoAutostart, $InstallSkill, $GitHubApiBase, $DownloadBase, $Help

function Write-InstallerHelp {
    @'
Usage: install.ps1 [-Version VERSION] [-Pre] [-InstallDir DIR] [-Yes]
                   [-GitHubToken TOKEN] [-DryRun] [-Help]

-Setup configures the administrator and emits one agentic MCP key. -Start
launches MCP-only mode. -Autostart is opt-in and current-user scoped.
-InstallSkill installs the release-pinned skill for both supported user roots.

Exit codes: 0 success; 1 generic/usage; 2 unsupported platform or asset;
3 checksum mismatch; 4 network or release-not-found; 10-14 reserved feature failures.
'@ | Write-Output
}

function ConvertTo-XETag {
    param([Parameter(Mandatory)][string]$Value)
    if ($Value.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) { return $Value }
    return "v$Value"
}

function Assert-XENetworkBase {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Url
    )
    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri)) {
        throw "$Name is not an absolute URL."
    }
    if ($uri.UserInfo) { throw "$Name must not contain URL user information." }
    if ($uri.Scheme -eq 'https') { return }
    if ($uri.Scheme -eq 'http' -and ($uri.Host -eq 'localhost' -or $uri.Host -eq '127.0.0.1')) { return }
    throw "$Name must use HTTPS; loopback HTTP is allowed only for explicit tests."
}

function Get-XERequestHeader {
    param([string]$Token, [string]$Uri)
    $headers = @{ Accept = 'application/vnd.github+json' }
    if ($Token -and $Uri) {
        $parsed = [Uri]$Uri
        if ($parsed.Scheme -eq 'https' -and $parsed.Host -eq 'api.github.com') {
            $headers.Authorization = "Bearer $Token"
        }
    }
    return $headers
}

function Invoke-XEWebRequest {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [string]$OutFile,
        [string]$Token
    )
    Assert-XENetworkBase -Name 'request URL' -Url $Uri
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $parameters = @{
        Uri             = $Uri
        Headers         = (Get-XERequestHeader -Token $Token -Uri $Uri)
        UseBasicParsing = $true
        ErrorAction     = 'Stop'
    }
    if ($OutFile) { $parameters.OutFile = $OutFile }
    return Invoke-WebRequest @parameters
}

function Get-XENextLink {
    param($Response)
    $link = [string]$Response.Headers['Link']
    if ($link -match '<([^>]+)>;\s*rel="next"') { return $Matches[1] }
    return $null
}

function Resolve-XERelease {
    param(
        [string]$RequestedVersion,
        [bool]$IncludePrerelease,
        [Parameter(Mandatory)][string]$ApiBase,
        [string]$Token
    )
    try {
        if ($RequestedVersion) {
            $tag = ConvertTo-XETag -Value $RequestedVersion
            $response = Invoke-XEWebRequest -Uri "$($ApiBase.TrimEnd('/'))/repos/$script:Repository/releases/tags/$tag" -Token $Token
            return ($response.Content | ConvertFrom-Json)
        }

        $url = "$($ApiBase.TrimEnd('/'))/repos/$script:Repository/releases?per_page=100"
        $releases = @()
        while ($url) {
            $response = Invoke-XEWebRequest -Uri $url -Token $Token
            $releases += @($response.Content | ConvertFrom-Json)
            $url = Get-XENextLink -Response $response
        }
        $candidates = @($releases | Where-Object {
                -not $_.draft -and ($IncludePrerelease -or -not $_.prerelease)
            } | Sort-Object -Property published_at -Descending)
        if ($candidates.Count -eq 0) { throw 'No matching release was found.' }
        return $candidates[0]
    }
    catch {
        throw "Release resolution failed: $($_.Exception.Message)"
    }
}

function Get-XEReleaseAsset {
    param(
        [Parameter(Mandatory)]$Release,
        [Parameter(Mandatory)][string]$Suffix
    )
    $assetMatches = @($Release.assets | Where-Object { $_.name.EndsWith($Suffix, [System.StringComparison]::OrdinalIgnoreCase) })
    if ($assetMatches.Count -ne 1) {
        throw "Release $($Release.tag_name) must contain exactly one *$Suffix asset (found $($assetMatches.Count))."
    }
    return $assetMatches[0]
}

function Get-XEDownloadUrl {
    param(
        [Parameter(Mandatory)][string]$OriginalUrl,
        [Parameter(Mandatory)][string]$BaseUrl
    )
    if ($BaseUrl -eq $script:DefaultDownloadBase) { return $OriginalUrl }
    $original = [Uri]$OriginalUrl
    return "$($BaseUrl.TrimEnd('/'))$($original.PathAndQuery)"
}

function Get-XEExpectedChecksum {
    param(
        [Parameter(Mandatory)][string]$ChecksumText,
        [Parameter(Mandatory)][string]$AssetName
    )
    foreach ($line in ($ChecksumText -split "`r?`n")) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\./(.+)$' -and $Matches[2] -ceq $AssetName) {
            return $Matches[1].ToLowerInvariant()
        }
    }
    return $null
}

function Test-XEReleaseManifest {
    param(
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$AssetName,
        [Parameter(Mandatory)][string]$AssetHash
    )
    $entry = @($Manifest.assets | Where-Object { $_.name -ceq $AssetName })
    return $Manifest.tag -ceq $Tag -and $entry.Count -eq 1 -and
        ([string]$entry[0].sha256).ToLowerInvariant() -eq $AssetHash.ToLowerInvariant()
}

function Resolve-XEInstallPath {
    param([Parameter(Mandatory)][string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootPath = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) { return $rootPath }
    return $fullPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-XESafeInstallPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$UserProfile,
        [Parameter(Mandatory)][string]$AppDataPath
    )
    $canonical = Resolve-XEInstallPath -Path $Path
    $root = [IO.Path]::GetPathRoot($canonical)
    $profilePath = Resolve-XEInstallPath -Path $UserProfile
    $data = Resolve-XEInstallPath -Path $AppDataPath
    if ($canonical.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
        $canonical.Equals($profilePath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Install directory must not be a filesystem root or the user profile directory.'
    }
    if ($canonical.Equals($data, [StringComparison]::OrdinalIgnoreCase) -or
        $canonical.StartsWith("$data$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase) -or
        $data.StartsWith("$canonical$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Install directory must not be the app-owned data directory.'
    }
    if (Test-Path -LiteralPath $canonical -PathType Leaf) { throw 'Install path exists and is not a directory.' }
    if (Test-Path -LiteralPath $canonical -PathType Container) {
        $entries = @(Get-ChildItem -LiteralPath $canonical -Force)
        if ($entries.Count -gt 0 -and -not (Test-XEInstallOwned -Path $canonical)) {
            throw "Refusing to replace non-empty directory without a valid $script:OwnershipMarker marker."
        }
    }
    return $canonical
}

function Test-XEInstallOwned {
    param([Parameter(Mandatory)][string]$Path)
    $marker = Join-Path $Path $script:OwnershipMarker
    return (Test-Path -LiteralPath $marker -PathType Leaf) -and
        (Get-Content -LiteralPath $marker -Raw).Trim() -ceq $script:OwnershipValue
}

function Test-XEInstallComplete {
    param([Parameter(Mandatory)][string]$Path)
    return (Test-Path -LiteralPath (Join-Path $Path 'XE-Local-AI-Engine.exe') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Path 'current/XE-Local-AI-Engine.Client.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Path 'current/XE-Local-AI-Engine.Client.runtimeconfig.json') -PathType Leaf)
}

function Test-XEAspNetCoreRuntimeCompatible {
    param(
        [Parameter(Mandatory)][string]$RequiredVersion,
        [Parameter(Mandatory)][AllowEmptyString()][string]$RuntimeInventory
    )
    $required = $null
    if (-not [System.Version]::TryParse($RequiredVersion, [ref]$required)) { return $false }
    foreach ($line in ($RuntimeInventory -split "`r?`n")) {
        if ($line -notmatch '^Microsoft\.AspNetCore\.App\s+([^\s]+)\s+') { continue }
        $installed = $null
        if (-not [System.Version]::TryParse($Matches[1], [ref]$installed)) { continue }
        if ($installed.Major -eq $required.Major -and
            $installed.Minor -eq $required.Minor -and
            $installed.Build -ge $required.Build) { return $true }
    }
    return $false
}

function Get-XERequiredAspNetCoreRuntime {
    param([Parameter(Mandatory)][string]$RuntimeConfigPath)
    $config = Get-Content -LiteralPath $RuntimeConfigPath -Raw | ConvertFrom-Json
    $framework = @($config.runtimeOptions.frameworks | Where-Object { $_.name -eq 'Microsoft.AspNetCore.App' })
    if ($framework.Count -ne 1 -or -not $framework[0].version) {
        throw 'The installed runtimeconfig does not name exactly one Microsoft.AspNetCore.App framework.'
    }
    return [string]$framework[0].version
}

function Assert-XEAspNetCoreRuntime {
    param(
        [Parameter(Mandatory)][string]$InstallPath,
        [AllowNull()][string]$RuntimeInventory
    )
    $runtimeConfig = Join-Path $InstallPath 'current/XE-Local-AI-Engine.Client.runtimeconfig.json'
    if (-not (Test-Path -LiteralPath $runtimeConfig)) { throw "Required runtimeconfig is missing: $runtimeConfig" }
    $required = Get-XERequiredAspNetCoreRuntime -RuntimeConfigPath $runtimeConfig
    if ($null -eq $RuntimeInventory) {
        $RuntimeInventory = ''
        if (Get-Command dotnet -ErrorAction SilentlyContinue) { $RuntimeInventory = (& dotnet --list-runtimes 2>$null) -join "`n" }
    }
    if (-not (Test-XEAspNetCoreRuntimeCompatible -RequiredVersion $required -RuntimeInventory $RuntimeInventory)) {
        throw "Microsoft.AspNetCore.App $required or later in the same major/minor is required. Download: https://dotnet.microsoft.com/en-us/download/dotnet/10.0. Hint (verify the package id on Windows): winget install --id Microsoft.DotNet.AspNetCore.10 --version $required --exact --silent --accept-package-agreements --accept-source-agreements"
    }
}

function Test-XEInstallReusable {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Version,
        [AllowNull()][string]$RuntimeInventory
    )
    if (-not (Test-XEInstallOwned -Path $Path) -or -not (Test-XEInstallComplete -Path $Path)) { return $false }
    $versionFile = Join-Path $Path 'xe-install-version.txt'
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf) -or
        (Get-Content -LiteralPath $versionFile -Raw).Trim() -cne $Version) { return $false }
    Assert-XEAspNetCoreRuntime -InstallPath $Path -RuntimeInventory $RuntimeInventory
    return $true
}

function Install-XEArchive {
    param(
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)][string]$Version,
        [AllowNull()][string]$RuntimeInventory,
        [switch]$TestFailAfterBackup,
        [switch]$TestFailBackupCleanup
    )
    $parent = Split-Path -Parent $TargetPath
    New-Item -ItemType Directory -Path $parent -Force -ErrorAction Stop | Out-Null
    $backup = $null
    $hadEmptyTarget = $false
    try {
        if (Test-Path -LiteralPath $TargetPath -PathType Container) {
            $entries = @(Get-ChildItem -LiteralPath $TargetPath -Force)
            if ($entries.Count -gt 0) {
                if (-not (Test-XEInstallOwned -Path $TargetPath)) { throw 'Refusing to replace an unowned install directory.' }
                $backup = Join-Path $parent ".xe-install-backup-$([Guid]::NewGuid().ToString('N'))"
                [IO.Directory]::Move($TargetPath, $backup)
            }
            else {
                $hadEmptyTarget = $true
                Remove-Item -LiteralPath $TargetPath -ErrorAction Stop
            }
        }

        New-Item -ItemType Directory -Path $TargetPath -ErrorAction Stop | Out-Null
        Unblock-File -LiteralPath $Archive -ErrorAction Stop
        if ($TestFailAfterBackup) { throw 'Injected extraction failure after backup.' }
        # Velopack portable layouts bind their updater to this final path. Never extract elsewhere and move.
        Expand-Archive -LiteralPath $Archive -DestinationPath $TargetPath -Force -ErrorAction Stop
        if (-not (Test-XEInstallComplete -Path $TargetPath)) { throw 'Extracted payload is incomplete.' }
        Assert-XEAspNetCoreRuntime -InstallPath $TargetPath -RuntimeInventory $RuntimeInventory
        Set-Content -LiteralPath (Join-Path $TargetPath 'xe-install-version.txt') -Value $Version -Encoding ASCII -ErrorAction Stop
        Set-Content -LiteralPath (Join-Path $TargetPath $script:OwnershipMarker) -Value $script:OwnershipValue -Encoding ASCII -ErrorAction Stop

    }
    catch {
        if (Test-Path -LiteralPath $TargetPath) {
            Remove-Item -LiteralPath $TargetPath -Recurse -Force -ErrorAction Stop
        }
        if ($backup -and (Test-Path -LiteralPath $backup)) {
            [IO.Directory]::Move($backup, $TargetPath)
            $backup = $null
        }
        elseif ($hadEmptyTarget) {
            New-Item -ItemType Directory -Path $TargetPath -ErrorAction Stop | Out-Null
        }
        throw
    }

    # Commit point: the final target is complete, runtime-valid, and marked. Backup disposal must
    # never enter the rollback catch above; cleanup failure preserves the validated new install.
    if ($backup) {
        try {
            if ($TestFailBackupCleanup) {
                $backupMarker = Join-Path $backup $script:OwnershipMarker
                if (Test-Path -LiteralPath $backupMarker) {
                    Remove-Item -LiteralPath $backupMarker -Force -ErrorAction Stop
                }
                throw 'Injected backup cleanup failure after partial disposal.'
            }
            Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "The new installation is committed, but rollback backup cleanup failed. Retained backup: $backup. $($_.Exception.Message)"
        }
    }
}

function Get-XEDataDirectory {
    if ($env:XE_DATA_DIR) { return (Resolve-XEInstallPath -Path $env:XE_DATA_DIR) }
    return (Join-Path $env:LOCALAPPDATA 'XE-Local-AI-Engine')
}

function Assert-XEAutostartPath {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Path
    )
    if (-not [IO.Path]::IsPathRooted($Path)) { throw "$Name must be an absolute path." }
    foreach ($character in $Path.ToCharArray()) {
        if ([char]::IsControl($character)) { throw "$Name contains unsupported control characters." }
    }
    return (Resolve-XEInstallPath -Path $Path)
}

function Resolve-XESetupCredential {
    param([bool]$NonInteractive)
    $email = $env:XE_ADMIN_EMAIL
    $password = $env:XE_ADMIN_PASSWORD
    if (-not $email -and -not $NonInteractive) { $email = Read-Host 'Administrator email' }
    if (-not $password -and -not $NonInteractive) {
        $secure = Read-Host 'Administrator password' -AsSecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    }
    if (-not $email -or -not $password) { throw '--setup requires XE_ADMIN_EMAIL and XE_ADMIN_PASSWORD in non-interactive mode.' }
    return [pscustomobject]@{ Email = $email; Password = $password }
}

function Get-XESanitizedDiagnostic {
    param(
        [AllowNull()][string[]]$Lines,
        [AllowNull()][string]$Secret
    )
    $sanitized = @()
    foreach ($lineValue in @($Lines)) {
        $line = [string]$lineValue
        if ($line -match '^XE_(SETUP|ADMIN_EMAIL|MCP_KEY)=') { continue }
        if ($Secret) { $line = $line -replace [regex]::Escape($Secret), '[REDACTED]' }
        $line = $line -replace 'xemcp_[^\s]+', '[REDACTED]'
        if ($line.Length -gt 500) { $line = $line.Substring(0, 500) }
        $sanitized += $line
        if ($sanitized.Count -ge 5) { break }
    }
    if ($sanitized.Count -eq 0) { return 'no sanitized engine diagnostic output' }
    return ($sanitized -join ' | ')
}

function Invoke-XESetup {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [bool]$NonInteractive
    )
    $credentials = Resolve-XESetupCredential -NonInteractive $NonInteractive
    $passwordForRedaction = $credentials.Password
    $oldEmail = $env:XE_ADMIN_EMAIL
    $oldPassword = $env:XE_ADMIN_PASSWORD
    try {
        $env:XE_ADMIN_EMAIL = $credentials.Email
        $env:XE_ADMIN_PASSWORD = $credentials.Password
        $setupOutput = @(& $Exe '--setup' 2>&1 | ForEach-Object { [string]$_ })
        $setupCode = $LASTEXITCODE
    }
    finally {
        $env:XE_ADMIN_EMAIL = $oldEmail
        $env:XE_ADMIN_PASSWORD = $oldPassword
    }
    if ($setupCode -ne 0) {
        $diagnostic = Get-XESanitizedDiagnostic -Lines $setupOutput -Secret $passwordForRedaction
        $credentials.Password = $null
        $passwordForRedaction = $null
        throw "Engine --setup exited with engine code $setupCode`: $diagnostic"
    }
    $credentials.Password = $null
    $passwordForRedaction = $null
    $setupLines = @($setupOutput | Where-Object { $_ -match '^XE_SETUP=(created|already-configured)$' })
    if ($setupLines.Count -ne 1) { throw 'Engine --setup did not return exactly one valid XE_SETUP line.' }
    $emailLines = @($setupOutput | Where-Object { $_ -match '^XE_ADMIN_EMAIL=[^\p{C}]+$' })
    if (($setupLines[0] -ceq 'XE_SETUP=created' -and $emailLines.Count -ne 1) -or
        ($setupLines[0] -ceq 'XE_SETUP=already-configured' -and $emailLines.Count -ne 0)) {
        throw 'Engine --setup returned an invalid XE_ADMIN_EMAIL contract.'
    }
    $setupLines[0] | Write-Output
    $emailLines | Write-Output

    $keyOutput = @(& $Exe '--mcp-key' 'agentic' 2>&1 | ForEach-Object { [string]$_ })
    $keyCode = $LASTEXITCODE
    if ($keyCode -ne 0) {
        $diagnostic = Get-XESanitizedDiagnostic -Lines $keyOutput -Secret $null
        throw "Engine --mcp-key agentic exited with engine code $keyCode`: $diagnostic"
    }
    $keyLines = @($keyOutput | Where-Object { $_ -match '^XE_MCP_KEY=xemcp_[^\s\p{C}]+$' })
    if ($keyLines.Count -ne 1) { throw 'Engine --mcp-key agentic did not return exactly one XE_MCP_KEY line.' }
    $keyLines[0] | Write-Output
}

function Test-XEReadyEvidence {
    param(
        [Parameter(Mandatory)]$Ready,
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$DataDirectory
    )
    if ([int]$Ready.pid -ne $ProcessId -or [string]$Ready.dataDir -cne $DataDirectory -or
        -not [string]$Ready.version -or -not [string]$Ready.startedAtUtc) { return $false }
    $url = $null
    if (-not [Uri]::TryCreate([string]$Ready.url, [UriKind]::Absolute, [ref]$url) -or
        $url.Scheme -ne 'http' -or $url.Port -lt 1 -or $url.Port -gt 65535 -or
        $url.Host -ne '127.0.0.1' -or $url.AbsolutePath -ne '/' -or
        $url.UserInfo -or $url.Query -or $url.Fragment) { return $false }
    return [string]$Ready.mcpUrl -ceq "$($url.GetLeftPart([UriPartial]::Authority))/api/local/v1/mcp/server"
}

function Start-XEEngine {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string]$DataDirectory,
        [int]$TimeoutSeconds = 60
    )
    if (-not $PSCmdlet.ShouldProcess($Exe, 'Start in MCP-only mode')) { return }
    if ($TimeoutSeconds -lt 0) { throw 'The readiness timeout must be a non-negative integer.' }
    $logOut = Join-Path (Split-Path -Parent $Exe) 'xe-mcp-only.stdout.log'
    $logError = Join-Path (Split-Path -Parent $Exe) 'xe-mcp-only.stderr.log'
    $process = Start-Process -FilePath $Exe -ArgumentList '--mcp-only' -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $logOut -RedirectStandardError $logError
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $readyPath = Join-Path $DataDirectory 'ready.json'
    while ([DateTime]::UtcNow -le $deadline) {
        if ($process.HasExited) { throw "The engine exited before readiness with engine code $($process.ExitCode)." }
        if (Test-Path -LiteralPath $readyPath -PathType Leaf) {
            try {
                $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
                if (Test-XEReadyEvidence -Ready $ready -ProcessId $process.Id -DataDirectory $DataDirectory) {
                    Invoke-WebRequest -Uri "$($ready.url)/health/ready" -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop | Out-Null
                    $readyLine = "XE_READY=1 XE_VERSION=$($ready.version) XE_URL=$($ready.url) XE_MCP_URL=$($ready.mcpUrl) XE_DATA_DIR=$DataDirectory"
                    $captured = if (Test-Path -LiteralPath $logOut -PathType Leaf) {
                        @(Get-Content -LiteralPath $logOut | Where-Object { $_ -ceq $readyLine })
                    }
                    else { @() }
                    if ($captured.Count -eq 1) { $captured[0] | Write-Output } else { $readyLine | Write-Output }
                    "XE_PID=$($process.Id)" | Write-Output
                    return
                }
            }
            catch { $ready = $null }
        }
        Start-Sleep -Milliseconds 200
    }
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    throw [TimeoutException]::new("Engine did not produce live canonical ready.json evidence and a healthy /health/ready response within ${TimeoutSeconds}s.")
}

function Assert-XEAutostartOwnership {
    param(
        [Parameter(Mandatory)][string]$LauncherDirectory,
        [AllowNull()]$ExistingTask
    )
    if (-not (Test-Path -LiteralPath $LauncherDirectory)) {
        if ($null -ne $ExistingTask) { throw 'Existing Scheduled Task has no installer-owned launcher directory.' }
        return $false
    }
    $directory = Get-Item -LiteralPath $LauncherDirectory -Force -ErrorAction Stop
    if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Existing autostart launcher path is not a regular installer-owned directory.'
    }
    $marker = Join-Path $LauncherDirectory $script:AutostartOwnershipMarker
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
        throw 'Existing autostart launcher has no installer ownership marker.'
    }
    $markerItem = Get-Item -LiteralPath $marker -Force
    if (($markerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        (Get-Content -LiteralPath $marker -Raw).Trim() -cne $script:AutostartOwnershipValue) {
        throw 'Existing autostart launcher has no valid installer ownership marker.'
    }
    foreach ($item in @(Get-ChildItem -LiteralPath $LauncherDirectory -Force)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Existing autostart content is a reparse point: $($item.Name)"
        }
        if ($item.Name -cne $script:AutostartOwnershipMarker -and $item.Name -cnotmatch '^launch-[a-f0-9]{64}\.ps1$') {
            throw "Existing autostart launcher directory contains unowned content: $($item.Name)"
        }
    }
    if ($null -ne $ExistingTask) {
        $ownedAction = @($ExistingTask.Actions | Where-Object {
                [string]$_.Arguments -match [regex]::Escape($LauncherDirectory) -and
                [string]$_.Arguments -match 'launch-[a-f0-9]{64}\.ps1'
            })
        if ($ownedAction.Count -ne 1) { throw 'Existing Scheduled Task is not bound to an installer-owned launcher.' }
    }
    return $true
}

function Write-XEAutostartLauncher {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string]$DataDirectory,
        [Parameter(Mandatory)][string]$LauncherDirectory
    )
    $resolvedExe = Assert-XEAutostartPath -Name 'Engine executable' -Path $Exe
    $resolvedData = Assert-XEAutostartPath -Name 'Engine data directory' -Path $DataDirectory
    $quotedExe = $resolvedExe.Replace("'", "''")
    $quotedData = $resolvedData.Replace("'", "''")
    $content = @"
`$ErrorActionPreference = 'Stop'
`$env:XE_DATA_DIR = '$quotedData'
& '$quotedExe' '--mcp-only'
exit `$LASTEXITCODE
"@
    $encoding = New-Object Text.UTF8Encoding($true)
    $body = $encoding.GetBytes($content)
    $bytes = New-Object byte[] ($encoding.GetPreamble().Length + $body.Length)
    [Array]::Copy($encoding.GetPreamble(), 0, $bytes, 0, $encoding.GetPreamble().Length)
    [Array]::Copy($body, 0, $bytes, $encoding.GetPreamble().Length, $body.Length)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
    $launcher = Join-Path $LauncherDirectory "launch-$digest.ps1"
    if (Test-Path -LiteralPath $launcher) {
        $existingDigest = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existingDigest -cne $digest) {
            throw 'Existing versioned autostart launcher does not match its content digest.'
        }
        return $launcher
    }
    $temporary = Join-Path $LauncherDirectory "launch.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllBytes($temporary, $bytes)
        Move-Item -LiteralPath $temporary -Destination $launcher -Force -ErrorAction Stop
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
    return $launcher
}

function Register-XEAutostart {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string]$DataDirectory
    )
    $taskName = 'XE Local AI Engine'
    $launcherDirectory = Assert-XEAutostartPath -Name 'Autostart launcher directory' `
        -Path (Join-Path $env:LOCALAPPDATA 'XE-Local-AI-Engine-Autostart')
    $existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    $hadDirectory = Assert-XEAutostartOwnership -LauncherDirectory $launcherDirectory -ExistingTask $existingTask
    $priorXml = if ($null -ne $existingTask) { Export-ScheduledTask -TaskName $taskName } else { $null }
    $launchersBefore = @()
    $launcher = $null
    try {
        if (-not $hadDirectory) {
            New-Item -ItemType Directory -Path $launcherDirectory -ErrorAction Stop | Out-Null
            Set-Content -LiteralPath (Join-Path $launcherDirectory $script:AutostartOwnershipMarker) `
                -Value $script:AutostartOwnershipValue -Encoding Ascii -NoNewline
        }
        $launchersBefore = @((Get-ChildItem -LiteralPath $launcherDirectory -Filter 'launch-*.ps1' -File).FullName)
        $launcher = Write-XEAutostartLauncher -Exe $Exe -DataDirectory $DataDirectory -LauncherDirectory $launcherDirectory
        $windowsPowerShell = if ($env:SystemRoot) {
            [IO.Path]::Combine($env:SystemRoot, 'System32\WindowsPowerShell\v1.0\powershell.exe')
        }
        else { 'powershell.exe' }
        $arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$launcher`""
        $action = New-ScheduledTaskAction -Execute $windowsPowerShell -Argument $arguments
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
        if ($env:XE_TEST_FAIL_AUTOSTART_REGISTER -eq '1') { throw 'Injected Scheduled Task registration failure.' }
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
    }
    catch {
        $registrationError = $_.Exception.Message
        $rollbackErrors = @()
        try {
            if ($null -ne $priorXml) {
                Register-ScheduledTask -TaskName $taskName -Xml $priorXml -Force | Out-Null
            }
            elseif ($null -ne (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) {
                Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
            }
        }
        catch { $rollbackErrors += "task state: $($_.Exception.Message)" }
        if ($launcher -and $launchersBefore -notcontains $launcher -and (Test-Path -LiteralPath $launcher)) {
            try { Remove-Item -LiteralPath $launcher -Force -ErrorAction Stop }
            catch { $rollbackErrors += "launcher bytes: $($_.Exception.Message)" }
        }
        if (-not $hadDirectory) {
            try { Remove-Item -LiteralPath $launcherDirectory -Recurse -Force -ErrorAction Stop }
            catch { $rollbackErrors += "launcher directory: $($_.Exception.Message)" }
        }
        if ($rollbackErrors.Count -gt 0) {
            throw "$registrationError Rollback failed: $($rollbackErrors -join '; ')"
        }
        throw "$registrationError Prior launcher bytes, data directory, and Scheduled Task state were restored."
    }
}

function Expand-XESkillArchive {
    param(
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$Destination
    )
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $selected = @()
        $roots = @{}
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            $parts = @($name.Split('/') | Where-Object { $_ -ne '' })
            if ($name.StartsWith('/') -or $parts -contains '..') {
                if ($name -match 'skills/xe-local-ai-engine') { throw 'The skill archive contains an unsafe path.' }
                continue
            }
            if ($parts.Count -lt 4 -or $parts[1] -cne 'skills' -or $parts[2] -cne 'xe-local-ai-engine') { continue }
            $unixMode = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixMode -eq 0xA000) { throw 'The skill archive contains a symbolic link.' }
            $roots[$parts[0]] = $true
            $selected += [pscustomobject]@{ Entry = $entry; Relative = @($parts[3..($parts.Count - 1)]) }
        }
        if ($roots.Count -ne 1 -or $selected.Count -eq 0 -or
            @($selected | Where-Object { $_.Relative[-1] -ceq 'SKILL.md' }).Count -eq 0) {
            throw 'The source archive did not contain one skills/xe-local-ai-engine tree.'
        }
        $destinationRoot = [IO.Path]::GetFullPath($Destination)
        New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
        foreach ($item in $selected) {
            $target = [IO.Path]::GetFullPath((Join-Path $destinationRoot ([string]::Join([IO.Path]::DirectorySeparatorChar, $item.Relative))))
            if (-not $target.StartsWith("$destinationRoot$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The skill archive path escaped staging.'
            }
            if (-not $item.Entry.Name) { New-Item -ItemType Directory -Path $target -Force | Out-Null; continue }
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            $source = $item.Entry.Open()
            try {
                $output = [IO.File]::Create($target)
                try { $source.CopyTo($output) } finally { $output.Dispose() }
            }
            finally { $source.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

function Install-XESkill {
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$ApiBase,
        [string]$Token,
        [Parameter(Mandatory)][string]$Scratch
    )
    $skillScratch = Join-Path $Scratch "xe-skill-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $skillScratch -Force | Out-Null
    try {
        $archive = Join-Path $skillScratch 'source.zip'
        $stage = Join-Path $skillScratch 'skill-source'
        Invoke-XEWebRequest -Uri "$($ApiBase.TrimEnd('/'))/repos/$script:Repository/zipball/$Version" -OutFile $archive -Token $Token | Out-Null
        Expand-XESkillArchive -Archive $archive -Destination $stage
        $destinations = @(
            (Join-Path $env:USERPROFILE '.claude/skills/xe-local-ai-engine'),
            (Join-Path $env:USERPROFILE '.agents/skills/xe-local-ai-engine')
        )
        $stages = @($null, $null)
        $backups = @($null, $null)
        $committed = @($false, $false)
        try {
            for ($index = 0; $index -lt $destinations.Count; $index++) {
                $destination = $destinations[$index]
                $parent = Split-Path -Parent $destination
                New-Item -ItemType Directory -Path $parent -Force | Out-Null
                if ((Test-Path -LiteralPath $destination) -and (Get-Item -LiteralPath $destination -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
                    throw "Refusing to replace reparse-point skill destination $destination."
                }
                if ((Test-Path -LiteralPath $destination) -and -not (Test-Path -LiteralPath $destination -PathType Container)) {
                    throw "Refusing to replace non-directory skill destination $destination."
                }
                $stages[$index] = Join-Path $parent ".xe-skill-staging-$([Guid]::NewGuid().ToString('N'))"
                Copy-Item -LiteralPath $stage -Destination $stages[$index] -Recurse -Force
            }

            # Preserve both old trees before committing either new tree.
            for ($index = 0; $index -lt $destinations.Count; $index++) {
                $destination = $destinations[$index]
                if (Test-Path -LiteralPath $destination) {
                    $parent = Split-Path -Parent $destination
                    $backups[$index] = Join-Path $parent ".xe-skill-backup-$([Guid]::NewGuid().ToString('N'))"
                    [IO.Directory]::Move($destination, $backups[$index])
                }
            }

            for ($index = 0; $index -lt $destinations.Count; $index++) {
                if ($index -eq 1 -and $env:XE_TEST_FAIL_SKILL_SECOND_SWAP -eq '1') {
                    throw 'Injected second skill destination swap failure.'
                }
                [IO.Directory]::Move($stages[$index], $destinations[$index])
                $stages[$index] = $null
                $committed[$index] = $true
            }
        }
        catch {
            $transactionError = $_.Exception.Message
            $retained = @()
            for ($index = 0; $index -lt $destinations.Count; $index++) {
                $destination = $destinations[$index]
                if ($committed[$index] -and (Test-Path -LiteralPath $destination)) {
                    try { Remove-Item -LiteralPath $destination -Recurse -Force }
                    catch { $retained += $(if ($backups[$index]) { $backups[$index] } else { $destination }); continue }
                }
                if ($backups[$index] -and (Test-Path -LiteralPath $backups[$index])) {
                    if ($retained.Count -eq 0 -and $env:XE_TEST_FAIL_SKILL_RESTORE -eq '1') {
                        $retained += $backups[$index]
                    }
                    else {
                        try { [IO.Directory]::Move($backups[$index], $destination) }
                        catch { $retained += $backups[$index] }
                    }
                }
                if ($stages[$index] -and (Test-Path -LiteralPath $stages[$index])) {
                    try { Remove-Item -LiteralPath $stages[$index] -Recurse -Force }
                    catch { $retained += $stages[$index] }
                }
            }
            if ($retained.Count -gt 0) {
                throw "$transactionError Rollback failed; retained backup: $($retained -join ', ')"
            }
            throw "$transactionError Prior skill destinations were restored."
        }

        foreach ($backup in $backups) {
            if (-not $backup) { continue }
            try { Remove-Item -LiteralPath $backup -Recurse -Force }
            catch { throw "Skills were committed, but backup cleanup failed; retained backup: $backup" }
        }
    }
    finally {
        if (Test-Path -LiteralPath $skillScratch) { Remove-Item -LiteralPath $skillScratch -Recurse -Force }
    }
}

function Invoke-XEPostInstallAction {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string]$ResolvedVersion,
        [Parameter(Mandatory)][string]$ApiBase,
        [string]$Token,
        [Parameter(Mandatory)][string]$Scratch,
        [bool]$DoSetup,
        [bool]$DoStart,
        [bool]$DoAutostart,
        [bool]$DoInstallSkill,
        [bool]$NonInteractive
    )
    $usesDataDirectory = $DoSetup -or $DoStart -or $DoAutostart
    $oldDataDirectory = $env:XE_DATA_DIR
    $dataDirectory = if ($usesDataDirectory) { Get-XEDataDirectory } else { $null }
    try {
        if ($usesDataDirectory) { $env:XE_DATA_DIR = $dataDirectory }
        if ($DoSetup) {
            try { Invoke-XESetup -Exe $Exe -NonInteractive $NonInteractive }
            catch { throw [InvalidOperationException]::new("XE_INSTALLER_CODE=11 $($_.Exception.Message)", $_.Exception) }
        }
        if ($DoInstallSkill) {
            try { Install-XESkill -Version $ResolvedVersion -ApiBase $ApiBase -Token $Token -Scratch $Scratch }
            catch { throw [InvalidOperationException]::new("XE_INSTALLER_CODE=13 $($_.Exception.Message)", $_.Exception) }
        }
        if ($DoAutostart) {
            try { Register-XEAutostart -Exe $Exe -DataDirectory $dataDirectory }
            catch { throw [InvalidOperationException]::new("XE_INSTALLER_CODE=14 $($_.Exception.Message)", $_.Exception) }
        }
        if ($DoStart) {
            try {
                $timeout = if ($env:XE_START_TIMEOUT_SECONDS) { [int]$env:XE_START_TIMEOUT_SECONDS } else { 60 }
                Start-XEEngine -Exe $Exe -DataDirectory $dataDirectory -TimeoutSeconds $timeout
            }
            catch {
                $code = if ($_.Exception -is [TimeoutException]) { 12 } else { 1 }
                throw [InvalidOperationException]::new("XE_INSTALLER_CODE=$code $($_.Exception.Message)", $_.Exception)
            }
        }
    }
    finally {
        $env:XE_DATA_DIR = $oldDataDirectory
    }
}

function Invoke-XEInstaller {
    if ($Help) { Write-InstallerHelp; return 0 }
    if ($env:OS -ne 'Windows_NT' -or
        [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
        [Console]::Error.WriteLine('install.ps1 supports win-x64 only.')
        return 2
    }
    $requestedVersion = if ($Version) { $Version } elseif ($env:XE_VERSION) { $env:XE_VERSION } else { $null }
    $includePrerelease = $Pre.IsPresent -or $env:XE_PRE -eq '1'
    $requestedTargetDir = if ($InstallDir) { $InstallDir } elseif ($env:XE_INSTALL_DIR) { $env:XE_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'XE-Local-AI-Engine-App' }
    $token = if ($GitHubToken) { $GitHubToken } else { $env:XE_GITHUB_TOKEN }
    $apiBase = if ($GitHubApiBase) { $GitHubApiBase } elseif ($env:XE_GITHUB_API_BASE) { $env:XE_GITHUB_API_BASE } else { $script:DefaultApiBase }
    $downloadRoot = if ($DownloadBase) { $DownloadBase } elseif ($env:XE_DOWNLOAD_BASE) { $env:XE_DOWNLOAD_BASE } else { $script:DefaultDownloadBase }

    $doSetup = $Setup.IsPresent -or $env:XE_SETUP -eq '1'
    $doStart = $Start.IsPresent -or $env:XE_START -eq '1'
    $doAutostart = (-not $NoAutostart.IsPresent) -and ($Autostart.IsPresent -or $env:XE_AUTOSTART -eq '1')
    $doInstallSkill = $InstallSkill.IsPresent -or $env:XE_INSTALL_SKILL -eq '1'
    $nonInteractive = $Yes.IsPresent -or $env:XE_NONINTERACTIVE -eq '1' -or $env:CI -or
        -not [Environment]::UserInteractive -or [Console]::IsInputRedirected
    try {
        Assert-XENetworkBase -Name 'GitHub API base' -Url $apiBase
        Assert-XENetworkBase -Name 'download base' -Url $downloadRoot
        $appDataPath = Join-Path $env:LOCALAPPDATA 'XE-Local-AI-Engine'
        $targetDir = Assert-XESafeInstallPath -Path $requestedTargetDir -UserProfile $env:USERPROFILE -AppDataPath $appDataPath
        $release = Resolve-XERelease -RequestedVersion $requestedVersion -IncludePrerelease $includePrerelease -ApiBase $apiBase -Token $token
        try {
            $asset = Get-XEReleaseAsset -Release $release -Suffix 'Portable.zip'
        }
        catch {
            [Console]::Error.WriteLine("ERROR: $($_.Exception.Message)")
            return 2
        }
        if ($DryRun) {
            "XE_INSTALL_PLAN=1 XE_INSTALLED=$targetDir XE_VERSION=$($release.tag_name) XE_ASSET=$($asset.name)" | Write-Output
            return 0
        }
        $exe = Join-Path $targetDir 'XE-Local-AI-Engine.exe'
        if (Test-XEInstallOwned -Path $targetDir) {
            try {
                if (Test-XEInstallReusable -Path $targetDir -Version $release.tag_name -RuntimeInventory $null) {
                    try {
                        Invoke-XEPostInstallAction -Exe $exe -ResolvedVersion $release.tag_name -ApiBase $apiBase `
                            -Token $token -Scratch ([IO.Path]::GetTempPath()) -DoSetup $doSetup -DoStart $doStart `
                            -DoAutostart $doAutostart -DoInstallSkill $doInstallSkill -NonInteractive $nonInteractive
                    }
                    catch {
                        [Console]::Error.WriteLine("Post-install action failed: $($_.Exception.Message)")
                        if ($_.Exception.Message -match '^XE_INSTALLER_CODE=(1[1-4]) ') { return [int]$Matches[1] }
                        return 1
                    }
                    "XE_INSTALLED=$targetDir XE_VERSION=$($release.tag_name) XE_EXE=$exe" | Write-Output
                    return 0
                }
            }
            catch {
                [Console]::Error.WriteLine("Runtime prerequisite failed: $($_.Exception.Message)")
                return 10
            }
        }

        $checksumAsset = @($release.assets | Where-Object { $_.name -ceq 'CHECKSUMS.sha256' })
        if ($checksumAsset.Count -ne 1) { [Console]::Error.WriteLine('CHECKSUMS.sha256 is missing.'); return 3 }
        $scratch = Join-Path ([IO.Path]::GetTempPath()) "xe-install-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $scratch -Force | Out-Null
        try {
            $archive = Join-Path $scratch $asset.name
            $checksums = Join-Path $scratch 'CHECKSUMS.sha256'
            $assetUrl = Get-XEDownloadUrl -OriginalUrl $asset.browser_download_url -BaseUrl $downloadRoot
            $checksumUrl = Get-XEDownloadUrl -OriginalUrl $checksumAsset[0].browser_download_url -BaseUrl $downloadRoot
            Assert-XENetworkBase -Name 'asset URL' -Url $assetUrl
            Assert-XENetworkBase -Name 'checksum URL' -Url $checksumUrl
            try {
                Invoke-XEWebRequest -Uri $assetUrl -OutFile $archive | Out-Null
                Invoke-XEWebRequest -Uri $checksumUrl -OutFile $checksums | Out-Null
            }
            catch {
                [Console]::Error.WriteLine("Download failed: $($_.Exception.Message)")
                return 4
            }
            $expected = Get-XEExpectedChecksum -ChecksumText (Get-Content -LiteralPath $checksums -Raw) -AssetName $asset.name
            $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
            if (-not $expected -or $actual -ne $expected) {
                [Console]::Error.WriteLine("Checksum mismatch for $($asset.name) (expected $expected, got $actual).")
                return 3
            }
            $manifestAsset = @($release.assets | Where-Object { $_.name -ceq 'RELEASE-MANIFEST.json' })
            if ($manifestAsset.Count -eq 1) {
                $manifestPath = Join-Path $scratch 'RELEASE-MANIFEST.json'
                try {
                    $manifestUrl = Get-XEDownloadUrl -OriginalUrl $manifestAsset[0].browser_download_url -BaseUrl $downloadRoot
                    Assert-XENetworkBase -Name 'manifest URL' -Url $manifestUrl
                    Invoke-XEWebRequest -Uri $manifestUrl -OutFile $manifestPath | Out-Null
                    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
                    if (-not (Test-XEReleaseManifest -Manifest $manifest -Tag $release.tag_name `
                                -AssetName $asset.name -AssetHash $actual)) {
                        [Console]::Error.WriteLine('RELEASE-MANIFEST.json does not agree with the verified tag and asset checksum.')
                        return 3
                    }
                }
                catch {
                    Write-Warning "RELEASE-MANIFEST.json could not be used: $($_.Exception.Message). The mandatory checksum was still verified."
                }
            }
            else {
                Write-Warning 'RELEASE-MANIFEST.json is absent; the mandatory checksum was still verified.'
            }
            try {
                Install-XEArchive -Archive $archive -TargetPath $targetDir -Version $release.tag_name -RuntimeInventory $null
            }
            catch {
                [Console]::Error.WriteLine("Installation failed: $($_.Exception.Message)")
                if ($_.Exception.Message -like 'Microsoft.AspNetCore.App*' -or
                    $_.Exception.Message -like 'Required runtimeconfig*' -or
                    $_.Exception.Message -like 'The installed runtimeconfig*') { return 10 }
                return 1
            }
            try {
                Invoke-XEPostInstallAction -Exe $exe -ResolvedVersion $release.tag_name -ApiBase $apiBase `
                    -Token $token -Scratch $scratch -DoSetup $doSetup -DoStart $doStart -DoAutostart $doAutostart `
                    -DoInstallSkill $doInstallSkill -NonInteractive $nonInteractive
            }
            catch {
                [Console]::Error.WriteLine("Post-install action failed: $($_.Exception.Message)")
                if ($_.Exception.Message -match '^XE_INSTALLER_CODE=(1[1-4]) ') { return [int]$Matches[1] }
                return 1
            }
            "XE_INSTALLED=$targetDir XE_VERSION=$($release.tag_name) XE_EXE=$exe" | Write-Output
            return 0
        }
        finally {
            if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }
    }
    catch {
        [Console]::Error.WriteLine("ERROR: $($_.Exception.Message)")
        if ($_.Exception.Message -like 'Release resolution failed:*') { return 4 }
        if ($_.Exception.Message -like 'Microsoft.AspNetCore.App*' -or $_.Exception.Message -like 'Required runtimeconfig*') { return 10 }
        return 1
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    exit (Invoke-XEInstaller)
}
