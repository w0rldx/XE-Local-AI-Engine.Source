# Agency-Agents starter-pack — provenance & audit

This folder vendors a curated subset of a third-party agent-persona library and the
transform of it into this project's `AgentDefinition` seed format. Both the **raw base**
and the **transformed output** are committed so we have a permanent audit of *what the base was*.

## Upstream source

|                      |                                                                                   |
|----------------------|-----------------------------------------------------------------------------------|
| Repo                 | [`msitarzewski/agency-agents`](https://github.com/msitarzewski/agency-agents)     |
| Pinned commit        | `783f6a72bfd7f3135700ac273c619d92821b419a`                                        |
| Commit date          | 2026-04-12                                                                        |
| Vendored on          | 2026-06-02                                                                        |
| License              | **MIT** — Copyright (c) 2025 AgentLand Contributors (see `LICENSE-agency-agents`) |
| Upstream size at pin | 172 agents across 14 divisions                                                    |

## What is committed here

```
Templates/
  sources/agency-agents/engineering/*.md   RAW vendored base — byte-for-byte from the pinned SHA. The audit.
  agent-templates.seed.json                 GENERATED transform (embedded resource). Do NOT hand-edit.
  LICENSE-agency-agents                     Upstream MIT license copy (attribution).
  PROVENANCE.md                             This file.
```

The vendored `.md` set **is** the curation: every `.md` under `sources/` becomes exactly one
template in the seed JSON. To add/remove agents, add/remove `.md` files and re-run the generator.

## Curated set (cut 1 — 14, all `engineering/`)

Chosen for broad usefulness + uniform frontmatter (lowest parser risk). Niche personas
(solidity, wechat, feishu, filament, voice-ai, embedded-firmware, …) were intentionally excluded.

`backend-architect`, `code-reviewer`, `data-engineer`, `database-optimizer`, `devops-automator`,
`frontend-developer`, `git-workflow-master`, `minimal-change-engineer`, `rapid-prototyper`,
`security-engineer`, `senior-developer`, `software-architect`, `sre`, `technical-writer`.

## Transform

Reproducible build-time tool (NOT in the runtime graph, excluded from `XE-Local-AI-Engine.slnx`):

```
tools/AgentTemplateGenerator/   # net10.0 console, YamlDotNet 18.0.0 (CPM-pinned), relaxed analyzers
```

Regenerate after changing the vendored sources:

```bash
dotnet run --project tools/AgentTemplateGenerator -- \
  XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents \
  XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/agent-templates.seed.json \
  783f6a72bfd7f3135700ac273c619d92821b419a
```

Transform rules:

- Bodies are emitted **verbatim** (the audited base is never edited; large prompts are surfaced via an
  estimated-token field, not condensed).
- `estimatedPromptTokens` = `ceil(bodyChars / 4)` — a heuristic, **not** a real tokenizer.
- `originalTools` captures any upstream `tools:` frontmatter for reference only; it is **not** mapped into
  `AllowedToolNames` (those are cloud tool names; the resolver drops unknown tools). Engineering cut 1 has none.
- Output is sorted by `slug` for a stable, reviewable diff. Malformed files are skipped + logged, never fatal.

## Seed JSON shape

```jsonc
{
  "source":   { "repo", "sha", "license", "copyright", "note" },
  "templates": [
    {
      "slug": "engineering-backend-architect",   // stable import key (= SeedSlug)
      "name": "Backend Architect",
      "description": "…",                          // from frontmatter
      "division": "engineering",
      "instructions": "<full body, verbatim>",     // becomes AgentDefinition.Instructions (encrypted at rest)
      "estimatedPromptTokens": 2290,
      "originalTools": [],                          // reference only, NOT mapped
      "sourceFile": "engineering/engineering-backend-architect.md"
    }
  ]
}
```

At pin, token estimates range 677–4304; only `security-engineer` (~4304) exceeds a 4000-token budget → the
UI flags it. Imported agents land as chat personas (`AllowedToolNames=[]`); behavioral quality is gated by the
existing Playbook P4 eval, not by this importer.
