# Windows 11 RC verification runbook

**Status:** Living Windows procedure with dated verification evidence retained in place.

**Last audited:** 2026-08-08 against code baseline `9405df91`.

Target: a tester RC portable build (Velopack, `--noInst`) on real Windows 11 with x64 ASP.NET Core Runtime
10.0.11 or a newer .NET 10 servicing patch installed. Nine checks, risk-ordered.
Budget ~1 hour. Everything below covers a code path that **cannot be exercised on the Linux/WSL dev box**, which
is why it is here at all.

Data root for every path below: `%LOCALAPPDATA%\XE-Local-AI-Engine`.

Useful once, up front:

```powershell
$XE   = "$env:LOCALAPPDATA\XE-Local-AI-Engine"
$LOGS = "$XE\logs\*.log"
function xe-proc {
  Get-CimInstance Win32_Process |
    Where-Object { $_.Name -like 'XE-Local-AI-Engine*' -or $_.CommandLine -like '*XE-Local-AI-Engine.Client.dll*' } |
    Select-Object ProcessId,Name,ExecutablePath,CommandLine
}
function llama  { Get-Process llama-server -ErrorAction SilentlyContinue | Select-Object Id,Path }
```

Note `llama-server` — the OS process table carries no extension on Windows, even though the file on disk is
`llama-server.exe` (`OsStaleLlamaServerProcessScanner.LlamaServerProcessName`).

### What changed since the last revision of this runbook

Checks **4, 5 and 9** describe fixed behaviour rather than known-broken behaviour; each carries a "Changed for
this RC" note saying what was proven on the dev box and what only you can prove.

**A 2026-08-02 session ran on a real Windows 11 box (build 26220, RTX 5090, git 2.55.0, .NET 10.0.302) and closed
several of the questions this runbook was written to ask.** Where that happened the check now says so, and says
what is left for you:

- **Checks 1 and 2** — the Job Object interop is no longer unverified. `WindowsJobObjectTreeKillTests` proves
  tree-kill *and* the `TerminateProcess` hard-kill path in the ordinary backend suite, with a negative control
  showing Windows does not reap orphans on its own. What you still add is a real `llama-server.exe` holding real
  VRAM through a real driver.
- **Check 5** — the `core.autocrlf` question is answered (the `i/` column is unaffected; the design is right), and
  the CRLF gate failure plus its fix are reproduced end-to-end on real Windows git. NTFS junctions now have tests.
  `MAX_PATH` on a box with long paths *disabled* is still open.
- **Check 9** — `wmic` is confirmed absent and the `Get-CimInstance` replacement confirmed to answer in ~1.3 s.
  An AMD/Intel-**only** box is still needed for the vendor result itself.

**A second session, 2026-08-03, ran the half that needs a real model on the real GPU.** Status after it:

| Check | State |
|---|---|
| 1 — Job Object tree-kill | **Verified** with a real `llama-server` holding 31741 MiB through driver 610.88 |
| 2 — stale-orphan reaper | **Verified** — 0 reaper lines, so the Job Object is what reaped it |
| 3 — `node.key` DPAPI wrap | Verified in the earlier session |
| 4 — `dp-keys` fail-closed ring | Verified in the earlier session, both directions |
| 5 — Development Mode surveys | **Blocked** by a redirected `%LOCALAPPDATA%` in that harness — see the note in check 5; not a product failure, but read it before re-running |
| 5b — Coder surveys | **Unproven** — attempted with a *reasoning* model, which cannot produce a tool call; see the note in 5b |
| 6 — consent disclosure | **Dialog verified live**; the containment-probe **line** half is **RETIRED** — it is unemittable on Windows, so it was never an open question (see check 6) |
| 7 — partial GPU offload | **Verified**, all five criteria, by deliberately oversizing the quant on a 32 GB card |
| 8 — zero-device runtime | **Answered: managed path PASSES**; only a bring-your-own override misreported the cause — fixed |
| 9 — non-NVIDIA adapter | **Not applicable** on a dual-adapter box; unexercised, not passed |

Still fully open: **checks 5 and 5b**, plus check 9's AMD/Intel result. (The probe-line half of check 6 was
listed here as open until 2026-08-03, when it turned out to be unreachable rather than unobserved — retired, not
answered.)

Everything measured in that session came from a **32 GB / 61 GB RAM / 32 CPU** box against a ≈16 GB consumer
target, so every timing, VRAM and fit figure in it **over-reports**. See "16 GB target notes" at the end.

One change can make a previously-starting install fail to start: check 4's fail-closed key ring. A hard startup
failure there may be the fix working correctly — read that check before concluding the RC is broken.

---

## 1. Job Object process tree-kill on hard kill

**What it proves.** That the Win32 Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) actually reaps
`llama-server.exe` when the host dies without running managed shutdown — catching the worst failure on a 16 GB
box: an orphan holding 8–14 GB of VRAM and a loopback port forever.

> ### Changed for this RC — the mechanism is now proven automatically; what you add is the real `llama-server`
>
> `WindowsJobObjectProcessHandle` used to have no covering test of any kind, and carried an
> operator-verification flag reading *"real tree-kill behavior MUST be verified on Windows 11"*. That flag has
> been discharged: `WindowsJobObjectTreeKillTests` now runs on Windows in the ordinary backend suite and proves
> both halves against real processes — `TreeKill` reaps a descendant the handle never knew about, and a
> `TerminateProcess` hard kill of the process that OWNS the job (no console-ctrl event, no managed code) reaps it
> too. The negative control is what makes that evidence: the same parent/grandchild shape with no Job Object,
> hard-killed the same way, leaves the grandchild running indefinitely — measured on Windows 11 (26220), still
> alive 5 s later and killed by hand. Windows does not reap orphans, so the Job Object is what did it.
>
> So a red check 1 is now much more likely to mean *something about the real spawn path* — the supervisor, the
> launch spec, `llama-server.exe` itself — than the interop. Run it anyway: the tests contain a PowerShell child,
> not a GPU process holding 8–14 GB of VRAM through a driver, and only you can supply that.

> ### Closing the console window is NOT a hard kill — do not test that path
>
> `DesktopLifecycle` installs a `SetConsoleCtrlHandler` that intercepts `CTRL_CLOSE_EVENT` / `CTRL_LOGOFF_EVENT` /
> `CTRL_SHUTDOWN_EVENT`, calls `StopApplication()`, then **blocks up to 4000 ms** waiting for `ApplicationStopped`
> (`DesktopLifecycle.ConsoleCloseDrainBudget` and `DesktopLifecycle.HandleConsoleCtrl`). That runs the full graceful supervisor teardown and proves nothing about
> the Job Object. `publish/TESTER-QUICKSTART.md` tells testers to stop the app exactly that way — correct advice
> for a tester, wrong for this check.
>
> **Task Manager → Details → End task on the parent is the only way to reach the Job Object.** It issues
> `TerminateProcess`, which delivers no console-ctrl event and runs no managed code.

**Do this.**

1. Launch the packaged exe. Wait for the browser to open.
2. Load a chat model (send one message — the model must actually be resident).
3. Baseline:
   ```powershell
   xe-proc
   llama
   nvidia-smi --query-gpu=memory.used --format=csv,noheader
   ```
4. Kill the **parent only**, one of:
   - Task Manager → **Details** tab (not Processes) → the `XE-Local-AI-Engine*` exe → **End task**, or
   - `Stop-Process -Id <parent-pid> -Force`
5. Do **not** use `taskkill /F /T` — that kills the child directly and proves nothing.
6. Within ~5 s:
   ```powershell
   llama
   nvidia-smi --query-gpu=memory.used --format=csv,noheader
   ```

**Pass looks like.** `llama` returns nothing (empty, no rows). `memory.used` back to the idle baseline from
step 3 (single-digit MiB on a headless GPU; a few GiB on a box whose desktop runs on the same card). No
`llama-server` in Task Manager.

> ### Closed 2026-08-03 on real Windows 11 with a real GPU model — the Job Object holds
>
> Measured on Windows 11 26220, RTX 5090 32 GB, packaged `0.1.0-rc.4.2` portable build, with
> `unsloth/Qwen3.6-27B-GGUF:Q8_0` (26.6 GiB) fully resident and holding **31741 MiB** of real VRAM through
> driver 610.88. `Stop-Process -Id <host> -Force` on the parent only — `TerminateProcess`, no console-ctrl
> event, no managed code:
>
> ```
> t=+1.0s  host=0  llama-server=1  vram=2857 MiB
> t=+2.1s  host=0  llama-server=1  vram=2873 MiB
> t=+3.2s  host=0  llama-server=0  vram=2907 MiB
> t=+5.3s  host=0  llama-server=0  vram=2988 MiB
> ```
>
> The child was gone by **+3.2 s** and ~29 GB of VRAM came back. Check 2 then found **zero** reaper lines, so
> the Job Object did it, not the next start. Two things a tester should not misread:
>
> - **VRAM falls before the process disappears.** At +1 s the process was still listed while its VRAM had
>   already dropped from 31741 to 2857 MiB. Sampling only `nvidia-smi` would say "done" a second early;
>   sampling only the process table would say "still there". Check both, and give it the full 5 s.
> - **Historical process note:** that 0.1.0-rc.4.2 measurement used the old self-contained
>   `XE-Local-AI-Engine.Client.exe`. Current Windows packages keep a C# launcher process alive and run the host as
>   `dotnet.exe XE-Local-AI-Engine.Client.dll`. For a current RC, the `dotnet.exe` process identified by that command
>   line owns the Job Object and is the process to end; the updated `xe-proc` helper shows both launcher and host.

**Fail looks like / next step.** A surviving `llama-server` process and VRAM still pinned. Record its PID and
`Path` (must be under `%LOCALAPPDATA%\XE-Local-AI-Engine\llama.cpp\`), kill it manually, and go straight to
check 2 to confirm — that is a P0: the Job Object is the only orphan defence on Windows and there is no
`setsid`/pgid fallback there.

---

## 2. Stale-orphan reaper on the next start (second-order confirmation of check 1)

**What it proves.** Independently confirms whether the Job Object held. `StaleLlamaServerReaper` is an
`IHostedService` that on every start scans for `llama-server` processes under this app's own binaries root and
kills them — so **reaper lines in the log mean the Job Object did NOT hold**, even if check 1 looked clean
because you were slow to observe.

**Do this.** Immediately after check 1, relaunch the app, then:

```powershell
Select-String -Path $LOGS -Pattern 'Reaping stale llama-server orphan|Reaped \d+ stale llama-server orphan'
```

**Pass looks like.** Zero matches.

> **Closed 2026-08-03 together with check 1.** After the hard kill above, the app was relaunched and the grep
> returned **0 matches** across every file in `logs\` *and* across the new run's own console output. Nothing
> was left to reap, which is the independent confirmation that the Job Object — not the next start — is what
> removed `llama-server`. Grep both places: the reaper logs through Serilog, so a console-only or file-only
> grep can miss it depending on sink configuration.

**Fail looks like / next step.** One or both of:

```
Reaping stale llama-server orphan (pid 12345) at C:\Users\<u>\AppData\Local\XE-Local-AI-Engine\llama.cpp\...\llama-server.exe
Reaped 1 stale llama-server orphan process(es) left by a previous run.
```

That is a check-1 fail regardless of what step 6 showed. Note the reaper matches **only** binaries under
`%LOCALAPPDATA%\XE-Local-AI-Engine\llama.cpp\` — an Ollama `llama-server` is never touched, so a match is
unambiguously ours.

---

## 3. First-run `node.key` generation + DPAPI wrap

**What it proves.** That `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` actually runs on Windows
(`DesktopBootstrap.ProtectSecretForAtRest`). **This branch is completely untested** —
`DesktopBootstrapTests.EnsureLocalDataConfiguration_OnNonWindows_PersistsKeyFileWithOwnerOnlyPermissions`
returns early `if (OperatingSystem.IsWindows())`, and `ProtectedData` appears in no test file. A regression here
writes the SQLite operator secret to disk in plaintext.

**There is no log line for any of this.** The desktop setup branch calls
`DesktopBootstrap.EnsureLocalDataConfiguration` before `CreateStartupLogger`, and `DesktopBootstrap` has no
`ILogger`/`Console` call anywhere. Verification is filesystem-only.

**Do this.** Full reset first, so this is a genuine first run:

```powershell
# stop the app (close the console) first
Remove-Item -Recurse -Force $XE
```

Launch, wait for first-run provisioning to reach the admin-password screen, then:

```powershell
$p   = "$XE\node.key"
$raw = (Get-Content $p -Raw).Trim()
$b   = [Convert]::FromBase64String($raw)
$b.Length                                   # discriminator
Add-Type -AssemblyName System.Security
[System.Security.Cryptography.ProtectedData]::Unprotect($b,$null,'CurrentUser').Length
(Get-Item $p).Length                        # single line, ASCII, no BOM/newline
Get-FileHash $p -Algorithm SHA256
```

Then close the console, relaunch, and re-run `Get-FileHash`.

**Pass looks like.**
- `$b.Length` is **> 32** (a few hundred) — wrapped.
- `Unprotect(...).Length` is **exactly 32**, no exception.
- `Get-FileHash` identical before and after the restart (key reused, not regenerated —
  `DesktopBootstrap.EnsureOperatorSecret` reads the existing winner while the file exists).

**Fail looks like / next step.**
- `$b.Length` is **exactly 32** → the secret is on disk in plaintext; the DPAPI branch did not run. P0.
  (This is the product's own discriminator: `DesktopBootstrap.UnwrapSecretBytes` plus the
  `NodeOperatorSecretProvider.ExpectedSecretLength` gate in `ReadAndValidateExistingSecret`.)
- `Unprotect` throws `CryptographicException` → the blob is not CurrentUser-scoped for this account.
- Hash changes across restart → regeneration; the previous `node.sqlite` is now unreadable.

Related failure signature worth recognising (do not chase it here): copying the data dir to another Windows user
produces a **~2 s hang then a misleading message** — *"The desktop operator key file '…' does not contain exactly
32 bytes…"* — even though it does. The DPAPI unwrap failed; the length gate is reporting the wrong cause.

---

## 4. `dp-keys` ring stability (`*.enc` orphaning is silent on Windows)

> **Changed for this RC.** The fail-closed resolver is now registered on **both** branches
> (`NodeDataProtectionKeyRingFailClosed.Decorate`, called outside the OS branch in `ConfigureServices`), with a
> DPAPI-specific failure classifier and remediation. An unreadable DPAPI ring should now **stop startup with a
> named error** rather than silently minting a new key. This is the one change in this pass that can make a
> previously-starting install fail to start, so read the fail section below carefully — a hard failure here may be
> the fix working correctly.

**What it proves.** That the fail-closed decorator actually fires on a real DPAPI ring, and — more important —
that it does **not** fire on a healthy one. The Linux tests cover the decision logic; only a Windows box has a
real `ProtectedData` blob to fail on.

**Do this.** After check 3, sign in / store a Hugging Face token, then:

```powershell
Get-ChildItem "$XE\dp-keys\*.xml" | Select-Object Name,LastWriteTime
Get-ChildItem "$XE\*.enc"         | Select-Object Name,Length
```

Close the console, relaunch, re-run both, and:

```powershell
Select-String -Path $LOGS -Pattern 'decryption failed. Clearing|Clearing stored credentials'
```

**Pass looks like.** The same `key-<guid>.xml` set after restart — **no new file per launch**. Zero matches from
`Select-String`. The HF token still present in the UI (Settings → Hugging Face shows authenticated, not anonymous).
The app starts normally: on a healthy ring the decorator must be invisible.

**Then deliberately break it**, because the interesting half is whether the new backstop fires at all. Stop the
app, copy `$XE\dp-keys` and `$XE\*.enc` to a second Windows user account's `%LOCALAPPDATA%\XE-Local-AI-Engine`,
and start the app as that user.

> **Corrected 2026-08-02 — measured on real Windows 11; the previous expectation here was wrong.** A broken ring
> does **not** stop the host. The key ring is resolved **lazily**, on the first operation that needs an
> `IDataProtector`, not during startup — so the app starts, serves, and answers HTTP 200, and the failure surfaces
> as a logged error plus a failing operation. Read the pass criteria below rather than the old "startup fails"
> wording, or you will file a working fix as a P0.
>
> **Easier trigger than a second Windows account.** The runbook used to say copy `dp-keys` to another user
> profile — which needs an account you may not be able to create. What the classifier actually matches is a
> `CryptographicException` out of DPAPI, and flipping two bytes inside the base64 `<value>` of the
> `<encryptedKey>` element in `dp-keys\key-<guid>.xml` raises exactly that, from exactly that call site. Back the
> file up first; restoring it restores the node. The second-account case is still the more faithful reproduction
> if you have one.

**Pass looks like (broken ring).** All three, together:

1. This exact error in the log — the classifier recognised the DPAPI failure and the remediation is Windows-specific:

   ```
   [ERR] An error occurred while reading the key ring.
   System.InvalidOperationException: Data Protection key '<guid>' is encrypted at rest but could not be decrypted.
   Refusing to regenerate the key-ring, which would silently orphan every stored credential and OAuth token.
   The key-ring is DPAPI-protected for the Windows user that created it. Sign in as that user and restart. …
   ```

2. **No new `key-<guid>.xml`.** This is the load-bearing one — it is the whole point of the fix. The ring must
   still hold exactly the file(s) it held before, with unchanged timestamps.

3. The host **stays up** and keeps serving. That is expected, not a failure: see the correction above.

Measured on Windows 11 (26220): the message appeared verbatim, the ring stayed at its single original key file,
and the host answered HTTP 200 throughout. Restoring the backed-up key file returned the node to a clean start
with zero key-ring log lines — on a healthy ring the decorator is invisible, which is the other half of the check.

**Fail looks like / next step.**

- A fresh `key-<guid>.xml` on each launch on the ORIGINAL account, and/or:

  ```
  Hugging Face token decryption failed. Clearing the stored token.
  Worker credential decryption failed. Clearing stored credentials and requiring re-pairing.
  ```

  The ring is still regenerating silently — the decorator did not fire. Capture whether the ring rotated on a
  plain restart or only after a Windows credential/profile change.
- The broken-ring case producing **no** `Refusing to regenerate the key-ring` line anywhere in the log → the
  decorator is registered but its classifier does not recognise what DPAPI actually threw. Capture the full log;
  the exception type and message out of `ProtectedData.Unprotect` is exactly what the classifier needs to match.
  (Do **not** read "the app started" as this failure — it starts either way. Grep for the line.)
- A broken ring that produces the line **and still writes a new `key-<guid>.xml`** → the throw is being caught
  somewhere that then lets regeneration proceed. That is the original defect resurfacing and is a P0.
- The **original** account failing to start → a false positive, and a P0 the other way. Capture the full message
  and the inner exception before deleting anything; that combination is unreachable from the Linux tests
  (`NodeDataProtectionKeyRingFailClosedTests` proves a readable-but-rotating ring stays quiet, but only against a
  fake key).

---

## 5. Development Mode: `list_files` / `search_text`, and the validation gate's first command

> **Changed for this RC.** `DevelopmentWorkspaceTools` no longer shells out to `find` or `grep` at all. Both
> surveys are managed code (`WorkspaceFileScanner`, at
> `Client.Application/Services/Workspace/WorkspaceFileScanner.cs` — earlier revisions of this runbook called it
> `DevelopmentWorkspaceFileScanner`, which is not a type that exists) on every platform, so the behaviour a Linux test
> exercises is the behaviour Windows runs — there is no longer an OS branch to get wrong, and no dependency on
> Git for Windows being installed. Separately, the gate's first command
> (`git diff --check`) now runs under a per-path whitespace policy the engine derives from the repository's own
> index (`DevelopmentWorkspaceWhitespacePolicy`). Both are covered by tests that run on the Linux dev box; what
> the tester adds is the real Windows filesystem and a real Windows `git`.

**What it proves.** That the two surveys return real results against a real NTFS workspace, and that a
repository which stores CRLF passes the validation gate instead of failing at command one.

**Do this.**

```powershell
where.exe find    # expect C:\Windows\System32\find.exe — that is now FINE, nothing invokes it
where.exe grep    # may find nothing — also fine
where.exe git     # this one IS required: Development Mode runs real git
```

Open `/development`, accept the consent gate, register a trusted repo, and run a Development task that exercises
**both** `list_files` and `search_text`. Run it twice: once against a repository stored with LF, once against one
stored with CRLF. `git ls-files --eol` in the source repository tells you which you have — look at the `i/`
column, not `w/`.

**Pass looks like.**

- Both tool calls return real results. `list_files` output is `./`-prefixed, name-sorted, and contains no `.git/`
  entry and no `.env`-style path.
- On the CRLF repository the validation gate gets **past** `git_diff_check` (it is the first command; a failure
  there means nothing else ran).
- In the managed workspace, `%LOCALAPPDATA%\XE-Local-AI-Engine\development\workspaces\<project>\<task>\.git\info\attributes`
  exists on a CRLF repository and names only the CRLF-stored paths (or `*` on an all-CRLF one). On an LF-only
  repository the file must **not** exist — its presence there would mean the CR check was retired for a
  repository that still needs it.

**Fail looks like / next step.**

- Either of these exact strings in the task output or log:

  ```
  The fixed Development list_files operation failed.
  The fixed Development search_text operation failed.
  ```

  This is now a filesystem or path failure, not a missing tool. Capture the repository path and whether it sits
  on a network drive, a junction, or a path near `MAX_PATH`.
- `git_diff_check` reporting `trailing whitespace` on lines the coder did not touch → the whitespace policy did
  not apply. Check that the attributes file above exists and that the paths it names match the failing file.

**Not verifiable without you.** These paths are exercised on Linux by `WorkspaceFileScannerTests` and
`DevelopmentWorkspaceWhitespacePolicyTests`. Three things used to be listed here as genuinely Windows-only; two of
them have since been closed on a real Windows 11 box, and the remaining one is what this check is now for.

- **`core.autocrlf` — answered, and the design is correct.** Git for Windows' system config
  (`C:\Program Files\Git\etc\gitconfig`) really does set `core.autocrlf=true`, and it applies inside the managed
  workspace: the engine redirects `HOME`, which suppresses the user's `~/.gitconfig` but not the *system* file,
  and nothing pins `GIT_CONFIG_NOSYSTEM`. Measured with it genuinely in effect (git 2.55.0): a CRLF-authored file
  is normalised to **`i/lf w/crlf`**, and after a fresh checkout *every* file reports `w/crlf` while the index
  stays `lf`. So the `i/` column is unaffected by `autocrlf` — the policy correctly derives *no* attributes file
  on such a repository, and sampling `w/` would have blanket-granted `cr-at-eol` to an ordinary LF repository on
  every Windows box. No action needed; do not "fix" the policy to read the worktree.
- **The gate failure and its fix are reproduced end-to-end on real Windows git.** On a genuinely CRLF-stored
  repository (`.gitattributes` `* -text`, so `i/crlf`), appending one CRLF line makes `git diff --check HEAD -- .`
  report `trailing whitespace` and exit **2**; with the engine's `.git/info/attributes` in place it exits **0**;
  and a real trailing-space defect on the same file still exits **2**. The `cr-at-eol` grant does not blind the
  check.
- **NTFS junctions — now covered by a test.** `WorkspaceFileScannerWindowsTests` plants real junctions and proves
  the scanner neither follows nor emits one, in list and in search, and refuses a scan root that is itself a
  junction. Junctions need no privilege, unlike symbolic links (which need Developer Mode or elevation, and whose
  tests skip on a stock box).
- **NEW 2026-08-03 — a redirected `%LOCALAPPDATA%` makes Development Mode fail before the model is ever called.**
  Found while running this check. Every Development attempt died instantly, 0 tokens, with the UI message
  *"The Development coder attempt violated a workspace security policy."* and this in the console:

  ```
  DevelopmentWorkspaceSecurityException: The preserved Development worktree no longer matches its exact trusted base.
     at DevelopmentWorkspaceProvider.ValidatePreservedWorktreeAsync(...)
     at DevelopmentWorkspaceProvider.PrepareAsync(...)
  ```

  The worktree had been created and fully populated first — the clone works. What fails is the identity check.
  `ValidatePreservedWorktreeAsync` compares `git rev-parse --show-toplevel` against `Path.GetFullPath` of the
  path the engine built from its own data directory, and **those two disagree whenever `%LOCALAPPDATA%` is
  redirected**, because .NET reports the virtual path while `git.exe` resolves the backing one:

  ```
  git : C:/Users/<u>/AppData/Local/Packages/<pkg>/LocalCache/Local/XE-Local-AI-Engine/development/workspaces/...
  .NET: C:\Users\<u>\AppData\Local\XE-Local-AI-Engine\development\workspaces\...
  ```

  Proof the two paths are one file: `node.key` at both hashed identically (SHA-256
  `96D5D49C…249B`), and a host launched outside the redirection picked a *different* loopback port because it
  resolved a genuinely different data root.

  In that session the redirection came from running the host inside an MSIX-packaged parent, which is a harness
  artifact and **not** how a tester launches the RC — launched normally, this check is unaffected. It is
  recorded here because the same mismatch is reachable on a real operator box wherever `AppData\Local` is
  redirected, junctioned, or `subst`ed (roaming/folder-redirection profiles, Store-packaged launchers). The
  symptom is total: Development Mode cannot run at all, and the message names a *security policy*, which reads
  like a deliberate refusal rather than a path-normalisation mismatch. If a tester reports it, get
  `git -C <worktree> rev-parse --show-toplevel` and compare it to the path in the log before treating it as
  tampering. A fix would resolve both sides through the same view (e.g. `GetFinalPathNameByHandle`) rather than
  comparing a .NET-normalised string to git's.

- **Still yours: `MAX_PATH` on a host with long paths DISABLED.** The scanner adds no length ceiling of its own —
  proven on a 399-character path — but that box had
  `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled = 1`. Check yours
  (`Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled`) and **record the
  value with your result** — a pass on a box with it enabled says nothing about one where it is not.

---

## 5b. Coder agent: `list_files` / `search_text`

> **Changed for this RC.** `CoderWorkspaceReader` was the last place `find` and `grep` were shelled out. Both are
> now provider operations (`ISandboxRuntimeProvider.ListFilesAsync` / `SearchTextAsync`), served by the process
> provider through the same jail confinement a read goes through. Coder previously failed on Windows exactly as
> Development Mode did; an earlier revision of this runbook told you to expect that, and it is no longer true.

**What it proves.** That Coder's two read tools work against a real Windows jail, and that the secret exclusions
still apply now that they are a result filter rather than a `grep --exclude` flag.

**Do this.** Select a project folder, then ask the Coder agent (read-only) to list the workspace files and to
search for a string you know appears in one source file. Put a `.env` file containing a recognisable sentinel in
the selected folder first.

**Pass looks like.** Both tools return real results. The listing shows source files and no `.env`, no
`node_modules`, no `bin`. The search returns `path:line:text` matches and **never** a line from `.env`. A regex
search (the tool's opt-in mode) works, and a deliberately invalid one — e.g. `alpha(` — comes back as
*"the search pattern is not a valid regular expression"* rather than as an empty result.

**Fail looks like / next step.** `list_files failed:` or `search_text failed:` in the tool output. Record the
sentence after the colon verbatim — each one names a distinct cause (no workspace, path rejected, path missing,
timed out, provider cannot survey), and which one appears is the whole diagnostic.

> **Pick a NON-REASONING model for this check, or you will prove nothing — measured 2026-08-03.** Gate 5 was
> confirmed correct on the Agents page: **Coder (read-only) grants 3 tools, Default Assistant grants 0**, exactly
> as documented. Gate 4 had also auto-admitted the model (see the note in `docs/agent-knowledge.md` §"Tool calling
> has FIVE independent gates"). Every gate said yes — and the run still produced **no tool call**. The model
> emitted the call as ordinary message text:
>
> ```
> {"name": "list_files", "parameters": {"path": "."}}
> ```
>
> That is the reasoning-model trap from `docs/agent-knowledge.md`, in a new place. `unsloth/Qwen3.6-27B-GGUF:Q8_0`
> carries a **THINKING** badge; it emits `reasoning_content` first, so llama.cpp never enters the constrained
> decoding branch, never compiles the tool grammar, and the JSON arrives as prose that nothing parses as a call.
> It is already documented as making `run-tool-grammar-smoke-local.sh` inert rather than red — the same applies to
> this check, and to **any** live tool-calling verification.
>
> So: run 5b with a non-reasoning tool-capable GGUF (e.g. `bartowski/Qwen2.5-3B-Instruct-GGUF:Q4_K_M`, which is
> also one of the shipped allow-list names). A run on a reasoning model is **not** a check-5b result in either
> direction — do not record it as a pass or a fail. **This half is still unproven on Windows.**

---

## 6. Development Mode: consent disclosure + containment probe

**What it proves.** That the Windows disclosure is truthful. On Windows the process sandbox provider contains
**nothing** — `HostSandboxContainmentProbe.MeasureCore` returns `SandboxContainment.None` (*"the Windows Job Object
path is not implemented"*), so there is no process group, no cgroup CPU/mem/PID ceiling, no network isolation, no
`O_NOFOLLOW`, and no orphan reaping. The consent dialog is the only place the user is told.

> **Unchanged by this pass, and worth stating because check 5 changed.** Moving `list_files`/`search_text` into
> managed code did **not** change containment, and it did not change what runs inside the sandbox — the engine
> already read the host worktree directly for its own workspace invariants and for evidence export, and the
> workspace is the same directory under every provider. Build, test and lint commands still run through the
> sandbox exactly as before. So the dialog text below must still be verbatim what it was; if it has changed,
> something else changed it.

**Do this.** Clear the acknowledged flag (fresh browser profile, or clear site data for the loopback origin),
open `/development`, read the dialog.

> **Do NOT grep for `Sandbox containment probe` on Windows — that line cannot be emitted there.** See the
> correction below; the whole log half of this check was based on a false premise and has been retired.

**Pass looks like.** The dialog shows the **process-provider** branch, containing verbatim:

```
CPU, memory and process-count limits are enforced on Linux only. On Windows there are none, so a runaway command is bounded only by the machine.
```

plus *"…run as the signed-in user account that runs the engine"* and *"They have network access, and nothing
restricts what they can reach."* No container-runtime panel is rendered anywhere on the page.

> **The dialog half is CLOSED — live-rendered and read on real Windows 11, 2026-08-03.** The packaged
> `0.1.0-rc.4.2` build served the **process** branch verbatim, all four bullets:
>
> ```
> On this node those commands run as the signed-in user account that runs the engine — with that account's
> access to your files, not just the repository.
> They have network access, and nothing restricts what they can reach.
> CPU, memory and process-count limits are enforced on Linux only. On Windows there are none, so a runaway
> command is bounded only by the machine.
> Register only repositories you trust. Repository code executes either way.
> ```
>
> plus the closing *"This notice is a disclosure, not a protection."* A scan of the rendered DOM for
> `read-only root filesystem`, `capabilities dropped`, `container`, `Docker`, `image` and `seccomp` returned
> **false for every one**, and the only `data-testid`s present were the consent dialog's own
> (`development-consent-dialog|-terms|-checkbox|-decline|-accept`). No container panel, no Docker preflight.
> What remains open in this check is only the probe LINE — see the correction below.

> ### RETIRED 2026-08-03 (second correction) — the probe line is UNEMITTABLE on Windows, not merely lazy
>
> **This half of the check was never answerable, and two earlier corrections both missed why.** The first said
> the line is emitted lazily; the second said the `/development` visit is not enough and you must run a real
> Development task. Both are wrong on Windows for the same reason: `HostSandboxContainmentProbe.MeasureCore()`
> **returns at its `if (!OperatingSystem.IsLinux())` guard**, and the
> `_logger.LogInformation("Sandbox containment probe: …")` call sits *below* that guard. `AddNodeAgentHome`
> registers exactly one `ISandboxContainmentProbe`, backed by `HostSandboxContainmentProbe`. So on Windows the line is
> **structurally unreachable** — no launch, no page visit, no Development task, and no amount of waiting will
> ever produce it.
>
> The two sessions that measured "0 matches" were therefore measuring the guard, not a missing probe, and the
> instruction to grep-run-grep sent an operator to chase an outcome the code cannot produce. **An empty grep for
> `Sandbox containment probe` on Windows is the correct and only possible result. Do not report it as a finding,
> and do not spend a session on it.**
>
> What the Windows containment posture actually is remains exactly as documented above: `SandboxContainment.None`
> with reason *"the host is not Linux (the Windows Job Object path is not implemented)"*, returned by
> `HostSandboxContainmentProbe.MeasureCore`.
> That posture is worth verifying — but through the **dialog** (closed above) and through the *absence* of any
> containment claim, not through a log line. If you want the reason string observed at runtime, it reaches the UI
> via the capability/consent payload, not the log.
>
> **What was confirmed statically for this RC, and what still needs your session.** The container branch cannot
> render on a stock node: the shipped `appsettings.json` leaves `Development:Sandbox:Provider` unset (the
> `Development` section is `{"Enabled": true}` only), `DockerSandboxRuntimeProvider` is selected only by an
> explicit `docker` value, and the consent gate picks its branch from the `sandboxProvider` the backend reports
> rather than from anything the screen assumes. So a false container-safety claim is structurally unreachable
> here. What was **not** exercised is the live render — that needs an authenticated browser session, which is
> yours to do.

There is **no** accompanying log line to look for. The text below is what the probe *would* log on a Linux host
and is kept only so nobody re-derives it and goes looking for it on Windows:

```
Sandbox containment probe: process group False, resource limits False (the host is not Linux (the Windows Job Object path is not implemented)), network isolation False (the host is not Linux (the Windows Job Object path is not implemented)).
```

**Fail looks like / next step.**
- The **container** branch bullets ("read-only root filesystem", "all capabilities dropped") on a `process` node →
  materially false safety claim. P0 for a disclosure.
- A container-runtime panel or Docker preflight rendered → the capability endpoint should return
  `containerRuntime: null` on a `process` node.
- Any `The 'process' sandbox provider cannot …` exception during a Dev run → structurally unreachable for the
  Development request today (it asks `NetworkPolicy.Unrestricted`, no `ResourceLimits`, and gates its one
  read-only mount behind a capability the process provider never advertises). If it fires, a caller regressed.

---

## 7. Partial GPU offload on 16 GB

**What it proves.** The full-offload path is already live-proven (Linux/CUDA: the app reported
`gpuOffloadedLayers 33 / gpuTotalLayers 33` for `unsloth/Qwen3.5-9B-GGUF:Q5_K_M`, matching llama.cpp's own
`offloaded 33/33 layers to GPU` banner). **The partial path is not**, and on 16 GB it is the routine case, not an
edge case. It also proves partial-offload and CPU-fallback are surfaced as *distinct* states —
`LlamaLayerPlacement.IsPartial`: *"This is NOT a CPU fallback — the GPU is in use, just not for the whole model."*

**Do this.** Load a model that cannot fit 16 GB (a Q5_K_M / Q6_K quant in the 24–32B class). Watch the console
during load, then open the Model Recommendations page → Hardware profile card. Then eject the model and re-check
the card.

**Pass looks like.**
- Console (forwarded at Information): `llama-server[<model>/Chat] ... load_tensors: offloaded N/M layers to GPU`
  with **N < M**.
- Supervisor warning: `llama-server placed N/M of model <name> role Chat layers on the GPU; the remainder runs
  from system RAM, which is substantially slower.`
- Card shows layers on GPU as `N / M` and the **partial-offload alert**
  (`data-testid="model-fit-hardware-partial-offload-alert"`) is visible.
- The **CPU-fallback alert** (`model-fit-hardware-cpu-fallback-alert`) is **NOT** visible, and
  `inferenceBackend` is `cuda`.
- After ejecting the model, the `N / M` reading and the partial alert **disappear**.

> ### CLOSED 2026-08-03 — all five criteria, on a 32 GB card, by deliberately oversizing the quant
>
> **Read the 32 GB caveat below before quoting any number here.** On this card the path had to be provoked: the
> 18.5 GB NVFP4 build fully offloads, and so does `Q8_0` at 26.6 GiB (`offloaded 65/65`, 31741 MiB, 467 MiB
> free) *despite the picker grading it `TIGHT`*. What forces the spill is a quant that cannot physically fit:
> **`unsloth/Qwen3.6-27B-GGUF:UD-Q8_K_XL`, 35,325,163,744 bytes (32.9 GiB) on a 31.8 GiB card.**
>
> All five criteria, measured:
>
> 1. **Console at `-lv 4`** — llama.cpp's own fit walk-down, which is worth reading in full because it shows the
>    decision rather than just the outcome:
>    ```
>    common_params_fit_impl: projected to use 33979 MiB of device memory vs. 30841 MiB of free device memory
>    common_params_fit_impl: cannot meet free memory target of 1024 MiB, need to reduce device memory by 4162 MiB
>    common_params_fit_impl: context size set by user to 65536 -> no change
>    common_params_fit_impl: filling dense layers back-to-front:
>    common_params_fit_impl: id=0, n_layer=65, ... mem= 33979 MiB
>    common_params_fit_impl: id=0, n_layer=56, ... mem= 30019 MiB
>    common_params_fit_impl: id=0, n_layer=55, ... mem= 29570 MiB
>    common_params_fit_impl:   - CUDA0 (NVIDIA GeForce RTX 5090): 55 layers,  29570 MiB used,   1270 MiB free
>    load_tensors: offloading 54 repeating layers to GPU
>    load_tensors: offloaded 55/65 layers to GPU
>    ```
>    Note it honoured the explicit `-c 65536` and spilled *layers* instead of shrinking context — the `-c` the
>    launch policy emits is respected, exactly as the spawn invariants say.
> 2. **Supervisor warning, same N/M:**
>    ```
>    [WRN] llama-server placed 55/65 of model unsloth/Qwen3.6-27B-GGUF:UD-Q8_K_XL role Chat layers on the GPU;
>    the remainder runs from system RAM, which is substantially slower.
>    ```
> 3. **Card:** `Layers on GPU  55 / 65`, and `model-fit-hardware-partial-offload-alert` visible — *"Only 55 of
>    …'s 65 layers fit on the GPU. The remaining 10 run from system RAM…"* plus its remediation testid.
> 4. **The two states stayed distinct.** `model-fit-hardware-cpu-fallback-alert` was **not in the DOM**, and the
>    profile response read `"inferenceBackend":"cuda"`, `"gpuExpected":true`, `"cpuFallback":false`,
>    `"cpuFallbackReason":null`, `"backendUndeterminedReason":null`, `"gpuOffloadedLayers":55`,
>    `"gpuTotalLayers":65`, `"gpuOffloadModelName":"unsloth/Qwen3.6-27B-GGUF:UD-Q8_K_XL"`,
>    `"gpuOffloadRole":"chat"`.
> 5. **Eject retired it.** After a graceful eject the card read `Layers on GPU  Not measured yet` and **both**
>    alerts were absent. The stale-partial-reading failure mode did not occur.
>
> **Choosing the model matters more than the fit verdict.** `TIGHT` did not spill and `WON'T FIT` did. If you
> need this check on a 16 GB box the routine quants get you there for free; on anything larger, pick a file whose
> **raw byte size exceeds free VRAM** and ignore the grading.

**Fail looks like / next step.**
- No `N / M` at all → the sniffer missed the banner. Confirm `-lv 4` is on the spawn's argument vector (grep the
  spawn line in the log); the banner only exists above the default verbosity.
- Both alerts fire → the two states are conflated; that is the bug this design exists to prevent.
- The `N / M` reading survives an eject → `TeardownProcess` did not retire it. Because `Current` ranks any
  partial reading above every full one, a stale partial keeps telling the user a model they unloaded is spilling
  to RAM, for the rest of the process lifetime.

---

## 8. Zero-device GPU runtime — which alert fires (open question)

**What it proves.** Resolves an unexplained branch. Observed on Linux this session: pointing
`XE_LLAMACPP_SERVER_PATH` at a Vulkan build that enumerates **zero** devices produced
`inferenceBackend: "unknown"` with a `backendUndeterminedReason` reading *"…the probe timed out or the binary
could not be started…"*, and **not** `cpuFallback: true`. Per `LlamaDeviceInventoryProbe`, a probe that runs
successfully and returns an empty device list should yield `ProbeSucceeded = true` + zero devices, which
`RuntimeDeviceAuditService.ResolveInferenceBackend` reads as `"cpu"`. So it is unclear which branch actually
fired — and the reason string blames *"a wedged or busy GPU driver"*, which is the **wrong diagnosis** if the
real cause is a missing/broken Vulkan ICD.

**Do this.** Point the override at a GPU llama-server build that enumerates zero devices on this box (e.g. a
Vulkan build with no working ICD), restart, refresh the hardware profile:

```powershell
$env:XE_LLAMACPP_SERVER_PATH = "C:\path\to\llama-server.exe"
```

Record verbatim, from the hardware profile card (or the profile response in the browser network tab):
`inferenceBackend`, `gpuExpected`, `cpuFallback`, `cpuFallbackReason`, `backendUndeterminedReason` — and which
of the two alerts renders.

**Pass looks like.** Self-consistent: zero devices with a GPU present ⇒ `inferenceBackend: "cpu"`,
`cpuFallback: true`, the **CPU-fallback** alert, and `backendUndeterminedReason: null`.

> ### ANSWERED 2026-08-03 on Windows 11 — the managed path PASSES; only a bring-your-own override misreported
>
> **Read this before re-testing: the first version of this note was wrong about the blast radius, and the
> correction is the useful part.** The check was initially recorded as a broad failure. Re-testing with the
> override removed showed the opposite, so the two configurations must be tested separately.
>
> **Managed binary (what a tester actually runs) — PASSES.** Same box, same `CUDA_VISIBLE_DEVICES=-1`, no
> `XE_LLAMACPP_SERVER_PATH`. Exactly the "Pass looks like" above:
>
> ```json
> "inferenceBackend": "cpu", "gpuExpected": true, "cpuFallback": true,
> "cpuFallbackReason": "The CUDA llama.cpp runtime is selected but enumerated no GPU devices …",
> "backendUndeterminedReason": null
> ```
>
> and the **CPU-fallback** alert renders ("Running on CPU despite a detected GPU"). So an AMD/Intel tester whose
> Vulkan build finds no ICD — the population this check was written for — gets the correct verdict. `BuildState`
> and `ResolveInferenceBackend` were right all along.
>
> **Bring-your-own override — misreported, now fixed.** With `XE_LLAMACPP_SERVER_PATH` at a GPU-variant binary
> that enumerates no devices, `LlamaCppBinaryManager.ResolveOverrideBinaryAsync` **refuses it on purpose** (the
> no-silent-CPU invariant, so a mis-tagged binary cannot run wrong-but-green). That deliberate refusal reached
> `LlamaDeviceInventoryProbe` as an exception indistinguishable from a spawn glitch, so the card said *"the probe
> timed out or the binary could not be started"* and blamed *"a wedged or busy GPU driver"* — sending the
> operator to diagnose a driver that was working perfectly. The control: `--list-devices` under
> `CUDA_VISIBLE_DEVICES=-1` prints `(none)` and **exits 0 in ~0.1 s**, so neither clause was true.
>
> Fixed: the text now lists the causes and names the override case first, and the probe logs the refusal at
> **Warning** instead of Debug so the reason is actually in the log the card points at. Re-verified from the
> packaged `0.1.0-rc.4.2` artifact at the shipped log level.
>
> **What is still open here**: on the override path `cpuFallback` stays false, so `GetEffectiveProfileAsync` does
> not zero the VRAM and sizing keeps trusting it. That is mitigated — the same refusal blocks inference outright,
> so the operator gets a hard failure rather than a silent wrong-sized run — but the state is still
> "undetermined-but-trusting". Distinguishing a refused binary from an unrunnable probe in the audit itself, so
> it can size against RAM, is the real follow-up.
>
> **The control that makes this evidence.** The probe does not time out and the binary starts fine — measured
> directly, before touching the app:
>
> ```
> > $env:CUDA_VISIBLE_DEVICES = "-1"; & llama-server.exe --list-devices
> 0.00.086.165 E ggml_cuda_init: failed to initialize CUDA: no CUDA-capable device is detected
> Available devices:
>   (none)
> exit=0
> ```
>
> Exit **0**, in ~0.1 s, with an empty device list. That is `ProbeSucceeded = true` + zero devices, which
> `RuntimeDeviceAuditService.ResolveInferenceBackend` is documented to read as `"cpu"`.
>
> **What the product actually reported**, verbatim from the hardware-profile response with the host restarted
> under `XE_LLAMACPP_SERVER_PATH` = the CUDA `llama-server.exe` and `CUDA_VISIBLE_DEVICES=-1`:
>
> ```json
> "inferenceBackend": "unknown",
> "gpuExpected": true,
> "cpuFallback": false,
> "cpuFallbackReason": null,
> "cpuFallbackRemediation": null,
> "backendUndeterminedReason": "The CUDA llama.cpp runtime is selected, but listing its GPU devices did not
>   complete (the probe timed out or the binary could not be started), so whether inference will use the GPU is
>   unknown. Model sizing on this page still assumes the GPU's VRAM is usable. A wedged or busy GPU driver is
>   the usual cause; refreshing the hardware profile re-runs the probe.",
> "gpuOffloadedLayers": null, "gpuTotalLayers": null
> ```
>
> The alert that renders is **`model-fit-hardware-backend-undetermined-alert`** ("Could not determine your GPU
> backend"). `model-fit-hardware-cpu-fallback-alert` is **not present in the DOM at all**.
>
> Three separate problems, worth filing as three:
>
> 1. **A successful probe returning zero devices is reported as a failed probe.** "the probe timed out or the
>    binary could not be started" is false in both clauses. Whatever feeds `ProbeSucceeded` is not distinguishing
>    exit-0-with-no-devices from a start/timeout failure.
> 2. **The remediation names the wrong cause.** "A wedged or busy GPU driver is the usual cause" sends the
>    operator to reboot a driver that is working perfectly — `nvidia-smi` and a normal-launch `--list-devices`
>    both answer instantly on the same box, seconds apart.
> 3. **Sizing keeps trusting the VRAM.** `vramKnown: true`, `gpuAccelAvailable: true`, `gpuExpected: true` and
>    the card still shows `VRAM 31.8 GB`, while the response itself says it does not know whether the GPU will be
>    used. The card says so out loud — *"Model sizing on this page still assumes the GPU's VRAM is usable."* —
>    which is honest, and is also the bug: a node that will actually run on the CPU is being sized as if it had
>    31.8 GB of VRAM.
>
> Reproduce it with the CUDA binary and `CUDA_VISIBLE_DEVICES=-1`; no exotic runtime or broken ICD is needed.

**Fail looks like / next step.** The undetermined-backend alert on the **managed** path (no
`XE_LLAMACPP_SERVER_PATH` set) — that is the regression this check guards, because the managed path is what a
tester runs and it is documented above as passing.

> **The string to match changed in `9f189ab0` — do not grep for the old one.** The wedged-driver wording quoted
> in the evidence block above (*"the probe timed out or the binary could not be started"* / *"A wedged or busy
> GPU driver is the usual cause"*) was **deleted**, and
> `RuntimeDeviceAuditServiceTests.BuildState_UndeterminedReason_NamesTheOverrideCause_AndAssertsNoSingleCause`
> now asserts it is absent. `RuntimeDeviceAuditService.BuildUndeterminedText` lists causes instead of asserting one:
>
> ```
> …but its GPU devices could not be listed… Common causes: a bring-your-own XE_LLAMACPP_SERVER_PATH override
> that was rejected…, a runtime whose libraries could not be loaded, or a busy GPU driver that made the probe
> overrun.
> ```
>
> Grepping for the retired phrase will always miss, which reads as "the alert never fired" — the opposite of the
> truth. Match on `backendUndeterminedReason` being non-null instead of on any particular sentence.

Capture whether the probe genuinely overran or ran and returned nothing — those must not collapse to the same
verdict. Also note the `cpuFallbackReason` remediation is still Linux/WSL-flavoured (*"commonly a missing Vulkan
ICD under WSL2"*, `RuntimeDeviceAuditService.BuildFallbackText`) and is shown verbatim to a Windows operator.

---

## 9. GPU detection on a non-NVIDIA adapter

Skip unless an AMD/Intel-only Windows box is available. Characterization, not pass/fail on the primary target.

> **NOT APPLICABLE on the 2026-08-02/03 verification box, and deliberately reported as such rather than as a
> pass.** That machine enumerates two adapters — `AMD Radeon(TM) Graphics` (integrated) listed **first**, then
> `NVIDIA GeForce RTX 5090`. `MapAdapterVendor` tests `nvidia` **before** `amd` against the whole listing, so it
> maps to `nvidia` regardless of enumeration order, and the run confirmed `"gpuVendor":"nvidia"` throughout. The
> AMD/Intel result is therefore **unreachable** here — not passing, not failing, unexercised. A genuinely
> AMD/Intel-**only** Windows box is still the only way to exercise it, and nothing in this pass changed that.

> **Changed for this RC.** Both detectors have stopped depending on `wmic`. `ProcessGpuVendorProbe` (which
> chooses the llama.cpp variant) and `HardwareProfiler` (which fills the hardware-profile card) now read adapter
> descriptions from `Win32_VideoController` via `Get-CimInstance`, preferring
> `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` by absolute path, then bare `powershell`, then
> `wmic` last. `HardwareProfiler.ProbeWindowsAdapterVendor` is no longer a hardcoded `Unknown` stub.
>
> **The query itself is now confirmed to answer on real Windows 11** (build 26220), which was the open half:
> `where.exe wmic` finds **nothing** — the premise holds, `wmic` really is absent by default — and the exact
> probe invocation
> (`…\v1.0\powershell.exe -NoProfile -NonInteractive -Command "Get-CimInstance -ClassName Win32_VideoController |
> Select-Object -ExpandProperty Name"`) returns the adapter list in **~1.3 s**, repeatably, against an 8 s
> per-tool deadline. The absolute System32 path exists.
>
> Two things that box could **not** settle, because it is an NVIDIA box:
> - Its `ProcessGpuVendorProbe` never reaches this code at all. `System32\nvml.dll` is present, so the NVML fast
>   path returns `Nvidia` with **zero** processes spawned. The CIM query was verified by running it directly, not
>   by observing the engine run it.
> - It has **two** adapters — `AMD Radeon(TM) Graphics` (integrated) listed **first**, then
>   `NVIDIA GeForce RTX 5090`. That is worth knowing because `MapAdapterVendor` tests `nvidia` **before** `amd`
>   against the whole listing, so a dual-adapter box maps to `nvidia` regardless of enumeration order. A
>   genuinely AMD/Intel-**only** box is still the only way to exercise the AMD/Intel result.

**What it still does not fix.** The CPU-fallback alert remains **structurally unreachable** on an AMD/Intel
Windows box, because `cpuFallback` requires `gpuExpected`, which is `vendor ∈ {nvidia, amd, intel} && vramBytes > 0`
(`RuntimeDeviceAuditService.BuildState`) — and there is still no Windows VRAM-bytes source for a non-NVIDIA
adapter. `ProbeWindowsNonNvidiaVramAsync` is deliberately still the deferred DXGI seam: `Win32_VideoController.AdapterRAM`
is a uint32 that misreports any adapter above 4 GB, and a wrong positive number would feed model-fit sizing, which
is worse than `null`. So the vendor is now named truthfully and the Vulkan binary is selected again, but the box
still shows CPU mode and still raises no alert.

**Do this.**

```powershell
where.exe wmic                                                  # expected: nothing, on a clean Windows 11
Get-CimInstance -ClassName Win32_VideoController | Select-Object -ExpandProperty Name
Measure-Command { Get-CimInstance -ClassName Win32_VideoController | Select-Object -ExpandProperty Name }
```

Then read the hardware profile card and record `gpuVendor`, `vramKnown`, `gpuAccelAvailable`, `gpuExpected`,
`cpuFallback`, `inferenceBackend`, and which badges/alerts render.

**Pass looks like.** `gpuVendor` is now `"amd"` or `"intel"` — **not** `"unknown"` — matching what the
`Get-CimInstance` line printed. `inferenceBackend: "vulkan"`. `vramKnown: false`, `gpuAccelAvailable: false`,
`gpuExpected: false`, `cpuFallback: false`, orange **"CPU mode"** badge shown. That combination is the documented
degraded state, now with a truthful vendor.

**Fail looks like / next step.**

- `gpuVendor: "unknown"` while the `Get-CimInstance` line above printed a real adapter name → the query works on
  your box but the engine's invocation of it does not. Capture the exact adapter string; the mapping is
  substring-based (`amd` / `radeon` / `advanced micro devices` / `intel`, case-insensitive) and an adapter name
  matching none of them is a real gap worth reporting verbatim.
- `inferenceBackend: "cpu"` on a Vulkan-capable box → the variant selector still could not name the vendor. This
  is the original defect surviving; capture whether `powershell.exe` exists at the System32 path above and
  whether it is under a constrained-language or execution policy that would suppress output.
- `Measure-Command` reporting more than **~5 s** → the *hardware-profile card's* deadline fires first and the
  vendor degrades to undetected.

  > **Two different deadlines, and the runbook previously named only the looser one.** The adapter query feeds
  > two independent consumers: `HardwareProfiler` (the hardware-profile card) bounds each native probe by
  > `HardwareProfilerOptions.HardwareProbeTimeoutSeconds`, which **defaults to 5 s**
  > (read by `HardwareProfiler.ResolveProbeTimeout`) and which `AddHardwareProfiler` wires without overriding.
  > The 8 s figure belongs to `ProcessGpuVendorProbe.DefaultProbeTimeout`, which selects the llama.cpp *variant*. So the card
  > degrades at **5 s**, the variant selector at 8 s. Judge against **5 s**. The measured 1.3 s clears both, so
  > no previously recorded pass is affected — only the threshold to fail against was wrong.

  Report the measured figure; the 8 s cap was chosen against typical PowerShell cold-start
  times and has never been measured on real Windows.

**Also record even if everything passes**: the wall-clock delay between launching the app and the first-run
provisioning line appearing. The adapter query is now on that path for non-NVIDIA boxes and its cost has not been
measured on Windows.

---

## 16 GB target notes

- **No VRAM tiers exist anywhere on the hardware-profile path.** Every VRAM decision is a presence test:
  `vramKnown = vramBytes is not null`, `gpuAccelAvailable = vramKnown && vendor ∈ {nvidia, amd, intel}`,
  `gpuExpected = vendor ∈ {…} && vramBytes > 0`. A 16 GB and a 32 GB NVIDIA box differ in exactly one wire field:
  the raw `vramBytes` number. Model-fit sizing is continuous arithmetic (768 MB runtime overhead, 0.12 safety
  margin), not bucketed.
- **The only tiered ladder is context tokens**, not VRAM:
  `[65536, 32768, 16384, 8192, 4096, 2048]` (`LlamaServerLaunchPolicyOptions.ChatContextTiers`). On 16 GB expect the OOM
  classifier to walk it down during load and log
  `llama-server automatic context allocation encountered a classified startup OOM; retrying at context tier <N>`.
  **That is normal on this box, not a failure.** A classified OOM also triggers a one-shot safe-config retry
  (KV-quant + flash-attention off), logged as `…optimized launch (KV-cache quant + flash attention) failed…;
  retrying once with the safe config.`
- **Partial offload is the routine 16 GB path.** The partial-offload alert will fire on 16 GB for models where a
  32 GB box shows nothing at all — same model, same build. Do not treat its appearance as a defect; treat its
  *absence* on a model that visibly spills (slow tokens/s, high system-RAM use) as the defect.

### What a 32 GB box measures, and why none of it is a consumer figure

Everything in this block was measured on **RTX 5090 32 GB / 61.4 GB RAM / 32 CPUs**, which is roughly **2× the
≈16 GB VRAM consumer target**. Every number here **over-reports** and must never be quoted as a consumer figure.
It is recorded so a future session does not re-derive the *shape* of the difference.

- **The spill threshold on this card sits above 26.6 GiB of weights**, so the models that exercise partial
  offload on 16 GB all fully offload here. Two data points, same repo, same build (`b10201` CUDA), same
  `-c 65536` tier:

  | Model | File size | Result | VRAM peak |
  |---|---|---|---|
  | `tngtech/Qwen3.6-27B-NVFP4-GGUF` | 18.5 GB | full offload | 22.7 GB |
  | `unsloth/Qwen3.6-27B-GGUF:Q8_0` | 26.6 GiB | **`offloaded 65/65 layers to GPU`** | **31741 MiB** |

  The second one is the useful one: the download picker graded it **`TIGHT`**, and it still fitted *entirely*,
  leaving **467 MiB** free. `--fit on` shrank batch/context around the weights rather than spilling. So a `TIGHT`
  fit verdict is **not** a prediction of partial offload, on this hardware or any other.

- **The picker's own fit ladder is a cheap way to see where the line is on your box.** On this one, for
  `unsloth/Qwen3.6-27B-GGUF`: `Q6_K` 21.0 GB `FITS` (and `RECOMMENDED`) · `UD-Q6_K_XL` 23.9 GB `FITS` ·
  `Q8_0` 26.6 GB `TIGHT` · `UD-Q8_K_XL` 32.9 GB `WON'T FIT` · `BF16` 50.1 GB `WON'T FIT`. On a 16 GB box the
  same ladder shifts down by roughly half, which is exactly why the routine quant there is a Q4/Q5 and the
  routine placement is partial.

- **The two VRAM readers already disagree at idle here, with nothing loaded.** `nvidia-smi memory.free` reported
  **28006 MiB** while `llama-server --list-devices` reported **30991 MiB free** on the same card seconds apart.
  §2's divergence note describes this under memory pressure; on Windows it is visible with the machine idle,
  because the desktop compositor's ~4 GB is global but is not charged to the probing process's budget. Do not
  treat a disagreement between those two numbers as evidence of anything by itself.

- **The desktop runs on the same card, so "idle baseline" is not near zero.** Measured idle: **3906–4194 MiB**
  used of 32607 MiB. Check 1's "back to baseline" therefore means back to ~3 GB, not to single-digit MiB. A
  headless GPU is the only place the runbook's original wording is literally right.

- **Unreachable here, and honestly unreachable — do not fake them.** The context-tier walk-down
  (`[65536, 32768, 16384, …]`), the classified startup OOM, and its one-shot safe-config retry never fired in
  this session: the chat spawn took the **top** tier (65536) first time and stayed there. On a 32 GB card there
  is no pressure to walk down. Trying to manufacture it by occupying VRAM from another process is worse than
  not testing it — see §2 of `docs/agent-knowledge.md`: on WDDM, VRAM exhaustion **silently demand-pages to host
  RAM instead of OOMing** (measured 161.7 vs 698.4 tok/s, a 4.3× slowdown with zero errors), so the OOM branch
  cannot be reached that way and every number taken under that pressure is a paged number.
- **Every GPU spawn pays `-lv 4`** (~213 extra startup lines at Information; ~22 lines/request demoted to Debug
  once serving). A chatty console during model load is by design — that output *is* the placement evidence.
- **Startup-failure capture is last-64-lines / 16 KB, deliberately.** At `-lv 4` the "out of memory" text lands
  around line 179 of 186; a first-N window would capture only loader metadata and silently disable the context
  down-tier. If you ever see an OOM that does not down-tier, that window is the first place to look.
