# Windows RC — remaining work, as a prompt for the Windows-side agent

> **Historical — superseded (noted 2026-08-17). Do not execute this prompt as written.**
> Its central premise — that the packaging -> upload -> publish -> self-update chain "has never once run end to
> end" and that "CI is disabled and has never produced an artifact" — is no longer true. The repository has
> since tagged and shipped `v0.1.0-rc.5.0`, `v0.1.0-rc.5.1` and `v1.0.0-rc.1`, and
> `.github/workflows/release.yml` is an active, tag-triggered (`push` on `v*`, plus `workflow_dispatch`)
> release workflow. The release identity no longer lives in `Directory.Build.props` either: it is
> `eng/ReleaseVersion.props` (`VersionPrefix` / `VersionSuffix`, currently `1.0.0` + `rc.1`). Several commit
> hashes cited below (including the `d56b426a` this file was derived from) no longer resolve in this
> repository, and the `file:line` anchors were taken at that commit and have drifted.
>
> **Current sources instead:** `docs/runbooks/windows-rc-verification-runbook.md` for the Windows product
> checks, and `docs/release-publication-checklist.md` for the release/publication chain. This file is kept in
> place as a dated snapshot of what was open on 2026-08-03.

**Date:** 2026-08-03
**Audience:** the coding agent running on the operator's **real Windows 11** box, with the repo checked out and a
packaged tester build available.
**Companion:** `docs/runbooks/windows-rc-verification-runbook.md` (the nine product checks). This file is the
*remaining* work after the 2026-08-02 and 2026-08-03 sessions closed most of that runbook — it does not repeat
what those sessions already proved.

> **Historical handoff:** commands and process names below describe the former self-contained Windows package. The
> current official design is framework-dependent and uses `XE-Local-AI-Engine.WindowsLauncher.exe` plus
> `dotnet.exe XE-Local-AI-Engine.Client.dll`. Use the companion runbook's current process helper and prerequisite checks.

## Why this file exists

The nine-check runbook is now mostly green. What is left splits into three kinds of work, and only one of them
is "run the runbook again":

1. **Things only a Windows box can execute** — the untested Windows branches, and the test suites themselves.
2. **Things only a Windows box can *release*** — the packaging → upload → publish → self-update chain, which has
   never once run end to end.
3. **Things that are Linux-dev-box code fixes** and must NOT consume a Windows session. They are listed at the
   bottom so the Windows agent hands them back rather than attempting them.

Everything below was derived by reading the code on 2026-08-03 at `d56b426a`. File:line references are from that
commit.

---

## THE PROMPT

> Copy everything between the rules into the Windows agent's session.

---

You are running on the operator's real Windows 11 machine, in a checkout of `XE-Local-AI-Engine`. Nobody
developing this repo has a Windows box, so a large set of Windows-only code paths has never executed anywhere.
You are the only environment that can close them. Work through the phases in order — each one gates the next.

**Ground rules, all learned the hard way. Violating any of these produces a result that looks real and is not:**

- **Read `docs/agent-knowledge.md` first**, then `docs/runbooks/windows-rc-verification-runbook.md`. They record
  traps that cost real debugging time. In particular §"Windows is a shipping target".
- **`dotnet build-server shutdown` before ANY test or pack run.** Leftover MSBuild daemons make process-spawning
  tests fail *at their timeout budget* rather than on an assertion. Three of five release-pack attempts on
  2026-08-03 were lost to this, none to a real defect. **Read the duration column before the assertion message:
  a failure at ~30 s or ~5 s is a load signature, not a behaviour signature.**
- **Release configuration is load-bearing.** Analyzers only run in Release (`Directory.Build.targets`). A green
  Debug build is not verification.
- **Never run a build and a test run concurrently.** Use `scripts/with-build-lock.ps1` and
  `scripts/assembly-guard.ps1`. **Exit 75 means the run is void — re-run it. It does not mean red.**
- **A green run is not evidence if the test skipped.** See Phase 1; this is the single most important thing on
  this page.
- **Report what you measured, verbatim.** Exit codes, log lines, JSON fields. If you did not observe it, say so.
  A plausible-sounding summary of an unrun check is worse than no check.

### Phase 0 — prerequisites (60 seconds, saves ~40 minutes)

Everything here fails *late* inside longer scripts if unmet.

```powershell
pwsh -Version                                    # must be 7.x — package-tester-win.ps1 needs #Requires 7.0
tzutil /g                                        # must NOT be a UTC zone (checked only AFTER the full build)
where.exe git                                    # 9 test classes shell out to a real git
where.exe pnpm                                   # MUST list a .exe — see Phase 2, item 4
node --version                                   # >= 22 for `pnpm test:tooling` (see Phase 2)
gh auth status                                   # needed only for a real release
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled
```

**Record the `LongPathsEnabled` value with every result you report.** Windows 11 ships it at `0`; developer
tooling (the Git for Windows installer) flips it to `1`. The previous verification box had it at `1`, so every
long-path result so far says nothing about a stock tester box. Nothing in this product opts into long paths:
there is no app manifest anywhere in the repo, no `longPathAware` entry, and no `\\?\` prefixing in any C# file.

Then decide about **Developer Mode** (Settings → System → For developers). It is worth turning on: roughly seven
security tests can only create the symbolic links they need to prove a guard when it is on. Run Phase 1 **both
ways** if you can — the delta is the point.

### Phase 1 — the test suites, and the reason a green run currently proves less than it looks

**Read this before running anything.** The test tree carries ~137 OS guards but only 15 of them are honest
`Skip.Test(...)` calls. The rest — including **56 of the 64 `if (!OperatingSystem.IsLinux())` guards** — are a
bare `return;`. A bare return makes TUnit report the test as **passed** having asserted nothing.

So a Windows run that says "5008 passed" is over-reporting by roughly 90 tests that did nothing. **You cannot
currently distinguish a real Windows pass from a no-op by reading the summary.** Treat the numbers accordingly,
and when you report results, report *which* areas actually executed rather than the total.

The areas that silently no-op on Windows are, in descending order of how much they matter:

- `Sandbox/ProcessSandboxRuntimeProviderTests.cs` — every symlink/jail-escape rejection test (`:197, :222, :260,
  :287, :316, :345, :372, :1360`). Note `ProcessSandboxRuntimeProvider.cs:1451-1456` **falls back to a plain
  write on non-Linux** because there is no `O_NOFOLLOW` there — so on Windows this guarantee is neither enforced
  nor tested.
- `Coder/CoderWorkspaceReaderTests.cs` — 9 of 18, the whole coder read/list/search surface.
- `Providers/Capabilities/ProcessProbeTests.cs` — 4 of 5, including process-tree kill.
- The CUDA/source-build classes (`CudaBuildServiceTests` 8 of 8, `LlamaCppSourceBuildPrerequisiteTests` 3 of 3,
  `OverrideBinaryManagerTests` 6 of 9) — these are Linux-only *features*, so their absence on Windows is correct.
  Do not report them as gaps; just do not count them as passes either.

Run the suites:

```powershell
dotnet build-server shutdown

.\scripts\with-build-lock.ps1 -- dotnet restore XE-Local-AI-Engine.slnx
.\scripts\with-build-lock.ps1 -- dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore

$log = Join-Path $env:TEMP "xe-tests-$([Guid]::NewGuid().ToString('N')).log"
.\scripts\with-build-lock.ps1 -- .\scripts\assembly-guard.ps1 guard -TestBins -- `
  dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1 |
  Tee-Object -FilePath $log
# hollow-gate guard: dotnet test exits 0 when zero projects enrol
if (-not (Select-String -Path $log -Pattern 'Passed!|Failed!' -Quiet)) { throw "zero test projects enrolled" }
```

**What running this on Windows actually buys** — these classes are inert on Linux and execute only here:

| Class | What it proves |
|---|---|
| `WindowsJobObjectTreeKillTests` | Job Object tree-kill, **and** that a hard `TerminateProcess` of the owning process still reaps the child |
| `WorkspaceFileScannerWindowsTests` | NTFS **junction** no-follow (needs no privilege), plus list/search past `MAX_PATH` |
| `ProcessSandboxRuntimeProviderTests` (the ~29 that do run) | the whole process-sandbox suite against `cmd.exe`/`ping`/`type` fixtures |
| `StaleLlamaServerReaperTests` / `StaleImageServerReaperTests` | the `OrdinalIgnoreCase` root-containment arm and `.exe` process naming |
| `SingleInstanceLeaseTests` | the Windows sharing/lock **HResult** discrimination in `SingleInstanceLease.cs:70,88` — a disk-full error must throw, not be misreported as "already running" |
| the 7 credential stores | `ApplyWindowsFileSecurity()` — the ACL write at 7 sites (`HfTokenStore.cs:141` and peers) is **unguarded**, so a throw fails the whole save. These tests execute it on Windows; they just do not assert the resulting ACL |

Then the frontend, exactly as the release script runs it:

```powershell
Push-Location XE-Local-AI-Engine.Client.React
pnpm install --frozen-lockfile
pnpm run lint             # the ONLY typecheck — the E2E fixture runs a bare `vite build`
pnpm run openapi:check
pnpm run licenses:check
pnpm run test:coverage:check
pnpm audit --prod --audit-level=high
pnpm run build
pnpm run test:tooling     # needs Node >= 22 on Windows: `node --test scripts/*.test.mjs` is a glob cmd.exe won't expand
Pop-Location
```

`pnpm` hands script bodies to **`cmd.exe`** on Windows (no `.npmrc`, no `scriptShell` anywhere in the repo), so
bare `&&` is fine. Of the 28 scripts only `test:tooling` has a real Windows problem, and it is not in any gate.

**Report:** total pass/fail per module, every failure with its **duration**, whether `LongPathsEnabled` was 0 or
1, and whether Developer Mode was on. If you ran both ways, give the delta.

### Phase 2 — the highest-value NEW Windows coverage

These are the things that no environment other than yours can settle. Ranked.

1. **`sd-server.exe` orphan containment — the untested twin of runbook check 1.**
   `WindowsImageJobObjectProcessHandle` (`Providers.StableDiffusionCpp/Implementation/
   WindowsImageJobObjectProcessHandle.cs:72`) is a copy of the llama-server Job Object handle, and has **zero
   test references anywhere in the repo** — `grep` finds it only in its own file and at
   `ImageServerProcessLauncher.cs:76`. The image runtime is registered unconditionally
   (`AddNodeImagesExtensions.cs:25-26`) and the route is capability-gated, so it ships.

   Do exactly what runbook check 1 does, but for images: generate an image so `sd-server.exe` is resident, then
   **Task Manager → Details → End task** on `XE-Local-AI-Engine.Client.exe` (not the root exe — that is the
   Velopack stub and has already exited). Within 5 s, `Get-Process sd-server` must return nothing and VRAM must
   return to baseline. Sample **both** the process table and `nvidia-smi`: VRAM drops about a second *before*
   the process disappears, so either signal alone lies.

   A surviving `sd-server.exe` is a **P0** — the Job Object is the only orphan defence on Windows.

2. **CUDA runtime DLL pairing.** `LlamaCppBinaryManager.EnsureCudartRuntimeAsync` (`:401-419`) and its
   sd-server twin (`StableDiffusionCppBinaryManager.cs:274-290`) download a *separate* cudart archive and place
   `cudart64_*.dll` / `cublas64_*.dll` beside the server exe. Only the **name derivation** is tested; the
   placement, verification and delete-on-failure have never run. Without those DLLs the CUDA backend silently
   loads CPU-only.

   After a fresh CUDA acquisition, list the runtime directory and confirm the DLLs sit next to the `.exe`. Then
   corrupt the cudart download and confirm no half-CUDA directory is left usable.

3. **Extend the junction pattern to the guards that are still unproven.** `WorkspaceFileScannerWindowsTests.cs:20-28`
   names five link guards it does *not* cover: AgentHome selected-folder prep, sandbox `CopyInto`,
   `DevelopmentWorkspaceGitConfig.RestoreMinimal`, and registered-path resolution. Junctions are reparse points
   and need **no privilege**, so `Testing/JunctionSupport.cs` can prove these on a stock box where symlinks
   cannot. This is the single highest-value *new* automated coverage available on Windows — and once written it
   runs forever.

4. **`where.exe pnpm` must list a `.exe` before you touch E2E.** `E2ETests/Infrastructure/XEReactClientFixture.cs:81`
   starts `pnpm` by bare name with `UseShellExecute = false`. `CreateProcessW` appends only `.exe` — it does not
   consult `PATHEXT` — so a corepack/npm `pnpm.cmd` shim fails opaquely inside fixture init and the entire E2E
   lane becomes uninterpretable. The repo's own JS already guards this (`scripts/RunPackageTool.mjs:15` sets
   `shell: platform === "win32"`); the C# fixture does not.

5. **E2E, with the vacuous-run guard ported.** `-p:RunE2ETests=true` is mandatory on **both** build and test —
   without it the csproj demotes itself to a library and the run passes with zero tests. There is no PowerShell
   port of `run-e2e-local.sh`, so carry its guard yourself:

   ```powershell
   .\scripts\with-build-lock.ps1 -- dotnet build `
     XE-Local-AI-Engine.Tests.E2ETests\XE-Local-AI-Engine.Tests.E2ETests.csproj `
     -p:RunE2ETests=true --configuration Release
   pwsh XE-Local-AI-Engine.Tests.E2ETests\bin\Release\net10.0\playwright.ps1 install chromium   # omit --with-deps (apt-only)
   $e2e = Join-Path $env:TEMP "xe-e2e-$([Guid]::NewGuid().ToString('N')).log"
   dotnet test XE-Local-AI-Engine.Tests.E2ETests\XE-Local-AI-Engine.Tests.E2ETests.csproj `
     --configuration Release --no-build -p:RunE2ETests=true | Tee-Object -FilePath $e2e
   if (Select-String -Path $e2e -Pattern 'total:\s*0([^0-9]|$)' -Quiet) { throw "ZERO tests ran — NOT a pass" }
   ```

6. **Second-instance UX.** `Program.cs:74-81` logs `Log.Fatal` and returns exit 1 when
   `SingleInstanceLease.TryAcquire` finds the data root held. Double-click the packaged exe twice and record what
   the operator actually sees — the console window may close before the message is readable, which would make a
   correct refusal look like a crash.

7. **PowerShell script lint + Pester** (there is no `lint-release-scripts.sh` equivalent on Windows):

   ```powershell
   Invoke-ScriptAnalyzer -Path scripts\with-build-lock.ps1,scripts\assembly-guard.ps1,`
     publish\package-tester-win.ps1,publish\windows\uninstall-xe-local-ai-engine.ps1 `
     -Settings scripts\PSScriptAnalyzerSettings.psd1
   $r = Invoke-Pester -Path publish/tests -PassThru
   if ($r.TotalCount -eq 0) { throw 'Pester discovered ZERO tests' }
   Invoke-Pester scripts/performance/tests    # NOT covered by publish/tests
   ```

### Phase 3 — the release chain, which has never completed once

This is the largest single risk in the RC, and it is entirely yours: `publish/package-tester-win.ps1` is the only
real release path (CI is disabled and has never produced an artifact).

**Start from the version — it has NOT been bumped, and the next release is `rc.5.0`, not `rc.4.x`.**
`Directory.Build.props:5-6` still reads `0.1.0-rc.4.2` (the release version now lives in `eng/ReleaseVersion.props`), and the newest tag in the repo and on `origin` is
**`v0.1.0-rc.4.0`** — 824 commits behind HEAD. An `rc.4.2` build was smoke-tested on 2026-08-03 and its tag was
never pushed.

**Operator decision, 2026-08-03: the next RC is `0.1.0-rc.5.0`** — the change volume since `rc.4.0` is large
enough that a patch-level bump would understate it. So the first action in this phase is:

```powershell
# Directory.Build.props: VersionSuffix rc.4.2 -> rc.5.0
git tag v0.1.0-rc.5.0
git push origin v0.1.0-rc.5.0        # -PublishDraft verifies the PUSHED tag resolves to HEAD
```

Do not publish until `Directory.Build.props` and the pushed tag agree. Nothing here re-cuts `rc.4.2`; treat it as
a build that existed only on the verification box.

Then, in order:

1. **Dress rehearsal, no upload:** `.\publish\package-tester-win.ps1 -SkipUpload -GitHubAppClientId "Iv23li..."`.
   Every gate runs. Afterwards, inspect the output directory and the generated
   `publish/dist/XE-Local-AI-Engine-<version>-win.sha256.json`.
2. **Verify the shipped bundle, not the publish directory.** The script's SPA check (`:574-578`) proves
   `wwwroot` exists in the *publish dir* — **nothing verifies the contents of the Portable.zip at all.** Unzip the
   actual artifact and confirm: `wwwroot\index.html` present; `LICENSE` and `NOTICE` present; **no**
   `REHEARSAL-DO-NOT-SHIP.txt` anywhere; and `appsettings.AppUpdate.json` reads `"Channel":"tester"` with a real
   `Iv…` client id.
3. **Verify the `Always`-copy fix** (`6fc12a4f`, `XE-Local-AI-Engine.Client.csproj:151-155`). It has **no
   automated test**. Reproduce it in reverse: plant a fake `"GitHubAppClientId": "Iv23liSTALE"` in the published
   `appsettings.AppUpdate.json`, give it a newer mtime than the source file, republish *without* the packaging
   script, and confirm the value reverts. Then republish with `-p:UpdateChannel=main` and confirm the file
   actually switches to the `main` config. Under the old `PreserveNewest` both would have silently kept the stale
   file — which is how a rotated credential could ride into a shipped artifact.
4. **Self-update, which needs two published releases to observe at all.** The tester repo already has
   **`0.1.0-rc.4.1`** published, so `rc.5.0` gives you the pair for free — and it is the *real* upgrade a tester
   will take, across a large version jump, which is a better test than a synthetic adjacent pair. Install
   `rc.4.1` from the tester repo first, publish `rc.5.0`, then from the `rc.4.1` install sign in and press
   Update. Record: does it relaunch by itself; does it come back on the **same loopback port**; is `node.key`'s
   SHA-256 **identical** before and after; is the `dp-keys\key-<guid>.xml` set unchanged; are the models still
   there; is any `llama-server` orphaned across the restart.

   > Note `rc.4.1` predates `Get-ExpectedVelopackAsset` (added in `93afa98d`, after `rc.4.1` shipped), so the
   > pack inventory gate has never gated a real published release. `rc.5.0` will be the first.

   A **new** `key-<guid>.xml` after an update, or `Hugging Face token decryption failed. Clearing the stored
   token.` in the log, is a **P0**: it silently signs the tester out of everything.

   Two specific unverified assumptions to watch: the updater constructs `new UpdateManager(source)` with no
   `UpdateOptions` (`VelopackUpdateManager.cs:119`) so its channel is Velopack's implicit default, while the pack
   used an explicit `--channel win`; and `ApplyUpdatesAndRestart` replaces the process while the
   `instance.lock` lease is still held, so an overlapping relaunch could hit the "already running" refusal.
5. **Uninstall completeness.** `Program.cs:37` is a bare `VelopackApp.Build().Run()` — **no `OnBeforeUninstall`
   hook is registered anywhere**, so a Velopack uninstall removes the install tree and leaves
   `%LOCALAPPDATA%\XE-Local-AI-Engine` (database, `node.key`, `dp-keys`, the llama.cpp binaries, every downloaded
   GGUF — potentially tens of GB) behind forever. Separately,
   `publish/windows/uninstall-xe-local-ai-engine.ps1:147-152` probes for a Velopack install only under
   `%LOCALAPPDATA%`, which a **portable** `--noInst` bundle never matches — so that whole branch is unreachable
   for the artifact that actually ships. Run the uninstaller with `-DryRun` first and confirm it does **not**
   claim to have found a Velopack install; then run it for real and record what is left behind (Start Menu
   entries, uninstall registry keys, `%APPDATA%` residue).
6. **MOTW / SmartScreen.** The build is unsigned. Download the zip **through a browser** (only that sets
   Mark-of-the-Web), confirm the `Zone.Identifier` stream exists, and record whether SmartScreen's "More info →
   Run anyway" is required. **`publish/TESTER-QUICKSTART.md` currently says nothing about this** — a tester will
   hit it on first launch with no warning. That doc gap should be closed before the RC goes out.
7. **Where the tester unzips matters.** A portable Velopack bundle updates **in place**. Repeat the update from a
   plain `C:\xe-rc42` (control), from a OneDrive-synced Desktop folder, and from `C:\Program Files\`. If either of
   the latter two fails, `TESTER-QUICKSTART.md` needs an explicit "unzip to a plain local folder" line.
8. **Wire the guards that already exist and have no callers.** Neither `scripts/with-build-lock.ps1` nor
   `scripts/assembly-guard.ps1` is referenced by `publish/package-tester-win.ps1` — its `dotnet test` at `:548`
   runs unguarded, so a *contaminated green* backend leg is currently indistinguishable from a real one and would
   ship. Until that is fixed in the script, run the pack through the lock by hand.

### Phase 4 — the two product checks still open in the runbook

Both need a Windows box, and both were blocked last time for reasons that are now understood.

- **Check 5 (Development Mode surveys).** Last attempt died because `%LOCALAPPDATA%` was redirected by an
  MSIX-packaged parent — a harness artifact, not how a tester launches. **Launch the packaged build normally and
  it should work.** If you do hit
  `DevelopmentWorkspaceSecurityException: The preserved Development worktree no longer matches its exact trusted
  base`, do not report it as tampering: run `git -C <worktree> rev-parse --show-toplevel` and compare it to the
  path in the log. A mismatch is the known path-normalisation bug (see the hand-back list below).
- **Check 5b (Coder surveys).** Last attempt used a **reasoning** model, which emits the tool call as prose and
  can never produce a real one. Use a non-reasoning tool-capable GGUF —
  `bartowski/Qwen2.5-3B-Instruct-GGUF:Q4_K_M` is on the shipped allow-list. A run on a reasoning model is not a
  check-5b result in either direction.

Note the runbook was corrected on 2026-08-03 in three places; use the current text, not remembered instructions:
the check-6 `Sandbox containment probe` log line is **unemittable on Windows** (do not grep for it), check 8's
wedged-driver string was **deleted** (grepping for it will always miss), and check 9's adapter-query deadline is
**5 s**, not 8.

### Do NOT attempt these — hand them back

They are Linux-dev-box code fixes, fully reproducible and testable off Windows. Spending a Windows session on
them wastes the one environment that can do everything above.

- **The refused-override sizing bug.** A deliberately refused `XE_LLAMACPP_SERVER_PATH` binary reaches
  `LlamaDeviceInventoryProbe` as an exception indistinguishable from a spawn failure, so `cpuFallback` stays
  false, `GetEffectiveProfileAsync` never zeroes the VRAM, and model-fit keeps sizing against a GPU that will not
  be used. The fix is a third state on `LlamaDeviceInventory`; `BuildState` is pure and already has a 15-test
  suite. No Windows dependency anywhere in the chain.
- **The `%LOCALAPPDATA%`-redirection path mismatch.** `DevelopmentWorkspaceProvider.PathEquals` (`:557-560`)
  compares `Path.GetFullPath` — which normalises *lexically* and does not resolve reparse points — against
  `git rev-parse --show-toplevel`, which resolves physically. They can never agree behind a junction, a `subst`
  drive, or a redirected profile. **This reproduces on Linux today** with a symlinked data root, so a failing
  test can be written on the dev box. Real-world likelihood on a stock tester box is low (OneDrive Known Folder
  Move and Group Policy redirection both leave `AppData\Local` alone) but it is total when it happens, and the
  message blames a *security policy* for what is a path bug.
- **Turning the ~90 bare-return guards into reported skips.** A mechanical `return;` → `Skip.Test("…")` sweep.
  Until it is done, no "green on Windows" claim is falsifiable. Best done on the dev box where the whole suite
  can be re-run cheaply.
- **Pinning `core.autocrlf=false` in the eight Development git fixtures that omit it**
  (`DevelopmentProfileGuardTests.cs:186`, `DevelopmentTemplateServiceTests.cs:188`,
  `DevelopmentMountBrokerTests.cs:211`, `DevelopmentValidationReviewAndApplyTests.cs:1022`,
  `DevelopmentRestartRecoveryHarness.cs:176`, `DevelopmentWorkspaceAndCoderTests.cs:1024`,
  `DevelopmentWorkspaceGitConfigTests.cs:223`, `TrustedDevelopmentHostApplyPortHardeningTests.cs:53`). Two other
  fixtures already pin it and the failure mode is documented at
  `DevelopmentSyntheticSolutionRepository.cs:249-265`. These inherit the operator's global git config, and Git
  for Windows defaults `core.autocrlf=true`.

---

## Open questions for the operator, not the agent

- ~~Is `rc.4.2` being re-cut, or is the next RC `rc.4.3`?~~ **Answered 2026-08-03: the next RC is `0.1.0-rc.5.0`.**
  The version has not been bumped yet — `Directory.Build.props` still reads `rc.4.2` — so bumping it and pushing
  the tag is the first action of Phase 3.
- **Does the RC ship signed?** Code signing is still deferred. If it ships unsigned, `TESTER-QUICKSTART.md` needs
  the SmartScreen paragraph it currently lacks.
- **Is an AMD/Intel-only Windows box available for check 9?** The verification box is dual-adapter, so
  `MapAdapterVendor` resolves `nvidia` regardless of enumeration order and the AMD/Intel result is unreachable
  there — unexercised, not passed.
