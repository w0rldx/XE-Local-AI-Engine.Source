# Agent Knowledge Evidence Ledger

This companion preserves dated measurements, incident summaries, and provenance behind [the mandatory rulebook](agent-knowledge.md). It is **not required reading** for ordinary work and is not the authority for current versions, hardware, workflow status, or command syntax. Re-run volatile probes before relying on them.

Use this file when:

- changing or deleting a rule in `agent-knowledge.md`;
- reproducing the same failure mode;
- deciding whether a historical workaround is still necessary;
- distinguishing measured evidence from a design inference.

## Provenance and scope

The original knowledge base grew from roughly 135 session-memory notes (June–July 2026), a partial review of about 30 recent commits on 2026-08-01, an August-note pass on 2026-08-07, and a freshness pass on 2026-08-17. It was never a systematic history review. Absence from either document is not evidence that no invariant exists.

The 2026-08-25 compaction retained the actionable rule in the main file and moved these categories here:

- dated machine/tool versions and external GitHub state;
- A/B timings, memory/RSS, throughput, and concurrency measurements;
- long incident narratives and superseded implementation chronology;
- live-model/model-server observations used to validate a rule;
- provenance of workarounds whose current code/test is the primary authority.

## 0. Documentation evidence

### Why symbol citations replaced line citations

A maintenance pass checked about 84 anchors. Five drifted in one run, all in concurrently edited files; untouched files did not drift. One citation remained in range but pointed to a different symbol, one had crossed into the wrong file, and one drifted twice during the same run. Symbol names used by the same documents remained stable. ADR 0004 records the same experience after `ProcessSandboxRuntimeProvider` documentation moved.

## 1. Build, test, CI, and packaging evidence

### Analyzer gate measurements

A full Debug rebuild of `XE-Local-AI-Engine.Tests` measured roughly 84 seconds with analyzers and 10 seconds without. That cost drove the local Debug gate in `Directory.Build.targets`; all authoritative gates still use Release. An incremental Release build can finish in about one second because MSBuild skips unchanged projects, and skipped analyzers do not replay diagnostics.

A Debug build with `RunAnalyzers=false` still discovered 209 tests in `XE-Local-AI-Engine.AI.Agent.Tests`, confirming source generators continued to run at the then-current pin.

### Filter behavior

Measured with TUnit/MTP on 2026-07-24:

- `QuantLadderTests`: 9 tests;
- `DesktopPortStoreTests`: 6 tests;
- `(QuantLadderTests|DesktopPortStoreTests)`: 15 tests, exactly the union;
- a filter with the wrong depth: exit 8, `Zero tests ran`.

Counts and package versions are volatile; `--list-tests` remains authoritative.

### GPU smoke evidence

On the same model and script, the GPU run peaked near 72% utilization with a 1,199 MiB VRAM rise; CPU fallback peaked near 11% with no VRAM rise and produced the same correct answer. This is why answer correctness cannot validate GPU execution.

The smoke's refuse-to-pass logic is tested without a GPU by `scripts/tests/gpu-smoke.test.sh`. Trust the script's printed check count rather than a count copied into documentation.

### CI batching and coverage

The Tests module is unusually sensitive to how coverage is parallelized:

| Shape | Approximate result on the measured box |
|---|---:|
| one process, eight-wide | 11:00 wall, ~10 GB |
| `JOBS=4` batches | 6:02 wall |
| `JOBS=10` batches | 2:18 wall, ~670 MB per batch |
| coverage, 98 namespace processes | 1,991 CPU-s, 7:07 wall |
| coverage, 8 groups | 830 CPU-s, 3:12 wall |
| coverage, 4 groups | 684 CPU-s, 2:43 wall |
| coverage, one process/width 4 | 677 CPU-s, 7:44 wall |

One-process-per-namespace was fast without coverage but paid static instrumentation of the roughly 240 MB output tree per process with coverage. CI run 32609813981 remained green but took 25.5 minutes versus the 22.5 minutes it replaced and contributed to a sibling timeout. This led to grouped alternation filters (`TEST_GROUPS=$(nproc)`) rather than one process per namespace.

MTP resolves coverage output relative to each results directory. Concurrent projects sharing one directory overwrite reports. `--report-trx` also copied `coverage.cobertura.xml` into the TRX attachment tree; recursive discovery double-counted identical files, so CI uses bounded-depth searches before `merge-cobertura.py` deduplicates source lines.

### Build/test contamination incident

A build running beside a `--no-build` test rewrote assemblies while MTP was loading them, producing both phantom failures and phantom green results. The first lock implementation used a conventional `flock <file> <command>` form; MSBuild daemons inherited the open descriptor and kept the lock after the command returned. The current helper marks the descriptor close-on-exec and exit 69 identifies a live holder. Assembly snapshots turn concurrent mutation into exit 75 rather than test evidence.

### Test-host memory and temp artifacts

The original full Tests module grew to roughly 3.5 GB. gcroot analysis identified long-lived host graphs from entry-point resolution and recurring rate-limiter/MCP allocations, not a normal fixture-reference leak. `WebApplicationFactory<Program>` was replaced with `TestServerWebAppFactory`; safe suites share a per-class host.

Before fixture cleanup, each host left SQLite, node-data, and web-root artifacts. Tens of thousands accumulated to roughly 15 GB and filled the 16 GB `/tmp` tmpfs. A killed test process still leaks because disposal never runs.

The migrated SQLite template measured approximately:

- empty database migration: 1,181 ms per host;
- template copy then normal startup: 712 ms per host;
- `JOBS=10` full module with template: 128–164 seconds;
- same shape without: 197–208 seconds.

Two assembly MVIDs in the filename prevent reuse after migrations or identity seed code changes. Publication is an atomic same-filesystem rename.

### Timing-test and build-daemon incidents

In the rc.4.2 manual packaging session, three of five attempts failed because lingering build daemons starved timing-sensitive tests; no corresponding product defect was found. Failure duration aligned with the timing budget. `dotnet build-server shutdown` restored the expected behavior.

### Browser and frontend timing measurements

A 69-test browser run showed no overlap between serial and pooled groups, but pooled ran before serial despite order values suggesting the reverse. Pooled: 27 tests, peak four concurrent, 95.1 seconds of test time in a 46.4-second span. Serial: 40 tests, peak one, 142.4-second span. The evidence proves disjoint phases, not direction.

In `ChatInputArea.sampling.test.tsx`, the first dynamic import took about 1,754 ms versus 91/139 ms after the component graph was warm. Under coverage across 209 files, import work exceeded Vitest's old five-second default intermittently. The 20-second timeout is for cold transform/evaluation, not permission for slow behavior.

### OpenAPI incidents

Two silent failure modes shipped:

1. `openapi:check` regenerated from the already checked-in spec, so a new endpoint was absent from both input and output and the gate passed.
2. Regeneration under a non-desktop host omitted every `IDesktopOnlyEndpoint`, causing generated exports to disappear and downstream `TS2305` failures.

A mise-managed environment created a third false failure: the live-check script isolates HOME/XDG, losing mise trust and tool installs. Pinning `MISE_TRUSTED_CONFIG_PATHS` and `MISE_DATA_DIR` preserves the backend launch.

### Packaging and repository consolidation

Manual tester releases through `0.1.0-rc.5.0` were published to a separate tester repository. Earlier releases use bare tags with v-prefixed names; later scripts used v-prefixed tags. The current `release.yml` publishes both RIDs to `w0rldx/XE-Local-AI-Engine.Source` with `GITHUB_TOKEN`; old manual scripts remain reference-only and deliberately preserve their historical repository/auth flow.

GitHub workflow state changed between checks. On 2026-07-24 workflows were reported disabled/unregistered; on 2026-08-17 `gh workflow list --all` reported build-and-test, E2E, release, and Dependabot active. Neither observation is current-state evidence.

## 2. Local runtime evidence

### WSL2 hardware and VRAM readers

A live probe (2026-07-26) found a different GPU generation than this entry had recorded, and a different CUDA toolkit version than assumed. Notes about the dev hardware go stale silently — probe (`nvidia-smi`, `nvcc --version`, compiled arch) rather than reading a recorded inventory.

Under WDDM pressure, a model with about 1.2 GB truly free still loaded and served rather than OOMing: 161.7 tok/s versus 698.4 tok/s unloaded, a 4.3× slowdown without an error. In another run, `nvidia-smi` reported 492 MiB free while llama.cpp's process-local `cudaMemGetInfo` view reported 29,697 MiB. `nvidia-smi --query-compute-apps` remained empty while processes held large allocations.

### Aspire teardown retest

On 2026-08-19, five cycles included a real `llama-server`, Docker `sqlite-web`, Aspire hosting packages 13.4.6/13.5.0, CLI 13.4.6/13.5.0, and one SIGKILL of the CLI. Plain `aspire stop` removed the observed process/container graph and VRAM returned to baseline every time. This disproved the claim that the fix began only in 13.5+, but did not identify the original orphan trigger. Invoking `dev-stop.sh` directly on a live stack still issued 15 SIGTERMs, so the fallback remains useful.

### Rootless container identity

Measured on rootless Docker Engine 29.6.1 with subuid base 100000:

- container `1000:1000` mapped to host 100999 and could not write the engine-owned bind mount;
- container `0:0` mapped to the invoking host user and wrote files owned by that user;
- `inspect` reported the requested identity in both cases and could not reveal the mapping.

The create-time write/stat probe verifies the outcome that configuration read-back cannot.

### `/tmp` tmpfs

With a read-only root filesystem, every `dotnet` command failed EROFS before project work because the CoreCLR named-mutex path is compiled under `/tmp/.dotnet/shm`. Redirecting `TMPDIR`, `TMP`, or `TEMP` did not change it. Mounting `/tmp/.dotnet` alone also failed because the PAL creates a sibling temp directory and renames it. A 1 GiB tmpfs under a 256 MiB cgroup OOM-killed the container near 254 MiB. Real use was about 4 KiB; the shipped limit is 64 MiB with `noexec,nosuid,nodev`.

### Host-git execution from repository configuration

On git 2.53.0, repository `core.fsmonitor` executed during index refresh, and an in-tree `.gitattributes` selected a `filter.*.clean` command during `git add`. Command-line `-c core.fsmonitor=` closes the finite key; arbitrary filter driver names cannot be pre-pinned. This is why `.git/config` is mounted read-only in the container and rewritten before host-side patch-evidence git calls.

### Tool grammar ceiling

Against llama-server b10201 and a non-reasoning Qwen2.5 model:

| Keyword | Observed boundary |
|---|---|
| `maxLength` | 2,000 failed; 1,990 passed in the isolated probe |
| `minLength` / `minItems` / `maxItems` | 8,000 failed |
| regex `{0,8000}` | failed; `{0,63}` passed |
| numeric min/max | 100,000 still passed |

The ceiling combines the whole tool catalog. A production offer still failed with every `maxLength` at 2,048 and compiled at 1,024. A reasoning Qwen3.6 request passed because it never entered the constrained grammar branch, proving why the live smoke requires a non-reasoning negative control.

### Work-session context incidents

A 27B model at a 65,536-token window overflowed at step 5 before forced step-bound compaction. A separate step made 14 tool calls (10 KB searches, each result already clipped near 16,041 characters) plus 1,094 reasoning chunks; repeated in-turn replay reached 71,172 tokens. These incidents motivated both step-boundary compaction and `MaxProviderCallsPerStep`; neither replaces the other.

The option initializer for `MaxToolResultCharacters` was 8,000 while an XML comment said 16,000. At 8,000 and chars/4, one clipped result estimates near 2,000 tokens. After 0.85 safety and a 12,000-token step context, about 21 results fit in a 65,536 window **before** reasoning replay, so 21 is an upper bound, not a recommended call cap.

### Windows live verification

The first native Windows run on 2026-08-03 found:

- persisted PATH contained 153 dead temp tool entries (~27 KB); `where ping` worked while `cmd /c ping` said not found. Three cancellation tests failed in 18–81 ms because their sleep command exited instantly. Cleanup reduced PATH from 28,387 to 847 characters and all three passed.
- one-line edits rewrote LF files to CRLF, producing 104/486 changed lines.
- pnpm had `.CMD`/PowerShell shims but no executable suitable for direct `CreateProcessW` under `UseShellExecute=false`.
- `-SkipUpload` failed immediately without `VPK_TOKEN` because the deprecated packager still downloaded the previous private release.
- repeat publish could leave `appsettings.AppUpdate.json` missing until the source timestamp changed despite `CopyToPublishDirectory=Always`.
- seven symlink tests skipped without privilege; junction-based tests exercised the directory reparse guards without elevation.

### BYO CUDA runtime without `llama-fit-params` (2026-09-05)

AI-trends follow-up pass B, B4 (D13 profile-authority live proof) round 1 against `~/cuda-llama/b10201/build/bin`: six host
restarts and about sixteen minutes produced five NOT RUN observations before the missing sibling binary explained them —
every Explore answered 400. Round 2 against a scratch copy of the same bin directory with the freshly built
`llama-fit-params` passed 6/6 in about twelve minutes; the shared directory's 25-file md5 manifest was identical before and
after. Evidence: `Plans/ai-trends-2026-09-02/progress/fu-b-evidence/` (`round1-blocked/`, `README.md`).

## 3. Model, inference, retrieval, and training evidence

### Large-model launch warmup timing

Historical measurements on the then-current large-model workloads put cold launch plus upstream warmup at roughly 45–110 seconds. That range is model-, hardware-, and version-specific, not a current universal timing or SLA. It showed that warmup could consume or exceed even a size-aware readiness window and trigger a kill/respawn loop; normal spawns therefore retain `--no-warmup`, while any synthetic warming belongs after readiness.

### NVFP4 live run

On 2026-07-31, `tngtech/Qwen3.6-27B-NVFP4-GGUF` (18.5 GB file) loaded to a 22.7 GB VRAM peak and generated near 95% GPU utilization on sm_120 using CUDA. The pinned llama.cpp was newer than the Blackwell NVFP4 kernel merge. A same-base/repo measurement used to estimate 4.25 bpw, but cross-repo sizes varied enough that actual blob size remains preferred.

### Silent CPU fallback

The managed Vulkan binary in the WSL environment had no Vulkan ICD and `--list-devices` returned an empty list; inference still answered on four CPU threads while the installed record and UI implied GPU. Conversely, a CUDA binary selected by `XE_LLAMACPP_SERVER_PATH` used the GPU while the installed record still said Vulkan. This validated `IRuntimeDeviceAudit` as the authority.

### Context and admission latch

A judge requiring a 16,384-token window was down-tiered during one tight-VRAM admission. The adjusted allocation was process-lifetime sticky, so later requests for the required window failed eight times, 25 seconds apart, even with no resident model, until restart. The immediate guard rejects instead of down-tiering a named requirement; reservation-scoped adjustments remain follow-up work.

### Benchmark evidence incidents

- A benchmark spawn carried fit capture and therefore missed the old verbosity branch; placement facts were absent until the benchmark-policy OR condition was added.
- Judge JSON written with Web/camelCase options was once read with default options. Deserialization did not throw; it produced a zeroed record and only successful judged runs crashed the frontend schema.
- `JsonPatch.Contains("$.timings.prompt_n")` returned false on a patch where `GetInt32` returned 123. Missing timing fields are now detected by `KeyNotFoundException`, with only the outer timings object checked by `Contains`.
- EF migration removal rolled the model snapshot behind an already-shipped sibling. Re-added migration emitted duplicate columns that failed only on database update.
- Interleaved branch migrations passed migrate-up tests, then SQLite table rebuild during rollback silently dropped sibling columns. Consolidating unshipped migrations at the tail restored a reversible chain.
- Hashing every installed model for eligible-model listing took 6 minutes 34 seconds on a large corpus; listing now trusts recorded facts and freeze verifies bytes.
- A per-run repeat insertion could lose CAS halfway, return no IDs, and leave already-created queued runs. Transactional `StartRunsAsync` makes the repeat group all-or-nothing.

### Knowledge retrieval evidence

Managed cosine outperformed sqlite-vec through the measured 100k-row corpus; sqlite-vec's default `vec0` remained brute-force. For pooled llama-server roles, ordinary ~2,000-character markdown chunks measured roughly 520–680 real tokens and failed the default 512 physical micro-batch even when `-c` was larger. Raising `-b/-ub` to effective context fixed ingestion; chat was excluded because causal decode splits normally.

### Training live-gate incidents

The 2026-08-15 end-to-end training gate found issues unit suites did not expose:

- llama-server strict schema promoted optional properties to required, so a small teacher emitted `"None"` rather than omitting a no-tool field;
- one lost non-streaming response parked a queue until the OpenAI SDK's 10-minute network timeout;
- `datasets.map` worker fork failed with EOF after CUDA initialization;
- unsloth banners preceded JSON protocol output;
- NumPy lacked the required Python 3.13 wheel at the research pin, torchvision from PyPI targeted a different CUDA major, and unconstrained uv resolution pulled incompatible Darwin-only packages;
- default Vulkan claimed all layers placed while `nvidia-smi` showed no work;
- missing `conversion/` beside llama.cpp conversion scripts caused `ModuleNotFoundError`;
- prebuilt archives lacked `llama-quantize`, while an F16 merge/export/smoke/promotion succeeded;
- `VBCSCompiler` held ~3.9 GB, leaving 4 GB free against a 6 GB estimate; shutting down build servers cleared the capacity refusal;
- an unlinked adapter could neither smoke-test nor promote;
- required route IDs in body DTOs made generated-client delete calls return 400 before route binding;
- startup recovery cleared a launch receipt before proving an orphan dead, making the process unidentifiable;
- directory swap and state write were separate rollback boundaries and could lose the previous working runtime;
- direct artifact-row deletion leaked multi-gigabyte staged bytes.

## 4. Agent, sandbox, and cloud evidence

### Sandbox no-follow failures

`Path.GetFullPath` plus prefix containment accepted a path whose interior component was a planted symlink. Passing an `O_NOFOLLOW` numeric value as `FileOptions` caused `ArgumentOutOfRangeException` for every file; raw `open()` with errno inspection was required. A file growing after sizing produced a truncated copy until the one-byte post-read probe was added.

### Bubblewrap live controls

- Read-only trees mounted under the jail's later `/tmp` bind disappeared without an error.
- `/proc` returned ENOENT for create attempts; `/dev` returned EROFS after remount-read-only.
- non-CLOEXEC FDs survived .NET `Process.Start` through setsid → systemd-run → bwrap.
- a trust fixture under `/tmp` failed the world-writable guard before ownership, hiding mutations to the ownership check.
- breaking `--remount-ro /dev` made the capability probe fail; a skip-on-probe-failure live test then skipped the regression it was created to find. Current live tests fail if trusted bwrap exists but isolation does not.

### MAF and cloud incidents

A positional MAF constructor once swapped name/instructions. Other failures came from setting nonexistent `ChatClientAgentOptions.Instructions`, double-sending instructions, and test fakes inspecting only System messages while MAF delivered `options.Instructions`.

A local model ID leaked into the Codex request because the per-call ID outranked the client default. Azure OpenAI per-call bearer policy was overwritten by a later API-key policy and surfaced as `IDX12741` about a malformed JWT; constructor-level authentication policy fixed final wire headers. Azure app-only tokens carried `roles`, so gateways checking `scp` rejected otherwise valid authentication.

## 5. Frontend and API evidence

### API boundary failures

- Generated no-body POSTs lacked Content-Type and received 415 from FastEndpoints.
- Untyped `Files` produced an empty OpenAPI request body; generated code sent `{}` as JSON. Global Axios JSON headers also overrode multipart serialization.
- Typed 409 bodies without `detail` became `ApiError.message = undefined`, rendering blank toasts.
- `%2F` remained encoded in Kestrel route values and failed raw validators.
- hey-api generated `z.coerce.bigint()` for C# long while TypeScript declared number; runtime arithmetic threw mixed BigInt/number errors.
- `WriteAsJsonAsync(value, ct)` overwrote a previously selected problem content type with `application/json`.
- Declaring an ASP.NET ProblemDetails body as FastEndpoints ProblemDetails made schema-validating clients reject extension properties.

### UI race failures

Mantine bounded inputs fired a mount-time `onChange` and persisted a min/default over “unset”. Globally mounted queries fired before auth, cached 401, and did not recover after login. A new SignalR hub missing from the dedicated Vite websocket list fell through the generic proxy and wedged other websocket routes. A new `InvocationState` field appeared in live dispatch but persisted null because `Clone()` did not copy it.

## Change history of this ledger

- **2026-08-25:** split evidence from the mandatory rulebook. No rule should rely on this file alone for current versions or external state.
