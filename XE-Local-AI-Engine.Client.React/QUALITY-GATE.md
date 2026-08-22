# Frontend Quality Gate

One deterministic command runs the full static-analysis gate for this React + Vite + TypeScript app:

```bash
pnpm validate
```

It chains four scripts that cover the **Biome → TypeScript → Knip → SignalR synchronization → dependency-cruiser** flow (stage 1 bundles Biome + TypeScript + stylelint), running in order and failing fast on the first error. **Tests are intentionally NOT part of the gate** — run `pnpm test` and `pnpm run test:tooling` separately.

## The flow

| Stage | Command | Catches |
| --- | --- | --- |
| 1. Lint | `pnpm run lint` (Biome + TypeScript + stylelint + custom check) | Type errors (`tsc --noEmit`), lint-rule violations (Biome), CSS issues (stylelint), and the `event.currentTarget`-in-updater guard. |
| 2. Knip | `pnpm run knip` | Growth beyond the committed unused-file/export/dependency baseline. |
| 3. SignalR sync | `pnpm run signalr:check` | A backend `Program.cs` `MapHub` registration missing from Vite, or a stale Vite WebSocket proxy. |
| 4. dependency-cruiser | `pnpm run depcruise` | Enforced architecture errors plus growth beyond the committed per-rule warning baseline. |

The full `validate` script:

```
pnpm run lint && pnpm run knip && pnpm run signalr:check && pnpm run depcruise
```

## Running each tool individually

```bash
pnpm run lint        # Biome + tsc --noEmit + stylelint + currentTarget guard
pnpm run knip        # dead-code no-growth baseline (config: Knip.ts + config/knip-baseline.json)
pnpm run knip:report # full Knip diagnostics, including the existing debt and config hints
pnpm run signalr:check # Vite websocket proxies match Program.cs hubs
pnpm run depcruise   # architecture boundaries + no-growth baseline
pnpm run depcruise:report # full dependency-cruiser diagnostics
```

dependency-cruiser's native exit code counts **only `error`-severity** violations. The wrapper additionally fingerprints every violation by rule/from/to and fails on any fingerprint absent from `config/dependency-baseline.json`. Paying debt down passes immediately; replacing one removed edge with a different edge still fails even when the total count is unchanged.

Knip follows the same incremental policy: each issue is fingerprinted by file/category/symbol against `config/knip-baseline.json`. The current baseline contains **26 fingerprints**; the separate `pnpm run knip:report` command continues to print every symbol plus configuration hints. Removing debt passes without a baseline edit, while replacement debt fails even when the total count is unchanged. The committed baseline file, rather than this explanatory count, is the gate's authority.

React Doctor is separately available as an offline, non-blocking advisory through `pnpm run doctor`; it
intentionally remains outside `validate`. See `REACT-DOCTOR.md` for its exact artifact, license, security, privacy,
and overlap evaluation.

## dependency-cruiser rule severities

**Enforced (`error` — gate fails if violated; all clean today):**

- `no-circular` — no circular dependencies.
- `not-to-test` — production source must not import test files.
- `not-to-dev-dep` — production source must not import devDependencies.

**Informational (`warn` — surfaced but non-blocking):**

- `no-orphans` — modules imported by nothing (excludes generated/`.d.ts`/config files).
- `no-core-to-features`, `no-core-to-legacy`, `no-cross-feature`, `no-feature-to-routes` — see tracked debt below.

## Tracked architecture debt (warn-level rules to promote to error later)

These rules are `warn` because the codebase has known existing violations. They surface the
debt without breaking the gate; promote each to `error` once its violations are paid down.
The enforced (`error`) rules are clean, so the executable baseline is warnings only. `config/dependency-baseline.json`
holds one fingerprint per accepted violation with the rule name first, and remains the gate's sole authority on how
much debt exists; run `pnpm run depcruise:report` for the current violations in full. This document names the rules but
deliberately does not restate their counts.

- **`no-cross-feature`** — one feature importing another feature's internals; shared
  code belongs in `core/`. Examples: `model-fit` <-> `models`, `agents` -> `chat`/`tools`/`skills`,
  `mcp` -> `tools`, `preview` -> `chat`/`agents`, `chat` -> `tools`/`agents`.
- **`no-core-to-legacy`** — `core/` reaching into the legacy `data`/`components`/`modules`
  trees. Examples: navigation/header components -> `data/navigation`, `data/language`,
  `components/Logo`, `modules/theme-configurator`.
- **`no-core-to-features`** — `core/` depending on feature-owned UI or diagnostics.
- **`no-orphans`** — modules imported by nothing in the current graph.
- **`no-feature-to-routes`** — features stay route-agnostic.

## Browser and bundle feedback

- Development builds install lightweight browser-console checks for missing image alt text, accessible names, form labels, and main-thread tasks lasting at least 100 ms. They are dynamically imported only when `import.meta.env.DEV` is true.
- `pnpm run build` finishes with `pnpm run bundle:check`. It recursively measures every deployed `.js` and `.mjs` file under `dist` and splits them across the budget categories declared in `config/bundle-budget.json`: `applicationJavaScriptBytes` covers everything a user downloads on boot, and `lazyEditorJavaScriptBytes` covers the Monaco editor core and its worker, which are fetched only when a `CodeEditor` first mounts — measured separately so one multi-megabyte vendor chunk cannot mask growth in the code everyone downloads. There are no Kokoro worker or ONNX Runtime categories because those payloads are no longer shipped. Budget increases require an explicit, measured decision rather than becoming warning noise; the committed JSON budget and fresh build output are authoritative, so neither the byte limits nor the current measurement are restated here.
