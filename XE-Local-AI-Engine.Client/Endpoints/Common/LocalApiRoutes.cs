namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static class LocalApiRoutes
{
    public const string Prefix = "api/local/v1";

    public static class ApiFoundation
    {
        public const string ValidationProblemProbe = "diagnostics/validation-probe";
        public const string UnhandledExceptionProbe = "diagnostics/exception-probe";
    }

    public static class Auth
    {
        public const string Status = "auth/status";
        public const string Setup = "auth/setup";
        public const string Login = "auth/login";
        public const string Refresh = "auth/refresh";
        public const string Logout = "auth/logout";
        public const string ChangePassword = "auth/change-password";
        public const string Me = "auth/me";
    }

    public static class LocalChat
    {
        public const string Hub = "/api/local/v1/chat/hub";
        public const string Conversations = "chat/conversations";
        public const string ConversationById = "chat/conversations/{conversationId}";
        public const string RenameConversation = "chat/conversations/{conversationId}/rename";
        public const string PinConversation = "chat/conversations/{conversationId}/pin";
        public const string ArchiveConversation = "chat/conversations/{conversationId}/archive";

        // Per-conversation temporary-chat (memory-excluded) override (adaptive memory). Literal "memory-excluded"
        // segment keeps it distinct from the other conversation action routes.
        public const string MemoryExcludedConversation = "chat/conversations/{conversationId}/memory-excluded";

        // Non-destructive compaction: summarize the older turns into a synopsis sent in their place. POST (an action that
        // mutates derived state), distinct literal "compact" segment.
        public const string CompactConversation = "chat/conversations/{conversationId}/compact";
        public const string BranchConversation = "chat/conversations/{conversationId}/branch/{messageId}";
        public const string MessageRevisions = "chat/conversations/{conversationId}/messages/{messageId}/revisions";
        public const string MessageFeedback = "chat/conversations/{conversationId}/messages/{messageId}/feedback";
        public const string SelectedPath = "chat/conversations/{conversationId}/selected-path";

        // Per-conversation uploaded-file attachments. Collection route (POST multipart upload, GET list) and the
        // individual file resource (DELETE). The literal "uploads" segment keeps these distinct from the other
        // conversation action routes; {fileId} is the server-generated file id (never a client-supplied path).
        public const string ConversationUploads = "chat/conversations/{conversationId}/uploads";
        public const string ConversationUploadById = "chat/conversations/{conversationId}/uploads/{fileId}";
        public const string Cancel = "chat/cancel";

        // Loopback tool-approval responder. In desktop/local mode there is no worker hub to resolve an MCP
        // tool's approval round-trip, so the browser posts the operator's decision here; the handler feeds it into the
        // in-process invocation runner to release the waiting turn. The literal "approvals/resolve" segments keep it
        // distinct from the other chat action routes; the body carries the approval request id + decision (no route param).
        public const string ResolveApproval = "chat/approvals/resolve";

        // Loopback ask_user responder, the question analogue of ResolveApproval: the browser posts the operator's
        // answers here and the handler feeds them into the in-process runner to release the parked turn. The literal
        // "questions/resolve" segments keep it distinct from the other chat action routes; the body carries the
        // question request id + the answers (no route param).
        public const string ResolveUserQuestion = "chat/questions/resolve";
    }

    public static class NodeBinding
    {
        public const string Start = "binding/start";
        public const string Poll = "binding/poll";
        public const string Cancel = "binding/cancel";
    }

    public static class Connection
    {
        public const string Status = "connection";
        public const string Connect = "connection/connect";
        public const string Disconnect = "connection/disconnect";
        public const string EnableAutoConnect = "connection/auto-connect/enable";
        public const string DisableAutoConnect = "connection/auto-connect/disable";
    }

    public static class NodeSettings
    {
        public const string Settings = "node-settings";
    }

    public static class CloudSettings
    {
        public const string Settings = "cloud-settings";

        // Entra ID device-code sign-in lifecycle for the stored Azure Foundry connection (interactive user sign-in
        // with no client secret configured). Kept under the CloudSettings surface rather than CloudCodex since it
        // authenticates the existing Azure Foundry connection, not a separate cloud provider. Never exposes token
        // material — start returns only the user code + verification URL; status reports state + those same
        // non-secret fields.
        public const string EntraDeviceCodeStart = "cloud-settings/entra/device-code/start";
        public const string EntraDeviceCodeStatus = "cloud-settings/entra/device-code/status";

        // Entra ID authorization-code sign-in lifecycle (confidential client + PKCE, Postman parity): browser
        // sign-in yields a delegated token while the stored client secret authenticates the code redemption. Start
        // returns only the authorize URL to open; status reports lifecycle state. Never exposes token material.
        public const string EntraAuthCodeStart = "cloud-settings/entra/auth-code/start";
        public const string EntraAuthCodeStatus = "cloud-settings/entra/auth-code/status";
    }

    /// <summary>
    ///     External OpenAI-compatible provider routes: the operator's named connections (an Unsloth-served
    ///     llama-server, vLLM, LM Studio, a hosted OpenAI-compatible API) and their manually registered models.
    ///     Every route is Operator-gated; the read path reports only whether an API key is stored, never the key.
    /// </summary>
    public static class ExternalProviders
    {
        /// <summary>The whole connection list (GET). Carries the store revision every write compares against.</summary>
        public const string Connections = "external-providers/connections";

        /// <summary>One connection resource: GET, PUT (insert-or-replace), DELETE. <c>{connectionId}</c> is the slug.</summary>
        public const string ConnectionById = "external-providers/connections/{connectionId}";

        // Connect-time reachability check against GET {base}/models, run SERVER-side because the browser cannot
        // reach an arbitrary operator endpoint through CORS. A literal segment beside the collection, so it can never
        // be parsed as a {connectionId}. POST because the body carries either a stored connection id or an inline
        // draft (base URL + optional key) that has not been saved yet — a "Test connection" before the first save.
        public const string Probe = "external-providers/probe";
    }

    /// <summary>
    ///     Per-user onboarding tour state routes. GET reads the current user's recorded tour entries; PUT upserts one.
    /// </summary>
    public static class Tutorial
    {
        public const string State = "tutorial-state";
    }

    /// <summary>
    ///     Codex (OpenAI ChatGPT subscription) OAuth sign-in routes. The login lifecycle is kept separate from
    ///     the key-based <see cref="CloudSettings" /> surface.
    /// </summary>
    public static class CloudCodex
    {
        public const string Login = "cloud/codex/login";
        public const string Status = "cloud/codex/status";
        public const string Logout = "cloud/codex/logout";
    }

    public static class LocalModels
    {
        public const string Models = "models";
        public const string ModelByName = "models/{modelName}";
        public const string ModelDetails = "models/{modelName}/details";
        public const string Select = "models/select";

        // Operator override of model classification. The literal "kind" segment keeps this distinct from ModelByName.
        public const string ModelKind = "models/{modelName}/kind";

        // Currently loaded (in-memory) models the runtime reports via /api/ps. The literal "running" segment after
        // "models" keeps it distinct from the {modelName} route param so it is never parsed as a model name.
        public const string Running = "models/running";

        // Graceful in-memory unload (keep_alive=0). The literal "unload" segment follows the model name, mirroring the
        // "kind" route, so it stays distinct from ModelByName.
        public const string Unload = "models/{modelName}/unload";

        // Developer/advanced per-model extra llama-server launch-argument override. The literal "launch-args" segment
        // follows the model name, mirroring "kind"/"unload", so it stays distinct from ModelByName.
        public const string ModelLaunchArguments = "models/{modelName}/launch-args";
    }

    public static class Invocations
    {
        public const string Monitor = "invocations";
    }

    public static class Agents
    {
        public const string Definitions = "agents";
        public const string DefinitionById = "agents/{agentDefinitionId}";

        // Distinct literal segment under the agents surface so it cannot collide with DefinitionById.
        public const string ToolCapableModels = "agents/tool-capable-models";

        // AI-assisted drafting. A literal segment under the collection, like the template actions below, so it can
        // never be parsed as an {agentDefinitionId}. Writes nothing — the draft only populates the operator's form.
        public const string Draft = "agents/draft";

        // Curated starter-pack catalog (GET list) and the operator-triggered import action. Literal segments after
        // "agents" keep these distinct from the {agentDefinitionId} route param.
        public const string Templates = "agents/templates";
        public const string TemplateImport = "agents/templates/import";

        // Per-agent playbook actions nested under the agent definition.
        public const string Playbook = "agents/{agentDefinitionId}/playbook";
        public const string PlaybookActionById = "agents/{agentDefinitionId}/playbook/{actionId}";

        // Analysis and review actions use literal segments so they remain distinct from action-id routes.
        public const string PlaybookAnalyze = "agents/{agentDefinitionId}/playbook/analyze";
        public const string PlaybookActionPromote = "agents/{agentDefinitionId}/playbook/{actionId}/promote";
        public const string PlaybookActionReject = "agents/{agentDefinitionId}/playbook/{actionId}/reject";
        public const string PlaybookActionSuggested = "agents/{agentDefinitionId}/playbook/{actionId}/suggested";

        // Golden-conversation evaluation for a specific suggested playbook action.
        public const string PlaybookActionEval = "agents/{agentDefinitionId}/playbook/{actionId}/eval";

        // Per-agent golden conversation set for manual authoring.
        public const string GoldenConversations = "agents/{agentDefinitionId}/golden-conversations";
        public const string GoldenConversation = "agents/{agentDefinitionId}/golden-conversations/{goldenConversationId}";

        // On-demand thumbs-up harvest and per-candidate approval. Literal action segments keep collection actions
        // distinct from golden-conversation id routes.
        public const string GoldenConversationsHarvest = "agents/{agentDefinitionId}/golden-conversations/harvest";
        public const string GoldenConversationApprove = "agents/{agentDefinitionId}/golden-conversations/{goldenConversationId}/approve";

        // Read-only per-agent feedback insights over message feedback aggregates.
        public const string FeedbackInsights = "agents/{agentDefinitionId}/feedback-insights";

        // Read-only cohort monitoring for enabled playbook actions.
        public const string PlaybookMonitor = "agents/{agentDefinitionId}/playbook/monitor";

        // Read-only adaptive-memory execution-log diagnostics (metadata only — no message content). Literal
        // "execution-logs" segment keeps it distinct from the {agentDefinitionId} route param.
        public const string ExecutionLogs = "agents/{agentDefinitionId}/execution-logs";

        // Read-only, versioned durable run-envelope lifecycle records (metadata only — no message content), optionally
        // filtered by conversationId. Literal "run-envelopes" segment keeps it distinct from the {agentDefinitionId} param.
        public const string RunEnvelopes = "agents/run-envelopes";

        // Read-only token-usage aggregation over the run-envelope ledger, grouped by model and UTC day (metadata only —
        // token counts, no content). Literal "usage-summary" segment keeps it distinct from the {agentDefinitionId} param.
        public const string UsageSummary = "agents/usage-summary";
    }

    public static class Benchmarks
    {
        public const string Projects = "benchmarks/projects";
        public const string ProjectById = "benchmarks/projects/{projectId}";
        public const string ProjectRuns = "benchmarks/projects/{projectId}/runs";

        // The whole model x KV-type matrix in one call. Its own route rather than a flag on ProjectRuns: it answers
        // 200 with a per-item outcome list, not the 202 + one run detail a single start answers with.
        public const string ProjectRunsBatch = "benchmarks/projects/{projectId}/runs/batch";

        // One project's whole record as a downloadable file: the JSON export carries every run at full detail
        // (transcript and judge verdict included), the CSV the same runs as flat, spreadsheet-ready rows.
        public const string ProjectExport = "benchmarks/projects/{projectId}/export";
        public const string ProjectExportCsv = "benchmarks/projects/{projectId}/export.csv";
        public const string RunById = "benchmarks/runs/{runId}";
        public const string RunCancel = "benchmarks/runs/{runId}/cancel";
        public const string RunScore = "benchmarks/runs/{runId}/score";
        public const string RunRejudge = "benchmarks/runs/{runId}/rejudge";

        // A project's task items are their own sub-resource, one level down from the project: they are a collection
        // with its own lifecycle (add, edit, delete, reorder), and each write recomputes the project's item-set hash,
        // which is not something a field on the project PUT could express.
        public const string ProjectTaskItems = "benchmarks/projects/{projectId}/items";
        public const string ProjectTaskItemById = "benchmarks/projects/{projectId}/items/{itemId}";

        // The measurement CELLS of a project: one model, one KV type, one repeat of the whole item suite. Its own
        // route rather than a shape on ProjectRuns, because a cell is what ranks and a run list cannot say which
        // items a cell is MISSING — the absence is the answer.
        public const string ProjectCells = "benchmarks/projects/{projectId}/cells";

        // Two to six named cells and the paired difference between each pair of them. Its own route rather than a
        // flag on ProjectCells: a comparison is over a SELECTION, and the interval is computed from the items that
        // selection shares -- a number that does not exist until someone says which cells they mean.
        public const string ProjectCompare = "benchmarks/projects/{projectId}/compare";

        // Reordering is its own verb on its own route: it names the whole order at once, which is also what makes it
        // safe under a concurrent add or delete.
        public const string ProjectTaskItemOrder = "benchmarks/projects/{projectId}/items/order";

        // The judge policy is its own sub-resource: it is the one part of a FROZEN project an operator may still
        // change, and doing so re-judges every run, so it never rides along on the project PUT.
        public const string ProjectJudge = "benchmarks/projects/{projectId}/judge";
        public const string ProjectRejudge = "benchmarks/projects/{projectId}/rejudge";

        // Quant fidelity is a display-only axis measured by its own work kind, so it gets its own sub-resources
        // rather than flags on the run or project routes.
        // The one part of a FROZEN project's configuration an operator may still change: it decides what gets
        // measured next, not what the existing runs were measured against. Its own sub-resource for the same reason
        // the judge has one — it never rides the project PUT, which the freeze refuses.
        public const string ProjectFidelity = "benchmarks/projects/{projectId}/fidelity";

        public const string ProjectKldEstimate = "benchmarks/projects/{projectId}/fidelity/kld-estimate";
        public const string ProjectFidelityCache = "benchmarks/projects/{projectId}/fidelity/cache";
        public const string RunFidelity = "benchmarks/runs/{runId}/fidelity";

        public const string RunFidelityAttempts = "benchmarks/runs/{runId}/fidelity/attempts";

        // The verdict matrix and the fit it produced are ONE question: a score shown beside a verdict set that did
        // not produce it is undetectable from the client, so they are never split across two routes.
        public const string ProjectComparisons = "benchmarks/projects/{projectId}/comparisons";
        public const string ProjectPairwiseEstimate = "benchmarks/projects/{projectId}/pairwise-estimate";
        public const string RubricPresets = "benchmarks/rubric-presets";
        public const string EligibleAgents = "benchmarks/eligible-agents";
        public const string EligibleModels = "benchmarks/eligible-models";
        public const string Hub = "/api/local/v1/benchmarks/hub";
    }

    /// <summary>
    ///     Node-wide agent skill library routes. Skills are SKILL.md documents (name + description + markdown body)
    ///     that agent definitions select into via <c>AllowedSkillIds</c> and load on demand at runtime.
    /// </summary>
    public static class Skills
    {
        // Skill collection (GET list — body omitted; POST create) and the individual skill resource (GET full incl.
        // body, PUT, DELETE).
        public const string Definitions = "skills";
        public const string DefinitionById = "skills/{skillId}";

        // Two-phase third-party import. The literal "import" segment sits under the collection, not the {skillId}
        // param, so it can never be parsed as an id. Preview writes nothing; the commit replays the previewed payload.
        public const string ImportPreview = "skills/import/preview";
        public const string Import = "skills/import";

        // AI-assisted drafting, same literal-segment-under-the-collection rule as "import". Writes nothing — the draft
        // only populates the operator's form; the existing create/update routes stay the sole persistence path.
        public const string Draft = "skills/draft";

        // Bundled skill files. {resourceName} is a skill-root-relative path, so it can carry slashes: the client
        // percent-escapes it and the endpoint decodes + validates before the lookup (see SkillResourceRouteName).
        public const string Resources = "skills/{skillId}/resources";
        public const string ResourceByName = "skills/{skillId}/resources/{resourceName}";
    }

    /// <summary>
    ///     Node-wide user-defined custom tool library routes. Custom tools are operator-authored HttpFetch/Command tools
    ///     that agent definitions enable per-agent (off by default) and run under the existing human-in-the-loop
    ///     approval. Every route is Operator-gated; the read path masks secret header/env values.
    /// </summary>
    public static class CustomTools
    {
        // Collection (GET list, POST create) and the individual tool resource (GET, PUT, DELETE).
        public const string Definitions = "custom-tools";
        public const string DefinitionById = "custom-tools/{customToolId}";

        // Authoring-time executable validation for the ProgramLaunch selector: POST a candidate absolute path, get
        // back ok/reason from the same O_NOFOLLOW host-executable guard the executor runs. Desktop-only (a headless
        // host has no operator picking a local binary); the literal "executable-probe" segment sits under the
        // collection so it can never be parsed as a {customToolId}. POST carries the path in the body (no 415 trap).
        public const string ExecutableProbe = "custom-tools/executable-probe";
    }

    public static class Scheduler
    {
        // Flat template catalog. Kept separate from job-id routes so templates cannot be parsed as ids.
        public const string Templates = "scheduler/templates";

        // Job collection (GET list, POST create) and individual job resource (GET, PUT, DELETE).
        public const string Jobs = "scheduler/jobs";
        public const string JobById = "scheduler/jobs/{scheduledJobId}";

        // Lifecycle actions use literal segments after the job id, keeping action names distinct from JobById.
        public const string JobEnable = "scheduler/jobs/{scheduledJobId}/enable";
        public const string JobDisable = "scheduler/jobs/{scheduledJobId}/disable";
        public const string JobTrigger = "scheduler/jobs/{scheduledJobId}/trigger";

        // Run history uses a flat query-filtered collection plus an individual run resource.
        public const string Runs = "scheduler/runs";
        public const string RunById = "scheduler/runs/{runId}";

        // Cancellation is run-scoped rather than job-scoped; the management service maps it to a Quartz interrupt.
        public const string RunCancel = "scheduler/runs/{runId}/cancel";

        // SignalR push hub for scheduler lifecycle events. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring LocalChat.Hub.
        public const string Hub = "/api/local/v1/scheduler/hub";
    }

    /// <summary>Development project, task, evidence, apply, and live-attempt routes.</summary>
    public static class Development
    {
        public const string Root = "development";
        public const string Capability = "development/capability";

        // Operator approval of the container runtime the capability preflight reports. A POST because
        // it changes what this node has approved; separate from the capability GET so a read can never pin a daemon.
        public const string ContainerRuntimeConfirmation = "development/container-runtime/confirmation";
        public const string Repositories = "development/repositories";

        // A GET with the folder id in the route: detection is a read of the repository, it takes no body, and a
        // body-less POST would land on this repo's 415 trap.
        public const string RepositoryProfileDetection = "development/repositories/{selectedFolderId}/profile-detection";

        // Templates are ordinary repositories the operator already has; the registry is list/add/remove, and
        // materializing one produces a NEW registered repository, which is why the create route sits under
        // repositories rather than under templates.
        public const string Templates = "development/templates";
        public const string TemplateById = "development/templates/{templateId}";
        public const string RepositoriesFromTemplate = "development/repositories/from-template";
        public const string Projects = "development/projects";
        public const string ProjectById = "development/projects/{projectId}";
        public const string RepositoryConnection = "development/projects/{projectId}/repository-connection";
        public const string TaskById = "development/projects/{projectId}/tasks/{taskId}";
        public const string NextAction = "development/projects/{projectId}/tasks/{taskId}/next-action";
        public const string CancelAttempt = "development/projects/{projectId}/tasks/{taskId}/attempts/{attemptId}/cancel";
        public const string Events = "development/projects/{projectId}/events";
        public const string TaskArtifacts = "development/projects/{projectId}/tasks/{taskId}/artifacts";
        public const string ArtifactById = "development/projects/{projectId}/tasks/{taskId}/artifacts/{artifactId}";
        public const string PatchPreview = "development/projects/{projectId}/tasks/{taskId}/preview";
        public const string Apply = "development/projects/{projectId}/tasks/{taskId}/apply";
        public const string Hub = "/api/local/v1/development/hub";
    }

    /// <summary>
    ///     Local API contract type for model-fit, the box-aware local model advisor. Cache-first: the latest
    ///     endpoint reads the cached recommendation snapshot and never runs the advisor; the refresh endpoint delegates
    ///     to the scheduler trigger and never executes the advisor directly. The advisor management routes are thin
    ///     transport over the llama.cpp binary/supervisor seams and the Hugging Face GGUF discovery/store/token seams.
    ///     There is no approved-image concept or provider-name param. Benchmark stays gated.
    /// </summary>
    public static class ModelFit
    {
        // Latest cached recommendation snapshot (query-filtered by useCase). The literal "latest" segment follows
        // "recommendations", so it never collides with the "refresh" action below.
        public const string RecommendationsLatest = "model-fit/recommendations/latest";

        // Manual refresh trigger — a template-guarded facade over the scheduler trigger service. The literal "refresh"
        // segment follows "recommendations", so it never collides with "latest".
        public const string RecommendationsRefresh = "model-fit/recommendations/refresh";

        // Sanitized hardware profile (RAM/VRAM/GPU vendor/CPU/disk aggregates only — no machine identifiers).
        // IHardwareProfiler passthrough.
        public const string HardwareProfile = "model-fit/hardware-profile";

        // GGUF repo discovery (IHuggingFaceGgufDiscovery search). The literal "browse" segment keeps it distinct.
        public const string GgufBrowse = "model-fit/gguf/browse";

        // Per-repo GGUF file inspection (IHuggingFaceGgufDiscovery inspect): the selectable quants + sizes for one
        // repo, so the browse UI can offer a quant picker. The literal "inspect" segment keeps it distinct.
        public const string GgufInspect = "model-fit/gguf/inspect";

        // Download a chosen GGUF file (IGgufModelStore) — starts a background, cancellable download keyed by
        // model name; the cancel action signals the in-flight download's token.
        public const string Download = "model-fit/download";
        public const string DownloadCancel = "model-fit/download/cancel";

        // Progress polling for in-flight and recently-finished GGUF downloads (IGgufDownloadCoordinator status registry).
        // List returns all tracked statuses; the {modelName} variant returns one (404 when unknown). The list endpoint is
        // the one-shot hydrate on mount; live progress streams over the DownloadHub below (no more per-second poll).
        public const string Downloads = "model-fit/gguf/downloads";
        public const string DownloadStatus = "model-fit/gguf/downloads/{modelName}";
        public const string DownloadOperationStatus = "model-fit/gguf/downloads/operations/{operationId:guid}";

        // SignalR push hub for GGUF download status changes. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring the other local hubs. Replaces the per-second downloads poll; each push carries the sanitized status.
        public const string DownloadHub = "/api/local/v1/model-fit/gguf/downloads/hub";

        public const string ImportCapability = "model-fit/gguf/import/capability";
        public const string ImportPreview = "model-fit/gguf/import/preview";
        public const string Import = "model-fit/gguf/import";
        public const string Imports = "model-fit/gguf/imports";
        public const string ImportStatus = "model-fit/gguf/imports/{operationId:guid}";
        public const string ImportCancel = "model-fit/gguf/imports/{operationId:guid}/cancel";

        // Running llama-server processes derived from the supervisor health snapshot; eject tree-kills one.
        public const string Running = "model-fit/running";
        public const string RunningEject = "model-fit/running/eject";

        // Curated model catalog: read-only metadata (version/source/fetchedAt) and an operator
        // forced-refresh trigger. The catalog content itself rides the existing recommendations/latest response
        // (section/tier fields on each row) — these two routes are catalog-provenance only.
        public const string CatalogInfo = "model-fit/catalog";
        public const string CatalogRefresh = "model-fit/catalog/refresh";

        // Resolved/pinned llama.cpp binary version (ILlamaCppBinaryManager). GET reads the pinned-tag + resolved
        // variant; POST ensures the binary for a chosen variant is present (download + hash-verify).
        public const string LlamaCppVersion = "model-fit/llamacpp/version";

        // Read-only dynamic-runtime status (ILlamaCppUpdateState + IInstalledRuntimeStore): installed vs recommended
        // (+ dev-mode upstream-latest) and whether a newer recommended runtime is available. Never triggers a download.
        public const string LlamaCppRuntime = "model-fit/llamacpp/runtime";

        // Operator-initiated install/update of a chosen llama.cpp release tag (ILlamaCppBinaryManager.InstallTagAsync via
        // the release catalog). Validates the tag format before resolving the asset + digest and installing.
        public const string LlamaCppUpdate = "model-fit/llamacpp/update";

        // In-app Linux CUDA source build (no upstream prebuilt exists). Prerequisites reports the itemized toolchain
        // checklist (any OS; non-Linux → canBuild=false). The build action is Linux+prereq+disk+eject-first+single-flight
        // gated server-side; status/cancel/remove drive the in-flight build and the adopted managed runtime. Literal
        // "cuda-build" segments follow "llamacpp" so none collide with the version/runtime/update routes above.
        public const string CudaBuildPrerequisites = "model-fit/llamacpp/cuda-build/prerequisites";
        public const string CudaBuild = "model-fit/llamacpp/cuda-build";
        public const string CudaBuildStatus = "model-fit/llamacpp/cuda-build/status";
        public const string CudaBuildCancel = "model-fit/llamacpp/cuda-build/cancel";
        public const string CudaBuildRemove = "model-fit/llamacpp/cuda-build/remove";

        // SignalR push hub for in-app CUDA build progress. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring the other local hubs. Each push carries the phase + appended log lines.
        public const string CudaBuildHub = "/api/local/v1/model-fit/llamacpp/cuda-build/hub";

        public const string SourceBuildPrerequisites = "model-fit/llamacpp/source-build/prerequisites";
        public const string SourceBuild = "model-fit/llamacpp/source-build";
        public const string SourceBuildStatus = "model-fit/llamacpp/source-build/status";
        public const string SourceBuildCancel = "model-fit/llamacpp/source-build/cancel";
        public const string SourceBuildRemove = "model-fit/llamacpp/source-build/remove";
        public const string SourceBuildHub = "/api/local/v1/model-fit/llamacpp/source-build/hub";

        // Read-only first-run runtime-acquisition snapshot (IRuntimeAcquisitionStatusRegistry): the GPU-probe → download →
        // verify → extract phase, byte progress, and the archive step counter. This is the one-shot hydrate on mount —
        // acquisition starts within seconds of boot, well before the client has authenticated and opened the hub below, so
        // without it the banner would never appear for the slow-first-run case it exists to explain. It NEVER triggers an
        // acquisition (unlike the ensure POST on LlamaCppVersion). The literal "acquisition" segment follows "llamacpp",
        // so it collides with none of the version/runtime/update/cuda-build/source-build routes above.
        public const string LlamaCppAcquisition = "model-fit/llamacpp/acquisition";

        // SignalR push hub for runtime acquisition progress. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring the other local hubs. Each push carries the same sanitized payload the hydrate GET serves, stamped
        // with the monotonic sequence the client reconciles hydrate and push by.
        public const string LlamaCppAcquisitionHub = "/api/local/v1/model-fit/llamacpp/acquisition/hub";

        // HF access-token set/clear (IHfTokenStore). The endpoint NEVER returns the token; GET reports presence
        // only (security gate).
        public const string HfToken = "model-fit/hf-token";

        // Inference Optimizer profile surface (IInferenceProfileService). The collection GET lists every persisted
        // node-local profile (machine key omitted). The four POST actions each carry their target in the body (never a
        // route param) so the POST always has a body, sidestepping the FastEndpoints 415-on-bodyless-POST issue. The
        // literal "explore|benchmark|freeze|invalidate" action segments follow "profiles", so none can be parsed as a
        // profile id. Benchmark stays the gate for freeze (a profile can only be frozen after a successful benchmark).
        public const string Profiles = "model-fit/profiles";
        public const string ProfilesExplore = "model-fit/profiles/explore";
        public const string ProfilesBenchmark = "model-fit/profiles/benchmark";
        public const string ProfilesFreeze = "model-fit/profiles/freeze";
        public const string ProfilesInvalidate = "model-fit/profiles/invalidate";
    }

    /// <summary>
    ///     Open Canvas (Preview) workflow builder routes and hub path. Workflows persist (encrypted graph library); runs
    ///     are one-shot, in-memory, never persisted.
    /// </summary>
    public static class Preview
    {
        // Workflow library: GET list (summaries, no graph) + POST create; individual workflow resource GET/PUT/DELETE.
        public const string Workflows = "preview/workflows";
        public const string WorkflowById = "preview/workflows/{workflowId}";

        // Execute a saved workflow by id. The literal "execute" segment follows the id so it cannot be parsed as one.
        public const string WorkflowExecute = "preview/workflows/{workflowId}/execute";

        // Execute an unsaved (inline) graph. A distinct top-level "runs/execute" literal keeps it off the workflow-id
        // surface; persists nothing.
        public const string RunExecute = "preview/runs/execute";

        // Run discovery: GET the runs this node currently knows about (live + still-replayable) and GET one by id.
        // Without these a runId that left the client's memory — a plain page reload — was unreachable forever.
        public const string Runs = "preview/runs";
        public const string RunById = "preview/runs/{runId}";

        // Cancel every live run. The literal segment sits where a run id would, but "cancel-all" is not a Guid so the
        // two never collide. Operator escape hatch for slots leaked before the abandoned-subscriber sweep reclaims them.
        public const string RunsCancelAll = "preview/runs/cancel-all";

        // Run lifecycle actions, run-scoped. Literal action segments follow the run id.
        public const string RunContinue = "preview/runs/{runId}/continue";
        public const string RunCancel = "preview/runs/{runId}/cancel";

        // SignalR push hub for run events. Full path (mapped via MapHub, not the FastEndpoints prefix), mirroring the
        // other local hubs.
        public const string Hub = "/api/local/v1/preview/hub";
    }

    /// <summary>
    ///     Local image-generation routes (jobs create/list/get/cancel, encrypted PNG retrieve, installed models) and the
    ///     progress hub path. Jobs persist; the coordinator serializes generation to one running job at a time.
    /// </summary>
    public static class Images
    {
        // Job collection (POST create, GET list) and individual job resource (GET status).
        public const string Jobs = "images/jobs";
        public const string JobById = "images/jobs/{jobId}";

        // Cancel is job-scoped; the coordinator picks clean-cancel (queued) vs kill+restart (generating) internally. The
        // literal "cancel" segment follows the job id, keeping it distinct from JobById.
        public const string JobCancel = "images/jobs/{jobId}/cancel";

        // Decrypted PNG retrieve. {imageId} is the server-generated image id (never a client-supplied path).
        public const string ImageById = "images/{imageId}";

        // Installed image-model registry (GET list).
        public const string Models = "images/models";

        // Image-model weight downloads: POST starts a detached file-set pull, GET lists every tracked download's phase
        // (Running/Completed/Cancelled/Failed) so a failure is observable instead of silent.
        public const string ModelDownloads = "images/models/downloads";

        // Cancels an in-flight file-set pull. An image model can be tens of gigabytes, so a mis-started download that
        // could not be stopped would hold the node's bandwidth and disk until it finished.
        public const string ModelDownloadCancel = "images/models/downloads/cancel";

        // Curated image-model catalog: the one-click install list, annotated with this box's hardware fit and whether
        // each entry is already installed. The literal "catalog" segment precedes nothing, so it cannot be captured by
        // ModelByName's {modelName} route (that one is DELETE-only in any case).
        public const string ModelCatalog = "images/models/catalog";

        // Hugging Face image-model repo discovery (IImageModelDiscovery search) and per-repo weight-file inspection —
        // the browse → inspect → pick pipeline that replaces hand-typing a repo id and a file name.
        public const string ModelBrowse = "images/models/browse";
        public const string ModelInspect = "images/models/inspect";

        // Removes an installed model's weights and registry entry. Without it a node that has installed several
        // multi-gigabyte file-sets has no in-app way to reclaim the disk.
        public const string ModelByName = "images/models/{modelName}";

        // Managed stable-diffusion.cpp runtime and Linux source-build orchestration.
        public const string Runtime = "images/runtime";
        public const string RuntimeEject = "images/runtime/eject";
        public const string RuntimeSourceBuild = "images/runtime/source-build";
        public const string RuntimeSourceBuildPrerequisites = "images/runtime/source-build/prerequisites";
        public const string RuntimeSourceBuildStatus = "images/runtime/source-build/status";
        public const string RuntimeSourceBuildCancel = "images/runtime/source-build/cancel";
        public const string RuntimeSourceBuildRemove = "images/runtime/source-build/remove";

        // SignalR push hub for image-job progress. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring the other local hubs. Each push carries the coarse status + seq.
        public const string Hub = "/api/local/v1/images/hub";

        // SignalR push hub for stable-diffusion.cpp source-build phase and log changes.
        public const string RuntimeSourceBuildHub = "/api/local/v1/images/runtime/source-build/hub";
    }

    /// <summary>
    ///     App self-update routes (Velopack). Status reads the cached snapshot (<c>?refresh=true</c> forces a check with a
    ///     10-minute floor); apply downloads + applies + relaunches. Desktop-mode only;
    ///     Operator-gated.
    /// </summary>
    public static class AppUpdate
    {
        public const string Status = "app-update/status";
        public const string Apply = "app-update/apply";
    }

    /// <summary>
    ///     Local knowledge-base (offline RAG) document management, search, reindex, and hub routes. Every route is
    ///     Operator-gated; none is <c>IDesktopOnlyEndpoint</c>, so all survive the headless OpenAPI regen. The document
    ///     collection route carries the multipart upload (POST) and the list (GET); the individual document resource is
    ///     GET/DELETE with the server-generated <c>{documentId}</c> (never a client-supplied path).
    /// </summary>
    public static class KnowledgeBase
    {
        // Document collection (POST multipart upload, GET list) and the individual document resource (GET detail, DELETE).
        public const string Documents = "knowledge-base/documents";
        public const string DocumentById = "knowledge-base/documents/{documentId}";

        // Re-run the ingestion pipeline for one document. The literal "reindex" segment follows the id so it cannot be
        // parsed as one.
        public const string DocumentReindex = "knowledge-base/documents/{documentId}/reindex";

        // Corpus-wide reindex of every stale-model document. A distinct top-level literal keeps it off the document-id
        // surface.
        public const string Reindex = "knowledge-base/reindex";

        // Hybrid retrieval over the indexed corpus (POST body: query + options).
        public const string Search = "knowledge-base/search";

        // Imports supported tracked/unignored files from an already-registered local Development repository.
        public const string RepositoryImport = "knowledge-base/repositories/import";

        // One-click download of the recommended cross-encoder reranker so an operator can enable KB reranking without
        // hunting for a repo/quant. Body-less POST; a distinct "reranker" literal keeps it off the document-id surface.
        public const string RerankerDownloadRecommended = "knowledge-base/reranker/download-recommended";

        // One-click download of the recommended embedding model. Same shape as the reranker route above, but this one is
        // load-bearing rather than optional: with no embedding model installed the knowledge base cannot index anything.
        public const string EmbeddingDownloadRecommended = "knowledge-base/embedding/download-recommended";

        // SignalR push hub for indexing status changes. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring the other local hubs. Each push carries the sanitized document id + status; Operator-gated because
        // subscribers see which documents are being indexed.
        public const string Hub = "/api/local/v1/knowledge-base/hub";
    }

    public static class Mcp
    {
        public const string Servers = "mcp/servers";
        public const string ServerById = "mcp/servers/{mcpServerId}";
        public const string ServerEnabled = "mcp/servers/{mcpServerId}/enabled";
        public const string ServerTools = "mcp/servers/{mcpServerId}/tools";

        // The full dynamic tool catalog (built-ins + enabled MCP tools). A distinct top-level literal so it never
        // collides with the {mcpServerId} route param under the servers surface.
        public const string ToolCatalog = "tool-catalog";

        // Inbound MCP server routes; preceding routes are outbound MCP client routes.

        /// <summary>
        ///     Operator-gated management of the single inbound bearer credential (GET reveal / POST generate /
        ///     DELETE revoke). A literal segment under <c>mcp/</c>, so it can never be parsed as an {mcpServerId}.
        /// </summary>
        public const string ServerApiKey = "mcp/server-key";

        /// <summary>
        ///     The MCP Streamable HTTP endpoint itself, mapped by <c>MapMcp</c> OUTSIDE FastEndpoints (like the SignalR
        ///     hubs) but deliberately INSIDE the <c>/api/local/v1</c> prefix so
        ///     <c>LocalApiSecurityMiddleware</c>'s loopback peer + Host + Origin gate still covers it. Moving it outside
        ///     the prefix would silently drop that gate and leave the bearer key as the only control.
        /// </summary>
        public const string ServerEndpoint = "mcp/server";
    }

    /// <summary>
    ///     Inbound OpenAI-compatible model proxy: the surface an EXTERNAL tool points at to use this node's local models
    ///     as a plain OpenAI provider, with none of the node's agent scaffolding (no persona/tools/memory/RAG).
    /// </summary>
    public static class Proxy
    {
        /// <summary>
        ///     Operator-gated management of the single inbound model-proxy bearer credential (GET status / POST generate /
        ///     DELETE revoke). A FastEndpoints route; the <c>v1/*</c> passthrough routes below are hand-mapped instead.
        /// </summary>
        public const string ApiKey = "proxy/key";

        /// <summary>
        ///     The OpenAI-compatible base an external tool configures (its <c>base_url</c>). The <c>v1/*</c> routes below
        ///     hang off it. Mapped OUTSIDE FastEndpoints (like <c>MapMcp</c>) but INSIDE the <c>/api/local/v1</c> prefix,
        ///     so <c>LocalApiSecurityMiddleware</c>'s loopback peer + Host + Origin gate still covers them. The full base
        ///     an operator hands to a client is <c>{scheme}://{host}/api/local/v1/proxy/v1</c>.
        /// </summary>
        public const string OpenAiBase = "proxy/v1";

        /// <summary>OpenAI chat-completions passthrough. Forwarded verbatim to the resolved llama-server child's own <c>/v1/chat/completions</c>.</summary>
        public const string ChatCompletions = "proxy/v1/chat/completions";

        /// <summary>OpenAI embeddings passthrough. Forwarded verbatim to the resolved llama-server child's own <c>/v1/embeddings</c>.</summary>
        public const string Embeddings = "proxy/v1/embeddings";

        /// <summary>OpenAI model list. SYNTHESIZED from the local GGUF catalog (a child only knows the one model it loaded), not a passthrough.</summary>
        public const string Models = "proxy/v1/models";
    }

    /// <summary>
    ///     Training group routes. The dataset half (definitions, datasets, samples, mocks) is declared here; the
    ///     runtime and base-artifact halves append their own constants.
    /// </summary>
    public static class Training
    {
        public const string Definitions = "training/definitions";
        public const string DefinitionById = "training/definitions/{definitionId}";
        public const string DefinitionGenerate = "training/definitions/{definitionId}/generate";
        public const string Datasets = "training/datasets";
        public const string DatasetById = "training/datasets/{datasetId}";
        public const string DatasetSamples = "training/datasets/{datasetId}/samples";
        public const string DatasetSampleById = "training/datasets/{datasetId}/samples/{sampleId}";
        public const string DatasetExport = "training/datasets/{datasetId}/export";
        public const string DatasetCancel = "training/datasets/{datasetId}/cancel";
        public const string Mocks = "training/mocks";
        public const string MockById = "training/mocks/{mockId}";
        public const string MockVerify = "training/mocks/{mockId}/verify";
        public const string DatasetGenerationHub = "/api/local/v1/training/datasets/hub";

        // Python training runtime (uv-managed venv). One machine-global runtime, so none of these are id-scoped.
        public const string RuntimeStatus = "training/runtime/status";
        public const string RuntimePrerequisites = "training/runtime/prerequisites";
        public const string RuntimeInstall = "training/runtime/install";
        public const string RuntimeRemove = "training/runtime/remove";

        /// <summary>SignalR push hub for training-runtime install phase and log changes.</summary>
        public const string RuntimeHub = "/api/local/v1/training/runtime/hub";

        // Base checkpoints downloaded from Hugging Face.
        public const string BaseArtifacts = "training/base-artifacts";
        public const string BaseArtifactById = "training/base-artifacts/{artifactId}";
        public const string BaseArtifactCancel = "training/base-artifacts/{artifactId}/cancel";
        public const string BaseArtifactLicense = "training/base-artifacts/{artifactId}/license";

        // Training runs. The queue is single-consumer, so create only enqueues — the run starts once the GPU is free.
        public const string Runs = "training/runs";
        public const string RunById = "training/runs/{runId}";
        public const string RunCancel = "training/runs/{runId}/cancel";

        /// <summary>Computed hyper-parameters plus the VRAM estimate and the licensing text the run wizard renders.</summary>
        public const string RunDefaults = "training/runs/defaults";

        /// <summary>SignalR push hub for per-run status, phase and training progress — evaluation progress rides it too.</summary>
        public const string RunHub = "/api/local/v1/training/runs/hub";

        // Evaluation runs. They ride the same single-consumer queue as training runs, so create only enqueues.
        public const string Evaluations = "training/evaluations";
        public const string EvaluationById = "training/evaluations/{evaluationId}";
        public const string EvaluationResume = "training/evaluations/{evaluationId}/resume";
        public const string EvaluationCancel = "training/evaluations/{evaluationId}/cancel";

        // Comparison reports over two evaluation runs.
        public const string Comparisons = "training/comparisons";
        public const string ComparisonById = "training/comparisons/{comparisonId}";

        /// <summary>Lineage auto-suggest: the two model names and evaluations one training run implies.</summary>
        public const string ComparisonSuggest = "training/comparisons/suggest";

        /// <summary>
        ///     Hand-off into the benchmark module: creates (or reuses) the benchmark project for one comparison and
        ///     enqueues its paired base/tuned runs. The existing deep link only SELECTS runs that already exist.
        /// </summary>
        public const string ComparisonBenchmark = "training/comparisons/{comparisonId}/benchmark";

        // Exports. Starting one and listing what a run produced are run-scoped; every action ON an artifact addresses
        // it by its own id, because an artifact outlives the export that produced it and is acted on without the run
        // in hand.
        public const string RunExports = "training/runs/{runId}/exports";
        public const string RunArtifacts = "training/runs/{runId}/artifacts";
        public const string ArtifactById = "training/artifacts/{artifactId}";
        public const string ArtifactSmoke = "training/artifacts/{artifactId}/smoke";
        public const string ArtifactPromote = "training/artifacts/{artifactId}/promote";
        public const string ArtifactQuality = "training/artifacts/{artifactId}/quality";
        public const string ArtifactQualityRevalidation = "training/artifacts/{artifactId}/quality/revalidation";
        public const string ArtifactQualityOverride = "training/artifacts/{artifactId}/quality/override";
        public const string ArtifactQualityDiscard = "training/artifacts/{artifactId}/quality/discard";
    }

    /// <summary>
    ///     Agent work sessions: the objective-scoped runs an operator starts, their lifecycle verbs, the five
    ///     sequence-filtered feeds the session view pages through, and the live-notification hub.
    ///     <para>
    ///         Approvals and <c>ask_user</c> answers deliberately have no route here — a session view embeds the
    ///         conversation the session owns and resolves both through the existing chat routes, so there is one
    ///         approval path on the node rather than two that can drift.
    ///     </para>
    /// </summary>
    public static class WorkSessions
    {
        public const string Root = "work-sessions";
        public const string ById = "work-sessions/{sessionId}";
        public const string Start = "work-sessions/{sessionId}/start";
        public const string Pause = "work-sessions/{sessionId}/pause";
        public const string Resume = "work-sessions/{sessionId}/resume";
        public const string Cancel = "work-sessions/{sessionId}/cancel";

        // The five feeds. Each takes an exclusive sinceSeq query parameter, so the client re-reads only what the hub
        // said had changed; only the event feed pages, because it is the only one that grows without bound.
        public const string Tasks = "work-sessions/{sessionId}/tasks";
        public const string Findings = "work-sessions/{sessionId}/findings";
        public const string Artifacts = "work-sessions/{sessionId}/artifacts";
        public const string Checkpoints = "work-sessions/{sessionId}/checkpoints";
        public const string Events = "work-sessions/{sessionId}/events";
        public const string ArtifactContent = "work-sessions/{sessionId}/artifacts/{artifactId}/content";

        // A user follow-up into the session's owned conversation. Not the chat hub's SendMessage: this one persists a
        // standalone user row and hands the turn to the supervisor rather than streaming an assistant reply.
        public const string Messages = "work-sessions/{sessionId}/messages";

        // SignalR notification hub. Full path (mapped via MapHub, not the FastEndpoints prefix), mirroring the others.
        public const string Hub = "/api/local/v1/work-sessions/hub";
    }

    /// <summary>
    ///     Development workflows: work items, definitions, runs and their node runs.
    ///     <para>
    ///         The whole surface is gated on <c>DevWorkflows:Enabled</c> by request-path middleware in <c>Program</c>
    ///         that answers 404 for anything under <see cref="Root" />, which is why the prefix is a constant rather
    ///         than spelled at each route. The literal first segment cannot be captured by any <c>development/…</c>
    ///         route parameter — the two families diverge at segment one.
    ///     </para>
    /// </summary>
    public static class DevelopmentWorkflows
    {
        public const string Root = "development-workflows";

        /// <summary>The work-item collection. <c>?status=</c> filters on the status the RUNTIME writes, never a client.</summary>
        public const string WorkItems = "development-workflows/work-items";

        public const string WorkItemById = "development-workflows/work-items/{workItemId}";

        /// <summary>
        ///     Run start, nested under the work item that owns it. The definition rides in the body rather than the
        ///     path because it is a per-run choice: one work item is re-runnable against a revised definition later.
        /// </summary>
        public const string WorkItemRuns = "development-workflows/work-items/{workItemId}/runs";

        /// <summary>The definition collection. <c>?includeArchived=</c> shows the ones DELETE archived.</summary>
        public const string Definitions = "development-workflows/definitions";

        public const string DefinitionById = "development-workflows/definitions/{definitionId}";

        /// <summary>
        ///     The rule-set collection. No filter: the resolver's own working set is "every enabled one", and the
        ///     management page draws the disabled ones beside them.
        /// </summary>
        public const string RuleSets = "development-workflows/rule-sets";

        /// <summary>DELETE here is a HARD delete, unlike a definition's: a node run recorded what applied, so nothing dangles.</summary>
        public const string RuleSetById = "development-workflows/rule-sets/{ruleSetId}";

        /// <summary>The run list, filtered by <c>?workItemId=</c>, <c>?status=</c> and <c>?limit=</c>.</summary>
        public const string Runs = "development-workflows/runs";

        /// <summary>
        ///     One run in full: its pinned graph, every node-run summary and the counters. This is THE fetch that
        ///     repaints the whole view, which is why there is deliberately no node-run list route — no node card ever
        ///     needs a second request.
        /// </summary>
        public const string RunById = "development-workflows/runs/{runId}";

        // The three lifecycle verbs. Each commits an intent and answers 202: the transition completes out of band on
        // the dispatcher's clock, which is exactly why the run status set carries Pausing and Cancelling.
        public const string RunPause = "development-workflows/runs/{runId}/pause";
        public const string RunResume = "development-workflows/runs/{runId}/resume";
        public const string RunCancel = "development-workflows/runs/{runId}/cancel";

        /// <summary>
        ///     The append-only event log, paged by an EXCLUSIVE <c>?sinceSeq=</c> lower bound. Sequences are strictly
        ///     increasing but NOT contiguous — the run's counter is shared with node-runs and artifacts — so a client
        ///     follows the watermark rather than counting rows.
        /// </summary>
        public const string RunEvents = "development-workflows/runs/{runId}/events";

        /// <summary>The heavier per-node drill-down: session and task ids, artifacts, applied rule sets, decisions.</summary>
        public const string NodeRunById = "development-workflows/runs/{runId}/nodes/{nodeRunId}";

        /// <summary>
        ///     The ONE decision surface. A gate's answer and a stuck node-run's intervention are the same human act, so
        ///     <c>Retry</c>, <c>Skip</c> and <c>Abandon</c> travel this route too — there is no separate retry endpoint.
        /// </summary>
        public const string NodeRunDecision = "development-workflows/runs/{runId}/nodes/{nodeRunId}/decision";

        /// <summary>
        ///     The artifact feed, <c>?sinceSeq=</c>-capable because artifact rows are append-only. Staleness flips do
        ///     NOT advance this cursor: they mutate rows without re-stamping a sequence, so they are announced on the
        ///     event feed and observed by refetching.
        /// </summary>
        public const string RunArtifacts = "development-workflows/runs/{runId}/artifacts";

        public const string RunArtifactContent = "development-workflows/runs/{runId}/artifacts/{artifactId}/content";

        /// <summary>SignalR notification hub. Full path (mapped via MapHub, not the FastEndpoints prefix).</summary>
        public const string Hub = "/api/local/v1/development-workflows/hub";
    }

    /// <summary>
    ///     Graph workflows: operator-authored definitions and the runs started from them.
    ///     <para>
    ///         The whole surface is gated on <c>GraphWorkflows:Enabled</c> by request-path middleware in <c>Program</c>
    ///         that answers 404 for anything under <see cref="Root" />, which is why the prefix is a constant rather
    ///         than spelled at each route. Later slices add the run, event, decision and hub paths under the same root,
    ///         so the gate already covers them.
    ///     </para>
    /// </summary>
    public static class GraphWorkflows
    {
        public const string Root = "graph-workflows";

        /// <summary>The definition collection: GET lists without the graph blob, POST validates the graph and stores it.</summary>
        public const string Definitions = "graph-workflows/definitions";

        /// <summary>GET / PUT with the version it was edited from / DELETE, which 409s while a live run pins the definition.</summary>
        public const string DefinitionById = "graph-workflows/definitions/{definitionId}";

        /// <summary>
        ///     Validation without saving: the editor asks the RUNTIME's own parser whether a graph would route, so the
        ///     answer it draws is the answer a run would get rather than a second implementation of the same rules.
        /// </summary>
        public const string DefinitionsValidate = "graph-workflows/definitions/validate";

        /// <summary>
        ///     The Tool node's picker feed: every tool a Tool node may actually run, already filtered server-side to
        ///     the D6 envelope by the same service the runtime invokes through, so the picker cannot offer a name the
        ///     run would then refuse.
        /// </summary>
        public const string Tools = "graph-workflows/tools";

        /// <summary>
        ///     Starts a run of one definition. 202 with the run id: the endpoint commits a durable intent and the
        ///     dispatcher advances it out of band, so the run legitimately reads <c>Pending</c> when the answer lands.
        ///     The caller's <c>requestId</c> is the idempotency key — the same one always answers with the same run.
        /// </summary>
        public const string DefinitionRuns = "graph-workflows/definitions/{definitionId}/runs";

        /// <summary>The run list, newest first.</summary>
        public const string Runs = "graph-workflows/runs";

        /// <summary>One run with its node-run summaries. No documents: those are a per-node read.</summary>
        public const string RunById = "graph-workflows/runs/{runId}";

        /// <summary>Requests a cancel. 202 like the start — live node runs drain first, so the run reads <c>Cancelling</c>.</summary>
        public const string RunCancel = "graph-workflows/runs/{runId}/cancel";

        /// <summary>One node run in full, input and output documents included. Keyed by node key, which is its identity within the run.</summary>
        public const string RunNodeByKey = "graph-workflows/runs/{runId}/nodes/{nodeKey}";

        /// <summary>
        ///     The run's event log, paged from an EXCLUSIVE watermark and capped at the configured replay limit, which
        ///     the response reports rather than leaving a client to infer from a full page.
        /// </summary>
        public const string RunEvents = "graph-workflows/runs/{runId}/events";

        /// <summary>SignalR notification hub. Full path (mapped via MapHub, not the FastEndpoints prefix).</summary>
        public const string Hub = "/api/local/v1/graph-workflows/hub";
    }

    public static class Automation
    {
        public const string Commands = "automation/commands";
        public const string CommandById = "automation/commands/{commandId}";
    }

    /// <summary>Operator-managed opaque workspace allowlist for inbound MCP delegation.</summary>
    public static class Workspaces
    {
        public const string Collection = "workspaces";
        public const string ById = "workspaces/{workspaceId}";
    }

    /// <summary>
    ///     Operator-gated management of the external integration surface: the named triggers an integrator invokes, the
    ///     <c>xeint_</c> credentials that authenticate it, the sessions those invocations own, and the executions they
    ///     produce. Ordinary FastEndpoints routes behind <c>NodeAuthorizationPolicies.Operator</c> — the surface an
    ///     integrator actually calls is <see cref="IntegrationApi" />, and the two never overlap.
    /// </summary>
    public static class Integrations
    {
        public const string Triggers = "integrations/triggers";
        public const string TriggerById = "integrations/triggers/{triggerId}";
        public const string Keys = "integrations/keys";
        public const string KeyById = "integrations/keys/{keyId}";
        public const string Sessions = "integrations/sessions";
        public const string SessionById = "integrations/sessions/{sessionId}";
        public const string Executions = "integrations/executions";
        public const string ExecutionById = "integrations/executions/{executionId}";
        public const string ExecutionEvents = "integrations/executions/{executionId}/events";
        public const string ExecutionCancel = "integrations/executions/{executionId}/cancel";
    }

    /// <summary>
    ///     The EXTERNAL integration API: what an automation, a sensor or a webhook receiver calls with an
    ///     <c>xeint_</c> bearer key. Mapped OUTSIDE FastEndpoints (like <c>MapMcp</c> and the model proxy) but
    ///     deliberately INSIDE the <c>/api/local/v1</c> prefix, so <c>LocalApiSecurityMiddleware</c>'s loopback peer +
    ///     Host + Origin gate still covers it; moving it outside the prefix would silently drop that layer and leave
    ///     the bearer key as the only control. Off the OpenAPI document for the same reason the proxy is: the bodies
    ///     are a caller contract rather than node DTOs.
    /// </summary>
    public static class IntegrationApi
    {
        public const string Invoke = "integration-api/triggers/{triggerName}/invoke";
        public const string ExecutionById = "integration-api/executions/{executionId}";
        public const string ExecutionEvents = "integration-api/executions/{executionId}/events";
        public const string ExecutionCancel = "integration-api/executions/{executionId}/cancel";
        public const string SessionById = "integration-api/sessions/{sessionId}";
    }
}
