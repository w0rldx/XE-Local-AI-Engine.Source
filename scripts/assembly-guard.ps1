#Requires -Version 7.0
<#
.SYNOPSIS
    Detect a test run whose assemblies were overwritten underneath it. Windows port of
    scripts/assembly-guard.sh.

.DESCRIPTION
    Why this exists
      `dotnet test --no-build` loads assemblies from bin/. A concurrent `dotnet build` in another
      process rewrites those files mid-run, and the test host then reports failures that have
      nothing to do with the code: observed on this repo as `failed: 97` (of 4225) and `failed: 1`,
      both clean on re-run, and as `FileNotFoundException:
      Microsoft.AspNetCore.SignalR.Client.Core, Version=10.0.9.0` in the E2E suite. In every case
      the DLL mtimes fell INSIDE the run window.

      The damage is not the lost run, it is the lost trust: a contaminated run is indistinguishable
      from a real regression, so either someone chases a phantom for a day, or — far worse — someone
      waves a REAL failure away as "probably contamination". With GitHub Actions disabled this suite
      is the only gate this project has.

      scripts/with-build-lock.ps1 prevents the collision between processes that opt in. This script
      is the safety net for everything that does not: a bare `dotnet build` in another terminal
      cannot be forced through a wrapper, but it CAN be caught after the fact. A run whose inputs
      changed is reported as CONTAMINATED with its own exit code — never as test failures, and never
      as a pass.

    What it compares
      For each root, every file that a build can rewrite and a test host can load: *.dll, *.exe,
      *.so, *.dylib, *.deps.json and *.runtimeconfig.json. Identity is (size, last-write time in
      100 ns ticks). Test runs write logs, .trx and temp files into these trees all the time; none
      of those are tracked, so a normal run produces no diff.

      The bash original also tracks extensionless executables, to catch the Microsoft.Testing.Platform
      apphost on Linux. That clause is deliberately absent here: NTFS has no execute bit to test, and
      on Windows the apphost is `<project>.exe`, already covered by the extension list. Adding a
      bare "extensionless file" rule would track unrelated content instead.

    Where the boundaries go
      Snapshot AFTER the build that the run itself performs, immediately before the first test
      process starts; verify immediately after the last one exits. A legitimate build-then-test
      sequence is therefore entirely outside the window and cannot produce a false positive. The
      `guard` subcommand does exactly this and is the form to prefer.

.NOTES
    Usage:
      scripts/assembly-guard.ps1 snapshot <state-file> [-TestBins] [-Root <dir>]...
      scripts/assembly-guard.ps1 verify   <state-file>
      scripts/assembly-guard.ps1 guard    [-State <file>] [-TestBins] [-Root <dir>]... -- <cmd> [args...]

    Options:
      -TestBins        Add every test project's build output (<repo>/*.Tests*/bin/*/net*) as a root.
      -Root <dir>      Add one root (repeatable). Non-existent roots are recorded and a root that
                       DISAPPEARS mid-run is itself reported — a sibling `clean` is contamination too.
      -State <file>    Where `guard` keeps its snapshot (default: a temp file it removes afterwards).

    Exit codes:
      0    — clean (for `guard`: clean AND the wrapped command succeeded)
      75   — CONTAMINATED: the build output changed during the run (EX_TEMPFAIL — "re-run required").
             `guard` returns 75 even when the wrapped command also failed: once the inputs moved, the
             failure cannot be attributed either, so the only honest verdict is "re-run".
      1-N  — for `guard`, the wrapped command's own exit status when the run was clean
      2    — usage error

.EXAMPLE
    ./scripts/assembly-guard.ps1 guard -TestBins -- dotnet test XE-Local-AI-Engine.slnx --no-build
#>
# param() is deliberately empty. A declared param() block makes PowerShell's parameter binder claim
# the `--` separator and fail with "the parameter name '' is ambiguous" before this script runs
# (measured on pwsh 7.6.3). With a bare param(), `pwsh -File assembly-guard.ps1 guard -TestBins --
# dotnet test` delivers every token through to $args verbatim.
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# EX_TEMPFAIL. Deliberately distinct from every status the test runners already produce (0 pass,
# 1 failures, 2 missing prerequisite, 8 zero-tests-matched) so "re-run me" can never be misread.
$ContaminatedExit = 75

function Write-GuardLog { param([string] $Message) [Console]::Error.WriteLine("[guard] $Message") }
function Exit-Usage { param([string] $Message) [Console]::Error.WriteLine("[guard] $Message"); exit 2 }

$projectRoot = & git -C $PSScriptRoot rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($projectRoot)) {
    $projectRoot = Split-Path -Parent $PSScriptRoot
}
$projectRoot = (Resolve-Path -LiteralPath $projectRoot).Path

$TrackedExtensions = @('.dll', '.exe', '.so', '.dylib')
$TrackedSuffixes = @('.deps.json', '.runtimeconfig.json')

# Every test project's output tree. Both configurations: a stale Debug tree is inert, and pinning
# only Release would miss a concurrent `dotnet build` that happens to use the other configuration.
function Get-TestBinRoot {
    $found = @()
    foreach ($proj in (Get-ChildItem -LiteralPath $projectRoot -Directory -Filter '*.Tests*' -ErrorAction SilentlyContinue)) {
        $bin = Join-Path $proj.FullName 'bin'
        if (-not (Test-Path -LiteralPath $bin)) { continue }
        foreach ($config in (Get-ChildItem -LiteralPath $bin -Directory -ErrorAction SilentlyContinue)) {
            foreach ($tfm in (Get-ChildItem -LiteralPath $config.FullName -Directory -Filter 'net*' -ErrorAction SilentlyContinue)) {
                $found += $tfm.FullName
            }
        }
    }
    return $found
}

function Test-Tracked {
    param([System.IO.FileInfo] $File)
    if ($TrackedExtensions -contains $File.Extension.ToLowerInvariant()) { return $true }
    $name = $File.Name.ToLowerInvariant()
    foreach ($suffix in $TrackedSuffixes) { if ($name.EndsWith($suffix)) { return $true } }
    return $false
}

# Tab-separated `size <TAB> ticks <TAB> path`, sorted by path. Windows paths cannot contain tabs, so
# the three fields stay unambiguous.
function Get-Manifest {
    param([string[]] $Roots)
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($root in $Roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($file in (Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue)) {
            if (-not (Test-Tracked -File $file)) { continue }
            $lines.Add("$($file.Length)`t$($file.LastWriteTimeUtc.Ticks)`t$($file.FullName)")
        }
    }
    return @($lines | Sort-Object -Property { ($_ -split "`t", 3)[2] } -CaseSensitive)
}

function Invoke-Snapshot {
    param([string] $State, [string[]] $Roots)
    if ($Roots.Count -eq 0) { Exit-Usage 'snapshot needs at least one root (or -TestBins)' }

    $header = @('# xe-assembly-guard v1', "# taken $([DateTimeOffset]::Now.ToString('o'))")
    foreach ($root in $Roots) { $header += "# root $root" }
    $body = Get-Manifest -Roots $Roots

    $stateDir = Split-Path -Parent $State
    if ($stateDir -and -not (Test-Path -LiteralPath $stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null }
    Set-Content -LiteralPath $State -Value ($header + $body) -Encoding utf8

    Write-GuardLog "snapshot: $($body.Count) assemblies across $($Roots.Count) root(s) -> $State"
}

function Invoke-Verify {
    param([string] $State)
    if (-not (Test-Path -LiteralPath $State -PathType Leaf)) { Exit-Usage "snapshot file not found: $State" }

    $stateLines = @(Get-Content -LiteralPath $State)
    $roots = @($stateLines | Where-Object { $_.StartsWith('# root ') } | ForEach-Object { $_.Substring(7) })
    if ($roots.Count -eq 0) { Exit-Usage "snapshot file $State records no roots — it is not a valid snapshot" }

    $before = @{}
    foreach ($line in $stateLines) {
        if ($line.StartsWith('#')) { continue }
        $parts = $line -split "`t", 3
        if ($parts.Count -eq 3) { $before[$parts[2]] = @($parts[0], $parts[1]) }
    }

    $report = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($line in (Get-Manifest -Roots $roots)) {
        $parts = $line -split "`t", 3
        $path = $parts[2]
        [void] $seen.Add($path)
        if (-not $before.ContainsKey($path)) { $report.Add("ADDED    $path"); continue }
        $old = $before[$path]
        if ($old[0] -ne $parts[0] -or $old[1] -ne $parts[1]) {
            $report.Add("CHANGED  $path (size $($old[0]) -> $($parts[0]), ticks $($old[1]) -> $($parts[1]))")
        }
    }
    foreach ($path in $before.Keys) {
        if (-not $seen.Contains($path)) { $report.Add("REMOVED  $path") }
    }

    if ($report.Count -eq 0) {
        Write-GuardLog 'verify: build output unchanged during the run — result is trustworthy.'
        return 0
    }

    $sorted = @($report | Sort-Object -CaseSensitive)
    [Console]::Error.WriteLine('')
    Write-GuardLog '================================================================'
    Write-GuardLog 'CONTAMINATED RUN — RE-RUN REQUIRED. This is NOT a test result.'
    Write-GuardLog '================================================================'
    Write-GuardLog "$($sorted.Count) tracked file(s) were rewritten while the tests were running, so the"
    Write-GuardLog 'test host was reading assemblies that changed underneath it. Whatever it reported'
    Write-GuardLog '— passes or failures — describes nothing. Do not treat it as either.'
    Write-GuardLog ''
    foreach ($entry in ($sorted | Select-Object -First 40)) { Write-GuardLog "  $entry" }
    if ($sorted.Count -gt 40) { Write-GuardLog "  ... and $($sorted.Count - 40) more" }
    Write-GuardLog ''
    Write-GuardLog 'Almost certainly another process ran ''dotnet build'' during the run. Find it, wait'
    Write-GuardLog 'for it to finish, then re-run. To make the collision impossible between cooperating'
    Write-GuardLog 'shells, run both through: scripts/with-build-lock.ps1 -- <command>'
    return $ContaminatedExit
}

function Invoke-Guard {
    param([string[]] $Arguments)

    $state = ''
    $ownState = $false
    $roots = @()
    $command = @()
    $i = 0
    while ($i -lt $Arguments.Count) {
        switch ($Arguments[$i]) {
            '-State' { if ($i + 1 -ge $Arguments.Count) { Exit-Usage '-State needs a value' }; $state = $Arguments[$i + 1]; $i += 2 }
            '-Root' { if ($i + 1 -ge $Arguments.Count) { Exit-Usage '-Root needs a value' }; $roots += $Arguments[$i + 1]; $i += 2 }
            '-TestBins' { $roots += Get-TestBinRoot; $i++ }
            '--' {
                # Guarded slice: PowerShell reverses a descending range, so a bare
                # $a[($i+1)..($n-1)] on a trailing `--` would yield the `--` itself as the command.
                $command = if ($i + 1 -lt $Arguments.Count) { @($Arguments[($i + 1)..($Arguments.Count - 1)]) } else { @() }
                $i = $Arguments.Count
            }
            default { Exit-Usage "guard: unexpected argument '$($Arguments[$i])' (the command must come after --)" }
        }
    }
    if ($command.Count -eq 0) { Exit-Usage 'guard: no command given (put it after --)' }
    if ($roots.Count -eq 0) { Exit-Usage 'guard: no roots given (use -Root and/or -TestBins)' }

    if ([string]::IsNullOrWhiteSpace($state)) {
        $tmp = Join-Path $projectRoot '.tmp'
        if (-not (Test-Path -LiteralPath $tmp)) { New-Item -ItemType Directory -Path $tmp -Force | Out-Null }
        $state = Join-Path $tmp "assembly-guard-$([System.Guid]::NewGuid().ToString('N').Substring(0,6)).state"
        $ownState = $true
    }

    Invoke-Snapshot -State $state -Roots $roots

    $commandArgs = if ($command.Count -gt 1) { @($command[1..($command.Count - 1)]) } else { @() }
    & $command[0] @commandArgs
    $status = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }

    $verifyStatus = Invoke-Verify -State $state
    if ($ownState) { Remove-Item -LiteralPath $state -Force -ErrorAction SilentlyContinue }

    if ($verifyStatus -ne 0) {
        if ($status -ne 0) {
            Write-GuardLog "(the wrapped command also exited $status, but that verdict is unusable — see above)"
        }
        return $verifyStatus
    }
    return $status
}

$argv = @($args)
if ($argv.Count -eq 0) { Exit-Usage 'no subcommand given (expected: snapshot | verify | guard)' }
$Subcommand = $argv[0]
$Rest = if ($argv.Count -gt 1) { @($argv[1..($argv.Count - 1)]) } else { @() }

switch -CaseSensitive ($Subcommand) {
    'snapshot' {
        if ($Rest.Count -eq 0) { Exit-Usage 'snapshot needs a state file path' }
        $state = $Rest[0]
        $roots = @()
        $i = 1
        while ($i -lt $Rest.Count) {
            switch -CaseSensitive ($Rest[$i]) {
                '-TestBins' { $roots += Get-TestBinRoot; $i++ }
                '-Root' { if ($i + 1 -ge $Rest.Count) { Exit-Usage '-Root needs a value' }; $roots += $Rest[$i + 1]; $i += 2 }
                default {
                    if ($Rest[$i].StartsWith('-')) { Exit-Usage "snapshot: unknown option '$($Rest[$i])'" }
                    $roots += $Rest[$i]; $i++
                }
            }
        }
        Invoke-Snapshot -State $state -Roots $roots
        exit 0
    }
    'verify' {
        if ($Rest.Count -ne 1) { Exit-Usage 'verify takes exactly one argument: the state file written by snapshot' }
        exit (Invoke-Verify -State $Rest[0])
    }
    'guard' {
        exit (Invoke-Guard -Arguments $Rest)
    }
    default { Exit-Usage "unknown subcommand '$Subcommand' (expected: snapshot | verify | guard)" }
}
