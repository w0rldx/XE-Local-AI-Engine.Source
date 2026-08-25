## Hard-won knowledge

Read `docs/agent-knowledge.md` before your first non-trivial change in this repo. It records the rules, invariants, and traps that reading the code will not tell you — each entry encodes a bug that was already paid for once. Highlights an agent will otherwise hit:

- **Always finish a backend change with a Release build** — `dotnet build XE-Local-AI-Engine.slnx --configuration Release`. Local Debug builds skip analyzer execution (84s → 10s), so the entire static-analysis wall is Release-only. A green Debug build is not verification.
- A bare `TODO`/`FIXME` in a C# comment **fails the build** (Sonar S1135 + warnings-as-errors) — but **only in Release**, per the rule above. Write "follow-up:" instead.
- A green E2E run does **not** typecheck the frontend anymore (the fixture runs `build:e2e`, a bare `vite build`). `pnpm run lint` is the only typecheck.
- OpenAPI regen without `XE_LAUNCH_MODE=desktop` **silently drops** desktop-only endpoints from the generated client.
- Stop the stack with `scripts/dev-stop.sh`, not bare `aspire stop`. (The old "`aspire stop` is a **no-op**" claim did not reproduce on 2026-08-19; the fallback stays because the original VRAM/port leak's trigger is still unidentified.)
- HostAgent was **deliberately removed** — don't reintroduce it. Docker is off the **inference path** and stays there, but is permitted for **Development Mode execution only** (ADR 0004). Ollama was **not** removed — it's a gated secondary provider; llama.cpp is the default runtime.
- The development environment **has** a CUDA GPU. Never infer the hardware — or its absence — from notes: run `nvidia-smi` / `nvcc --version`. The hardware has changed before and the stale notes were wrong.
- **NVFP4 GGUFs work** on native sm_120 (Blackwell) kernels when the llama.cpp pin carries `GGML_TYPE_NVFP4` (pin `b10201` does; live-verified). Only NVFP4 **safetensors** are unloadable — that's a container limit, not a format one. Don't re-research this.

The doc's "Stale beliefs corrected" table lists rules that were true once and are false now — check it before acting on a remembered convention.

`docs/wiki/` is the code-grounded architecture reference; `AGENTS.md` has the validation commands.

<!-- CODEGRAPH_START -->
## CodeGraph

In repositories indexed by CodeGraph (a `.codegraph/` directory exists at the repo root), reach for it BEFORE grep/find or reading files when you need to understand or locate code:

- **MCP tool** (when available): `codegraph_explore` answers most code questions in one call — the relevant symbols' verbatim source plus the call paths between them, including dynamic-dispatch hops grep can't follow. Name a file or symbol in the query to read its current line-numbered source. If it's listed but deferred, load it by name via tool search.
- **Shell** (always works): `codegraph explore "<symbol names or question>"` prints the same output.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->
