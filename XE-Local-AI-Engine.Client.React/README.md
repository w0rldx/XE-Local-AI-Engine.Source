# XE Node React Client

This is the standalone React management UI served by `XE-Local-AI-Engine.Client` at the web root. It talks only to the node-local FastEndpoints API under `/api/local/v1`. The setup/login flow obtains a short-lived bearer access token; the shared Axios client keeps that token in memory and uses the refresh flow when required.

## Build

Run from the repository root:

```sh
cd XE-Local-AI-Engine.Client.React
pnpm install --frozen-lockfile
pnpm run build
```

`pnpm run build` also enforces recursive deployed-script budgets for application, TTS-worker, and ORT `.js`/`.mjs` output and prints the five largest emitted scripts.

## Standalone development server

Start the desktop backend first, then run the frontend from this directory:

```sh
pnpm install --frozen-lockfile
pnpm dev
```

Vite serves the UI at `https://localhost:5173` and proxies `/api`, `/openapi`, and every SignalR hub to `https://localhost:50722`. That is the desktop backend's standard local HTTPS address. To use a different loopback port, set an absolute localhost target:

```sh
VITE_PROXY_TARGET="https://127.0.0.1:51437" pnpm dev
```

Non-loopback proxy targets are rejected. The development certificate plugin may prompt for local certificate setup on the first run. Browser developer tools also report common missing accessible names/labels and main-thread tasks lasting at least 100 ms; these checks are development-only and are not included in production bundles.

Before committing, run:

```sh
pnpm validate
pnpm test
pnpm run test:tooling
pnpm run build
```

## Dependency update validation

After changing `package.json` or `pnpm-lock.yaml`, run from `XE-Local-AI-Engine.Client.React`:

```sh
pnpm run dependencies:refresh
```

The command requires a successful frozen install before it runs any generator. It then checks OpenAPI drift,
regenerates and checks the About-dialog license manifest, runs frontend validation, and performs the production build
that creates the exact bundled-license corpus. Independent post-install failures are collected so one code-generation
error does not hide a later license or build diagnostic; any failed required stage produces a non-zero exit. The final
report lists staged and unstaged tracked generated files that must be committed with the dependency update.

The command never downloads or retargets curated license evidence. If an upgraded package lacks embedded terms, review
the reported old/new package URLs, pinned evidence path, upstream source/tag, and SHA-256 before changing
`third-party/npm/frontend-license-overrides.json`.

## OpenAPI client generation

The frontend uses `@hey-api/openapi-ts` to generate a typed Axios client from the committed OpenAPI snapshot at `openapi/v1.json`.

### Regenerate the client

Run from `XE-Local-AI-Engine.Client.React`:

```sh
pnpm run openapi
```

This fetches the current OpenAPI document and regenerates `src/core/api/generated/**`. For a local HTTPS API with a self-signed certificate, use:

```sh
OPENAPI_INSECURE=1 OPENAPI_SPEC_URL="https://localhost:50722/openapi/local/v1/v1.json" pnpm run openapi
```

To regenerate from the committed snapshot only and check for drift:

```sh
pnpm run openapi:check
```

To compare a running desktop-mode backend with both the committed snapshot and generated client, supply its absolute specification URL:

```sh
OPENAPI_SPEC_URL="https://localhost:50722/openapi/local/v1/v1.json" OPENAPI_INSECURE=1 pnpm run openapi:check:live
```

The live command requires `OPENAPI_SPEC_URL`, normalizes the live document exactly like `openapi:fetch`, compares it byte-for-byte with `openapi/v1.json`, then runs the unchanged snapshot-only generation check. Dynamic top-level loopback `servers` origins are removed during materialization so an isolated backend's allocated port is not contract drift. Fetches time out after 10 seconds and reject documents above 8 MiB. The command does not start or stop the backend.

### Important files

- `openapi/v1.json` — committed OpenAPI snapshot.
- `OpenapiTs.config.ts` — Hey API generator configuration.
- `src/core/api/Generated.runtime.ts` — runtime bridge that injects the existing `axiosInstance` into the generated SDK.
- `src/core/api/generated/**` — generated output; do not hand-edit these files.

### Axios and auth behavior

Generated SDK calls use the shared node-local `axiosInstance`, so the in-memory bearer token, refresh handling, rate-limit toast, and ProblemDetails interceptors remain active. The browser never receives cloud-provider credentials or platform worker credentials.

### Migration cookbook

When migrating a handwritten API module:

1. Import the generated operation from `@/core/api/generated/sdk.gen`.
2. Keep the existing public function name/signature so current components and mutations do not need to change.
3. Call the generated operation with `throwOnError: true`.
4. Map path/query/body values to the generated operation shape.
5. Return the existing frontend model type until call sites are ready to consume generated DTO names directly.
