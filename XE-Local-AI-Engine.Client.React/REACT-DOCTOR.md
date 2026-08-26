# React Doctor advisory

React Doctor is an optional, developer-invoked second opinion. It is not a merge gate, is not part of
`pnpm run validate`, and is not shipped in the product. Run it with:

```bash
pnpm run doctor
```

The command disables telemetry, scoring, supply-chain requests, caches, dead-code analysis, parallel workers,
color, and blocking exits. Knip remains the unused-file/export/dependency authority; dependency-cruiser remains
the architecture-boundary authority; `pnpm audit` remains the vulnerability authority. React Doctor contributes
React-specific correctness, performance, security, and accessibility heuristics without duplicating those gates.

## Pinned artifact and reproducibility

The development dependency is pinned exactly to `react-doctor@0.9.12` in `package.json` and `pnpm-lock.yaml`.
Use `pnpm install --frozen-lockfile` and `pnpm run doctor`; do not substitute `npx react-doctor@latest`, because that
would run an unreviewed artifact and make results irreproducible.

- Registry: <https://www.npmjs.com/package/react-doctor/v/0.9.12>
- Tarball: <https://registry.npmjs.org/react-doctor/-/react-doctor-0.9.12.tgz>
- Integrity: `sha512-H7RNg13RYKwpQvi3+O3IkSj8pAD1pGTxSetxu7aOwXX3wmF+BydCLDiWxkPT9Pq7bIMHjuJ0taSZLsxEjlNjcA==`
- Tarball SHA-1: `01c7adc95237508786f2cfc1812526395ff6c21b`

## Configuration and authority

`doctor.config.jsonc` excludes generated API clients, generated route files, and generated runtime/axios surfaces.
Do not treat findings in generated code as hand-editable work; change the generator or source contract instead.

The committed package script disables telemetry, scoring, supply-chain requests, caches, dead-code analysis,
parallel workers, and color. It uses `--blocking none`, so React Doctor remains advisory and does not fail solely
because it found an error-level heuristic. Knip remains authoritative for unused files, exports, and dependencies;
dependency-cruiser remains authoritative for architecture boundaries; `pnpm audit` remains authoritative for
vulnerabilities.

## License and security decision

The published package declares `SEE LICENSE IN LICENSE`, not SPDX `MIT`. Its exact 1,732-byte license text is
preserved in `config/react-doctor-0.9.12-license.txt`; SHA-256:
`aa9b278de35d20d320e40789db8b3e242096ed4a89c5aaf8fbe23a5e55c08ff1`.

That modified MIT text requires prior written permission for model-training/improvement pipelines and for a paid
hosted or managed offering whose value derives substantially from React Doctor. The approved use is narrower:
local, developer-invoked analysis of this application's React source. React Doctor is not redistributed, used as
training data, offered as a service, or included in the About-dialog production license corpus. Re-evaluate the
license before changing any of those boundaries.

Dependency vulnerability status changes over time and is intentionally not snapshotted here. Run the repository's
current `pnpm audit` gate for the authoritative result.
