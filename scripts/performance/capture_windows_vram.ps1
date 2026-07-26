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
    $globalRaw = @(& nvidia-smi.exe --query-gpu=index,name,uuid,driver_version,memory.total,memory.free,memory.used,utilization.gpu --format=csv,noheader,nounits 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "nvidia-smi global query failed: $($globalRaw -join [Environment]::NewLine)" }

    $processBudgetRaw = @(& $resolvedServer --list-devices 2>&1)
    $processBudgetExitCode = $LASTEXITCODE
    $computeAppsRaw = @(& nvidia-smi.exe --query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits 2>&1)
    $computeAppsExitCode = $LASTEXITCODE

    $global = @(
        foreach ($line in $globalRaw) {
            $parts = @($line -split ',' | ForEach-Object { $_.Trim() })
            if ($parts.Count -eq 8) {
                [ordered]@{
                    index = [int]$parts[0]; name = $parts[1]; uuid = $parts[2]; driver_version = $parts[3]
                    total_mib = [long]$parts[4]; free_mib = [long]$parts[5]; used_mib = [long]$parts[6]; utilization_percent = [int]$parts[7]
                }
            }
        }
    )

    $samples.Add([ordered]@{
        captured_at_utc = $capturedAt.ToString('o')
        global_vram = $global
        process_budget_probe = [ordered]@{
            argv = @($resolvedServer, '--list-devices')
            exit_code = $processBudgetExitCode
            raw_output = ($processBudgetRaw -join [Environment]::NewLine)
        }
        compute_apps = [ordered]@{
            exit_code = $computeAppsExitCode
            raw_output = ($computeAppsRaw -join [Environment]::NewLine)
        }
    })
    Start-Sleep -Seconds $IntervalSeconds
}

$artifact = [ordered]@{
    schema_version = '1.0'
    kind = 'windows-vram-evidence'
    scenario = $Scenario
    workload_label = $WorkloadLabel
    started_at_utc = $startedAt.ToString('o')
    completed_at_utc = [DateTimeOffset]::UtcNow.ToString('o')
    interval_seconds = $IntervalSeconds
    host = [ordered]@{
        os = [System.Environment]::OSVersion.VersionString
        machine_name = [System.Environment]::MachineName
        processor = (Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name -First 1)
    }
    llama_server = [ordered]@{ path = $resolvedServer; sha256 = $serverHash }
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
