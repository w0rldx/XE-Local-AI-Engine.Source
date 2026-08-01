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

---

## 1. Job Object process tree-kill on hard kill

**What it proves.** That the Win32 Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) actually reaps
`llama-server.exe` when the host dies without running managed shutdown — catching the worst failure on a 16 GB
box: an orphan holding 8–14 GB of VRAM and a loopback port forever. The code carries an explicit
operator-verification flag (`WindowsJobObjectProcessHandle.cs:17-23`: *"real tree-kill behavior MUST be verified
on Windows 11"*) and has never been executed on Windows.

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

**What it proves.** The fail-closed key-ring resolver that refuses to regenerate an undecryptable ring
(`NodeDataProtectionKeyRingFailClosedKeyResolver.cs:65-68`) is **registered on Linux only** —
`ConfigureServices.cs:111-135` takes the `ProtectKeysWithDpapi` branch on Windows and leaves ASP.NET Core's stock
`DefaultKeyResolver` in place. So on Windows an unreadable DPAPI ring silently mints a new key and orphans every
`*.enc` credential (HF token, GitHub auth, cloud creds) with no hard failure.

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

**Fail looks like / next step.** A fresh `key-<guid>.xml` on each launch and/or:

```
Hugging Face token decryption failed. Clearing the stored token.
Worker credential decryption failed. Clearing stored credentials and requiring re-pairing.
```

Ring is regenerating. On Windows this is silent self-healing by design gap, not by intent — the loud Linux
backstop does not exist here. Capture whether the ring rotated on a plain restart (bug) or only after a Windows
credential/profile change (expected DPAPI behaviour, but still silent).

---

## 5. Development Mode: `find` / `grep` shell-out

**What it proves.** `DevelopmentWorkspaceTools` invokes the **bare** executables `find` and `grep` with POSIX-only
argument vectors (`DevelopmentWorkspaceTools.cs:127` and `:233`; args built at `:160-187` and `:225-230`) and
there is **no OS branch anywhere in the file**. On stock Windows 11 `grep` does not exist and `find` resolves to
the DOS `find.exe`, which rejects `-maxdepth`/`-iname`/`-prune`. This is the most likely genuine Windows failure
in the whole build, and every test covering it skips off Linux
(`DevelopmentWorkspaceAndCoderTests.cs:319-323`, `:376-380`, `:518-522`, `:724-728`, `:914-917`).

**Do this.**

```powershell
where.exe find
where.exe grep
```

Then open `/development`, accept the consent gate, register a trusted repo, and run a Development task that
exercises **both** `list_files` and `search_text`.

**Pass looks like.** `where.exe grep` resolves (typically `C:\Program Files\Git\usr\bin\grep.exe`), the GNU `find`
precedes `C:\Windows\System32\find.exe` in the `where.exe find` output, and both tool calls return real results.

**Fail looks like / next step.** Either of these exact strings in the task output or log:

```
The fixed Development list_files operation failed.
The fixed Development search_text operation failed.
```

Prepend `C:\Program Files\Git\usr\bin` to `PATH` and retest. **If that fixes it, it is a product bug, not an
environment problem** — the shipped RC cannot assume Git-for-Windows. Note that `System32` normally precedes
Git's `usr\bin` in `PATH`, so `find` will resolve to the DOS tool on most boxes even with Git installed; expect
`list_files` to fail more often than `search_text`.

---

## 6. Development Mode: consent disclosure + containment probe

**What it proves.** That the Windows disclosure is truthful. On Windows the process sandbox provider contains
**nothing** — `HostSandboxContainmentProbe.cs:72-83` returns `SandboxContainment.None` (*"the Windows Job Object
path is not implemented"*), so there is no process group, no cgroup CPU/mem/PID ceiling, no network isolation, no
`O_NOFOLLOW`, and no orphan reaping. The consent dialog is the only place the user is told.

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
restricts what they can reach."* No container-runtime panel is rendered anywhere on the page. Exactly **one**
log line, matching:

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

**What it proves.** Three known gaps at once: (a) Windows adapter enumeration shells out to **`wmic`**
(`ProcessGpuVendorProbe.cs:161-174`), which is a deprecated, disabled-by-default Feature-on-Demand on current
Windows 11 — its absence is swallowed and collapses the vendor to `None` → `GpuVariant.Cpu`, i.e. a Vulkan-capable
box runs CPU-only with no alert; (b) the two detectors disagree — Detector A can select the **Vulkan** binary
while `HardwareProfiler.ProbeWindowsAdapterVendor()` (`HardwareProfiler.cs:328-341`) is a hardcoded
`GpuVendor.Unknown` stub, so the profile shows `gpuVendor: "unknown"`, `gpuAccelAvailable: false`; (c) because
`cpuFallback` requires `gpuExpected` (`RuntimeDeviceAuditService.cs:176`), the **CPU-fallback alert is
structurally unreachable** on exactly the machine most likely to be silently CPU-bound.

**Do this.**

```powershell
where.exe wmic
wmic path win32_VideoController get name
```

Then read the hardware profile card and record `gpuVendor`, `vramKnown`, `gpuAccelAvailable`, `gpuExpected`,
`cpuFallback`, `inferenceBackend`, and which badges/alerts render.

**Pass looks like** (the documented degraded behaviour, already disclosed in `CHANGELOG.md`):
`gpuVendor: "unknown"`, `vramKnown: false`, `gpuAccelAvailable: false`, `gpuExpected: false`,
`cpuFallback: false`, orange **"CPU mode"** badge shown, and `inferenceBackend` either `"vulkan"` (wmic matched
the adapter, so the Vulkan binary was selected) or `"cpu"` (it did not).

**Fail looks like / next step.** `where.exe wmic` finds nothing → `inferenceBackend: "cpu"` on a
Vulkan-capable box with **no alert of any kind**. That is a silent full-performance loss. Next step is the
deferred DXGI/`Win32_VideoController` seam named in the code comment at `HardwareProfiler.cs:328-341`; note that
`ProcessGpuVendorProbe` has no PowerShell/`Get-CimInstance` fallback either, so both detectors fail together.

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
