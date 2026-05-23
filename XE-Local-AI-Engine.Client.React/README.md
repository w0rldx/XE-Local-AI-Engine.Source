# C0re React Client

## OpenAPI client generation

The frontend uses `@hey-api/openapi-ts` to generate a typed Axios client from the committed OpenAPI snapshot at `openapi/v1.json`.

### Regenerate the client

Run from `C0re.Client.React.Web`:

```sh
pnpm run openapi
```

This fetches the current OpenAPI document and regenerates `src/core/api/generated/**`. For a local HTTPS API with a self-signed certificate, use:

```sh
OPENAPI_INSECURE=1 OPENAPI_SPEC_URL="https://localhost:7003/openapi/v1/v1.json" pnpm run openapi
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

Generated SDK calls use the shared `axiosInstance`, so the existing auth header, 401 refresh queue, rate-limit toast, and ProblemDetails interceptors remain active.

`src/core/auth/api/TokenRefresh.ts` intentionally stays on raw `axios`. Do not migrate it to the generated SDK or shared `axiosInstance`, because the refresh path must bypass the 401 interceptor to avoid recursive refresh loops.

### Migration cookbook

When migrating a handwritten API module:

1. Import the generated operation from `@/core/api/generated/sdk.gen`.
2. Keep the existing public function name/signature so current components and mutations do not need to change.
3. Call the generated operation with `throwOnError: true`.
4. Map path/query/body values to the generated operation shape.
5. Return the existing frontend model type until call sites are ready to consume generated DTO names directly.
