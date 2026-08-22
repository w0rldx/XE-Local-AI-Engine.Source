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

## Accepted artifact: `react-doctor@0.9.12`

- Registry: <https://www.npmjs.com/package/react-doctor/v/0.9.12>
- Tarball: <https://registry.npmjs.org/react-doctor/-/react-doctor-0.9.12.tgz>
- Integrity: `sha512-H7RNg13RYKwpQvi3+O3IkSj8pAD1pGTxSetxu7aOwXX3wmF+BydCLDiWxkPT9Pq7bIMHjuJ0taSZLsxEjlNjcA==`
- Tarball SHA-1: `01c7adc95237508786f2cfc1812526395ff6c21b`
- Packed size: 2,116,748 bytes; unpacked size: 9,789,568 bytes; 40 files.
- Published: 2026-08-13. The upstream repository was updated on 2026-08-22 and had 14,584 stars, 468 forks,
  and 1,399,212 npm downloads for 2026-08-15 through 2026-08-21 when evaluated. Sources:
  <https://github.com/millionco/react-doctor>,
  <https://api.npmjs.org/downloads/point/last-week/react-doctor>.
- Maintenance response was active: recent issues were closed from about 20 minutes to two days after filing.
  Examples: <https://github.com/millionco/react-doctor/issues/1657>,
  <https://github.com/millionco/react-doctor/issues/1661>.

## Compatibility and behavior evidence

- This project requires Node `>=22.13.0`, a supported subset of React Doctor's broader
  `^20.19.0 || >=22.13.0` range. Evaluation ran on Node 24.18.0 and pnpm 11.2.2.
- The root compiler surface is intentionally pinned to exact `@types/node` 22.3.0 for Node 22 compatibility and a
  trust-clean resolution. A separate transitive `@types/node` version used privately by another types package does
  not define the project's runtime baseline; do not add a pnpm override unless that private copy causes a real
  compile failure.
- React Doctor resolves its private TypeScript 5.9.3 dependency while the application retains TypeScript 6.0.3;
  pnpm reported no peer conflict and the scan completed.
- Two isolated, cache-disabled, serial JSON scans produced the same 147 fingerprints: 31 errors and 116 warnings
  across 81 files. `--blocking none` exited 0; the same findings with `--blocking error` exited 1.
- With `--no-telemetry --no-supply-chain --no-cache --no-dead-code`, isolated HOME/XDG directories remained empty,
  no network sockets were observed while polling the process and its children, and only an explicitly requested
  output directory received `diagnostics.json` plus per-rule text reports. The committed command does not request
  an output directory, so its durable output is stdout/stderr only.
- The exact CLI documents that `--no-telemetry` disables the score API, share URL, and crash reporting. Upstream's
  default telemetry disclosure is at <https://www.npmjs.com/package/react-doctor/v/0.9.12>.

## License and security decision

The published package declares `SEE LICENSE IN LICENSE`, not SPDX `MIT`. Its exact 1,732-byte license text is
preserved in `config/react-doctor-0.9.12-license.txt`; SHA-256:
`aa9b278de35d20d320e40789db8b3e242096ed4a89c5aaf8fbe23a5e55c08ff1`.

That modified MIT text requires prior written permission for model-training/improvement pipelines and for a paid
hosted or managed offering whose value derives substantially from React Doctor. The approved use is narrower:
local, developer-invoked analysis of this application's React source. React Doctor is not redistributed, used as
training data, offered as a service, or included in the About-dialog production license corpus. Re-evaluate the
license before changing any of those boundaries.

`pnpm audit --audit-level=high` reported the same eight findings before and after installation: two low, four
moderate, and two high. Both high advisories were pre-existing `js-yaml` paths under `@hey-api/openapi-ts`
(<https://github.com/advisories/GHSA-52cp-r559-cp3m> and
<https://github.com/advisories/GHSA-5p4m-2wfm-xmqj>); no React Doctor path appeared. This records zero observed
vulnerability delta, not a claim that the dependency graph is vulnerability-free.
