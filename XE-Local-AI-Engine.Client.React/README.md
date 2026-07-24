# XE Node React Client

This is the standalone React management UI served by `XE-Local-AI-Engine.Client` at the web root. It talks only to the node-local FastEndpoints API under `/api/local/v1`. The setup/login flow obtains a short-lived bearer access token; the shared Axios client keeps that token in memory and uses the refresh flow when required.

## Build

Run from the repository root:

```sh
cd XE-Local-AI-Engine.Client.React
pnpm install --frozen-lockfile
pnpm run build
```

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
