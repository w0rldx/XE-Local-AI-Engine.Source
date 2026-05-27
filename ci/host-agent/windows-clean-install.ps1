param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,

    [Parameter(Mandatory = $true)]
    [string]$TranscriptPath,

    [int]$TimeoutSeconds = 900,

    [switch]$AllowRebootRequired,

    [string]$ExpectedSha256,

    [switch]$RequireTrustedSignature
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$AutostartPattern = 'XE[-_\s]*Local[-_\s]*AI[-_\s]*Engine|xe[-_\s]*host[-_\s]*agent'

function Write-Evidence {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    Write-Host "[windows-clean-install] $Message"
}

function Test-IsElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)

    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-FileExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "File not found: $Path"
    }
}

function Assert-ExpectedHash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$ExpectedHash
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedHash)) {
        Write-Evidence 'MSI SHA-256 validation: skipped'
        return
    }

    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash

    Write-Evidence "MSI SHA-256: $actualHash"

    if (-not $actualHash.Equals($ExpectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "MSI SHA-256 mismatch. Expected '$ExpectedHash', got '$actualHash'."
    }

    Write-Evidence 'MSI SHA-256 validation: passed'
}

function Assert-TrustedSignature {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [switch]$Required
    )

    if (-not $Required) {
        Write-Evidence 'MSI Authenticode signature validation: skipped'
        return
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path

    Write-Evidence "MSI signature status: $($signature.Status)"

    if ($signature.SignerCertificate) {
        Write-Evidence "MSI signer subject: $($signature.SignerCertificate.Subject)"
        Write-Evidence "MSI signer thumbprint: $($signature.SignerCertificate.Thumbprint)"
    }

    if ($signature.Status -ne 'Valid') {
        throw "MSI Authenticode signature is not valid. Status: $($signature.Status)"
    }

    Write-Evidence 'MSI Authenticode signature validation: passed'
}

function Get-RegistryAutostartHits {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    $registryPaths = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce'
    )

    $hits = @()

    foreach ($registryPath in $registryPaths) {
        if (-not (Test-Path -LiteralPath $registryPath)) {
            continue
        }

        $properties = Get-ItemProperty -LiteralPath $registryPath

        foreach ($property in $properties.PSObject.Properties) {
            if ($property.Name -like 'PS*') {
                continue
            }

            $entry = "$($property.Name)=$($property.Value)"

            if ($entry -match $Pattern) {
                $hits += [pscustomobject]@{
                    Location = $registryPath
                    Name     = $property.Name
                    Value    = $property.Value
                }
            }
        }
    }

    return $hits
}

function Get-StartupFolderAutostartHits {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    $startupFolders = @(
        [Environment]::GetFolderPath('Startup'),
        [Environment]::GetFolderPath('CommonStartup')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    $hits = @()

    foreach ($startupFolder in $startupFolders) {
        if (-not (Test-Path -LiteralPath $startupFolder)) {
            continue
        }

        $items = Get-ChildItem -LiteralPath $startupFolder -Force -ErrorAction SilentlyContinue

        foreach ($item in $items) {
            $entry = "$($item.Name) $($item.FullName)"

            if ($entry -match $Pattern) {
                $hits += [pscustomobject]@{
                    Location = $startupFolder
                    Name     = $item.Name
                    Path     = $item.FullName
                }
            }
        }
    }

    return $hits
}

function Get-ServiceAutostartHits {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    $services = Get-Service -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -match $Pattern -or
            $_.DisplayName -match $Pattern
        }

    return @($services)
}

function Get-ScheduledTaskAutostartHits {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    $tasks = Get-ScheduledTask -ErrorAction SilentlyContinue |
        Where-Object {
            $_.TaskName -match $Pattern -or
            $_.TaskPath -match $Pattern -or
            (
                $_.Actions |
                    Out-String
            ) -match $Pattern
        }

    return @($tasks)
}

function Assert-NoAutostart {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    $services = Get-ServiceAutostartHits -Pattern $Pattern
    $tasks = Get-ScheduledTaskAutostartHits -Pattern $Pattern
    $registryEntries = Get-RegistryAutostartHits -Pattern $Pattern
    $startupFolderEntries = Get-StartupFolderAutostartHits -Pattern $Pattern

    if ($services.Count -gt 0) {
        Write-Evidence 'Autostart guard failure: matching services found'

        foreach ($service in $services) {
            Write-Evidence "Service: Name='$($service.Name)', DisplayName='$($service.DisplayName)', Status='$($service.Status)', StartType='$($service.StartType)'"
        }
    }

    if ($tasks.Count -gt 0) {
        Write-Evidence 'Autostart guard failure: matching scheduled tasks found'

        foreach ($task in $tasks) {
            Write-Evidence "ScheduledTask: Path='$($task.TaskPath)', Name='$($task.TaskName)', State='$($task.State)'"
        }
    }

    if ($registryEntries.Count -gt 0) {
        Write-Evidence 'Autostart guard failure: matching registry entries found'

        foreach ($entry in $registryEntries) {
            Write-Evidence "Registry: Location='$($entry.Location)', Name='$($entry.Name)', Value='$($entry.Value)'"
        }
    }

    if ($startupFolderEntries.Count -gt 0) {
        Write-Evidence 'Autostart guard failure: matching startup folder entries found'

        foreach ($entry in $startupFolderEntries) {
            Write-Evidence "StartupFolder: Location='$($entry.Location)', Name='$($entry.Name)', Path='$($entry.Path)'"
        }
    }

    if (
        $services.Count -gt 0 -or
        $tasks.Count -gt 0 -or
        $registryEntries.Count -gt 0 -or
        $startupFolderEntries.Count -gt 0
    ) {
        throw 'Autostart guard failed: service, scheduled task, registry entry, or startup folder entry found.'
    }

    Write-Evidence 'Autostart guard: no service, no scheduled task, no registry Run/RunOnce entry, no startup folder entry'
}

function Invoke-MsiInstall {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$LogPath,

        [Parameter(Mandatory = $true)]
        [int]$Timeout,

        [switch]$AllowReboot
    )

    $argumentString = "/i `"$Path`" /qn /norestart /L*v `"$LogPath`""

    Write-Evidence "MSI arguments: $argumentString"
    Write-Evidence "MSI log: $LogPath"

    $process = Start-Process `
        -FilePath 'msiexec.exe' `
        -ArgumentList $argumentString `
        -PassThru

    $completed = $process.WaitForExit($Timeout * 1000)

    if (-not $completed) {
        Write-Evidence "MSI install timed out after $Timeout seconds"

        try {
            $process.Kill()
            $process.WaitForExit()
            Write-Evidence 'Timed-out msiexec process killed'
        }
        catch {
            Write-Evidence "Failed to kill timed-out msiexec process: $_"
        }

        throw "MSI install timed out after $Timeout seconds"
    }

    Write-Evidence "MSI exit code: $($process.ExitCode)"

    if ($process.ExitCode -eq 3010) {
        if ($AllowReboot) {
            Write-Evidence 'MSI completed successfully but requested reboot. Exit code 3010 accepted because AllowRebootRequired was set.'
            return
        }

        throw 'MSI completed but requested reboot. Exit code 3010 rejected because AllowRebootRequired was not set.'
    }

    if ($process.ExitCode -ne 0) {
        throw "MSI failed with exit code $($process.ExitCode)"
    }

    Write-Evidence 'MSI install completed successfully'
}

$transcriptDirectory = Split-Path -Parent $TranscriptPath

if ($transcriptDirectory -and -not (Test-Path -LiteralPath $transcriptDirectory)) {
    New-Item -ItemType Directory -Path $transcriptDirectory -Force | Out-Null
}

Start-Transcript -Path $TranscriptPath -Force | Out-Null

try {
    Write-Evidence "Started: $(Get-Date -Format o)"
    Write-Evidence "Runner: $env:COMPUTERNAME"
    Write-Evidence "User: $env:USERNAME"
    Write-Evidence "Elevated: $(Test-IsElevated)"
    Write-Evidence "Timeout budget seconds: $TimeoutSeconds"

    Assert-FileExists -Path $MsiPath

    $fullMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
    $msiLogPath = [System.IO.Path]::ChangeExtension($TranscriptPath, '.msi.log')

    Write-Evidence "MSI: $fullMsiPath"

    Assert-ExpectedHash -Path $fullMsiPath -ExpectedHash $ExpectedSha256
    Assert-TrustedSignature -Path $fullMsiPath -Required:$RequireTrustedSignature

    Invoke-MsiInstall `
        -Path $fullMsiPath `
        -LogPath $msiLogPath `
        -Timeout $TimeoutSeconds `
        -AllowReboot:$AllowRebootRequired

    Assert-NoAutostart -Pattern $AutostartPattern

    Write-Evidence 'User launch: pending external desktop shortcut invocation by clean-runner harness'
    Write-Evidence 'HostAgent admin status: pending post-launch status capture'
    Write-Evidence 'WorkerHub: pending post-launch status capture'
    Write-Evidence 'Tray: pending post-launch status capture'
    Write-Evidence 'Open Web UI: pending post-launch browser assertion'

    Write-Evidence "Completed: $(Get-Date -Format o)"
}
finally {
    Stop-Transcript | Out-Null
}
