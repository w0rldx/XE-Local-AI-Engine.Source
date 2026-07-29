## Hard-won knowledge

Read `docs/agent-knowledge.md` before your first non-trivial change in this repo. It records the rules, invariants, and traps that reading the code will not tell you — each entry encodes a bug that was already paid for once. Highlights an agent will otherwise hit:

- A bare `TODO`/`FIXME` in a C# comment **fails the build** (Sonar S1135 + warnings-as-errors). Write "follow-up:" instead.
- OpenAPI regen without `XE_LAUNCH_MODE=desktop` **silently drops** desktop-only endpoints from the generated client.
- `aspire stop` is a **no-op** on this stack — use `scripts/dev-stop.sh`, or you leave an orphaned `llama-server` holding a port and VRAM.
- HostAgent was **deliberately removed** — don't reintroduce it. Docker is off the **inference path** and stays there, but is permitted for **Development Mode execution only** (ADR 0004). Ollama was **not** removed — it's a gated secondary provider; llama.cpp is the default runtime.
- This WSL box **has** an RTX 5090 (32 GB, sm_120) + CUDA. Older notes claiming no GPU — or claiming a 4080/16 GB/sm_89 — are wrong.

The doc's "Stale beliefs corrected" table lists rules that were true once and are false now — check it before acting on a remembered convention.

`docs/wiki/` is the code-grounded architecture reference; `AGENTS.md` has the validation commands.

<!-- CODEGRAPH_START -->
## CodeGraph

In repositories indexed by CodeGraph (a `.codegraph/` directory exists at the repo root), reach for it BEFORE grep/find or reading files when you need to understand or locate code:

- **MCP tool** (when available): `codegraph_explore` answers most code questions in one call — the relevant symbols' verbatim source plus the call paths between them, including dynamic-dispatch hops grep can't follow. Name a file or symbol in the query to read its current line-numbered source. If it's listed but deferred, load it by name via tool search.
- **Shell** (always works): `codegraph explore "<symbol names or question>"` prints the same output.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->
