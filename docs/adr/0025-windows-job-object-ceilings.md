# ADR 0025: Windows Job Object ceilings for the process sandbox

- **Status:** Proposed (2026-11-16). Decision 1 is implemented; Decision 2 (the AppContainer roles, through a broker) is
  added in its own change. The operator accepts this record before either merges.
- **Date:** 2026-11-16
- **Scope:** CPU, memory and process-count ceilings for commands the `process` provider runs on Windows, and what those
  ceilings do and do not guarantee. Linux is unchanged.
- **Authority:** Operator decisions of 2026-11-15 (one job per command) and 2026-11-16 (the plain-path ceilings approved, memory =
  the derived default, never UI limits, a job is a governor and not a boundary), on the Windows measurements of 2026-11-16.
- **Amends:** [ADR 0019](0019-execution-previews-and-the-appcontainer-boundary.md) Decision 8 and its timeout-only residual
  risk, and the [ADR 0020](0020-sandbox-security-profile.md) residual risk "`high` refuses every executing workload on
  today's Windows". **Notes** the Windows containment non-goal of
  [ADR 0007](0007-sandbox-execution-substrate-and-backend-selection.md).

## Context

On Windows a plain sandbox command (AgentHome, Coder, work sessions, Development Mode) ran as a bare child with no
ceiling: a runaway loop or fork bomb cost the machine, and the tree-kill missed descendants reparented after the root had
exited. The AppContainer roles (`run_python`, Sandboxed MCP) are timeout-only for the same reason (ADR 0019). Under `high`
every executing workload was refused on Windows because none could be bounded.

Measured on a Windows 11 host on 2026-11-16: a job created by the engine bounds a child at its commit limit; a nested MXC job
works under an outer limits job; a UI-limited job breaks WebView2 and MXC's own spawn (error 50); `dotnet build -m`
peaks near 9 GB, passes under 75% of RAM and fails at 4 GB; each .NET process in a job sizes its GC heap to 75% of the
WHOLE job; and a member of a 512 MB, 4-process job escapes through WMI `Win32_Process.Create` and the Task Scheduler and
can open its owner with `VM_WRITE`, `CREATE_THREAD` and `TERMINATE`.

## Decision

1. **Plain Windows launches carry an engine-owned job.**
   - Attached at creation: the process is created suspended with `PROC_THREAD_ATTRIBUTE_JOB_LIST` and a
     `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` of exactly its three stdio ends, its membership is verified with `IsProcessInJob`,
     and only then is it resumed (`WindowsJobLauncher`). Any failure before the resume terminates and closes the job and
     every handle; the payload never runs, and the command is refused. There is no "run unbounded" path.
   - One job per command. Whatever the command started dies when it ends: MSBuild reuse nodes, `VBCSCompiler`, a dev
     server an agent started. This matches Linux.
   - Memory is the job-wide commit (`JOB_OBJECT_LIMIT_JOB_MEMORY`, computed in 64 bits) with the same derived defaults as
     Linux (`SandboxResourceCeilings`); CPU is a hard-capped rate of `ceil(CpuCount * 10000 / S)`, clamped to 1..10000,
     where `S` is the system's logical processor count, never the process-relative one; the process count is active
     processes (`ActiveProcessLimit`), with no thread ceiling.
   - Flags: kill on job close, die on unhandled exception, never a breakaway flag, never UI limits. Every value, the flags
     included, is read back and must match exactly.
   - Advertised only after a performed probe: `WindowsJobCeilingsProbe` runs controls through the same launcher (a commit
     under the limit succeeds and one past it fails with `ERROR_COMMITMENT_LIMIT` while the same commit under a wider limit
     succeeds, since that error also means host-wide commit exhaustion; a process chain inside the cap runs, and one past it
     does not while the same chain under a wider cap does). A failure, an inconclusive control included, withdraws the
     ceilings with its reason and never anything else. The probe runs once per engine process, on the startup path: five
     short `cmd.exe` launches, the three process chains each bounded by a 30 s wait, so a healthy host pays well under a
     second, a contended one (a first antivirus scan, a model load) is not misread as unsupported, and a hung control
     costs at most about 90 s. A wrong exit code is a verdict and is never retried. Its result is cached until the engine
     restarts, so a failed probe leaves the ceilings off until then.
   - Output is read overlapped. Windows documents cancelling a blocked read on another thread only for overlapped I/O
     (`CancelIoEx`; synchronous I/O needs `CancelSynchronousIo` on the reading thread), so stdout and stderr are named-pipe
     server ends opened with `FILE_FLAG_OVERLAPPED`, and the drain budget cancels their reads through a token.
   - Text is UTF-8 both ways, with no preamble, as on the MXC path, on Linux and in Docker. git, Python and dotnet write
     UTF-8 whatever the console code page, and a captured `git diff` is stored as a UTF-8 patch, so decoding with an OEM or
     ANSI code page would corrupt non-ASCII patches. Accepted trade-off: a `cmd` builtin that prints in the OEM code page
     is not decoded faithfully.
   - The launcher always passes exactly the three stdio ends in its handle list. It has no path yet that inherits nothing
     (`bInheritHandles = FALSE`, no handle list), which the broker of Decision 2 needs.
   - The ceilings serve the plain path only. One predicate, `SandboxRequirements.CeilingsServed`, decides for every
     consumer; a role the AppContainer boundary serves stays unbounded and is reported so, and the raw capability flag is
     read nowhere else (an architecture test).
2. **The AppContainer roles** get the same ceilings through a broker; that decision is added by the change that builds it.

## Status of the mechanism

A Job Object is a **resource governor, not a security boundary**. It bounds the processes that are its members. Work a
member starts through WMI or the Task Scheduler is outside it (measured, and a Windows test documents the WMI case),
out-of-process COM is untested and presumed to escape, and a member can act through the engine process. The threat model
records this as invariant 18.

## GC note

Each .NET process in a job sizes its GC heap to 75% of the whole job's commit limit, not of the machine. The derived
default fits a parallel build (measured); a much smaller ceiling makes .NET tools fail at their commit cap. This is
stated, not mitigated.

## Accepted residual risk

- **The escapes above.** Approval gating and the jail remain the controls for what a plain command may do; the job bounds
  only what runs inside it.
- **A handle window.** The three child pipe ends are inheritable only around the launcher's own `CreateProcessW`, under
  the launcher's lock. .NET 10's `Process.Start` inherits every inheritable handle under a lock no other code can take, so
  another engine process start running at the same time, outside the launcher's lock, can inherit them: an engine-owned
  child can then hold a sandbox pipe end and delay that command's end of output. The reader's cancellable drain budget
  bounds the delay; `ProcessStartInfo.InheritedHandles`, documented for .NET 11 only (checked on 2026-11-20), or a launch
  path without inheritable handles retires it.
- **A blocked stdin write.** A root that exits while a descendant holds stdin unread can block the engine's synchronous
  stdin write before the job is terminated, as on the Windows `Process` path. An overlapped, cancellable stdin pipe is the
  follow-up.
- **Commit, not resident memory,** is what Windows counts.
- **A CPU rate under RDS with DFSS** is refused by the OS; the probe then reports the ceilings unavailable.
- **A parent job with a CPU rate** (a Windows container started with `--cpus`, some hosted runners) tightens the cap
  further: a nested job's rate is a share of its parent's, and `Environment.ProcessorCount` already reflects the parent,
  so a command gets less CPU than its numbers say. This fails safe; it never loosens a ceiling.

## Consequences

- On a probe-passing Windows host, AgentHome, Coder, work sessions and Development Mode run bounded, and `high` no longer
  refuses them for ceilings (it still refuses them for egress and, where preferred, a Stable filesystem boundary).
- `run_python` and Sandboxed MCP are unchanged: timeout-only under the AppContainer boundary, refused under `high`, and an
  unattended `run_python` (`requireResourceLimits`) is still refused before provisioning.
- `SandboxLaunchPlan.Create` throws for a policy carrying ceilings that no mechanism applies, instead of building a plain
  child.

## Rejected alternatives

- **Assigning a job after `Process.Start`.** The child runs its first instructions, and may start children, outside the
  job; it cannot be advertised honestly.
- **A fixed Windows memory default (4 GB).** It fails `dotnet build -m` (measured).
- **Warn and run unbounded when the job cannot be set up.** A ceiling the policy carries is never dropped silently.
