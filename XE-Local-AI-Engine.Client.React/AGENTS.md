# AGENTS.md — React client

Frontend-only rules. The root [`AGENTS.md`](../AGENTS.md) holds the working rules, the backend gate and the
runtime commands; the full frontend conventions are in
[`docs/wiki/16-code-conventions.md`](../docs/wiki/16-code-conventions.md) and
[`docs/wiki/10-react-client.md`](../docs/wiki/10-react-client.md).

## Validation

Run from this directory; `dotnet tool restore --tool-manifest ../dotnet-tools.json` once first.

```bash
pnpm install --frozen-lockfile
pnpm run acceptance          # validate + coverage thresholds + tooling tests + production bundle
pnpm audit --prod --audit-level=high
```

- `pnpm run build` remains a checked standalone build (lint + production bundle). The acceptance gate runs lint
  once through `validate`; `build:bundle` is its already-validated bundling step, including licenses and size checks.
- Use `pnpm run validate`, `pnpm run test:coverage:check` and `pnpm run test:tooling` independently while iterating.
- `pnpm run lint` is the typecheck (`tsc --noEmit` + Biome + Stylelint + the `currentTarget` guard).
- After a backend contract change: `pnpm run openapi:check` regenerates the hey-api client and fails on drift.
  Commit the regenerated `openapi/` and `src/core/api/generated/`; never hand-edit them. Against a running
  desktop backend: `OPENAPI_SPEC_URL=<spec-url> pnpm run openapi:check:live`.
- After a dependency change: `pnpm run licenses:check`; on dependency-update branches `pnpm run dependencies:refresh`.
- Knip and dependency-cruiser are no-growth baselines. Fix the code, do not widen the baseline without saying so.
- `pnpm run doctor` (react-doctor) and `pnpm run spellCheck` are advisory, not gates.
- E2E (`scripts/run-e2e-local.sh`, from the repo root) is ask-gated unless the task targets E2E behavior.

## Conventions

- Feature folders under `src/features/<feature>/`; shared code under `src/core/`.
- Data layer is the generated hey-api client only; no hand-written axios calls. Server state lives in TanStack
  Query and is never mirrored into a store; Zustand holds UI-only state with atomic selectors (no `useShallow`).
- Forms are manual: Mantine + `useState` + Zod on submit. No form library.
- User-facing strings go through react-i18next keys. Adding a language: [`docs/translating.md`](../docs/translating.md).
- Some lint suppressions are load-bearing (the SignalR hub hooks and chat adapters; listed in wiki 16). Do not "fix" them.
- An `await import()` inside `it()` counts against `testTimeout`; hoist imports.
- i18n is initialised suite-wide: `src/i18n.ts` is the first vitest `setupFiles` entry, so every test file — hand-rolled
  wrapper or not — resolves `t()` against the shipped `en` bundle. Assert the bundle string the operator sees, never the
  in-code `defaultValue`; `scripts/CheckI18nDefaults.mjs` fails lint on a drifted or missing default.
  The `"New conversation"` literal in `Chat.tsx` stays untranslated (persisted, compared by exact string).
- Dependency decisions that look like dead weight but are not: `recharts` is a required peer of `@mantine/charts`;
  UnoCSS runs `presetWind3()` (never wind4); `@types/node` stays at 22.3.0 behind pnpm's trust-downgrade gate.
