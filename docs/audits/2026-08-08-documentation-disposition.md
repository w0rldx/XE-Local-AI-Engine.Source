# Documentation disposition audit — 2026-08-08

- **Base revision:** `50cae1410b23fa1e7258d343c1f2d926c6eb41fb`
- **Worktree branch:** `docs/codebase-refresh-2026-08`
- **Scope:** every Markdown file tracked at the base revision, followed by targeted correction of living documentation
- **Inventory command:** `git ls-tree -r --name-only 50cae1410b23fa1e7258d343c1f2d926c6eb41fb | awk '/\.md$/' | LC_ALL=C sort`
- **Frozen base inventory:** 102 paths

This is a new point-in-time audit artifact created after the inventory was frozen. Its own path did not exist at the base revision and is intentionally absent from the disposition rows; adding it would make the inventory self-referential. Counts, stale probes, and validation results below are execution-derived rather than copied from earlier documentation.

## Categories

- **Living/current:** current product, developer, user, operator, release, troubleshooting, or architecture documentation.
- **Historical snapshot/evidence:** point-in-time audits, completed investigations, evidence, dated roadmaps, or handoffs.
- **ADR:** protected decision records; the ADR index may receive evidenced metadata corrections.
- **Controlled instruction/governance:** authoritative agent, contribution, security-policy, quality-gate, or release-governance instructions.
- **Controlled compliance:** compliance registers, non-applicability determinations, and risk acceptances.
- **Generated/vendor/template:** generated, vendored, template, provenance, or third-party-derived Markdown.
- **Justified out-of-product-doc scope:** available only with a path-specific rationale; no path initially uses this category.

## Category totals

| Category | Count |
|---|---:|
| Living/current | 49 |
| Historical snapshot/evidence | 16 |
| ADR | 5 |
| Controlled instruction/governance | 10 |
| Controlled compliance | 4 |
| Generated/vendor/template | 18 |
| Justified out-of-product-doc scope | 0 |

## Disposition matrix

Every base path has a final disposition and appears exactly once.

| Path | Category | Status | Evidence basis | Citation closure | Link/media result | Notes |
|---|---|---|---|---|---|---|
| `.claude/CLAUDE.md` | Controlled instruction/governance | protected-no-change | Repository or process authority surface; contradiction audit only unless approved scope clearly permits correction. | retained-frozen | not-applicable |  |
| `.claude/skills/aspire/SKILL.md` | Controlled instruction/governance | protected-no-change | Repository or process authority surface; contradiction audit only unless approved scope clearly permits correction. | retained-frozen | not-applicable |  |
| `.github/PULL_REQUEST_TEMPLATE.md` | Controlled instruction/governance | protected-no-change | Repository or process authority surface; contradiction audit only unless approved scope clearly permits correction. | retained-frozen | not-applicable |  |
| `AGENTS.md` | Controlled instruction/governance | protected-no-change | Repository or process authority surface; contradiction audit only unless approved scope clearly permits correction. | retained-frozen | not-applicable |  |
| `CHANGELOG.md` | Living/current | updated-code-grounded | Hand-maintained Keep a Changelog content compared with `eng/ReleaseVersion.props`, current git refs, and historical release provenance. | closed-no-mutable-lines | passed | Corrected the current source version from 0.1.0-rc.5.2 to 1.0.0-rc.1 and reconciled the nine historical tester releases/nine matching source tags, including `v0.1.0-rc.4.1`. |
| `CONTRIBUTING.md` | Controlled instruction/governance | protected-no-change | Repository or process authority surface; contradiction audit only unless approved scope clearly permits correction. | retained-frozen | not-applicable |  |
| `README.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Added the previously undocumented Custom Tools surface and distinguished the node default, UI authoring default, API-requested enablement, approval, validation, and secret-handling boundaries. |
| `SECURITY.md` | Controlled instruction/governance | protected-no-change | Repository or process authority surface; contradiction audit only unless approved scope clearly permits correction. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/PROVENANCE.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-backend-architect.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-code-reviewer.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-data-engineer.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-database-optimizer.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-devops-automator.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-frontend-developer.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-git-workflow-master.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-minimal-change-engineer.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-rapid-prototyper.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-security-engineer.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-senior-developer.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-software-architect.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-sre.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/sources/agency-agents/engineering/engineering-technical-writer.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.React/.claude/skills/react-doctor/SKILL.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.React/AGENTS.md` | Controlled instruction/governance | protected-no-change | Repository or process authority surface; contradiction audit only unless approved scope clearly permits correction. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.React/QUALITY-GATE.md` | Controlled instruction/governance | protected-no-change | Repository or process authority surface; contradiction audit only unless approved scope clearly permits correction. | retained-frozen | not-applicable |  |
| `XE-Local-AI-Engine.Client.React/README.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `XE-Local-AI-Engine.Client/README.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/adr/0001-development-mode-restart-recovery.md` | ADR | protected-no-change | Accepted decision record or ADR index; protect bodies and allow only evidenced index metadata. | retained-frozen | not-applicable |  |
| `docs/adr/0002-development-cloud-egress-carrier.md` | ADR | protected-no-change | Accepted decision record or ADR index; protect bodies and allow only evidenced index metadata. | retained-frozen | not-applicable |  |
| `docs/adr/0003-six-plan-operator-decisions.md` | ADR | protected-no-change | Accepted decision record or ADR index; protect bodies and allow only evidenced index metadata. | retained-frozen | not-applicable |  |
| `docs/adr/0004-development-mode-container-execution-docker-stopgap.md` | ADR | protected-no-change | Accepted decision record or ADR index; protect bodies and allow only evidenced index metadata. | retained-frozen | not-applicable |  |
| `docs/adr/README.md` | ADR | metadata-updated | ADR index only: baseline/review metadata and the mutable epic line reference were corrected; accepted ADR bodies remain unchanged. | closed-stable-reference | passed | Bodies 0001–0004 are preserved as accepted decisions. |
| `docs/agent-framework/completion-evidence.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/agent-knowledge.md` | Controlled instruction/governance | protected-no-change | Repository or process authority surface; contradiction audit only unless approved scope clearly permits correction. | retained-frozen | not-applicable |  |
| `docs/ai-runtime.md` | Living/current | updated-code-grounded | Central package/runtime pins plus current Microsoft, OpenAI, llama.cpp, Hugging Face, and Ollama primary sources. | closed-no-mutable-lines | passed | Updated MEAI/MAF/OpenAI/llama.cpp pins and primary references; added Custom Tools maintenance gates and version-sensitive guidance. |
| `docs/audits/2026-07-26-model-role-audit.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/audits/2026-08-07-ai-inference-stack-performance-audit-v2.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/audits/2026-08-07-ai-inference-stack-performance-audit.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/audits/2026-08-07-open-source-readiness-review.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/audits/technical-security-architecture/01-system-context-and-trust-boundaries.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/audits/technical-security-architecture/02-sensitive-assets-data-and-credential-lifecycle.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/audits/technical-security-architecture/03-threat-scenarios-controls-and-residual-risk.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/audits/technical-security-architecture/04-operations-resilience-and-response.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/audits/technical-security-architecture/05-supply-chain-release-and-governance.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/audits/technical-security-architecture/06-claim-traceability-and-evidence-availability.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/audits/technical-security-architecture/README.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/backend-commentary-map.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Added the Custom Tools ownership/security commentary area. |
| `docs/comment-cleanup-grounding.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Aligned active AI package pins with Directory.Packages.props. |
| `docs/compliance/README.md` | Controlled compliance | protected-no-change | Compliance or risk-acceptance record; audit for contradictions and preserve body without authority. | retained-frozen | not-applicable |  |
| `docs/compliance/non-applicability.md` | Controlled compliance | protected-no-change | Compliance or risk-acceptance record; audit for contradictions and preserve body without authority. | retained-frozen | not-applicable |  |
| `docs/compliance/unsigned-build-risk-acceptance.md` | Controlled compliance | protected-no-change | Compliance or risk-acceptance record; audit for contradictions and preserve body without authority. | retained-frozen | not-applicable |  |
| `docs/compliance/utf-unknown-mpl-source-availability.md` | Controlled compliance | protected-no-change | Compliance or risk-acceptance record; audit for contradictions and preserve body without authority. | retained-frozen | not-applicable |  |
| `docs/performance/2026-07-26-evidence-summary.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/performance/2026-07-26-lane4-no-change.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/performance/inference-capture-workflow.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/performance/validation-matrix.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/release-publication-checklist.md` | Controlled instruction/governance | protected-no-change | Repository or process authority surface; contradiction audit only unless approved scope clearly permits correction. | retained-frozen | not-applicable |  |
| `docs/resharper-cleanup-guide.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/roadmaps/development-mode-container-status.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Removed the broken link to an untracked plan and made this living page the verified status authority. |
| `docs/roadmaps/streaming-budget-redesign.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/runbooks/connect-an-mcp-client-runbook.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/runbooks/linux-cuda-override-operator-runbook.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Distinguished managed in-app source builds from operator-owned overrides, lifecycle, integrity, and orphan ownership. |
| `docs/runbooks/otel-export-operator-runbook.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Replaced the mutable AgentTelemetryOptions line citation with the stable CaptureSensitiveContent symbol. |
| `docs/runbooks/windows-rc-remaining-work-agent-prompt.md` | Historical snapshot/evidence | protected-no-change | Point-in-time audit, evidence, roadmap, or handoff purpose; preserve recorded conclusions and base-specific citations. | retained-frozen | not-applicable |  |
| `docs/runbooks/windows-rc-verification-runbook.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Classified as a living procedure with dated evidence; refreshed metadata and replaced every mutable source-line citation with symbols or quoted behavior. |
| `docs/troubleshooting.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/user-guide/PROVENANCE.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |
| `docs/user-guide/README.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Added Custom Tools and corrected the Development:Enabled operator off switch. |
| `docs/user-guide/docs/download-from-github.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/user-guide/docs/faq.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/user-guide/docs/features.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Added the Custom Tools workflow, defaults, secrets, allow-host, and executable-path warnings. |
| `docs/user-guide/docs/feedback.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/user-guide/docs/first-run.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Added Custom Tools navigation and corrected the Development:Enabled off switch. |
| `docs/user-guide/docs/for-experienced-users.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Added Custom Tools, corrected Development Mode, and replaced unresolved runtime guesses with code-backed API/KV/multi-GPU boundaries. |
| `docs/user-guide/docs/glossary.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Added the Custom Tools navigation entry. |
| `docs/user-guide/docs/install-linux.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/user-guide/docs/install-windows.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/user-guide/docs/privacy-and-data.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Added Custom Tools egress/host risk and corrected Development Mode and Docker-provider availability. |
| `docs/user-guide/docs/updating.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/velopack-release-install-guide.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/wiki/01-architecture-overview.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Refreshed topology/counts, WindowsLauncher, Custom Tools, runtime acquisition, and the MEAI 10.8.3 carrier warning. |
| `docs/wiki/02-project-layout.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Refreshed the 21-project layout, application modules/service areas, React features, and WindowsLauncher. |
| `docs/wiki/03-local-runtime-and-providers.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Documented current managed acquisition phases/registry/hub/UI and runtime ownership. |
| `docs/wiki/04-agent-mode.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Documented Custom Tools registry, offer, approval, execution, and unattended-path exclusions. |
| `docs/wiki/05-chat.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Documented delta-only frames, bounded queues, resume/reconciliation, disconnect grace, and the configurable 256 KiB inbound cap with composer precheck. |
| `docs/wiki/06-scheduler.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Refreshed current scheduler/API/persistence relationships and unattended tool restrictions. |
| `docs/wiki/07-model-fit.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Refreshed current runtime-acquisition and advisor/profile relationships. |
| `docs/wiki/08-data-and-persistence.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Documented Custom Tools persistence and refreshed 44 DbSets plus 53 migration implementations. |
| `docs/wiki/09-api-and-hubs.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Refreshed 24 route families, 201 route constants, 10 unconditional plus one conditional hub, Custom Tools, and runtime acquisition. |
| `docs/wiki/10-react-client.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Refreshed 27 features, 11 possible hub paths, Custom Tools UI, and runtime-acquisition hydration/push behavior. |
| `docs/wiki/11-hosting-and-deployment.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Refreshed 21-project/WindowsLauncher and release-lifecycle documentation. |
| `docs/wiki/12-security-and-privacy.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Added Custom Tools secret, SSRF/DNS-pinning, approval, execution, and residual-risk boundaries. |
| `docs/wiki/13-testing-and-validation.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Corrected migration and coverage claims and documented all four standalone runners including the grammar negative control. |
| `docs/wiki/14-image-generation.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/wiki/15-knowledge-base.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `docs/wiki/16-code-conventions.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Removed mutable/count-heavy references and added Custom Tools boundary conventions. |
| `docs/wiki/Home.md` | Living/current | updated-code-grounded | Current source, configuration, scripts, tests, and cross-page consistency at `50cae141`. | closed-no-mutable-lines | passed | Refreshed headline inventories and added Custom Tools navigation. |
| `publish/README.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `publish/TESTER-QUICKSTART.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `scripts/README-dev-stop.md` | Living/current | audited-no-change | Compared with current source, configuration, scripts, workflows, or release metadata at `50cae141`; no correction required. | closed-no-mutable-lines | passed | No drift found in the assigned code-grounded audit. |
| `third-party/nuget/upstream/FastEndpoints-8.2.0-LICENSE.md` | Generated/vendor/template | protected-no-change | Generated, vendored, template, provenance, or third-party material; exclude from prose normalization. | retained-frozen | not-applicable |  |

## Evidence-derived changed-file allowlist

The final allowlist is documentation-only: 30 corrected living pages, the ADR index metadata, and this new audit artifact. No source, generated client, package, workflow, migration, compliance-body, or governance-body file is changed.

```text
CHANGELOG.md
README.md
docs/adr/README.md
docs/ai-runtime.md
docs/audits/2026-08-08-documentation-disposition.md
docs/backend-commentary-map.md
docs/comment-cleanup-grounding.md
docs/roadmaps/development-mode-container-status.md
docs/runbooks/linux-cuda-override-operator-runbook.md
docs/runbooks/otel-export-operator-runbook.md
docs/runbooks/windows-rc-verification-runbook.md
docs/user-guide/README.md
docs/user-guide/docs/features.md
docs/user-guide/docs/first-run.md
docs/user-guide/docs/for-experienced-users.md
docs/user-guide/docs/glossary.md
docs/user-guide/docs/privacy-and-data.md
docs/wiki/01-architecture-overview.md
docs/wiki/02-project-layout.md
docs/wiki/03-local-runtime-and-providers.md
docs/wiki/04-agent-mode.md
docs/wiki/05-chat.md
docs/wiki/06-scheduler.md
docs/wiki/07-model-fit.md
docs/wiki/08-data-and-persistence.md
docs/wiki/09-api-and-hubs.md
docs/wiki/10-react-client.md
docs/wiki/11-hosting-and-deployment.md
docs/wiki/12-security-and-privacy.md
docs/wiki/13-testing-and-validation.md
docs/wiki/16-code-conventions.md
docs/wiki/Home.md
```

## Stale-probe results

- Frozen-inventory reconciliation: **102 base Markdown paths, 102 rows, 102 unique paths, 0 missing, 0 extra, 0 duplicates**.
- Current inventory anchors re-derived from source: **21 solution projects**, **24 route-family classes / 201 route constants**, **11 `MapHub` registrations**, **27 React feature directories**, **53 migration implementations**, and **44 `NodeChatDbContext` DbSets**.
- All **49 living/current** files were inspected; **30 corrected**, **19 audited with no change**.
- Mutable `file:line` citations across the 49 living files: **0 remaining**. Historical evidence, ADR bodies, and controlled records retain their base-specific citations.
- Living-doc stale probes for obsolete MEAI 10.7, old hub/route/feature/migration counts, and the false “no Development Mode off switch” wording: **0 remaining**. Older pins remain only in dated ADR/evidence/baseline material where they describe the recorded state.
- Local link/media/fragment validation over the 49 living files plus the ADR index uses occurrence counts outside fenced blocks: **704 cross-file Markdown targets + 27 same-document anchors + 11 local HTML `href`/`src` references = 742 local references, 87 fragments, 0 errors**. External URLs and root-relative SPA routes without repository files are excluded; local paths and GitHub-style heading fragments are resolved from the containing document.

## Official/upstream research record

Local pins were compared with current official/upstream sources before editing:

| Component | Repository pin | Primary evidence used |
|---|---|---|
| Microsoft.Extensions.AI | 10.8.3 | Microsoft Learn AI abstractions/pipeline documentation and the official NuGet flat-container nuspec |
| Microsoft Agent Framework | 1.17.0 | Microsoft Learn Agent Framework, tool-approval and checkpoint documentation, plus the official NuGet flat-container nuspec |
| llama.cpp | b10201 / `8f4646a63ee29f2e0ab971b0290b141938769762` | The pinned upstream server README and b10201 release |
| Aspire | 13.4.6 (Browsers preview package separately pinned) | Aspire official overview plus central package pins |
| OpenAI SDK | 2.12.0 | Central package pin; OpenAI's Codex agent-loop article used to distinguish ChatGPT-login Codex transport from the public API-key Responses endpoint |

The Codex OAuth row now states that its ChatGPT-login endpoint is integration-sensitive rather than presenting the public Responses API as OAuth documentation. One NuGet version-list probe failed because gzip bytes were decoded as UTF-8, and a direct NuGet Gallery HEAD probe for `Microsoft.Agents.AI/1.17.0` returned 404; neither was retried after failure. Successful official Learn, upstream release, and NuGet flat-container evidence was used instead.

## Windows runbook classification

`docs/runbooks/windows-rc-verification-runbook.md` remains **Living/current** because it is the active procedure for product behavior that can only be verified on Windows. Its dated 2026-08-02/03 transcripts remain explicitly labeled historical evidence inside that procedure. The separate `windows-rc-remaining-work-agent-prompt.md` remains a protected historical handoff. Current runtime requirements and release paths were compared with `eng/ReleaseVersion.props`, `release.yml`, launcher code, and current verification scripts.

## Controlled-record contradiction log

- `docs/agent-knowledge.md` is a controlled durable-memory surface. Its MAF transition warnings still describe real traps, but some parenthetical version anchors predate the current MAF 1.17.0 / MEAI 10.8.3 pins. No governed body edit was made; rewording that durable memory remains an architect-approved follow-up if desired.
- `docs/compliance/*.md`, `docs/release-publication-checklist.md`, contributor/security instructions, and generated/vendor/template documents were reviewed for contradictions relevant to this refresh. No confirmed body correction was made without the required authority.
- Accepted ADR bodies and dated audits/evidence were not modernized merely to match current pins; doing so would rewrite historical claims.

## Validation and first-review record

Candidate validation completed before first independent review:

- `pnpm install --frozen-lockfile` — passed; lockfile unchanged.
- `pnpm run signalr:check` — passed: proxy paths match all **11** `Program.cs` hub registrations.
- `git diff --check` — passed after replacing one Markdown hard-break trailing-space sequence with paragraphs.
- Documentation scope check — **32 Markdown paths** are modified/untracked; no non-Markdown path is changed and nothing is staged.
- Inventory, link/media/fragment, mutable-citation, stale-phrase, and source-derived-count checks — passed with the figures recorded above.
- Backend build/test and Aspire/Chrome smoke were not run: this candidate changes documentation only and does not alter executable behavior, contracts, generated clients, packages, configuration, or workflows.

**First independent review:** a separate `code-reviewer` reviewed all 31 paths in the pre-fix candidate and requested four changes (0 critical, 1 high, 3 medium):

1. **High — inbound MCP key disclosure:** `docs/wiki/09-api-and-hubs.md` incorrectly said GET revealed the key. Corrected to GET status, POST generation with one-time plaintext return, and DELETE revoke; GET is now explicitly non-disclosing.
2. **Medium — Custom Tools creation default:** several pages overstated “new tools start disabled” as a server invariant. Corrected every occurrence to distinguish the node-wide default, the built-in form's disabled initial value, API-requested enablement after acknowledgement, and per-call approval.
3. **Medium — stale OpenAI pin:** `docs/wiki/05-chat.md` named OpenAI 2.11.0. The explanation is now grounded in the current `MapEffortLevel` behavior (`xhigh` maps to `ResponseReasoningEffortLevel.High`) without an obsolete package claim.
4. **Medium — changelog classification:** the hand-maintained `CHANGELOG.md` was misclassified as generated. It is now Living/current, was audited and corrected from `0.1.0-rc.5.2` to the current `1.0.0-rc.1`, and its historical tester-release totals were reconciled. Category totals and living-document counts were recomputed. A later final-review probe established that `v0.1.0-rc.4.1` now exists, bringing the current repository to nine matching source tags rather than the earlier eight-tag count.

The reviewer reported one failed read-only `nl` path lookup for guessed MCP endpoint locations and, per the repository failure rule, did not retry; the MCP finding had already been established from endpoint implementations inspected before that command. Post-resolution validation passed: **102/102** inventory rows, **49** living documents, **704 cross-file Markdown targets + 27 same-document anchors + 11 local HTML references = 742 local references**, **87** fragments, **0** mutable citations, **32** Markdown-only changed paths, `git diff --check`, and the **11/11** SignalR proxy check.

The first final re-review read all 32 changed Markdown paths and returned **not ready** with three medium findings:

1. the Linux CUDA override runbook used substring-wide `pkill -f` cleanup instead of a verified PID;
2. the `AgentExecutionLog` inventory omitted approval-decision rows (`record_kind = 2`) and the requirement that every reader and aggregate filter by kind; and
3. the release documentation still described six source tags. Fresh `git tag`/`git show` evidence established nine source tags, including `v0.1.0-rc.4.1` at `b6d5a895`.

The runbook now requires `/proc` executable/command-line verification followed by `kill -- <pid>`; the persistence page documents record kinds 0, 1, and 2 plus the filtering invariant; and the changelog/hosting page now agree with the nine current source refs. The reviewer also reported a failed read-only `sed` for a guessed Custom Tool mapper path and, per the repository failure rule, stopped without retrying. Post-resolution validation passed: **102/102** unique inventory rows with the documented category totals, **49** living documents, **742** local references under the documented **704 + 27 + 11** accounting, **87** fragments, **0** link/fragment errors, **0** mutable citations, **0** stale findings, **32** Markdown-only changed paths, `git diff --check`, and the **11/11** SignalR proxy check.

The repeated final re-review again read all 32 changed Markdown paths and returned **not ready** with one high and one medium finding. The user guide and chat page overstated Custom Tools as prompting on every invocation: the implementation keeps every tool approval-wrapped but permits an explicit, conversation-scoped approval for a `Fixed` tool, bound to its version, while a `Parameterized` tool must re-prompt for every model-selected argument set. Separately, the `0.1.0-rc.5.0` changelog section retained one sentence saying `0.1.0-rc.4.1` had no matching source tag. All affected user, root, and wiki wording now distinguishes those approval scopes, and the stale changelog sentence now agrees with the current `v0.1.0-rc.4.1` ref at `b6d5a895`.

The reviewer reported that its read-only shell lost `sed` and `nl` after assigning zsh's special `path` variable and, per the repository failure rule, stopped without retrying. Post-resolution validation passed: **102/102** unique inventory rows with the documented category totals, **49** living documents, **742** local references under the documented **704 + 27 + 11** accounting, **87** fragments, **0** link/fragment errors, **0** mutable citations, **0** stale findings, **32** Markdown-only changed paths, `git diff --check`, and the **11/11** SignalR proxy check.

The third final independent review read all 32 changed Markdown paths and returned **not ready** with one medium finding: `docs/wiki/13-testing-and-validation.md` reported 25 migration-test files in `XE-Local-AI-Engine.Client.Persistence.Tests`, while the tracked project inventory contains 26, including `Development/DevelopmentMigrationTests.cs`. All three stale occurrences now say 26. The reviewer also reported that an external tester-release URL was rejected by its web-open safety check and, per the repository failure rule, stopped without retrying; local git refs had already established the nine source tags.

Post-resolution validation passed: the project-local migration-test inventory is **26**, the disposition remains **102/102** unique rows with the documented category totals, and the candidate has **49** living documents, **742** local references under the documented **704 + 27 + 11** accounting, **87** fragments, **0** link/fragment errors, **0** mutable citations, **0** stale findings, **32** Markdown-only changed paths, a clean `git diff --check`, and the **11/11** SignalR proxy check.

The fourth final independent review read all 32 changed Markdown paths and returned **not ready** with one medium finding: release-tag prose treated the nine `v0.1.0-rc.*` refs as every tag in the repository, overlooking the non-release `codex/rollback-prior-ai-pins` tag. `CHANGELOG.md` and the hosting wiki now scope the nine/nine reconciliation and `v`-prefix convention explicitly to release/version tags and identify the non-release tag as outside that convention.

Post-resolution validation passed: git reports **nine release/version tags plus one explicitly non-release tag**, the disposition remains **102/102** unique rows with the documented category totals, and the candidate has **49** living documents, **742** local references under the documented **704 + 27 + 11** accounting, **87** fragments, **0** link/fragment errors, **0** mutable citations, **0** stale findings, **26** persistence migration-test files, **32** Markdown-only changed paths, a clean `git diff --check`, and the **11/11** SignalR proxy check.

The fifth final independent review read all 32 changed Markdown paths. Its initial pass stopped on four guessed source paths; after resuming with path discovery, it found one medium audit-artifact issue: validation records mixed the earlier 704/60 cross-file-only figures with a later unexplained 726/87 total. The artifact now defines one reproducible method across the 49 living files plus ADR index: 704 cross-file Markdown targets, 27 same-document anchors, 11 local HTML references, and 87 total fragments, all error-free. The resumed reviewer also reported a normal `git diff --no-index` difference exit as a command failure and stopped before SignalR, per the repository failure rule.

Post-resolution validation reproduced the documented method exactly: **704** cross-file Markdown targets + **27** same-document anchors + **11** local HTML references = **742** local references, **87** fragments, and **0** errors. The disposition remains **102/102** unique rows with the documented category totals; mutable citations and stale findings remain **0**; scope remains **32** Markdown-only paths; `git diff --check` is clean; and the SignalR proxy check remains **11/11**.

This artifact and the 32-path documentation candidate are now frozen for a sixth final independent review. The clean-review verdict and commit SHA are reported externally so this reviewed artifact is not edited after approval.
