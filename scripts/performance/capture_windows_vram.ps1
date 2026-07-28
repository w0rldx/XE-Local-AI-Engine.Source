#Requires -Version 7.0
<#
.SYNOPSIS
Captures native-Windows global VRAM and llama.cpp process-visible budget evidence.

.DESCRIPTION
Run once while the desktop is idle and once while the target game/workload is resident.
The script never starts or stops workloads. It samples NVIDIA's global free/used VRAM and
the exact `llama-server --list-devices` output used as the process-budget proxy, preserving
raw output when a backend format cannot be parsed. No administrator rights are required.

.EXAMPLE
./scripts/performance/capture_windows_vram.ps1 -Scenario idle -LlamaServerPath C:\llama\llama-server.exe -OutputPath artifacts\vram-idle.json

.EXAMPLE
./scripts/performance/capture_windows_vram.ps1 -Scenario game -WorkloadLabel "Game name / scene" -LlamaServerPath C:\llama\llama-server.exe -OutputPath artifacts\vram-game.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('idle', 'game', 'custom')][string]$Scenario,
    [Parameter(Mandatory)][ValidateScript({ Test-Path $_ -PathType Leaf })][string]$LlamaServerPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$WorkloadLabel = 'none',
    [ValidateRange(2, 3600)][int]$DurationSeconds = 30,
    [ValidateRange(1, 60)][int]$IntervalSeconds = 2
)

$ErrorActionPreference = 'Stop'

function Invoke-NativeCapture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [ValidateRange(1, 300000)][int]$TimeoutMilliseconds = 15000
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    $result = $null
    $failure = $null
    $cleanupFailure = $null
    try {
        if (-not $process.Start()) {
            throw "Could not start native command '$FilePath'."
        }
        $started = $true

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutMilliseconds)
        if ($timedOut) {
            $process.Kill($true)
            if (-not $process.WaitForExit(5000)) {
                throw "Cleanup failed: native process tree for '$FilePath' did not exit after termination."
            }
        }
        if (-not [System.Threading.Tasks.Task]::WaitAll(
                [System.Threading.Tasks.Task[]]@($stdoutTask, $stderrTask),
                5000)) {
            throw "Cleanup failed: redirected output readers for '$FilePath' did not finish."
        }

        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
        $outputLines = @($stdout, $stderr) |
            ForEach-Object { $_ -split '\r?\n' } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $result = [pscustomobject]@{
            Output = @($outputLines)
            ExitCode = $process.ExitCode
            TimedOut = $timedOut
        }
    }
    catch {
        $failure = $_
    }
    finally {
        if ($started -and -not $process.HasExited) {
            try {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) {
                    $cleanupFailure = "Cleanup failed: native process tree for '$FilePath' remained alive."
                }
            }
            catch {
                $cleanupFailure = "Cleanup failed for native process tree '$FilePath': $($_.Exception.Message)"
            }
        }
        $process.Dispose()
    }
    if ($null -ne $cleanupFailure) {
        throw $cleanupFailure
    }
    if ($null -ne $failure) {
        throw $failure
    }
    $result
}

function Protect-CaptureText {
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][object[]]$Output,
        [AllowEmptyCollection()][string[]]$SensitiveValues = @()
    )

    $text = ($Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    foreach ($sensitiveValue in @($SensitiveValues) + @($userProfile)) {
        if (-not [string]::IsNullOrWhiteSpace($sensitiveValue)) {
            $text = $text.Replace($sensitiveValue, '<redacted-path>', [StringComparison]::OrdinalIgnoreCase)
        }
    }

    $text = [regex]::Replace($text, '(?i)[A-Z]:\\Users\\[^,\r\n]+', '<redacted-user-path>')
    $uuidCore = '[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}'
    [regex]::Replace(
        $text,
        "(?i)\b(?:MIG-GPU-$uuidCore/\d+/\d+|MIG-$uuidCore|GPU-$uuidCore)\b",
        '<redacted-gpu-uuid>')
}

function ConvertFrom-NvidiaGlobalOutput {
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][object[]]$Output
    )

    $records = [System.Collections.Generic.List[object]]::new()
    $malformed = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $Output) {
        if ([string]::IsNullOrWhiteSpace([string]$line)) {
            continue
        }
        $parts = @([string]$line -split ',' | ForEach-Object { $_.Trim() })
        if ($parts.Count -ne 7) {
            $malformed.Add([string]$line)
            continue
        }

        $index = 0
        $totalMiB = 0L
        $freeMiB = 0L
        $usedMiB = 0L
        $utilizationPercent = 0
        if (-not [int]::TryParse($parts[0], [ref]$index) -or
            -not [long]::TryParse($parts[3], [ref]$totalMiB) -or
            -not [long]::TryParse($parts[4], [ref]$freeMiB) -or
            -not [long]::TryParse($parts[5], [ref]$usedMiB) -or
            -not [int]::TryParse($parts[6], [ref]$utilizationPercent)) {
            $malformed.Add([string]$line)
            continue
        }

        $records.Add([ordered]@{
            index = $index
            name = $parts[1]
            driver_version = $parts[2]
            total_mib = $totalMiB
            free_mib = $freeMiB
            used_mib = $usedMiB
            utilization_percent = $utilizationPercent
        })
    }
    if ($malformed.Count -gt 0 -or $records.Count -eq 0) {
        $diagnosticSource = if ($malformed.Count -gt 0) { $malformed } else { $Output }
        $diagnostic = Protect-CaptureText -Output $diagnosticSource
        if ([string]::IsNullOrWhiteSpace($diagnostic)) {
            $diagnostic = '<no output>'
        }
        throw "nvidia-smi global query contained missing or malformed GPU rows. Sanitized output: $diagnostic"
    }

    $records.ToArray()
}

if (-not (Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue)) {
    throw 'nvidia-smi.exe is required for global VRAM capture and was not found on PATH.'
}

$resolvedServer = (Resolve-Path $LlamaServerPath).Path
$serverHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedServer).Hash.ToLowerInvariant()
$samples = [System.Collections.Generic.List[object]]::new()
$startedAt = [DateTimeOffset]::UtcNow
$deadline = $startedAt.AddSeconds($DurationSeconds)

while ([DateTimeOffset]::UtcNow -lt $deadline) {
    $capturedAt = [DateTimeOffset]::UtcNow
    $globalProbe = Invoke-NativeCapture -FilePath 'nvidia-smi.exe' -ArgumentList @(
        '--query-gpu=index,name,driver_version,memory.total,memory.free,memory.used,utilization.gpu',
        '--format=csv,noheader,nounits'
    )
    if ($globalProbe.TimedOut) { throw 'nvidia-smi global query timed out.' }
    if ($globalProbe.ExitCode -ne 0) {
        throw "nvidia-smi global query failed: $(Protect-CaptureText -Output $globalProbe.Output)"
    }

    $processBudgetProbe = Invoke-NativeCapture -FilePath $resolvedServer -ArgumentList @('--list-devices')
    $computeAppsProbe = Invoke-NativeCapture -FilePath 'nvidia-smi.exe' -ArgumentList @(
        '--query-compute-apps=pid,process_name,used_memory',
        '--format=csv,noheader,nounits'
    )

    $global = @(ConvertFrom-NvidiaGlobalOutput -Output $globalProbe.Output)

    $samples.Add([ordered]@{
        captured_at_utc = $capturedAt.ToString('o')
        global_vram = $global
        process_budget_probe = [ordered]@{
            argv = @([System.IO.Path]::GetFileName($resolvedServer), '--list-devices')
            exit_code = $processBudgetProbe.ExitCode
            timed_out = $processBudgetProbe.TimedOut
            raw_output = Protect-CaptureText -Output $processBudgetProbe.Output -SensitiveValues @($resolvedServer)
        }
        compute_apps = [ordered]@{
            exit_code = $computeAppsProbe.ExitCode
            timed_out = $computeAppsProbe.TimedOut
            raw_output = Protect-CaptureText -Output $computeAppsProbe.Output -SensitiveValues @($resolvedServer)
        }
    })
    Start-Sleep -Seconds $IntervalSeconds
}

$artifact = [ordered]@{
    schema_version = '1.0'
    kind = 'windows-vram-evidence'
    scenario = $Scenario
    workload_label = Protect-CaptureText -Output @($WorkloadLabel) -SensitiveValues @($resolvedServer)
    started_at_utc = $startedAt.ToString('o')
    completed_at_utc = [DateTimeOffset]::UtcNow.ToString('o')
    interval_seconds = $IntervalSeconds
    host = [ordered]@{
        os = [System.Environment]::OSVersion.VersionString
        processor = (Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name -First 1)
    }
    llama_server = [ordered]@{ file_name = [System.IO.Path]::GetFileName($resolvedServer); sha256 = $serverHash }
    interpretation = [ordered]@{
        global_reader = 'nvidia-smi memory.free/memory.used'
        process_budget_reader = 'llama-server --list-devices (same-process CUDA/WDDM view; raw output retained)'
        divergence_rule = 'Raw process-budget/global-free divergence is not proof of external pressure. Compare paired idle/workload captures and subtract the expected ambient baseline before applying materiality thresholds.'
    }
    samples = $samples
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$parent = Split-Path -Parent $resolvedOutput
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$temporary = "$resolvedOutput.tmp"
$artifact | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
Move-Item -Force -LiteralPath $temporary -Destination $resolvedOutput
Write-Output "Wrote $resolvedOutput"
