# ADR 0014: Update channels are app state, and Development builds ship on their own Velopack feed

- **Status:** Accepted — by the repository owner (`w0rldx`) on 2026-09-22.
- **Date:** 2026-09-22
- **Scope:** Which releases an installed node is willing to see, where that choice lives, and how automated
  snapshots of `develop` are published without becoming visible to anyone who did not ask for them. It changes
  nothing about the engine's API shape beyond one status response and one endpoint, nothing about the
  `open-source-release` approval gate on tag releases, and nothing about `publish-release` semantics.
- **Authority:** Operator decisions taken 2026-09-22 (same repository for Development builds; a paginating update
  source with 30-release retention; one shared packaging workflow; the pre-1.0 version rule adopted immediately;
  no human approval gate for Development publication; internal flavour values `main|tester|dev`).
- **Amends:** the two-flavour update model described in §6 of
  [Hosting, AppHost & Deployment](../wiki/11-hosting-and-deployment.md). It extends that model; it supersedes no ADR.

## Context

The updater baked its policy at publish time. `-p:UpdateChannel=main|tester` selected one of two
`appsettings.AppUpdate.*.json` files, whose only difference was a release-track enum that flipped Velopack's
"include prereleases" bool. A user could not change channel; a tester on an RC build could not step back to stable
without a reinstall, and a developer wanting `develop` snapshots had nowhere to get them.

Five properties of the pinned Velopack 1.2.0 shape a correct answer here. Each was read from the source at commit
`f2edcbca` or executed against it:

1. **`GithubSource.GetReleases` reads only the 10 newest GitHub releases** (`per_page=10, page=1`), and applies the
   prerelease filter *after* that truncation. Eleven or more prereleases therefore evict every stable release from
   the window, and `CheckForUpdatesAsync` starts returning `null` to stable users silently and indefinitely.
2. **A release is skipped silently when it does not carry the requested feed index.** `GitBase.GetReleaseFeed`
   catches the missing-asset failure and continues to the next release, so a release holding only
   `releases.win-dev.json` is invisible to a `win` feed read. This is the isolation mechanism, not a side effect.
3. **`SemanticVersion` splits the prerelease label at the *first* hyphen and ranks any non-numeric label above any
   numeric one.** `1.0.0-rc.2-dev.1` therefore outranks `1.0.0-rc.9`. Executed ordering confirms the safe form:
   `rc.2 < rc.2.dev.20260922.1 < rc.2.dev.20260923.1 < rc.3 < 1.0.0 < 1.0.1-dev.20260922.1 < 1.0.1-rc.1 < 1.0.1`,
   and `dev.9 < dev.10` because numeric labels compare numerically.
4. **A gap in the delta chain degrades to a full-package download.** Deleting an old release costs bandwidth, not
   correctness.
5. **The channel baked into a package becomes sticky** in the installed `Locator.Channel`, so an update check must
   state the channel it wants rather than inherit it.

Velopack's `--channel` is already spent as the OS discriminator (`win`, `linux`), which is why a third channel
cannot simply be "the dev channel".

## Decision

### D1 — Development builds are GitHub prereleases in this same repository

One release per Development build, carrying both platforms (`win-x64` + `linux-x64`) exactly as an RC release does.
No second repository, no rolling release, no object store. *Consequence:* Development releases share the release
window that property (1) above bounds, which is what D2 and D3 exist to survive.

### D2 — The app ships its own paginating update source

Because of property (1), the node does not use Velopack's stock `GithubSource`. It pages the releases API
(`per_page=100`, bounded page cap) and trims to the newest few releases per feed-index name, so a check reads a
bounded number of feed documents regardless of how many releases exist. *Consequence:* a Development user cannot
blind a Stable user, and the per-check HTTP cost is bounded rather than proportional to release history.

### D3 — Retention keeps the newest 30 Development releases; their tags are kept forever

`dev-build.yml`'s prune job deletes older Development *releases* after a successful publish, never with
`--cleanup-tag`. The number lives in exactly one place, `scripts/release/dev-build-identity.py`'s `--keep` default.
*Consequence:* every Development version ever reported still maps to a commit, and by property (4) a pruned delta
base costs a full download at worst.

### D4 — One shared packaging workflow

The packaging matrix lives in `.github/workflows/package-velopack.yml` as a `workflow_call` workflow that both
`release.yml` and `dev-build.yml` invoke, differing only by inputs. *Consequence:* the 17-step packaging job cannot
drift into two copies, and a tag release's observable behaviour is unchanged by the extraction.

### D5 — The version rule, adopted immediately

The anchor is the newest `v*` tag reachable from the built commit, never `eng/ReleaseVersion.props`, which still
names an already-published version. A prerelease anchor is extended with dots
(`1.0.0-rc.2` → `1.0.0-rc.2.dev.<yyyymmdd>.<n>`); a stable anchor bumps the patch
(`1.0.0` → `1.0.1-dev.<yyyymmdd>.<n>`). `<n>` starts at 1 and increments per existing `dev/` tag with the same
anchor and date. **A second hyphen is forbidden** by property (3), and `compose_dev_version` asserts its own output
to keep it unreachable. *Consequence:* a Development build always sorts above the RC it was cut from and below the
next RC, and the two-rule split exists because a plain `1.0.0-dev.*` label sorts *below* `1.0.0-rc.2`, which is the
wrong window before 1.0.0 ships.

### D6 — Forward only, for free

`UpdateOptions.AllowVersionDowngrade` is never enabled anywhere. The selected channel is node state and is passed
explicitly on every check rather than read back from the installed package's sticky channel (property 5). A
source-text guard test asserts the option never appears under `Services/AppUpdate`. *Consequence:* changing channel
never offers a lower version, and leaving Development means a reinstall — stated plainly rather than engineered
around.

### D7 — No human approval gate for Development publication

`dev-build.yml` publishes with `GITHUB_TOKEN` and `contents: write`, with no protected deployment gate. Every
technical gate an official release has stays: the shared validation workflow, the license corpus, the payload SBOM,
artifact-shape verification, checksums, remote-asset verification and envelope verification. It joins the
`official-release-${{ github.repository }}` concurrency group with `cancel-in-progress: false`. *Consequence:* a
daily automated snapshot needs no human, and it can never interleave its delta-predecessor lookup with a real
release's.

### D8 — Names

| Surface | Values |
|---|---|
| Baked flavour (`-p:UpdateChannel=`) | `main`, `tester`, `dev` |
| Channel model in code | `Stable`, `Preview`, `Development` |
| UI strings | "Stable — Recommended", "Preview", "Development" |
| Velopack channels | `win` / `linux` (Stable + Preview payloads), `win-dev` / `linux-dev` (Development payloads) |
| Development git tags | `dev/<version>` |
| Development release title | `Development Build <version>` (prerelease flag on) |

*Consequence:* `dev/<version>` does not match the release tag handling in `cliff.toml`, and the "is HEAD tagged"
probe in `scripts/generate-release-notes.sh` is restricted to `--match 'v*'` so a Development tag can never steer an
official release's notes.

## Consequences

- **Stable and Preview users are unaffected by Development builds**, and not by policy but by mechanism: a release
  carrying only `releases.<os>-dev.json` is skipped by their feed read (property 2).
- **A Development user on `1.0.1-dev.*` cannot move down to `1.0.0`** under D6, and needs a reinstall to return to
  Stable. This is the deliberate cost of forward-only.
- **Pruning old Development releases costs bandwidth, not correctness** (property 4). Their tags survive, so
  provenance does not depend on retention.
- **The paginating source adds per-check HTTP cost**, bounded by the newest-few-per-index trim rather than by how
  many releases the repository holds.
- **The 30-release cap is a cap, not the fix.** Property (1) is answered by D2; D3 only keeps the repository tidy and
  the window sane. Development builds must not be enabled on the schedule before the paginating source is in place.
- **The baked flavour is now only a default.** A published build's channel can change without repackaging, which
  means the flavour file no longer tells you what an installed node will fetch.

## Alternatives considered

- **A second repository for Development releases.** Rejected by the operator: it doubles the release surface, the
  token story and the compliance evidence, for isolation that property (2) already gives inside one repository.
- **Keep Velopack's stock `GithubSource` and rely on retention alone.** Rejected: the 10-release window is read
  *before* the prerelease filter, so any mix of releases that exceeds it silently blinds Stable users. Retention
  makes that less likely, never impossible.
- **A plain `dev` prerelease label at the same patch level (`1.0.0-dev.N`).** Rejected: it sorts *below*
  `1.0.0-rc.2`, so an RC tester would be offered a "newer" Development build that Velopack ranks lower.
- **A second hyphen for readability (`1.0.0-rc.2-dev.1`).** Rejected: property (3) makes it outrank every RC.
- **`AllowVersionDowngrade` to let a user step back to Stable.** Rejected as a non-goal: it turns every update check
  into a potential downgrade and has no bounded blast radius.
- **A protected approval gate on Development publication.** Rejected: a daily snapshot that needs a human is a daily
  snapshot that does not happen, and the technical gates are the ones that catch a broken build.
- **Making the SPDX envelope optional for Development builds.** Rejected: it would teach the strictest gate in the
  repository a second required-metadata set to save about two minutes on a job that already generates and validates
  a payload SBOM. Development releases carry the identical envelope shape; only the manifest's `development` object
  is new.
