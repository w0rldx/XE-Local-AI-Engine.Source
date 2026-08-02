# Windows 11 RC verification runbook

Target: a tester RC portable build (Velopack, `--noInst`) on real Windows 11. Nine checks, risk-ordered.
Budget ~1 hour. Everything below covers a code path that **cannot be exercised on the Linux/WSL dev box**, which
is why it is here at all.

Data root for every path below: `%LOCALAPPDATA%\XE-Local-AI-Engine`.

Useful once, up front:

```powershell
$XE   = "$env:LOCALAPPDATA\XE-Local-AI-Engine"
$LOGS = "$XE\logs\*.log"
function xe-proc { Get-Process | Where-Object ProcessName -like 'XE-Local-AI-Engine*' | Select-Object Id,ProcessName,Path }
function llama  { Get-Process llama-server -ErrorAction SilentlyContinue | Select-Object Id,Path }
```

Note `llama-server` — the OS process table carries no extension on Windows, even though the file on disk is
`llama-server.exe` (`OsStaleLlamaServerProcessScanner.cs:17`).

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

Still fully open, and still the reason this runbook exists: **checks 3, 7 and 8**, and the deliberately-broken
half of check 4.

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
> (`DesktopLifecycle.cs:39`, `:153-181`). That runs the full graceful supervisor teardown and proves nothing about
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
step 3 (single-digit MiB on a headless GPU). No `llama-server` in Task Manager.

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
(`DesktopBootstrap.cs:393-402`). **This branch is completely untested** — the only OS-conditional test
(`DesktopBootstrapTests.cs:201-221`) returns early `if (OperatingSystem.IsWindows())`, and `ProtectedData`
appears in no test file. A regression here writes the SQLite operator secret to disk in plaintext.

**There is no log line for any of this.** `DesktopBootstrap` runs at `Program.cs:92`, before the logger exists at
`:104`, and has no `ILogger`/`Console` call anywhere. Verification is filesystem-only.

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
  `DesktopBootstrap.cs:207-218` cannot regenerate while the file exists).

**Fail looks like / next step.**
- `$b.Length` is **exactly 32** → the secret is on disk in plaintext; the DPAPI branch did not run. P0.
  (This is the product's own discriminator: `UnwrapSecretBytes` + the length gate at `DesktopBootstrap.cs:253-259`.)
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
> surveys are managed code (`DevelopmentWorkspaceFileScanner`) on every platform, so the behaviour a Linux test
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

---

## 6. Development Mode: consent disclosure + containment probe

**What it proves.** That the Windows disclosure is truthful. On Windows the process sandbox provider contains
**nothing** — `HostSandboxContainmentProbe.cs:72-83` returns `SandboxContainment.None` (*"the Windows Job Object
path is not implemented"*), so there is no process group, no cgroup CPU/mem/PID ceiling, no network isolation, no
`O_NOFOLLOW`, and no orphan reaping. The consent dialog is the only place the user is told.

> **Unchanged by this pass, and worth stating because check 5 changed.** Moving `list_files`/`search_text` into
> managed code did **not** change containment, and it did not change what runs inside the sandbox — the engine
> already read the host worktree directly for its own workspace invariants and for evidence export, and the
> workspace is the same directory under every provider. Build, test and lint commands still run through the
> sandbox exactly as before. So the dialog text below must still be verbatim what it was; if it has changed,
> something else changed it.

**Do this.** Clear the acknowledged flag (fresh browser profile, or clear site data for the loopback origin),
open `/development`, read the dialog. Then:

```powershell
Select-String -Path $LOGS -Pattern 'Sandbox containment probe'
```

**Pass looks like.** The dialog shows the **process-provider** branch, containing verbatim:

```
CPU, memory and process-count limits are enforced on Linux only. On Windows there are none, so a runaway command is bounded only by the machine.
```

plus *"…run as the signed-in user account that runs the engine"* and *"They have network access, and nothing
restricts what they can reach."* No container-runtime panel is rendered anywhere on the page.

> **The probe line is emitted lazily — do the `/development` visit BEFORE you grep.** Measured on Windows 11
> across four launches of the packaged build: `Sandbox containment probe` appears **nowhere** in the log after an
> ordinary start. Containment is measured on first use of the sandbox, not during host startup, so an empty grep
> on a node where nobody opened Development Mode means "not probed yet", not "the probe is missing". Grepping
> first and finding nothing is the easiest way to file a false failure here.
>
> **What was confirmed statically for this RC, and what still needs your session.** The container branch cannot
> render on a stock node: the shipped `appsettings.json` leaves `Development:Sandbox:Provider` unset (the
> `Development` section is `{"Enabled": true}` only), `DockerSandboxRuntimeProvider` is selected only by an
> explicit `docker` value, and the consent gate picks its branch from the `sandboxProvider` the backend reports
> rather than from anything the screen assumes. So a false container-safety claim is structurally unreachable
> here. What was **not** exercised is the live render — that needs an authenticated browser session, which is
> yours to do.

Then exactly **one** log line, matching:

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
`LlamaLayerPlacement.cs:20-24`: *"This is NOT a CPU fallback — the GPU is in use, just not for the whole model."*

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

**Fail looks like / next step.** The undetermined-backend alert instead, with a reason blaming a wedged/busy
driver or a timed-out probe. Capture the full string and whether the probe genuinely timed out (15 s cap) or ran
and returned nothing — those must not collapse to the same verdict. Also note the remediation text is
Linux/WSL-flavoured (*"commonly a missing Vulkan ICD under WSL2"*) and is shown verbatim to a Windows operator.

---

## 9. GPU detection on a non-NVIDIA adapter

Skip unless an AMD/Intel-only Windows box is available. Characterization, not pass/fail on the primary target.

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
- `Measure-Command` reporting more than ~8 s → the probe's per-tool deadline would fire and the vendor would
  degrade to undetected. Report the measured figure; the 8 s cap was chosen against typical PowerShell cold-start
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
  `[65536, 32768, 16384, 8192, 4096, 2048]` (`LlamaServerLaunchPolicyOptions.cs:25`). On 16 GB expect the OOM
  classifier to walk it down during load and log
  `llama-server automatic context allocation encountered a classified startup OOM; retrying at context tier <N>`.
  **That is normal on this box, not a failure.** A classified OOM also triggers a one-shot safe-config retry
  (KV-quant + flash-attention off), logged as `…optimized launch (KV-cache quant + flash attention) failed…;
  retrying once with the safe config.`
- **Partial offload is the routine 16 GB path.** The partial-offload alert will fire on 16 GB for models where a
  32 GB box shows nothing at all — same model, same build. Do not treat its appearance as a defect; treat its
  *absence* on a model that visibly spills (slow tokens/s, high system-RAM use) as the defect.
- **Every GPU spawn pays `-lv 4`** (~213 extra startup lines at Information; ~22 lines/request demoted to Debug
  once serving). A chatty console during model load is by design — that output *is* the placement evidence.
- **Startup-failure capture is last-64-lines / 16 KB, deliberately.** At `-lv 4` the "out of memory" text lands
  around line 179 of 186; a first-N window would capture only loader metadata and silently disable the context
  down-tier. If you ever see an OOM that does not down-tier, that window is the first place to look.
