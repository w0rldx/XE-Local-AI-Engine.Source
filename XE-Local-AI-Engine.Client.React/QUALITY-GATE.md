# Frontend Quality Gate

One deterministic command runs the full static-analysis gate for this React + Vite + TypeScript app:

```bash
pnpm validate
```

It chains three scripts that cover the **Biome → TypeScript → Knip → dependency-cruiser** flow (stage 1 bundles Biome + TypeScript + stylelint), running in order and failing fast on the first error. **Tests are intentionally NOT part of the gate** — run `pnpm test` separately.

## The flow

| Stage | Command | Catches |
| --- | --- | --- |
| 1. Lint | `pnpm run lint` (Biome + TypeScript + stylelint + custom check) | Type errors (`tsc --noEmit`), lint-rule violations (Biome), CSS issues (stylelint), and the `event.currentTarget`-in-updater guard. |
| 2. Knip | `pnpm run knip` | Unused files, exports, and dependencies (dead code). |
| 3. dependency-cruiser | `pnpm run depcruise` | Architecture boundary violations: circular deps, cross-layer/cross-feature imports, source importing tests or devDependencies. |

The full `validate` script:

```
pnpm run lint && pnpm run knip && pnpm run depcruise
```

## Running each tool individually

```bash
pnpm run lint        # Biome + tsc --noEmit + stylelint + currentTarget guard
pnpm run knip        # dead-code / unused-dependency detection (config: Knip.ts)
pnpm run depcruise   # architecture boundaries (config: .dependency-cruiser.cjs)
```

dependency-cruiser's exit code counts **only `error`-severity** violations. `warn`/`info`
violations are printed but do **not** fail the gate.

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
Current snapshot: **0 errors, 38 warnings**.

- **`no-cross-feature` (26)** — one feature importing another feature's internals; shared
  code belongs in `core/`. Examples: `model-fit` <-> `models`, `agents` -> `chat`/`tools`/`skills`,
  `mcp` -> `tools`, `preview` -> `chat`/`agents`, `chat` -> `tools`/`agents`.
- **`no-core-to-legacy` (9)** — `core/` reaching into the legacy `data`/`components`/`modules`
  trees. Examples: navigation/header components -> `data/navigation`, `data/language`,
  `components/Logo`, `modules/theme-configurator`.
- **`no-core-to-features` (1)** — `core/` depending on a feature. `HeaderBar` -> `features/about`.
- **`no-orphans` (2)** — orphan modules (`ValidationProblemProbeApi.ts`, `ExpandableTextField.tsx`).
- **`no-feature-to-routes` (0)** — no current violations; features stay route-agnostic.
