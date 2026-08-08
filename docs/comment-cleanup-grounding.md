# Comment cleanup grounding

Last reviewed: 2026-07-24

This note records the official documentation checked before tightening XML comments and implementation comments in the launch-feature cleanup pass. Use it as source grounding for comments only; it does not change runtime behavior.

> **2026-07-24 re-grounding, amended 2026-07-29.** Guidance for two deleted subsystems — **HostAgent/tray (Avalonia)** and the **WSL managed distro** — is retired, along with the **gRPC** transport guidance. None of those exist in the tree, and a comment describing any of them as live is a defect. **Ollama is not in that list**: it was *not* removed and its guidance below still stands. Package pins in this file were re-read from `Directory.Packages.props` on 2026-07-24.
>
> **Docker was on that list and no longer is — read this before deleting a Docker comment.** [ADR 0004](adr/0004-development-mode-container-execution-docker-stopgap.md) (Accepted 2026-07-29) narrows "no Docker anywhere" to **no Docker on the inference path**, permitting it for **Development Mode build/test/lint execution only**. So the rule is now directional, not a blanket:
>
> - A comment describing Docker **on the inference path**, in the model runtime, in model acquisition, or as part of **HostAgent** or the deleted `LocalContainerSandboxProvider` is still a **defect**.
> - A comment describing Docker as the **Development Mode execution sandbox** is **correct and must not be deleted** — even though this file previously told you it was retired guidance. Verify against the ADR before touching it.
> - **Rootless Docker guidance stays retired.** The ADR documents that Docker-socket access is root-equivalent on Linux rather than mitigating it; the product neither depends on rootless Docker nor claims it. Do not write a comment implying otherwise.
> - **WSL nuance:** the deleted subsystem was the *managed distro* (`wsl --unregister xe-engine-runtime`), and it stays deleted. That is **not** the same as decision D1's requirement that Development Mode's data root live inside the WSL2 filesystem on Windows — a comment describing *that* is correct.

## Review cadence and version anchors

- Re-check these sources before future source-comment edits when a package major version or runtime target changes. Several seams are version-sensitive; the current pins in `Directory.Packages.props` are `FastEndpoints`/`FastEndpoints.Swagger` 8.2, `Microsoft.Extensions.AI` 10.8.3, Microsoft Agent Framework (`Microsoft.Agents.AI`, `.Abstractions`, and `.Workflows`) 1.17.0, OpenAI 2.12.0, EF Core 10.x, ASP.NET Core/SignalR 10.x, Quartz 3.18.2, OpenTelemetry 1.16, OllamaSharp 5.4, and Aspire 13.4. **gRPC and Avalonia are no longer pins at all** — both were removed with HostAgent (no `Grpc.*` package, no `.proto` file, no Avalonia package remains in the tree).
- Prefer versioned official pages where available. If a source comment explains library mechanics, cite the stable concept in Markdown/reporting rather than pasting long upstream excerpts into `.cs` comments.
- Treat `global.json` as the .NET SDK selection contract for local tools and CI restore/build behavior. It is not the same thing as a project target framework.
- Worker-2 task-2 refresh on 2026-06-02 rechecked the C# XML documentation-comments specification, Quartz 3.x DI/hosted-service pages, and the ASP.NET Core gRPC overview before editing scheduler/AgentHome/sandbox/HostAgent comments. The gRPC and HostAgent parts of that pass are now moot — both were removed from the tree afterwards.

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

## SignalR transport comments

> **gRPC is gone.** This section previously also covered gRPC authentication, deadlines, and the HostAgent Unix-domain-socket IPC seam. There is no `Grpc.*` package, no `.proto` file, and no HostAgent in the tree — do not write or restore gRPC comments.

- SignalR hubs expose connection lifecycle hooks such as `OnConnectedAsync`/`OnDisconnectedAsync`; default hub errors suppress sensitive details, and explicit `HubException` messages are sent to clients.
- SignalR comments should reserve realtime ownership for hub-mediated server/client messages. Do not imply direct client-to-client transport or leak stack traces through user-facing error guidance.
- SignalR does **not** replay to late joiners. Any comment on a hub that streams run/tool events should name the buffer + replay + sequence/dedupe contract rather than implying delivery is guaranteed on subscribe (see [`agent-knowledge.md`](agent-knowledge.md)).

Sources:
- https://learn.microsoft.com/en-us/aspnet/core/signalr/hubs

## OpenTelemetry and Ollama comments

- OpenTelemetry .NET covers generation and collection of traces, metrics, and logs through the OpenTelemetry APIs/SDKs.
- OpenTelemetry comments should name signals and instrumentation ownership, not a particular exporter pipeline, unless the comment sits in the service-defaults/exporter configuration seam.
- Ollama's HTTP API accepts JSON generation/chat requests at the local API surface, with `model` required for generation and prompt/message payloads depending on endpoint.
- The repo's Ollama comments should preserve provider boundary language: Ollama-specific API details stay in the provider project; application-layer comments should refer to provider-neutral chat/model abstractions.

Sources:
- https://opentelemetry.io/docs/languages/dotnet/
- https://docs.ollama.com/api/generate
- https://docs.ollama.com/api/chat

## Desktop launch and local admin HTTP comments

> **Retired subsystems — do not write comments for these.** This section previously carried active guidance for **HostAgent launch/tray** (Avalonia tray app), **WSL managed-distro**, and **rootless Docker** comments. Those three are **deliberately deleted** and stay deleted: there is no Avalonia tray app, no managed WSL distro, and no rootless-Docker dependency. The Windows-elevation need the HostAgent existed for is served by an in-app unprivileged process supervisor. Never reintroduce a comment that describes any of them as live — see the locked runtime decisions in [`agent-knowledge.md`](agent-knowledge.md). **Ollama is the exception: it was *not* removed** — it remains a gated, opt-in secondary provider (llama.cpp is the default runtime), so the Ollama guidance above still applies.
>
> **Two corrections to the sentence this blockquote used to carry (2026-07-29).** First, "no Docker anywhere in the tree" is **no longer the rule** — see the amended re-grounding at the top of this file. Docker is permitted for **Development Mode execution only** under [ADR 0004](adr/0004-development-mode-container-execution-docker-stopgap.md), so a Dev-Mode container comment is correct and must not be deleted by a cleanup pass; Docker on the inference path remains a defect. Second, "tool sandboxing is a supervised native process" is now **feature-specific**: `ProcessSandboxRuntimeProvider` is the provider for **AgentHome and Coder**, which stay on it, while Development Mode moves to the container provider behind the same `ISandboxRuntimeProvider` seam. Do not "correct" a comment that says so.

What remains applicable from this seam:

- Desktop-launch comments should describe the `XE_LAUNCH_MODE=desktop` / `--desktop` / Velopack-managed-install signals and the graceful-shutdown chain (`SIGHUP` on Linux, `CTRL_CLOSE_EVENT` on Windows), not a service or autostart contract. The launch contract is user-initiated: do not imply a system service, systemd unit, enabled linger, or boot-time autostart.
- Linux desktop-launcher comments should distinguish freedesktop desktop entries from XDG Autostart entries.
- Local admin HTTP comments should distinguish CORS/origin behavior from non-browser loopback calls, and should keep token redaction and loopback-only host checks explicit.

Sources:
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
