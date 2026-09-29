# ADR 0017: Web access is two operator-enabled built-in tools, and a graph reaches the web only through an allowlisted fetch

- **Status:** Proposed
- **Date:** 2026-09-27
- **Scope:** The built-in `web_search` and `web_fetch` agent tools: where they are offered, how they reach the network, how their
  output re-enters model context, and the one exception they make to the graph Tool-node admission rule. It changes nothing about
  Custom Tools, MCP, the sandbox substrate (ADR 0007) or the Training runtime (ADR 0005).
- **Authority:** Operator decisions taken 2026-09-27: .NET-native implementation (no Python); DuckDuckGo HTML as the default search
  backend with an operator-configured SearXNG URL replacing it; delivery before 1.0; graphs get no open search and fetch only
  against a user-maintained allowlist; each request is consented to before it is sent and retrieved content is reviewed before it enters context, with a per-conversation auto mode behind a one-time risk notice; SmartReader for main-content extraction;
  allowlist entries are URL prefixes.

## Context

The node had no way for a model to read the web. The egress invariant in `docs/wiki/12-security-and-privacy.md` allows outbound
connections only behind a feature the operator turned on. `CustomToolSsrfGuard` already implements the private-address deny list,
the DNS-rebind-safe pinned connect and the URL canonicalisation checks for operator-authored HTTP custom tools. Graph workflow Tool
nodes run unattended, so `ToolInvocationService` admits only `ReadLocal` tools there (`docs/wiki/21-graph-workflows.md` §4.3, on
the basis of ADR 0006's "nobody to ask" reasoning). Fetched web pages are the most direct route for indirect prompt injection and
for exfiltration through a model-chosen URL (`docs/security/sandbox-threat-model.md` T5, AB4).

A Python implementation (DDGS + Trafilatura in a uv environment) was considered and rejected: ADR 0005 scopes the uv runtime to
Training, the `run_python` sandbox has no network, and the Python environments are Linux x86_64 only.

## Decision

1. **Two built-in tools, off by default.** `web_search` (query → title/URL/snippet) and `web_fetch` (URL → readable text), both
   `ToolCategory.Network`. A node setting enables them; while it is off they are absent from every offer and refused at execution.
   They join the whole offer (so the default assistant gets them), a bound agent gets them only through `AllowedToolNames`, and a
   model outside the trust boundary never gets them.
2. **Search backends.** DuckDuckGo's HTML endpoint is the zero-configuration default and is best-effort: a block or rate limit
   yields a structured "unavailable" result, never a fabricated one. An operator-configured SearXNG URL replaces it. The SearXNG
   URL is operator-typed and may be local, so its client is bound to that base URL and the model controls only the query string.
3. **Fetch boundary.** Every request and every redirect hop passes `CustomToolSsrfGuard` (private, loopback, link-local, metadata
   and reserved addresses denied; pinned connect). GET only, bounded hops, one time budget across hops, a decompressed-size cap and
   a content-type allowlist. No proxy, no cookies.
4. **Untrusted output.** Fetched text and search results re-enter context inside `UntrustedContentFraming` fences with the
   untrusted trust label. Fetched bodies are never logged.
5. **Request consent, then result review.** Nothing goes out until the user allowed that exact request: a consent card shows the
   URL (`web_fetch`) or query (`web_search`), and a denial sends nothing and hands the model a decline note. Consent is per request
   (no session or domain scope) and covers the initial URL; redirect hops stay server-side (SSRF-checked per hop) and the review
   shows the final URL. After an allowed request, content either tool retrieves reaches the model only after the user reviewed and
   accepted it; a rejection hands the model a decline note instead. Parallel web calls get one card each, one after another. A
   per-conversation auto mode skips both the consent and the review, and its notice says requests go out unconfirmed; the first time a user enables it they acknowledge a risk
   notice. The review reuses the approval pause (both tools are approval-flagged, like `ask_user`), so unattended runs never fetch
   and the tools are withheld where no one could review (orchestration participants, agentic MCP scope). The node administrator
   is the only user, so auto mode also skips a review the node approval policy would add.
6. **Graphs.** No graph Agent node is offered either web tool, and the shared tool-invocation service keeps refusing both. A
   Tool node may run `web_fetch` only when it carries a non-empty allowlist of URL prefixes (same scheme, host and port; path
   prefix on a segment boundary); the graph executor calls the fetch service directly with that list, the initial URL and every
   redirect hop must match, and private addresses stay blocked even when listed. This is the single exception to the
   `ReadLocal`-only Tool-node rule: the allowlist, authored before the run, is the consent an unattended node cannot ask for, and
   it replaces the review for exactly those URLs.

## Consequences

- Search quality and availability depend on DuckDuckGo's unofficial HTML surface, which can change or block the node; SearXNG is the
  supported way out. Scraping that surface is at odds with DuckDuckGo's terms; the operator accepted that risk.
- A new NuGet dependency (SmartReader, with AngleSharp) enters the license inventory.
- Fencing reduces but does not remove prompt injection; the review is what keeps injected text out of context, and auto mode
  gives that up by the user's choice. Consent covers only the initial URL, so a redirect target that carries data out is still
  requested (SSRF-checked, at most 5 hops) and only the review sees the final URL.
- Headless dataset generation mocks `Network` tools, so teacher datasets may contain fabricated web results.
- Browser rendering, research orchestration and paid search APIs remain out of scope; adding one is a new decision.

## Amendment 2026-09-29: consent before the request

Decision item 5 originally reviewed only the result and sent the request unconfirmed. That left a hole: a model-composed URL or
query could carry data out (in the path, query string or search terms) before any user decision. The user is now asked before
the request is sent, and the result review runs after it. Auto mode skips both. Unattended runs, graph Tool nodes (allowlist),
orchestration, agentic MCP and workflow-owned work sessions are unchanged: they never run the web tools interactively.
