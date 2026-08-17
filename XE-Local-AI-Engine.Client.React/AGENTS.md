# AGENTS.md

## Approval Gates

Do not edit files, run mutating commands, perform infrastructure changes, or clean up files before an approved plan.

For failures, stop and report the failing command/output before attempting fixes.

## Validation

Backend:

- `dotnet restore XE-Local-AI-Engine.slnx`
- `dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore`
- `dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1`

The Release configuration and serial test-module setting are required. The root [`AGENTS.md`](../AGENTS.md#validation) is authoritative for these commands and explains why CI's per-project test loop differs. Use the repository build lock and assembly guard described there; never overlap a backend build with `dotnet test --no-build`.

Frontend:

- `cd XE-Local-AI-Engine.Client.React`
- `pnpm install --frozen-lockfile`
- `pnpm run lint`
- `pnpm test`
- `pnpm run test:tooling`
- `pnpm run build`

`pnpm validate` additionally runs Knip, the SignalR proxy synchronization check, and the dependency architecture baseline. Backend contract changes must run `pnpm openapi:check`; a running desktop backend can be checked with `OPENAPI_SPEC_URL=<absolute-spec-url> pnpm openapi:check:live`.

E2E is ask-gated unless the task specifically targets E2E behavior.

## Parallel Work

For parallel implementation tasks, prefer git worktrees under `.tmp/worktrees/` and avoid multiple tasks claiming the same file unless explicitly approved.

## Durable Memory

Agents may propose updates to project intelligence or standards, but durable context promotion requires human architect approval.

<!-- CODEGRAPH_START -->
## CodeGraph

In repositories indexed by CodeGraph (a `.codegraph/` directory exists at the repo root), reach for it BEFORE grep/find or reading files when you need to understand or locate code:

- **MCP tool** (when available): `codegraph_explore` answers most code questions in one call — the relevant symbols' verbatim source plus the call paths between them, including dynamic-dispatch hops grep can't follow. Name a file or symbol in the query to read its current line-numbered source. If it's listed but deferred, load it by name via tool search.
- **Shell** (always works): `codegraph explore "<symbol names or question>"` prints the same output.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->
