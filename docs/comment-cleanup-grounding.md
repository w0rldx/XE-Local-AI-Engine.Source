# Comment cleanup grounding

Last reviewed: 2026-06-02

This note records the official documentation checked before tightening XML comments and implementation comments in the launch-feature cleanup pass. Use it as source grounding for comments only; it does not change runtime behavior.

## Review cadence and version anchors

- Re-check these sources before future source-comment edits when a package major version or runtime target changes. Several seams are version-sensitive: `FastEndpoints`/`FastEndpoints.Swagger` 8.x, `Microsoft.Extensions.AI` 10.x, Microsoft Agent Framework 1.8.x, EF Core 10.x, ASP.NET Core/SignalR 10.x, gRPC 2.80.x, Quartz 3.18.x, OpenTelemetry 1.15.x, OllamaSharp 5.x, Avalonia 12.x, and Aspire 13.x are the current repository pins.
- Prefer versioned official pages where available. If a source comment explains library mechanics, cite the stable concept in Markdown/reporting rather than pasting long upstream excerpts into `.cs` comments.
- Treat `global.json` as the .NET SDK selection contract for local tools and CI restore/build behavior. It is not the same thing as a project target framework.
- Worker-2 task-2 refresh on 2026-06-02 rechecked the C# XML documentation-comments specification, Quartz 3.x DI/hosted-service pages, and ASP.NET Core gRPC overview before editing scheduler/AgentHome/sandbox/HostAgent comments.

## XML documentation comments

- C# documentation comments are XML text attached with `///` or `/** ... */`; the XML must be well formed, and invalid XML causes compiler documentation warnings.
- Keep `<summary>` focused on what a type/member represents. Move operational caveats, invariants, or rationale to `<remarks>` when the detail is more than the API summary.
- Prefer `<see cref="..." />`, `<paramref name="..." />`, `<c>...</c>`, `<returns>`, and `<inheritdoc />` where they keep public API docs accurate without copy-pasting implementation detail.

Sources:
- https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/documentation-comments
- https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/xmldoc/recommended-tags

## FastEndpoints and OpenAPI comments

- FastEndpoints can populate OpenAPI descriptions from XML comments, `Summary()`, `EndpointSummary`, or `Summary<TEndpoint,TRequest>`.
- Endpoint request/response comments should be consumer-facing and stable: describe route/query/body semantics, not internal handler mechanics.

Source:
- https://fast-endpoints.com/docs/swagger-support

## Microsoft.Extensions.AI and Agent Framework

- `Microsoft.Extensions.AI` centers provider-neutral abstractions such as `IChatClient`; provider-specific clients such as Ollama can be wrapped in `ChatClientBuilder` pipelines for caching, rate limiting, function invocation, and OpenTelemetry.
- Agent Framework docs distinguish agent basics, tools, multi-turn conversations, memory/persistence, workflows, and hosting. Comments should preserve those runtime boundary concepts when code names refer to agents, tools, memory, workflows, or hosting.
- Agent Framework tool approval and workflow checkpoint comments should distinguish pending approval/checkpoint state from completed model output or durable application data.

Sources:
- https://learn.microsoft.com/en-us/dotnet/ai/ichatclient
- https://learn.microsoft.com/en-us/dotnet/ai/ai-extensions
- https://learn.microsoft.com/en-us/agent-framework/
- https://learn.microsoft.com/en-us/agent-framework/agents/tools/tool-approval
- https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints

## Quartz.NET scheduler comments

- Quartz `IJobDetail` owns job-definition settings and can carry a `JobDataMap` for state/data associated with the job instance.
- `JobKey` and `TriggerKey` names are unique within groups, so comments about scheduler identity should distinguish the job definition from each trigger/fire instance.
- Quartz's Microsoft DI integration creates scoped jobs through the default job factory in modern 3.x versions. Scheduler comments should avoid preserving obsolete guidance that tells maintainers to opt into now-deprecated DI job-factory helpers.
- Hosted-service comments should distinguish Quartz scheduler startup/shutdown from application-level run history, realtime hub events, and cancellation/interruption records owned by this repo.
- Comments in this repo should prefer stable terms such as scheduled-job store, definition, trigger, fire instance, run history, and interrupt over historical migration labels.

Sources:
- https://www.quartz-scheduler.net/documentation/quartz-3.x/tutorial/jobs-and-triggers.html
- https://www.quartz-scheduler.net/documentation/quartz-3.x/packages/microsoft-di-integration.html
- https://www.quartz-scheduler.net/documentation/quartz-3.x/packages/hosted-services-integration.html
- https://www.quartz-scheduler.net/documentation/best-practices.html

## EF Core persistence comments

- EF Core relationships are represented by foreign keys; deleting a principal requires either nulling optional FK values or deleting dependent rows through cascade delete.
- EF Core interceptors can observe or change EF Core operations. Comments about encryption/decryption at persistence boundaries should identify the boundary and avoid implying that application services own interceptor mechanics.
- EF Core value conversions are the supported mapping hook for model/provider value conversion, but custom encryption converters require careful handling of security implications.
- EF Core providers are version-sensitive; comments about SQLite behavior should distinguish Microsoft-maintained provider behavior from application invariants, especially when migrations or provider limitations are involved.

Sources:
- https://learn.microsoft.com/en-us/ef/core/providers/
- https://learn.microsoft.com/en-us/ef/core/providers/sqlite/
- https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations
- https://learn.microsoft.com/en-us/ef/core/saving/cascade-delete
- https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors
- https://learn.microsoft.com/en-us/ef/core/modeling/value-conversions

## SignalR and gRPC transport comments

- SignalR hubs expose connection lifecycle hooks such as `OnConnectedAsync`/`OnDisconnectedAsync`; default hub errors suppress sensitive details, and explicit `HubException` messages are sent to clients.
- SignalR comments should reserve realtime ownership for hub-mediated server/client messages. Do not imply direct client-to-client transport or leak stack traces through user-facing error guidance.
- ASP.NET Core gRPC authentication can use client certificates at TLS level before ASP.NET Core resolves the request principal.
- gRPC deadlines are propagated with calls and tracked by client and service; cancellation should stop server-side work for abandoned or expired calls.
- ASP.NET Core gRPC can also use Unix domain sockets for same-machine IPC; comments about the HostAgent socket should describe file-permission/HMAC boundaries instead of implying browser-accessible transport.

Sources:
- https://learn.microsoft.com/en-us/aspnet/core/signalr/hubs
- https://learn.microsoft.com/en-us/aspnet/core/grpc/authn-and-authz
- https://learn.microsoft.com/en-us/aspnet/core/grpc/deadlines-cancellation
- https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess

## OpenTelemetry and Ollama comments

- OpenTelemetry .NET covers generation and collection of traces, metrics, and logs through the OpenTelemetry APIs/SDKs.
- OpenTelemetry comments should name signals and instrumentation ownership, not a particular exporter pipeline, unless the comment sits in the service-defaults/exporter configuration seam.
- Ollama's HTTP API accepts JSON generation/chat requests at the local API surface, with `model` required for generation and prompt/message payloads depending on endpoint.
- The repo's Ollama comments should preserve provider boundary language: Ollama-specific API details stay in the provider project; application-layer comments should refer to provider-neutral chat/model abstractions.

Sources:
- https://opentelemetry.io/docs/languages/dotnet/
- https://docs.ollama.com/api/generate
- https://docs.ollama.com/api/chat

## HostAgent launch, tray, and installer comments

- Avalonia desktop apps use a classic desktop lifetime and tray icons can own native menus; Tray comments should stay focused on launch/reattach and menu behavior, not model-management workflows owned by the Web UI.
- WSL comments should distinguish install, import, distribution command execution, and termination/bootstrap phase boundaries.
- Linux native runtime comments should use systemd user-manager and user-unit terminology. Do not imply a system service, enabled linger, or boot-time autostart when the launch contract is user-initiated.
- Rootless Docker comments should preserve the non-root daemon/container boundary, `newuidmap`/`newgidmap` prerequisite, and `$XDG_RUNTIME_DIR` socket behavior.
- Linux desktop-launcher and no-autostart comments should distinguish freedesktop desktop entries from XDG Autostart entries.
- Local admin HTTP comments should distinguish CORS/origin behavior from non-browser loopback calls, and should keep token redaction and loopback-only host checks explicit.

Sources:
- https://docs.avaloniaui.net/docs/fundamentals/application-lifetimes
- https://docs.avaloniaui.net/controls/navigation/trayicon
- https://docs.avaloniaui.net/controls/menus/nativemenu
- https://learn.microsoft.com/en-us/windows/wsl/install
- https://learn.microsoft.com/en-us/windows/wsl/basic-commands
- https://www.freedesktop.org/software/systemd/man/latest/systemd.unit.html
- https://www.freedesktop.org/software/systemd/man/user%40.service.html
- https://docs.docker.com/engine/security/rootless/
- https://docs.docker.com/engine/security/rootless/tips/
- https://specifications.freedesktop.org/desktop-entry/latest-single/
- https://www.freedesktop.org/wiki/Specifications/autostart-spec/
- https://learn.microsoft.com/en-us/aspnet/core/security/cors

## Launch and build toolchain comments

- `global.json` comments and docs should treat the SDK version and `rollForward` policy as a .NET CLI selection contract, not as the runtime target framework.
- Aspire AppHost comments should describe local orchestration/resources and launch profiles; packaging/release-gate evidence remains a separate concern.
- Aspire comments should distinguish local developer orchestration from production packaging/runtime lifecycle, because AppHost launch profiles do not validate packaged release behavior.
- ASP.NET Core environment-variable comments should distinguish host configuration, app configuration, and double-underscore hierarchical keys. Do not imply that AppHost, installers, user shells, and package scripts own the same variable lifetime.
- Release comments should treat `dotnet publish` as the deployment-preparation command and should keep runtime identifiers plus self-contained settings explicit when documenting MSI/deb/rpm artifact inputs.
- React client launch docs should preserve the repository's explicit Node engine and pnpm package-manager pins before citing generic frontend tooling examples.
- React/Vite comments should distinguish browser-exposed `VITE_*` build-time values, local `.env.*.local` files, and Node-side script variables from the Node Web Server that serves the built UI in release paths.
- pnpm script comments should remember that `pnpm run` augments script `PATH` with local `node_modules/.bin`; do not imply globally installed toolchain binaries are required unless a script actually shells out to one.

Sources:
- https://learn.microsoft.com/en-us/dotnet/core/tools/global-json
- https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish
- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration
- https://aspire.dev/get-started/app-host/
- https://aspire.dev/get-started/prerequisites/
- https://nodejs.org/api/packages.html
- https://nodejs.org/api/environment_variables.html
- https://pnpm.io/package_json
- https://pnpm.io/cli/run
- https://react.dev/learn
- https://vite.dev/guide/
- https://vite.dev/guide/env-and-mode.html
