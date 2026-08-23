# Git-history remediation decision

The repository's reachable git history was published in 2026-08-05 carrying
material that the pre-publication review said must be removed first. The
history rewrite required by blocker **B1** of
`docs/audits/2026-08-07-open-source-readiness-review.md`, together with the
related concerns **S1** and **S2** of the same review, had been prepared but
was not run before publication.

This record decides that the rewrite is executed.

## Decision

The project owner (`w0rldx`), as the repository owner and the party authorized
to decide the visibility and remediation of this repository's history, decides
that a full history rewrite is executed before the `v1.0.0-rc.2` tag is
created. The rewrite removes the material identified by the review items named
above, replaces an affected historical configuration value in place, and
normalizes commit authorship metadata to the project's public identity.

- **Approver:** `w0rldx`
- **Authority basis:** Repository owner and data owner of the affected material.
- **Decision date:** 2026-08-23
- **Review date:** 2027-08-23

The itemized inventory of what is removed — paths, objects, commits, and
affected commit messages — is a restricted remediation record retained outside
this repository. It is deliberately not published here: enumerating removed
material in a committed document would republish the pointers the rewrite
exists to remove.

Sequence: run the rewrite, force-update the approved refs, verify from a fresh
clone of the canonical remote that the removed objects are unreachable, then
create the `v1.0.0-rc.2` tag. A hosting-provider support request to garbage
collect the now-unreachable objects is filed as part of the same operation;
the tag does not wait on it.

## Limits of this remediation

These consequences are understood and accepted; the rewrite is worth
performing in spite of them, not because they do not apply.

- **Existing clones are not reached.** Copies taken while the pre-rewrite
  history was public retain it, and no push can retract them. The rewrite
  removes the material from the canonical repository going forward; it does
  not unpublish it.
- **The server-side purge is asynchronous and externally owned.** A force-push
  makes the old objects unreachable, but they can survive in the hosting
  provider's storage until its support request is processed. Completion is
  outside the project's control and is not a gate on the `v1.0.0-rc.2` tag.
- **Every commit identifier changes.** All pre-rewrite tags, branches, and any
  external link or citation that names a pre-rewrite commit are invalidated.
- **The `v1.0.0-rc.1` release record is permanently pre-rewrite.** Its
  manifest, checksums, and provenance evidence bind to a source revision that
  no longer exists in the rewritten history. That binding is left intact
  rather than falsified: `v1.0.0-rc.1` is a record of what was actually
  published, and the discontinuity between it and `v1.0.0-rc.2` is documented
  and accepted.
- **Dependent operational follow-ups still apply.** Removing a historical
  value from the repository does not retroactively secure anything derived
  from it while it was published; the applicable rotation guidance in the
  engineering documentation stands independently of the rewrite.

## Evidence

**Status: pending — the rewrite has not been executed.** This decision is not
satisfied by its own existence. When the rewrite runs, each of the following
must be recorded and retained with the release evidence, and this section
updated to reference it:

- **Affected refs inventory.** Every ref rewritten and force-updated, captured
  before and after.
- **Restricted remediation record.** The itemized inventory of the removed
  material, retained as a controlled private record and referenced here by a
  non-sensitive identifier only.
- **Fresh-clone absence verification.** A clone taken from the canonical
  remote after the force-push, showing the removed material absent from the
  full history and the authorship metadata normalized.
- **Full-history secret scans.** Scanner results over the complete rewritten
  history, including the targeted rule set from the restricted record —
  stock scanner defaults are known not to cover everything the rewrite
  removes, so a default-rules pass alone is not sufficient evidence.
- **Support-ticket reference.** The hosting provider's garbage-collection
  request identifier and its outcome, recorded when it completes.

If the rewrite is abandoned or materially rescoped, that reversal is recorded
as its own dated decision rather than by editing this one.
