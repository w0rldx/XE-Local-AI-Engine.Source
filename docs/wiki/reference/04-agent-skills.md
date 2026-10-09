# Agent Mode reference: Agent Skills

> Reference page for [Agent Mode](../04-agent-mode.md) §4 · Reviewed: 2026-10-07 · Code-grounded.

It lists the skill tables, the resolution rules, the three MAF skill tools, the import pipeline stages and the approval-scope rules.

## 4.1 Data model

| Table | Key columns | Notes |
|---|---|---|
| `agent_skills` | `name`, `description` (encrypted), `body` (encrypted), `enabled`, `frontmatter_json` (encrypted, nullable), `origin`, `source_uri`, `imported_at_utc`, `content_sha256`, `version` | `AgentSkill.cs`. `frontmatter_json` is **one** encrypted blob holding `{license, compatibility, allowedTools, metadata}` rather than four columns — the fields are optional, sparsely used, and `metadata` is arbitrary operator- or third-party-supplied content. `origin` (`AgentSkillOrigin`: `Local=0`/`Imported=1`) is **plaintext and structural** — the resolver branches on it to decide whether to fence the body (§4.2), and it is **promote-only**: a row can move `Local → Imported`, never back, because demoting would silently strip the untrusted-content fence from a row an operator believes is now theirs. `version` bumps on any content-affecting edit (name/description/body/frontmatter, or a resource add/edit/remove) and drives the runtime config hash, so an edit invalidates a parked resume. `Enabled` toggles do **not** bump it. |
| `agent_skill_resources` | `id`, `skill_id` (FK, **cascade delete**), `name`, `description`, `media_type`, `content` (encrypted), `size_bytes` | `AgentSkillResource.cs`. This is the level-3 payload — the `references/`/`assets/` files a real skill's body links to. `name` is the skill-root-relative path (`references/FAQ.md`) because MAF's generated skill content tells the model to quote the name back exactly, and it is **immutable**: it is part of the content AAD, so renaming a resource is a delete-and-reinsert, never an in-place update. |

**The resource AAD binds `skill_id` **and** the resource `name`, not just the row id** — every other
encrypted column in this schema authenticates only its own row id. That would be wrong here: the
threat is a database *writer*, not a reader. Without `skill_id` in the AAD, anyone with write access
to the DB could re-parent an existing encrypted resource row onto a different skill and have its
content injected into another agent's context without forging a single byte of ciphertext. A test
performs exactly that raw-SQL re-parenting and asserts the read throws `CryptographicException`
(`AgentSkillStoreTests.cs`). `source_uri` stores the **kind only** (`upload`) for an uploaded archive —
an operator-chosen filename must not become the one plaintext free-text string in a table where
everything else is AEAD-sealed — but keeps the full `github:owner/repo` for a GitHub import, since that
value is already public.

Storage stays DB-only and encrypted **by decision**, not by omission: skills never touch disk, so MAF's
own `AgentFileSkillsSource` and its path-traversal/symlink guards — which protect *reading skills from
disk* — cannot be used at runtime. The node's exposure is instead at *import time*, where untrusted
archive entries are parsed (§4.5) — a deliberate trade that keeps the at-rest encryption posture and
reuses the repo's own hardened path helpers rather than inventing new ones.

## 4.2 Resolution — the single choke point

Continues [Agent Mode](../04-agent-mode.md#42-resolution--the-single-choke-point), which keeps the section introduction.

1. **Only enabled and assigned skills resolve.** A skill missing from the resolved set (deleted or
   disabled) is dropped and logged **by id only** — never the name, body, or description. This is the
   strongest control in the whole feature: an imported skill lands `enabled=false` (§4.5), so a
   third-party instruction cannot reach a model until an operator makes a **second, deliberate** act to
   turn it on.
2. **A MAF-invalid name is dropped fail-soft, not thrown.** `AgentSkillFrontmatter.ValidateName` (the
   validation authority — see below) is checked again at resolve time; a skill whose stored name it
   would reject (e.g. a legacy row with consecutive hyphens, `foo--bar`) is dropped with a
   `LogWarning` naming only the definition and skill ids, and the agent still builds. This mirrors the
   existing dropped-tool posture in `ProjectAllowedTools`: degrade, log, never fabricate, never throw.
   Before this guard, an invalid name persisted cleanly through the editor and then threw
   `ArgumentException` out of `AgentInlineSkill`'s constructor at agent-construction time, in **both**
   `InvocationAgentFactory` and `SubAgentSpawnService` — the turn died before the model was ever
   reached, and the skill had to be un-assigned or renamed to recover.
3. **Imported content is fenced.** `Origin == Imported` bodies and every one of that skill's resource
   payloads are wrapped through `UntrustedContentFraming.WrapDocument` before they leave the resolver —
   the same nonce-fencing every other attacker-controlled channel in this engine already gets
   (knowledge-base search/read, chat attachments, coder workspace reads). Before this, imported skill
   markdown was the **only** such channel reaching the model unfenced, and it landed in the *instruction*
   position — ranked above the operator's own documents. The reachable attack needed zero approvals: a
   skill body could direct `search_knowledge_base` + `read_file` (both approval-free) and then
   `spawn_subagent` (also approval-free, caller-chosen model) to hand what it found to a cloud model as
   a prompt — a path the cloud-egress gate does not cover, because it withholds local-data *tools* from
   a cloud model but says nothing about local data already read being forwarded as text.

   The nonce seed is `agent-skill:{id:N}:{version}` (`BuildFenceNonceSeed`,
   `AgentDefinitionResolver.cs`) — the id is a server-minted GUID that never appears in the skill
   file, so a body author cannot derive the nonce and forge a closing marker, and the seed is
   deterministic so the fenced text — and therefore the config hash — stays byte-stable across resolves.
   Local, operator-authored skills are **not** fenced; only `Origin == Imported` rows pay this cost.

   **Residual, stated rather than hidden:** MAF renders each skill's and resource's `name`/`description`
   into the generated skill content **outside** any fence the resolver controls, because they are the
   lookup keys the model must quote back verbatim to call `load_skill` / `read_skill_resource`. Those
   four fields are therefore attacker-chosen text that reaches the model unfenced for an imported skill.
   The mitigations are defence-in-depth, not a fence: MAF's own length caps (name 64, description 1024),
   an import-time charset guard on resource names (§4.5), visibility in the import preview, and the same
   values additionally carried *inside* the fence as metadata.

**Validation authority is `AgentSkillFrontmatter.ValidateName` / `ValidateDescription`**, not a
hand-rolled regex — `AgentSkillService.ValidateAsync` delegates to it directly, so the app and MAF can
no longer drift the way they did for the consecutive-hyphen defect above. `Microsoft.Agents.AI` is a
**direct** `PackageReference` on `Client.Application` for exactly this reason: a validation authority
must not ride a transitive dependency flow.

## 4.3 Runtime — MAF progressive disclosure and the three tools

Continues [Agent Mode](../04-agent-mode.md#43-runtime--maf-progressive-disclosure-and-the-three-tools), which keeps the section introduction.

The provider injects three tools, MAF-named and not present in this repo's own tool catalog:

| Tool | Purpose | Approval, default options |
|---|---|---|
| `load_skill` | Loads a skill's `SKILL.md` body (level 2) | **Required** |
| `read_skill_resource` | Fetches one bundled resource by name (level 3) | **Required** |
| `run_skill_script` | Would execute a bundled script | **Required**, but always fails closed here |

**All three are approval-gated by default** at the pin — a live regression this feature
uncovered relative to the 1.8.0 baseline the original skills work was verified against, since neither
construction call site set `AgentSkillsProviderOptions` or registered an auto-approval rule. A contract
test (`AgentSkillsProviderContractTests.AgentSkillsProviderOptions_GateEverySkillToolByDefault`) pins
all three defaults so a future MAF bump that flips one fails loudly instead of silently changing
behaviour.

`run_skill_script` is advertised and callable, but this engine never registers a script for it —
`AgentInlineSkill.AddScript` only accepts a `Delegate`, and scripts are a deliberate non-goal (§4.5). It
therefore **always fails closed**: invoking it with no registered script returns
`"Error: Script 'x' not found in skill '<name>'."`, never reaching an execution path. Its approval gate
is never disabled, on any path, including the sub-agent waiver below.

Because these three tools are injected by the context provider rather than resolved through
`InvocationToolResolver`, they never appear in the ordinary tool catalog and would otherwise audit as
`ToolCategory.Unknown`. `ToolApprovalCoordinator` carries its own `SkillToolCategories` map
(`ToolApprovalCoordinator.cs`) — `load_skill`/`read_skill_resource → ReadLocal`, `run_skill_script →
WriteExecute` — consulted **before** the normal offer-based category lookup in
`ResolveApprovalToolCategory`, purely so the approval audit trail can tell a skill-tool decision apart
from a genuinely uncategorized one. This does not put the tools under `IToolApprovalPolicy` (OPP-03):
that policy is tighten-only and is applied by re-projecting an *offered* tool's `RequiresApproval`, and
these tools are never offered — MAF owns their approval decision outright, and the node policy has no
lever over it, which is the correct direction (nothing is being waived) and needed no new mechanism.

## 4.4 The sub-agent waiver

Continues [Agent Mode](../04-agent-mode.md#44-the-sub-agent-waiver), which keeps the section introduction.

The fix constructs the child's provider with `DisableLoadSkillApproval = true` and
`DisableReadSkillResourceApproval = true` (`InvocationSkillsProvider.CreateForSubAgentChild`,
called only from `SubAgentSpawnService.AttachSkillsProvider`). The justification is the
same one that already governs every other capability a child inherits: **the operator already approved
the spawn**, and there is no human downstream of that decision to ask. This is a security-relevant
deviation, made deliberately and logged, not a silent default. `run_skill_script`'s approval is **never**
waived, for the child or anyone else — it is inert (§4.3), so there is nothing to gain and the one
tool that could execute something stays gated unconditionally.

## 4.5 Import pipeline

Continues [Agent Mode](../04-agent-mode.md#45-import-pipeline), which keeps the section introduction.

- **Preview** (`PreviewArchiveAsync` / `PreviewMarkdownAsync` / `PreviewGitHubRepositoryAsync`) parses,
  guards, and returns a `SkillImportPreview` report. **It writes nothing.**
- **Commit** (`CommitAsync`) replays the *materialised preview payload* against a single-use report
  token — it never re-parses the upload or re-fetches the repository. Re-deriving the content at commit
  time would reopen exactly the divergence the two phases exist to close: a GitHub repository can change
  between the two calls, so the operator would be approving one payload and persisting another.
- **Imported skills always land `enabled=false`** with `Origin=Imported` provenance. Enabling is a
  separate, deliberate act — this is the strongest control in the design (§4.2 point 1), not the preview
  or the acknowledgement checkbox.

**Three sources, one archive-extraction path.** Upload (`.zip`), pasted raw `SKILL.md` text, and a
GitHub `owner/repo` (`GitHubSkillArchiveDownloader.cs` — host allowlisted to `github.com` /
`codeload.github.com`, redirect host revalidated on every hop, a pasted URL is **never** accepted). A
pasted document has no containing directory, so its frontmatter `name` is authoritative and it imports
instructions-only. A collection repository (e.g. `microsoft/skills`, ~175 skills) is never bulk-imported
— the report lists every skill found and the operator **selects**.

**Extraction is in-memory** (`SkillArchiveReader.cs`) — nothing is written to disk at any point, which
removes the symlink and TOCTOU classes entirely rather than guarding them. The guards, all fail-closed
with an operator-visible reason, and all bound to `SkillImportOptions` so an operator can tighten them
without a rebuild:

| Guard | Default | What it actually bounds |
|---|---|---|
| Entry count | 8192 | Central-directory enumeration cost only — cheap, so this is deliberately generous |
| Per-entry inflated bytes | 1 MiB | The real per-file memory guard |
| Total inflated bytes | 32 MiB | Zip-bomb ceiling across everything kept |
| Compression ratio | 100:1 | Zip-bomb ceiling per entry |
| Archive size | 50 MiB | Hard cap on the upload as received |
| Resources per skill | 64 | A whole-archive cap alone would let one skill carry hundreds |

The entry-count/size caps started far tighter (512 entries, 10 MiB total) and made the flagship import
target, `microsoft/skills`, **unimportable** — entry count only bounds the cheap enumeration walk, while
the caps that actually bound memory are the per-entry and total *inflated* byte caps, and only entries
the import intends to keep are ever inflated. The limits were widened once that was measured, not
loosened casually.

**The guards bound bytes actually inflated, never `ZipArchiveEntry.Length`/`CompressedLength`** — those
are attacker-authored header fields. A `new byte[entry.Length]` pattern is the naive mistake this avoids:
an over-declared length steers such code into an OOM on an otherwise harmless archive (the *reachable*
lie — measurement during implementation showed an *under*-declared length is not constructible through
`ZipArchive` at all, because its own read path stops at the declared size regardless of what is
requested). UTF-8 decoding uses `new UTF8Encoding(false, throwOnInvalidBytes: true)`, not
`Encoding.UTF8`, which silently substitutes `U+FFFD` and would make that guard a no-op. Duplicate entry
`FullName`s are rejected outright, because `ZipArchive` resolves a duplicate differently when enumerating
than when fetched by name — letting preview and commit silently disagree otherwise. Symlink entries are
dropped **per-entry**, not treated as a reason to abort the whole archive: collection repositories
publish skills through symlinked directories whose targets are real folders in the same archive, so
dropping the link still finds every skill without ever resolving one.

**Resource names are charset-guarded** (`^(?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+$`, 1–200 chars, no `..`)
because a resource name is model-facing, approval-facing, *and* a log field — a newline could inject
instructions above the reviewed body, and a homoglyph or a right-to-left override could make the import
preview render something other than what actually gets stored. A rejected name is never echoed back into
the report.

**Scripts are detected and refused, never imported** (locked decision) — listed in the report so the
operator can see what was withheld, but no execution surface is added by this feature. File-backed
scripts would need `AgentFileSkill` + a custom script runner + on-disk storage, reversing the DB-only
storage decision in §4.1 as well; it is not a small increment.

**Files outside the resource allowlist are ignored, not refused, and listed** (`IgnoredFiles` on each
candidate) so the report names everything the skill shipped: accepted resources, refused scripts and
ignored files. They are never inflated. An ignored name that fails the resource-name charset guard is not
listed; such names are counted into one preview warning that echoes none of them.

Endpoints (`Endpoints/Skills/V1/`, routes under `LocalApiRoutes.Skills`): `POST skills/import/preview`
(multipart, all three sources with an explicit discriminator), `POST skills/import` (report token +
selection + `acknowledged: true`), `GET skills/import/preview/{token}/skills/{skillName}/resources/{resourceName}`
(one resource's content read from the live preview so it can be reviewed before the commit; the report
itself carries no resource content; 404 once the token is expired or consumed),
`GET skills/{id}/resources`, `GET skills/{id}/resources/{name}`. An
export endpoint was deliberately **not** shipped — it would stream decrypted skill bodies as a zip
(exfiltration-shaped) and re-materialise attacker-chosen file names onto the operator's filesystem;
the round-trip is instead proven as an in-process unit test. Skill body/resource content is excluded
from every generated OpenAPI example.

## 4.6 Approval scoping

Continues [Agent Mode](../04-agent-mode.md#46-approval-scoping), which keeps the section introduction.

- **`Once`** — today's behaviour, unchanged.
- **`Session`** — remembered for the rest of the conversation, keyed by
  `ApprovalMemoKey(ConversationId, ToolName, SkillName, SkillVersion, ResourceName)`
  (`TryResolveSessionApprovalKey`, `ToolApprovalCoordinator.cs`). Every field is load-bearing:
  - **`SkillVersion`** binds the approval to *content* — an edit, or a re-import that replaces the
    skill, bumps `agent_skills.version` (§4.1) and silently invalidates the memo rather than letting new
    content ride an old consent.
  - **`ResourceName`** is required for `read_skill_resource`; without it, one approval of one reference
    file would blanket-approve every resource the skill carries, including files the operator never saw
    in the import preview.
  - The memo is **hard-allowlisted to `load_skill` and `read_skill_resource` only** — `run_skill_script`
    can never be remembered, by construction, not by a runtime check.
  - **`Origin.Imported` skills are withheld from session scope entirely** — names in that content are
    attacker-chosen, and a durable approval sitting on a phished skill name is the worst case a memo
    could produce.
  - A node-level `NodeToolApprovalPolicy.SkillSessionScopeDisabled` flag turns the whole mechanism off,
    for an operator who wants a skill tool to prompt every time regardless.
  - A memo-suppressed approval **still writes its audit row** (`SessionScopeApprovalDecision`) — a
    approval that leaves no trace would thin the record of what an agent was allowed to do.
  - Denials are **never** remembered, under any scope.
  - The chat card only OFFERS session scope where the node can honor it. `SessionApprovalEligibility`
    is the one predicate `TryResolveSessionApprovalKey` and the node tool-catalog response
    (`ToolCatalogEntryResponse.SessionScopeEligible`) both read, so `ToolCallCard` hides its "Approve
    for this session" button for every tool that can never carry a memo — an MCP tool,
    `run_in_agent_home`, a `Parameterized` custom tool, or anything at all while
    `SkillSessionScopeDisabled` is on. The catalog answer is a tool-identity UPPER BOUND: the runner
    still applies the per-call narrowings above (imported skill, skill not in the package, unnamed
    resource), which only ever remove eligibility. The `tool-catalog` endpoint reaches both this
    predicate and the effective-approval flag through one `Client.Application` service,
    `ToolCatalogService`, rather than taking the approval policy itself. The MAF skill tools are per-agent and therefore
    absent from the node catalog, so an entry the card cannot find keeps offering the button.

**Unattended runs fail fast, and the check runs before the memo, not after.** A scheduled `run-agent`
job carries `RuntimePackage.IsUnattended` (excluded from the config hash, same posture as
`SupportsThinking`) all the way to `RequestToolApprovalAsync`, which throws
`ApprovalUnavailableException("approval required in an unattended run: <tool>/<skill>")` **immediately**
— before registering a pending approval, before consulting the session memo. Ordering here is
security-critical: checking the memo first would let a future pre-authorisation feature that populates
it become a way to satisfy approvals inside a run with no human in it, which is exactly what the
unattended guard exists to prevent. Before this guard, an unattended run with a pending approval simply
**blocked** for the full `_maxPendingToolCallAge` and then failed with a generic timeout — correct in
that it did not hang forever, but slow and unhelpful about *why*. The guard sits at the one place every
approval-required tool funnels through, so its blast radius is every such tool, not only skills, and
that is intended. `ask_user` is deliberately **not** unified with this behaviour: an unattended approval
*fails* (executing a tool nobody sanctioned is not a safe default), while an unattended question
*continues* with "not answered" (the model asked for input it can proceed without) — see
[Chat](../05-chat.md) for `ask_user`.

`Scope` rides `ResolveToolApprovalRequest` (the local endpoint DTO) and the Application-internal
dispatcher only. `ApprovalResolvedEvent` in `AI.Contracts` — the cross-repo SignalR contract the
platform hub also produces — is untouched: session scope is a loopback-only concept the hub cannot
produce, since it has no access to the memo.
