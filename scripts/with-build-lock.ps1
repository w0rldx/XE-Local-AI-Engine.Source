#Requires -Version 7.0
<#
.SYNOPSIS
    Run a command holding the repo-wide, cross-process build lock. Windows port of
    scripts/with-build-lock.sh.

.DESCRIPTION
    Why this exists
      `dotnet test --no-build` reads the assemblies in bin/. When a second process runs
      `dotnet build` while those tests are executing, it overwrites the assemblies mid-run and the
      test host reports PHANTOM failures — observed on this repo as `failed: 97` and `failed: 1` on
      runs that were clean on re-run, and as `FileNotFoundException:
      Microsoft.AspNetCore.SignalR.Client.Core` in E2E, each with DLL mtimes falling inside the run
      window. A contaminated run is indistinguishable from a real regression, and this suite is
      the last gate before a release is cut.

      This is PREVENTION and it is only half the story: it can only serialize commands that opt in.
      A bare `dotnet build` in another terminal bypasses it entirely. That is what
      scripts/assembly-guard.ps1 (DETECTION) is for — the two layers are independent by design.

    THE HANDLE-INHERITANCE TRAP, and why this port uses a named mutex
      The bash original documents the trap that cost this repo a day: an flock lives on an open file
      DESCRIPTOR, descriptors are inherited across fork/exec, and `dotnet build` leaves MSBuild
      node-reuse daemons and VBCSCompiler alive for ~15 idle minutes. Those daemons inherited the
      lock fd and held the lock while idle, starving every other agent. The bash fix is `"$@" 9>&-`.

      A Win32 named mutex has no such failure mode and that is why it is the primitive chosen here,
      rather than a FileShare.None handle on the lock file (the closer literal translation of flock).
      A handle is only inherited when it was created with an inheritable SECURITY_ATTRIBUTES and the
      child is created with bInheritHandles = TRUE; .NET's Mutex never marks its handle inheritable,
      so an MSBuild daemon spawned under this wrapper cannot acquire, hold, or leak the lock. Node
      reuse and shared compilation therefore stay ENABLED and there is no build-speed cost.

      The mutex is named from a hash of the CANONICAL lock-file path, so it is scoped to a checkout
      exactly as the bash lock file is, and two worktrees do not contend. The name uses the session
      ("Local\") namespace deliberately: the "Global\" namespace needs SeCreateGlobalPrivilege,
      which an unelevated agent shell does not have, and cooperating shells on this box share a
      session.

      Crash safety comes free: if the holder dies without releasing, the kernel abandons the mutex
      and the next waiter's WaitOne throws AbandonedMutexException — which this script treats as a
      successful acquisition (and says so), because that is precisely the state the abandoned mutex
      is reporting.

.NOTES
    Arguments (parsed by hand from $args — see "Why param() is empty" below):
      -TimeoutSeconds <n>  Max time to wait for the lock. Default: $env:BUILD_LOCK_TIMEOUT, else
                           1800. A full Release build plus a solution test run legitimately takes
                           many minutes, so the default is deliberately generous. It is bounded,
                           never infinite.
      -LockFile <path>     Lock file to use. Default: $env:BUILD_LOCK_FILE, else
                           <repo>/.tmp/build.lock (gitignored).
      --                   End of options. Everything after it is the command and its arguments.

    Why param() is empty
      A declared param() block makes PowerShell's parameter binder claim the `--` separator, and it
      fails with "the parameter name '' is ambiguous" before a single line of this script runs —
      measured on pwsh 7.6.3. With a bare param(), `pwsh -File with-build-lock.ps1 -- dotnet build`
      delivers `--` through to $args verbatim, while the call operator (`& ./with-build-lock.ps1 --
      dotnet build`) strips it. Parsing $args by hand and discarding a leading `--` is what makes
      BOTH invocation forms work, which matters because the .sh original is invoked both ways.

    Env knobs:
      BUILD_LOCK_TIMEOUT   Same as -TimeoutSeconds.
      BUILD_LOCK_FILE      Same as -LockFile.
      XE_BUILD_LOCK_HELD   Set BY this script for the command it runs. If it already names the same
                           lock file, the wrapper is a pass-through instead of deadlocking on
                           itself. Do not set it by hand — doing so disables locking for that
                           subtree.

    Re-entrancy
      Nesting is safe: an inner wrapper sees XE_BUILD_LOCK_HELD matching its lock file and runs the
      command directly. The corollary is that a wrapped command which itself forks PARALLEL work is
      NOT serialized internally — the lock cannot subdivide a critical section someone else created.

    Exit codes:
      0-N  — the wrapped command's own exit status (passed through unchanged)
      69   — could not acquire the lock within the timeout (EX_UNAVAILABLE); nothing was run
      2    — usage error

.EXAMPLE
    ./scripts/with-build-lock.ps1 -- dotnet build XE-Local-AI-Engine.slnx --configuration Release
#>
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# EX_UNAVAILABLE. Distinct from every status the wrapped runners produce, so "the lock was busy"
# can never be misread as "the tests failed".
$LockBusyExit = 69

function Write-LockLog { param([string] $Message) [Console]::Error.WriteLine("[build-lock] $Message") }
function Exit-Usage { param([string] $Message) [Console]::Error.WriteLine("[build-lock] $Message"); exit 2 }

$projectRoot = & git -C $PSScriptRoot rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($projectRoot)) {
    $projectRoot = Split-Path -Parent $PSScriptRoot
}
$projectRoot = (Resolve-Path -LiteralPath $projectRoot).Path

$TimeoutSeconds = if ($env:BUILD_LOCK_TIMEOUT) { [int] $env:BUILD_LOCK_TIMEOUT } else { 1800 }
$LockFile = if ($env:BUILD_LOCK_FILE) { $env:BUILD_LOCK_FILE } else { Join-Path $projectRoot '.tmp/build.lock' }
$Command = @()

$argv = @($args)
$i = 0
while ($i -lt $argv.Count) {
    switch -CaseSensitive ($argv[$i]) {
        '-TimeoutSeconds' {
            if ($i + 1 -ge $argv.Count) { Exit-Usage '-TimeoutSeconds needs a value' }
            $raw = $argv[$i + 1]
            if ($raw -notmatch '^[0-9]+$') { Exit-Usage "-TimeoutSeconds must be a whole number of seconds, got '$raw'" }
            $TimeoutSeconds = [int] $raw; $i += 2
        }
        '-LockFile' {
            if ($i + 1 -ge $argv.Count) { Exit-Usage '-LockFile needs a value' }
            $LockFile = $argv[$i + 1]; $i += 2
        }
        '--' { $Command = @($argv[($i + 1)..($argv.Count - 1)]); $i = $argv.Count }
        default {
            if ($argv[$i].StartsWith('-')) { Exit-Usage "Unknown option: $($argv[$i])" }
            # No `--` was given; treat the rest as the command, matching the .sh behaviour.
            $Command = @($argv[$i..($argv.Count - 1)]); $i = $argv.Count
        }
    }
}

if ($TimeoutSeconds -le 0) { Exit-Usage '-TimeoutSeconds must be a positive whole number of seconds.' }
if ($Command.Count -eq 0) {
    Exit-Usage 'no command given. Usage: scripts/with-build-lock.ps1 [-TimeoutSeconds n] [-LockFile p] -- <command> [args...]'
}

$lockDir = Split-Path -Parent $LockFile
if (-not (Test-Path -LiteralPath $lockDir)) {
    New-Item -ItemType Directory -Path $lockDir -Force | Out-Null
}
# Canonicalise so the re-entrancy comparison is not defeated by a relative path, a different
# casing, or a substituted/symlinked root. The lock file itself is created if absent — it is the
# durable, human-findable name for the lock even though the mutex is the primitive.
if (-not (Test-Path -LiteralPath $LockFile)) {
    New-Item -ItemType File -Path $LockFile -Force | Out-Null
}
$LockFile = (Resolve-Path -LiteralPath $LockFile).Path
$ownerFile = "$LockFile.owner"

# PowerShell reverses a descending range, so $a[1..0] on a one-element array yields element 0 —
# i.e. a bare `$Command[1..($Command.Count-1)]` would silently pass the executable to itself as an
# argument whenever the command takes none. Materialise the tail once, guarded.
$CommandArgs = if ($Command.Count -gt 1) { @($Command[1..($Command.Count - 1)]) } else { @() }

# Already inside a lock for this same file: run through. See "Re-entrancy" above.
if ($env:XE_BUILD_LOCK_HELD -eq $LockFile) {
    & $Command[0] @CommandArgs
    exit $(if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 })
}

function Get-OwnerDescription {
    # Diagnostic only, and inherently racy: the holder may have released between our failed attempt
    # and this read. Never used for control flow.
    if ((Test-Path -LiteralPath $ownerFile) -and (Get-Item -LiteralPath $ownerFile).Length -gt 0) {
        try { return ((Get-Content -LiteralPath $ownerFile -Raw) -replace '\r?\n', ' ').Trim() } catch { return 'unknown (owner record unreadable)' }
    }
    return 'unknown (no owner record)'
}

# Name the mutex from the canonical path so the lock is per-checkout, exactly like the bash lock
# file. Hash rather than the path itself: mutex names cannot contain '\' and are length-capped.
$pathBytes = [System.Text.Encoding]::UTF8.GetBytes($LockFile.ToLowerInvariant())
$pathHash = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($pathBytes)).Replace('-', '').Substring(0, 32)
$mutexName = "Local\xe-build-lock-$pathHash"

$mutex = [System.Threading.Mutex]::new($false, $mutexName)
$held = $false
try {
    try {
        $held = $mutex.WaitOne(0)
    } catch [System.Threading.AbandonedMutexException] {
        # The previous holder died without releasing. The kernel handed us the mutex anyway; that is
        # the intended crash-recovery path, not an error.
        Write-LockLog "the previous holder exited without releasing the lock — taking it over."
        $held = $true
    }

    if (-not $held) {
        Write-LockLog "waiting up to ${TimeoutSeconds}s for the build lock — held by: $(Get-OwnerDescription)"
        try {
            $held = $mutex.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds))
        } catch [System.Threading.AbandonedMutexException] {
            Write-LockLog "the previous holder exited without releasing the lock — taking it over."
            $held = $true
        }
    }

    if (-not $held) {
        [Console]::Error.WriteLine("[build-lock] FAIL: could not acquire $LockFile within ${TimeoutSeconds}s.")
        [Console]::Error.WriteLine("[build-lock]   Current holder: $(Get-OwnerDescription)")
        [Console]::Error.WriteLine("[build-lock]   Nothing was run. Wait for that build/test to finish, or re-run with")
        [Console]::Error.WriteLine("[build-lock]   -TimeoutSeconds <seconds> if it is legitimately slower than ${TimeoutSeconds}s.")
        exit $LockBusyExit
    }

    $record = "pid=$PID started=$([DateTimeOffset]::Now.ToString('o')) cmd=$($Command -join ' ')"
    # Best-effort: the owner record is diagnostic only (it names the holder in a waiter's timeout
    # message). Failing to write it must never take down a run that has already taken the lock.
    try { Set-Content -LiteralPath $ownerFile -Value $record -NoNewline -Encoding utf8 }
    catch { Write-LockLog "could not write the owner record ($($_.Exception.Message)) — continuing." }

    $previousHeld = $env:XE_BUILD_LOCK_HELD
    $env:XE_BUILD_LOCK_HELD = $LockFile
    try {
        & $Command[0] @CommandArgs
        $status = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }
    } finally {
        $env:XE_BUILD_LOCK_HELD = $previousHeld
    }
    exit $status
} finally {
    if ($held) {
        # Truncate rather than delete: the next waiter's owner description should read "unknown",
        # not the stale record of a process that has already finished. Best-effort for the same
        # reason as the write above — releasing the mutex is what actually matters here.
        try { Set-Content -LiteralPath $ownerFile -Value '' -NoNewline -Encoding utf8 }
        catch { Write-LockLog "could not clear the owner record ($($_.Exception.Message))." }
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
