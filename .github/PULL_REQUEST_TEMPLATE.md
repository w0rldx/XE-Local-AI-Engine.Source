<!-- Target the `develop` branch. For security issues, do NOT open a PR — see SECURITY.md. -->

## What & why

<!-- What does this change do, and why? Link any related issue (e.g. Closes #123). -->

## How it was validated

<!-- Tick what you ran (see CONTRIBUTING.md / AGENTS.md for the full commands). -->

- [ ] Backend: `dotnet build` + `dotnet test` in **Release** pass
- [ ] Frontend (`XE-Local-AI-Engine.Client.React/`): `pnpm run lint`, `pnpm test`, `pnpm run build` pass
- [ ] `pnpm openapi:check` run (if a backend contract changed)
- [ ] E2E run (`scripts/run-e2e-local.sh`), if the change affects end-to-end behavior

## Checklist

- [ ] No secrets, credentials, or runtime data committed
- [ ] Generated files (e.g. the hey-api client) were regenerated, not hand-edited
- [ ] Docs updated if behavior or contracts changed
- [ ] Docs: wiki inventories updated if hubs/routes/features/projects changed (`python3 scripts/docs-inventory-check.py`)
