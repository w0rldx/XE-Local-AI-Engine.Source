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
    try {
        if (-not $process.Start()) {
            throw "Could not start native command '$FilePath'."
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutMilliseconds)
        if ($timedOut) {
            $process.Kill($true)
            $process.WaitForExit()
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $outputLines = @($stdout, $stderr) |
            ForEach-Object { $_ -split '\r?\n' } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        [pscustomobject]@{
            Output = @($outputLines)
            ExitCode = $process.ExitCode
            TimedOut = $timedOut
        }
    }
    finally {
        $process.Dispose()
    }
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

    [regex]::Replace($text, '(?i)[A-Z]:\\Users\\[^,\r\n]+', '<redacted-user-path>')
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

    $global = @(
        foreach ($line in $globalProbe.Output) {
            $parts = @($line -split ',' | ForEach-Object { $_.Trim() })
            if ($parts.Count -eq 7) {
                [ordered]@{
                    index = [int]$parts[0]; name = $parts[1]; driver_version = $parts[2]
                    total_mib = [long]$parts[3]; free_mib = [long]$parts[4]; used_mib = [long]$parts[5]; utilization_percent = [int]$parts[6]
                }
            }
        }
    )

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
        divergence_rule = 'A materially higher process-visible budget than global free VRAM is external pressure/WDDM divergence. Do not use that sample for throughput claims.'
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
