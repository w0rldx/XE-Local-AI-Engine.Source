# Backend commentary cleanup inventory

> Point-in-time / superseded — this is a dated 2026-06-02 snapshot of the cleanup state, not a living document. The living convention is [docs/backend-commentary-map.md](./backend-commentary-map.md); defer to it.

Date: 2026-06-02
Scope: baseline guardrails for comment/XML-doc/Markdown-only cleanup.

This inventory captures the starting stale-term state for the backend commentary cleanup PRD. It is intentionally a Markdown-only guardrail artifact: do not use it as approval to change executable code, string literals, route constants, attributes, pragmas, SQL, migrations, or test assertions.

## Baseline commands

Run from the worker worktree root:

```bash
git status --short
rg -n -i "\b(Playbook P[0-9]|Loop P[0-9]|Marker [A-Z0-9-]+|plan §|AgentHome plan|Lane [0-9]|Agent [A-Z]\b)" --glob '*.cs' --glob '!**/bin/**' --glob '!**/obj/**'
rg -n -i "\bp""ans\b" --glob '*.cs' --glob '*.md'
git diff --check
dotnet restore XE-Local-AI-Engine.slnx
dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
```

## Initial stale-term inventory

- `git status --short`: clean before this inventory file was added.
- Stale-term scan: 18 hits.
- Typo-sentinel scan: 0 hits.
- Hit distribution:
  - `XE-Local-AI-Engine.Client.Persistence.Tests`: 10 hits.
  - `XE-Local-AI-Engine.Tests`: 5 hits.
  - `XE-Local-AI-Engine.AI.Agent`: 2 hits.
  - `XE-Local-AI-Engine.AI.Agent.Tests`: 1 hit.

## Review lanes

The cleanup is split into two review lanes so future workers do not treat every regex hit as an edit target:

- Inventory lane: run the stale-term and typo-sentinel scans, group hits by path and token type, and identify whether
  each hit is a standalone source comment, XML doc, inline executable-line comment, string literal, pragma, Markdown
  archive, or test fixture value.
- Allowlist lane: preserve hits that are outside the approved comment/XML-doc edit guard, and record why they remain.
  Allowlisted hits are not skipped work; they are the intentional boundary between commentary cleanup and behavior,
  test-output, route, directive, or public-contract changes.

## Review lanes

The cleanup is split into two review lanes so future workers do not treat every regex hit as an edit target:

- Inventory lane: run the stale-term and typo-sentinel scans, group hits by path and token type, and identify whether
  each hit is a standalone source comment, XML doc, inline executable-line comment, string literal, pragma, Markdown
  archive, or test fixture value.
- Allowlist lane: preserve hits that are outside the approved comment/XML-doc edit guard, and record why they remain.
  Allowlisted hits are not skipped work; they are the intentional boundary between commentary cleanup and behavior,
  test-output, route, directive, or public-contract changes.

## Comment-edit candidates

These hits are safe candidates only when the edit changes comments/XML-doc text and does not touch executable code or compiler directives:

- `XE-Local-AI-Engine.Client.Application/DependencyInjection/NodeSchedulerServiceCollectionExtensions.cs:35` — inline scheduler comment references the old Marker 1 migration; rewrite to the current Quartz schema-validation invariant.
- `XE-Local-AI-Engine.Client.Application/Services/Scheduler/**` — standalone XML docs can replace old migration labels with
  stable scheduled-job store / Quartz scheduler terminology.
- `XE-Local-AI-Engine.Client.Application/Services/AgentHome/**` and `.../Services/Workspace/**` — standalone XML docs can
  replace item-number/future-lane wording with current AgentHome gateway, run-log, sandbox, and host-path invariants.
- `XE-Local-AI-Engine.Tests/AgentHome`, `XE-Local-AI-Engine.Tests/Sandbox`, and `XE-Local-AI-Engine.Tests/HostAgent` —
  standalone comments may clarify fake-vs-real Docker/gRPC coverage. Skip messages and fixture/assertion strings remain
  in the allowlist lane.

## Guardrail allowlist

The stale-term scan intentionally catches several non-comment or directive-adjacent strings. Leave these unchanged unless a later approved scope explicitly permits code/string/pragma changes:

- `XE-Local-AI-Engine.AI.Agent/Eval/Implementation/MafPlaybookEvalAgentRunner.cs:17` — private constant string used as an agent description; changing it would modify a string literal.
- `XE-Local-AI-Engine.AI.Agent/Invocation/Orchestration/Implementation/OrchestrationAgentFactory.cs:120` — `#pragma warning disable` line; PRD forbids pragma changes even though the trailing comment includes a historical plan reference.
- `XE-Local-AI-Engine.AI.Agent.Tests/Invocation/Orchestration/OrchestrationAgentFactoryTests.cs:5` — `#pragma warning disable` line; same pragma guardrail.
- `XE-Local-AI-Engine.Tests/Sandbox/AgentHomeRealGitSmokeTests.cs:229`, `:235`, `:241`, `:249`, `:260` — skipped-test message string literals; changing them would alter test output strings.
- `XE-Local-AI-Engine.Client.Persistence.Tests/GoldenHarvestSourceStoreTests.cs:40`, `:81`, `:82`, `:113`, `:145`; `PlaybookMonitorStoreTests.cs:35`, `:36`; `FeedbackInsightsStoreTests.cs:34`, `:35`, `:76` — test fixture names/assertion string literals using `Agent A` / `Agent B` as sample agent names.
- `XE-Local-AI-Engine.Client.Application/DependencyInjection/NodeSchedulerServiceCollectionExtensions.cs:35` — remains
  allowlisted when the active guard requires changed C# diff lines to be standalone comment/XML-doc lines only; the stale
  term is inside a trailing inline comment on an executable configuration assignment.

## Comment-only diff guardrails

Before completing any cleanup batch:

1. Run the stale-term and typo-sentinel scans above.
2. Run `git diff --check`.
3. Review `git diff -- '*.cs'` and confirm each edited C# hunk only changes `//`, `///`, or block-comment text. Do not change executable code, string literals, attributes, pragmas, route constants, SQL, migrations, or test assertions.
4. Treat endpoint/request/response XML comments as public documentation if Swagger XML comments are enabled later; note intentional public-doc description changes in the cleanup report.
5. Keep allowlisted non-comment hits in the report until a broader non-comment cleanup is approved.

## Current worker-1 refresh notes

- 2026-06-02T15:07Z refresh: PRD stale-term scan returned 18 residual hits, all allowlisted non-comment string literals or pragma directives.
- 2026-06-02T15:07Z refresh: typo-sentinel scan returned 0 hits.
- Editable stale source-comment hits: 0.

## Baseline validation notes

- `dotnet restore XE-Local-AI-Engine.slnx`: completed package restore, but reported the missing sibling project `../../../C0re.AI.Shared.Contracts/C0re.AI.Shared.Contracts.csproj`.
- `dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore`: failed before cleanup edits because the worker worktree does not contain that sibling project. The failure surfaces as the missing `C0re` namespace plus dependent missing types such as `MessageRole`, `ApprovalResolvedEvent`, `InvocationStatus`, and `InvocationCancelledEvent`.
