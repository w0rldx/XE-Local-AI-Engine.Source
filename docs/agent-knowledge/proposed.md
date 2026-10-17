# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### A new project's publish profile is ignored until `.gitignore` un-ignores its folder

**Rule:** after adding `Properties/PublishProfiles/*.pubxml` to a new project, add
`!/<project>/Properties/PublishProfiles/*.pubxml` beside the existing un-ignore lines in `.gitignore` and confirm with
`git ls-files <project>/Properties` before any packaging step or test names the profile. **Prevents:** a profile that
exists in the worktree, passes `RuntimeLicensePackagingTests` and local publishes, but is in no commit, so CI and a
clean checkout publish without the RID, self-contained and single-file settings (the `xe` client, 2026-10-17; caught by
review, not by a gate). **Authority:** the `*.pubxml` rule and its per-project exceptions in `.gitignore`.

### A `Directory.Packages.props` addition also runs `pnpm run licenses:check`

**Rule:** after adding or bumping any `<PackageVersion>`, run `( cd XE-Local-AI-Engine.Client.React && pnpm run
licenses:generate )` and commit `src/features/about/data/third-party-licenses.generated.json` in the same commit;
`pnpm run acceptance` does not include `licenses:check`, and the routing table sends a `Directory.Packages.props`
change to build-and-analyzers, where the license rule (frontend-and-api, "`openapi:check` and `licenses:check` diff
the working tree against the INDEX") is never read. **Prevents:** a backend-only package addition that passes the
backend gate and the frontend acceptance locally and turns the CI `licenses:check` step red (the `xe` client's two
packages, 2026-10-17; caught by review). **Authority:** `licenses:check` in `XE-Local-AI-Engine.Client.React/package.json`;
`.github/workflows/build-and-test.yml`.

### A worker never rewrites a shared ledger file; rows are edited in place

**Rule:** a findings or status ledger that several agents update (`FINDINGS.md`, a plan's checklist) is edited row by row with targeted replacements; no agent writes the whole file, and a coordinator re-reads the row count after every worker hand-off. **Prevents:** one worker's full-file write silently discarding every other lane's rows (the 2026-10-16 live-QA ledger lost 36 of 40 rows once and was rebuilt from the round files). **Authority:** `AGENTS.md` "never let two tasks claim the same file"; `Plans/live-qa-deep-dive-2026-10-16/FINDINGS.md` header rule.

### `lab-up.sh` in a worktree needs the models hard-linked, projector included, and a Debug AppHost built before `--no-build`

**Rule:** before `scripts/lab-up.sh` runs from a worktree, hard-link every installed model file the round needs into that worktree's lab models dir, including the multimodal projector GGUF beside its weights, and build the AppHost once in Debug; only then is `--no-build` valid. **Prevents:** a lab that boots but reports the 27B as not installed (projector missing) or that fails on a stale or absent Debug build. **Authority:** `scripts/lab-up.sh` header; dev-runtime "Lab models dir holds hard links, never symlinks".

### A NuGet advisory published mid-task reds every gate: pin on a one-commit chore branch that lands first

**Rule:** when `NuGetAudit` plus `TreatWarningsAsErrors` turns a fresh advisory (NU1902) into a restore failure, open a one-commit chore branch that pins the transitive package under `CentralPackageTransitivePinningEnabled` in `Directory.Packages.props`, land it first, and cherry-pick that commit onto every open branch so their gates run; drop the pin once a parent package floors at the fixed version. **Prevents:** every open branch and `develop` CI staying red for a day, and fix branches carrying unrelated version bumps. **Authority:** `Directory.Packages.props`; `scripts/run-backend-tests.sh`; MessagePack GHSA-qhrr-8q5h-9q3h pin, 2026-10-16.

### A worker that idles on a background gate is not reliably woken: poll in a bounded loop

**Rule:** a worker agent that starts the backend gate or `pnpm run acceptance` in the background must not end its turn waiting for the completion notice; it polls the gate's log or lock (`scripts/build-lock-status.sh`) in a bounded loop with a finite deadline and reports the final line itself. **Prevents:** a lane that looks finished to the coordinator while its gate result was never read, and a worker parked for an hour on a gate that ended in ten minutes. **Authority:** `scripts/build-lock-status.sh`; backend-tests-runner "run the whole test project before hand-off".

### Qwen3.5-9B puts its answer inside a reasoning part and emits trailing reasoning-only deltas after an approval request

**Rule:** a live round or benchmark on `unsloth/Qwen3.5-9B-GGUF` must read the reasoning part as well as the content part before judging a turn empty or unranked, and must tolerate reasoning-only deltas that arrive after `approval-requested`; these are model behaviours, not stream defects, and a fix that "repairs" them in the stream service breaks the 27B. **Prevents:** filing stream bugs against the chat runner for the 9B's output shape, and benchmark runs on the 9B marked unranked because the answer sat in the reasoning part. **Authority:** `docs/wiki/14-inference-runtime.md` reasoning-part handling; live-QA 2026-10-16 rounds R4 and R7.

### A backend gate in a lab worktree rewrites that host's `node-settings.json`

**Rule:** the dev host keeps its node settings in `XE-Local-AI-Engine.Client/node-settings.json` under the content root (gitignored), and the Integration test hosts started by `scripts/run-backend-tests.sh` share that content root and rewrite the file with defaults. Never queue the backend gate in a worktree whose lab host you still need; after an unavoidable gate, re-apply the lab's settings with `PUT node-settings` and restart the host, because `ToolApprovalCoordinator` reads `maxPendingToolCallAgeMinutes` once per process. Check `GET node-settings` right after every `scripts/lab-up.sh`.

**Prevents:** a lab host that silently comes back with default settings after a restart (custom tools gone from the offer, pending-card cap back at 10 minutes) and a round of void evidence before the cause is visible; nothing in the node log says the settings were reset.

**Authority:** Plans/live-qa-followups-2026-10-17/evidence/lane-d1-phase5.md (2026-10-17: the fix-pass gate ran between two lab restarts; `api-14-age2-prefix.log` shows `customToolsEnabled: true`, the next PUT after the restart shows null).
