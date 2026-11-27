# Docs website workflow

> Reference page for [Testing & Validation](../13-testing-and-validation.md) "Continuous integration" · Reviewed: 2026-11-27 · Code-grounded.

The docs website is a Docusaurus site in `website/`. It renders the published folders of `docs/` and is hosted on
GitHub Pages at <https://w0rldx.github.io/XE-Local-AI-Engine.Source/>, with the docs under `/docs/`. `docs/` stays
the only source: the site reads the Markdown in place and never copies it. This page describes
[`docs-site.yml`](../../../.github/workflows/docs-site.yml), the workflow that builds and publishes it. What is
published and how links are handled is in [CONTRIBUTING.md](../../../CONTRIBUTING.md#docs-website).

## Triggers

| Event | Runs |
| --- | --- |
| `pull_request` to `develop` | `build` only |
| `push` to `develop` | `build`, then `deploy` |
| `workflow_dispatch` on `develop` | `build`, then `deploy` |
| `workflow_dispatch` on another branch | `build` only |

`pull_request` and `push` share one path filter: `docs/**`, `website/**` and the workflow file. The filter is all of
`docs/` rather than the published folders. The published set has one home, `website/src/lib/scope.mjs`, and a second
copy in the workflow could drift from it. A change to an unpublished file costs one extra build.

## Not a required check

`build-and-test` stays the only required status check. `docs-site` is not part of it and must not become a required
check: a required check with a path filter stays pending on every PR that does not touch those paths. A red run does
not block a merge, but the site is not published until a later run on `develop` is green.

## Jobs and permissions

The workflow grants `contents: read` at the top level, and `build` runs every dependency (install, tests, build)
with that read-only token. Only `deploy` adds `pages: write` and `id-token: write`, runs in the `github-pages`
environment, and runs no dependency code. A PR run, including one from a fork, never gets a Pages token. Every action is pinned by commit SHA; the `github-actions` Dependabot group moves the pins.

`build` runs in `website/`, with pnpm from `website/package.json` (`packageManager`) and Node 26:

1. `pnpm install --frozen-lockfile`.
2. `pnpm audit --audit-level high` over the whole tree, not only production: every package is build input.
   GitHub's dependency alerts do not read pnpm 12 lockfiles, so this step is the site's only advisory check.
3. `pnpm run test`: the unit tests for the link rewriter and the license policy.
4. `pnpm run licenses:check`: every package in the lockfile, for every platform, has a license on the allowlist
   or a reviewed exact-version exception. Packages not installed on the runner are judged from registry metadata.
5. `pnpm run build`: writes the third-party notices, then builds into `website/build`. Broken links and anchors
   fail the build.
6. `pnpm run verify:build`: no excluded folder in the output or the search index, every published file has a page,
   every local image and video resolves, an authored `<img width>` keeps its width, and the edit links point at
   `develop`.
7. On `develop` push or dispatch only: upload `website/build` as the Pages artifact, named with the run attempt so
   a re-run never leaves two of the same name.

`deploy` publishes that artifact with `actions/deploy-pages`, waits up to five minutes until the live
`version.txt` names this deployment (built commit, run and attempt), then requires a final HTTP 200 (redirects followed) for the home page, one
wiki page and the notices page.

## Concurrency and what a publish builds

Only push and dispatch runs on `develop` can deploy. They share one `docs-site-pages` group and never cancel a
running publish, so publishes run one at a time; a newer queued run replaces an older queued one. Every other run
(PRs, dispatches on other branches) shares a group per ref and cancels the older run.

A publish checks out the head of `develop` as it is when the run starts, not the commit that triggered it, and logs
the commit it built as a notice. A slow run or a full re-run of an old one therefore never overwrites a site built
from a newer one, and a queued run that a newer one replaced loses nothing: the newer run builds a later head. A
commit that changes no docs input starts no run, and needs none: the site built before it has the same inputs.

Re-running only a failed `deploy` would publish what an earlier attempt built, so `deploy` refuses it and says so:
re-run all jobs, or run `workflow_dispatch` on `develop`. A publish also stops before building when `docs-site.yml` at the checked-out head differs
from the workflow file the run itself uses (a re-run or a queued run that spans a workflow change); a dispatch on
`develop` then runs the current workflow. Such a refused retry still takes the queued slot of a newer
publish while it waits; if that happens, the same dispatch publishes the current head.

## Running it locally

The engine does not need to run. With the toolchain from `mise.toml` (Node 26, pnpm), in `website/`:

```bash
pnpm install --frozen-lockfile
pnpm start                  # live preview
pnpm audit --audit-level high
pnpm run test
pnpm run licenses:check
pnpm run build && pnpm run verify:build
pnpm run serve              # serve the built site
```

`python3 scripts/docs-inventory-check.py` is not repeated in this workflow: `build-and-test` runs it on every PR, so
the sources stay valid on GitHub as well.

## Dependency updates

Dependabot has an npm entry for `/website`: weekly, a 7-day cooldown and one group for every package. A bump PR runs
this workflow, so the audit, license check and build prove it.

## One-time Pages setup

The operator does this once in the repository settings before the first deploy; the workflow does not change
settings:

1. Settings → Pages → Source: **GitHub Actions**.
2. Settings → Environments → `github-pages`: limit deployment branches to `develop`.
3. If Pages is enabled after the workflow is already on `develop`, run `docs-site` once by `workflow_dispatch` on
   `develop`.
