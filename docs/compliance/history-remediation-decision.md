# Git-history remediation decision

The reachable git history of this repository carries material that should not
have been published. Blocker **B1** of
`docs/audits/2026-08-07-open-source-readiness-review.md`, together with concerns
**S1** and **S2** of the same review, required a history rewrite before the
repository was made public. That rewrite was prepared alongside commit
`e397e823` and deliberately not run, and the repository went public on
2026-08-05 without it.

This record decides that the rewrite will be executed.

## What is exposed

The audit and commit `e397e823` describe the exposure; this section summarizes it
without adding retrieval detail. Exact object, blob, and commit locators are
deliberately not published here, consistent with the audit's own handling — they
belong in the restricted remediation manifest named under **Evidence** below.

- **A former development-database encryption key.** The `node-sqlite-key`
  development setting carried a default value in the tracked AppHost development
  settings file. Commit `ddb6e244` (2026-08-01) removed it from the tracked tree
  and gitignored `node.key` and `*.sqlite*`, but every earlier commit still
  carries the value.
- **Internal planning and tooling content.** Commit `e397e823` (2026-08-05)
  untracked 639 files under `.opencode/` and 202 under `Plans/` — 841 files that
  remain reachable in earlier commits. A deleted historical `opencode.jsonc`
  containing private workflow disclosure and stale maintainer instructions is
  also still reachable (audit **S2**).
- **Deleted runtime-generated artifacts.** AgentHome workspace state, one
  encrypted knowledge-base document, and six encrypted generated images were
  deleted from the tree but not purged from history. The payloads have
  near-random entropy; their plaintext sensitivity is unknown.
- **Personal author metadata.** Author and committer metadata across
  substantially all of the roughly 2,090 commits on `develop` uses a personal
  mailbox domain rather than a platform noreply address (audit **S1**). A
  `.mailmap` would change display and attribution only; it does not alter the
  stored objects.

## Decision

The project owner (`w0rldx`), as the repository owner and the party authorized to
decide the visibility and remediation of this repository's history, decides that
a `git-filter-repo` rewrite is executed before the `v1.0.0-rc.2` tag is created.

- **Approver:** `w0rldx`
- **Authority basis:** Repository owner; also the data owner of the runtime
  artifacts and the subject of the exposed author metadata.
- **Decision date:** 2026-08-23
- **Review date:** 2027-08-23

The rewrite covers both a content purge and an identity remapping:

- **Content purge.** Remove `.opencode/`, `Plans/`, the deleted runtime-generated
  artifacts, and the historical `opencode.jsonc` from every reachable commit.
- **Value replacement.** Replace the historical `node-sqlite-key` value in place
  via `git-filter-repo --replace-text`, so no commit retains the literal.
- **Identity remapping.** Map the personal author and committer address to the
  owner's GitHub noreply address, `20070711+w0rldx@users.noreply.github.com`.
  The noreply address is bound to the same GitHub account, so contribution
  history and the contribution graph are preserved while the personal mailbox
  leaves the objects.

Sequence: run the rewrite, force-push the approved refs, verify from a fresh
remote clone that the purged objects are unreachable and the remapped identity is
in place, then create the `v1.0.0-rc.2` tag. A GitHub Support request to garbage
collect the now-unreachable objects is filed as part of the same operation; the
tag does not wait on it.

## Limits of this remediation

These consequences are understood and accepted; the rewrite is worth performing
in spite of them, not because they do not apply.

- **Existing clones and forks are not reached.** The repository has been public
  since 2026-08-05. Anyone who cloned or forked it before the rewrite keeps the
  pre-rewrite objects, and no push can retract them. The rewrite removes the
  material from the canonical repository going forward; it does not unpublish it.
- **The server-side purge is asynchronous and externally owned.** A force-push
  makes the old objects unreachable, but they survive in GitHub's storage — and
  can remain retrievable by direct object reference — until GitHub Support
  processes the garbage-collection request. Completion is outside the project's
  control and is not a gate on the `v1.0.0-rc.2` tag.
- **Every commit identifier changes.** All eleven existing tags, every branch
  including the open Dependabot branches on the public remote, and any external
  link or citation that names a pre-rewrite commit SHA are invalidated. Open pull
  requests against pre-rewrite refs will need to be recreated.
- **The `v1.0.0-rc.1` release record is permanently pre-rewrite.** That release
  was tagged before this decision, and its manifest, checksums, and provenance
  evidence bind to a source SHA that will no longer exist in the rewritten
  history. That binding is left intact rather than falsified: `v1.0.0-rc.1` is a
  record of what was actually published, and the discontinuity between it and
  `v1.0.0-rc.2` is documented and accepted.
- **Rotation is still required.** Removing the historical key value from the
  objects does not make data sealed under it safe. Any development database
  created under the old committed key must still be rotated, as
  `docs/agent-knowledge.md` documents ("Rotate any dev DB created under the old
  committed key").

## Evidence

**Status: pending — the rewrite has not been executed.** This decision is not
satisfied by its own existence. When the rewrite runs, each of the following must
be recorded and retained with the release evidence, and this section updated to
reference it:

- **Affected refs inventory.** Every ref rewritten and force-updated: all
  branches (including the public remote's Dependabot branches), all eleven tags,
  and any other ref carrying the affected ancestry, captured before and after.
- **Restricted remediation manifest.** The exact paths, object IDs, and commit
  IDs of the purged material. This is retained as a controlled private record and
  is deliberately **not** committed to this repository; reference it here by its
  non-sensitive record identifier only.
- **Fresh-clone absence verification.** A clone taken from the canonical remote
  after the force-push, showing the purged paths absent from every commit, the
  key value absent from the full history, and author/committer metadata carrying
  the noreply address.
- **Full-history secret scans.** `gitleaks` and `trufflehog` results over the
  complete rewritten history, not the working tree alone. The 2026-08-07 audit
  had neither tool available, so a tree-level clean result from that period is
  not a substitute.
- **Support-ticket reference.** The GitHub Support garbage-collection request
  identifier and its outcome, recorded when it completes.

If the rewrite is abandoned or materially rescoped, that reversal is recorded as
its own dated decision rather than by editing this one.
