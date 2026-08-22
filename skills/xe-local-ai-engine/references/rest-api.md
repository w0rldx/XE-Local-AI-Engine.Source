# REST API boundary

The committed [OpenAPI document](../../../XE-Local-AI-Engine.Client.React/openapi/v1.json) is the
static source of truth for `/api/local/v1` REST endpoints. Read it; do not regenerate it as part of
an external-agent workflow. A regeneration that does not use desktop launch mode silently omits
desktop-only endpoints.

REST authentication is distinct from inbound MCP authentication:

1. `POST /api/local/v1/auth/setup` creates the initial administrator when setup is still available.
2. `POST /api/local/v1/auth/login` returns the Operator JWT used by authenticated REST endpoints.
3. `POST /api/local/v1/mcp/server-key` mints the separate, one-time `xemcp_...` bearer credential for
   `/api/local/v1/mcp/server`.

The POST body is optional for backward compatibility; no body mints `delegate`, while
`{"scope":"delegate"}` or `{"scope":"agentic"}` selects the trust level explicitly. There is one
key row, so every mint rotates the credential and immediately invalidates the previous key.

An MCP key is not an Operator JWT. An `agentic` credential is operator-equivalent only for its
explicitly enumerated 23-tool inbound MCP surface; it does not authorize arbitrary REST calls.

The local API middleware enforces loopback peers and strict `Host`/`Origin` handling. Do not work
around those controls, expose the listener on a routable address, log credentials, or place them in
committed examples. For the broader boundary, see the repository's
[Security & Privacy wiki page](../../../docs/wiki/12-security-and-privacy.md).
