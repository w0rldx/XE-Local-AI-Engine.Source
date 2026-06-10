# Changelog

All notable changes to XE-Local-AI-Engine are recorded here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Tag convention: `vX.Y.Z-rc.N` on the RC branch; `vX.Y.Z` on develop after RC validation.

## [Unreleased]

### Added
- Agent Mode foundation: AgentHome write-back loop, Playbook phases P1–P5 (manual, feedback,
  analysis, eval-gate, monitoring + retrieval), embedding-cosine ranker, harvest-golden.
- Agency-agents starter-pack: 14 MIT-licensed persona templates importable via Operator UI.
- Codex OAuth cloud chat provider (ChatGPT-subscription sign-in) + tool-calling support.
- Scheduler foundation (Quartz.NET) with React management UI and realtime SignalR push.
- Model-fit recommendations (llmfit image, Quartz-driven refresh, React read-only UI).
- Model type classification (Chat/Embedding/Unknown) with Ollama capability detection.
- Model capability gating (thinking/tools auto-detected; Ollama 0.30.5 pinned).
- Unified dialog system (`DialogShell`, `useUnsavedChangesGuard`, `MarkdownEditorField`).
- hey-api single source of truth migration: backend OpenAPI drives all React REST clients.
- Full cross-platform uninstaller (Windows PowerShell + Linux shell, install-type-aware).
- Chat ordered parts rendering (reasoning ↔ tool ↔ answer in a single ordered list).
- Chat advanced sampling options (dev-gated per-send temp/top_p/min_p/num_ctx).
- Conversation title encryption (interceptor + additive migration).
- Table pagination (`useTablePagination` + `TablePaginationFooter`, default 25 rows).

### Fixed
- Non-UTC timezone failure in `CapabilityReporterTests` (TZ=Europe/Berlin in CI).
- Chat error shown once as alert; regen + survives reload.
- Per-turn reasoning effort preserved across model switches.
- HF model delete 400 (`encodeURIComponent` slash encoding in model-name-path endpoints).
- OpenAPI client drift gate (`client.gen.ts` 1-line Biome formatting diff resolved).

### Known issues / RC1-accepted behavior
- Conversation titles are now encrypted at rest; pre-existing titles (including operator renames) are
  re-derived from the first user message by a one-time startup backfill. Custom renames from before the
  migration are not preserved; conversations without a user message keep a `NULL` title.
- Local-only mode (no `CentralPlatform:BaseUrl`): cloud services remain registered and fail with a generic
  HTTP error if invoked directly; the UI surface is capability-gated off. Proper fail-fast messaging is an
  RC2 follow-up.

## [0.1.0-rc.1] — first release candidate (target: 2026-06-10)

This is the first developer RC. It targets Windows 11 external testers via a self-contained ZIP
with a PowerShell install script. MSI/deb/rpm packaging is deferred to RC2/GA.

See `Plans/2026-06-10-first-rc-readiness-plan.md` for the full RC readiness audit and checklist.
