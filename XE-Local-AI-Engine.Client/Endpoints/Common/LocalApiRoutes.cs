namespace XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>
///     Route constants for the node-local HTTP and hub API surface.
/// </summary>
public static class LocalApiRoutes
{
    public const string Prefix = "api/local/v1";

    /// <summary>
    ///     Diagnostic and framework-probe endpoints.
    /// </summary>
    public static class ApiFoundation
    {
        public const string ValidationProblemProbe = "diagnostics/validation-probe";
        public const string UnhandledExceptionProbe = "diagnostics/exception-probe";
    }

    /// <summary>
    ///     Node-operator authentication endpoints.
    /// </summary>
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

    /// <summary>
    ///     Local chat conversation and streaming routes.
    /// </summary>
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
    }

    /// <summary>
    ///     Worker-node binding routes.
    /// </summary>
    public static class NodeBinding
    {
        public const string Start = "binding/start";
        public const string Poll = "binding/poll";
        public const string Cancel = "binding/cancel";
    }

    /// <summary>
    ///     Central-platform connection control routes.
    /// </summary>
    public static class Connection
    {
        public const string Status = "connection";
        public const string Connect = "connection/connect";
        public const string Disconnect = "connection/disconnect";
        public const string EnableAutoConnect = "connection/auto-connect/enable";
        public const string DisableAutoConnect = "connection/auto-connect/disable";
    }

    /// <summary>
    ///     Node settings routes.
    /// </summary>
    public static class NodeSettings
    {
        public const string Settings = "node-settings";
    }

    /// <summary>
    ///     Cloud-provider settings routes.
    /// </summary>
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
    ///     Client voice (TTS) runtime routes. The manifest is config-only (allowed models, voice profiles, feature flag,
    ///     integrity hashes, download URLs) — the backend serves no audio.
    /// </summary>
    public static class Voice
    {
        public const string Manifest = "voice/manifest";
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

    /// <summary>
    ///     Local model management routes.
    /// </summary>
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
    }

    /// <summary>
    ///     Invocation monitor routes.
    /// </summary>
    public static class Invocations
    {
        public const string Monitor = "invocations";
    }

    /// <summary>
    ///     Agent definition, playbook, evaluation, and monitoring routes.
    /// </summary>
    public static class Agents
    {
        public const string Definitions = "agents";
        public const string DefinitionById = "agents/{agentDefinitionId}";

        // Distinct literal segment under the agents surface so it cannot collide with DefinitionById.
        public const string ToolCapableModels = "agents/tool-capable-models";

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
    }

    /// <summary>
    ///     Scheduler management, run history, cancellation, and hub routes.
    /// </summary>
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

        // SignalR push hub for GGUF download status changes. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring the other local hubs. Replaces the per-second downloads poll; each push carries the sanitized status.
        public const string DownloadHub = "/api/local/v1/model-fit/gguf/downloads/hub";

        // Running llama-server processes derived from the supervisor health snapshot; eject tree-kills one.
        public const string Running = "model-fit/running";
        public const string RunningEject = "model-fit/running/eject";

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

        // Image-model weight download (POST start). Fire-and-forget file-set pull; presence surfaces via Models.
        public const string ModelDownloads = "images/models/downloads";

        // SignalR push hub for image-job progress. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring the other local hubs. Each push carries the coarse status + seq.
        public const string Hub = "/api/local/v1/images/hub";
    }

    /// <summary>
    ///     GitHub App device-flow sign-in routes for app self-update. The device_code (secret polling credential) and the
    ///     access token NEVER appear in any of these contracts — start returns only the user code + verification URI, and
    ///     status/poll report presence + login only. Desktop-mode only; Operator-gated.
    /// </summary>
    public static class GitHubAuth
    {
        public const string Start = "github-auth/start";
        public const string Poll = "github-auth/poll";
        public const string Status = "github-auth/status";
        public const string SignOut = "github-auth/sign-out";
    }

    /// <summary>
    ///     App self-update routes (Velopack). Status reads the cached snapshot (<c>?refresh=true</c> forces a check with a
    ///     60s floor); apply downloads + applies + relaunches. Never exposes the GitHub token. Desktop-mode only;
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

        // SignalR push hub for indexing status changes. Full path (mapped via MapHub, not the FastEndpoints prefix),
        // mirroring the other local hubs. Each push carries the sanitized document id + status; Operator-gated because
        // subscribers see which documents are being indexed.
        public const string Hub = "/api/local/v1/knowledge-base/hub";
    }

    /// <summary>
    ///     MCP server registration and tool-catalog routes.
    /// </summary>
    public static class Mcp
    {
        public const string Servers = "mcp/servers";
        public const string ServerById = "mcp/servers/{mcpServerId}";
        public const string ServerEnabled = "mcp/servers/{mcpServerId}/enabled";
        public const string ServerTools = "mcp/servers/{mcpServerId}/tools";

        // The full dynamic tool catalog (built-ins + enabled MCP tools). A distinct top-level literal so it never
        // collides with the {mcpServerId} route param under the servers surface.
        public const string ToolCatalog = "tool-catalog";
    }
}
