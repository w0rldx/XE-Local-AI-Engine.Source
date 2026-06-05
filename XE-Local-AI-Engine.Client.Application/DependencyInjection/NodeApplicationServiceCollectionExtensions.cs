namespace XE_Local_AI_Engine.Client;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Connection.Implementation;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.DeadLetter.Implementation;
using XE_Local_AI_Engine.Client.Services.Embeddings;
using XE_Local_AI_Engine.Client.Services.Embeddings.Implementation;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.HostAgent;
using XE_Local_AI_Engine.Client.Services.HostAgent.Implementation;
using XE_Local_AI_Engine.Client.Services.Analysis;
using XE_Local_AI_Engine.Client.Services.Analysis.Implementation;
using XE_Local_AI_Engine.Client.Services.Insights;
using XE_Local_AI_Engine.Client.Services.Insights.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage.Implementation;
using XE_Local_AI_Engine.Client.Services.Manager;
using XE_Local_AI_Engine.Client.Services.Manager.Implementation;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Monitoring;
using XE_Local_AI_Engine.Client.Services.Monitoring.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Shutdown;
using XE_Local_AI_Engine.Client.Services.Shutdown.Implementation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Client.Services.Validation.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Ollama;
using ClientSecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

/// <summary>
///     Registers the node-local application services, persistence boundaries, model providers, and host-agent clients.
/// </summary>
public static class NodeApplicationServiceCollectionExtensions
{
    private const string UseLocalModelProviderConfigurationKey = "XE_USE_LOCAL_MODEL_PROVIDER";

    public static IHostApplicationBuilder AddNodeApplication(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<CentralPlatformOptions>()
               .Bind(configuration.GetSection(CentralPlatformOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<WorkerNodeOptions>()
               .Bind(configuration.GetSection(WorkerNodeOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<ClientSecurityOptions>()
               .Bind(configuration.GetSection(ClientSecurityOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<NodeAuthOptions>()
               .Bind(configuration.GetSection(NodeAuthOptions.SectionName))
               .ValidateDataAnnotations()
               .Validate(static options => !string.IsNullOrWhiteSpace(options.Jwt.Issuer), "NodeAuth:Jwt:Issuer is required.")
               .Validate(static options => !string.IsNullOrWhiteSpace(options.Jwt.Audience), "NodeAuth:Jwt:Audience is required.")
               .Validate(static options => options.Jwt.AccessTokenMinutes is >= 1 and <= 1440, "NodeAuth:Jwt:AccessTokenMinutes must be between 1 and 1440.")
               .Validate(static options => options.RefreshTokenDays is >= 1 and <= 365, "NodeAuth:RefreshTokenDays must be between 1 and 365.")
               .ValidateOnStart();
        builder.Services.AddOptions<CloudProviderOptions>()
               .Bind(configuration.GetSection(CloudProviderOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<NodeChatMigrationRecoveryOptions>()
               .Bind(configuration.GetSection(NodeChatMigrationRecoveryOptions.SectionName))
               .Validate(static options => options.MigrationAttemptTimeout > TimeSpan.Zero, "Migration attempt timeout must be greater than zero.")
               .Validate(static options => options.StartupLockTimeout > TimeSpan.Zero, "Startup lock timeout must be greater than zero.")
               .Validate(static options => options.StartupLockPollInterval > TimeSpan.Zero, "Startup lock poll interval must be greater than zero.")
               .ValidateOnStart();
        builder.Services.AddOptions<WorkerShutdownDrainOptions>();

        builder.Services.AddSingleton<IValidateOptions<CentralPlatformOptions>, CentralPlatformOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<WorkerNodeOptions>, WorkerNodeOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<ClientSecurityOptions>, SecurityOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<CloudProviderOptions>, CloudProviderOptionsValidator>();

        var centralPlatformBaseUrl = configuration.GetValue<string>("CentralPlatform:BaseUrl")
                                     ?? throw new InvalidOperationException("CentralPlatform:BaseUrl is required.");

        if (!centralPlatformBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (builder.Environment.IsDevelopment())
            {
                Console.Error.WriteLine("WARNING: CentralPlatform:BaseUrl is not HTTPS. Tokens may be transmitted in plaintext.");
            }
            else
            {
                throw new InvalidOperationException("CentralPlatform:BaseUrl must use HTTPS in non-development environments.");
            }
        }

        builder.Services.AddHttpClient("CentralPlatformApi", client =>
        {
            client.BaseAddress = new Uri(centralPlatformBaseUrl, UriKind.Absolute);
        }).AddStandardResilienceHandler();

        builder.Services.AddSingleton<ITokenStore, TokenStore>();
        builder.Services.AddSingleton<INodeOperatorSecretProvider, NodeOperatorSecretProvider>();
        builder.Services.AddSingleton<INodeJwtKeyProvider, NodeJwtKeyProvider>();
        builder.Services.AddSingleton<INodeTokenService, NodeTokenService>();
        builder.Services.AddScoped<INodeAuthService, NodeAuthService>();
        builder.Services.AddSingleton<NodeIdentityInitializationService>();
        builder.Services.AddSingleton<ICloudCredentialStore, CloudCredentialStore>();
        builder.Services.AddSingleton<INodeSettingsStore, NodeSettingsStore>();
        builder.Services.AddSingleton<IAzureFoundryChatClientFactory, AzureFoundryChatClientFactory>();
        builder.Services.AddSingleton<INodeKeyRegistry, NodeKeyRegistry>();
        builder.Services.AddSingleton<IPairingService, PairingService>();
        builder.Services.AddSingleton<IWorkerTokenRefreshService, WorkerTokenRefreshService>();
        builder.Services.AddSingleton<INodeBindingService, NodeBindingService>();
        builder.Services.AddSingleton<ConnectionState>();
        builder.Services.AddSingleton<IConnectionControlService, ConnectionControlService>();
        builder.Services.AddSingleton(HostAgentClientOptions.FromConfiguration(configuration));
        builder.Services.AddSingleton<IHostAgentClient, GrpcHostAgentClient>();
        builder.Services.AddSingleton(HostAgentStartupGateOptions.FromConfiguration(configuration));
        builder.Services.AddSingleton<IHostAgentReadinessClient>(sp =>
        {
            var options = sp.GetRequiredService<HostAgentStartupGateOptions>();
            return options.Enabled
                ? ActivatorUtilities.CreateInstance<GrpcHostAgentReadinessClient>(sp)
                : new DisabledHostAgentReadinessClient();
        });
        builder.Services.AddSingleton(sp => new Lazy<IHubMessageSender>(() => sp.GetRequiredService<IHubMessageSender>()));
        builder.Services.AddSingleton(sp => new Lazy<IWorkerEventDispatcher>(() => sp.GetRequiredService<IWorkerEventDispatcher>()));
        builder.Services.AddSingleton<ModelNameValidator>();
        builder.Services.AddSingleton<IRuntimePackageValidator, RuntimePackageValidator>();
        builder.Services.AddSingleton<IHostAgentManagerService, HostAgentManagerService>();
        builder.Services.AddSingleton<IEnvelopeCryptoService, EnvelopeCryptoService>();
        builder.Services.AddSingleton<IRuntimePackageEnvelopeAssembler, RuntimePackageEnvelopeAssembler>();
        builder.Services.AddSingleton<IInvocationRunner, InvocationRunner>();
        builder.Services.AddSingleton<IInvocationHistory, InvocationHistory>();
        builder.Services.AddSingleton<IWorkerEventDispatcher, WorkerEventDispatcher>();
        builder.Services.AddSingleton<ICapabilityReporter, CapabilityReporter>();
        builder.Services.AddSingleton(sp => new Lazy<ICapabilityReporter>(() => sp.GetRequiredService<ICapabilityReporter>()));
        builder.Services.AddSingleton<IDeadLetterStore, FileDeadLetterStore>();
        builder.Services.AddSingleton<INodeSqliteKeyHolder, NodeSqliteKeyHolder>();
        builder.Services.AddSingleton<NodeEncryptionSaveChangesInterceptor>();
        builder.Services.AddSingleton<NodeEncryptionMaterializationInterceptor>();
        builder.Services.AddScoped<INodeRetentionStore, NodeRetentionStore>();
        // Selected-folder store plus safe resolver. The host path is encrypted at rest; the model-facing surface sees
        // only opaque folder ids and aliases. Registration stays available while AgentHome is disabled because the
        // workspace-copy and AgentHome gateway paths both depend on it.
        builder.Services.AddScoped<INodeSelectedFolderStore, NodeSelectedFolderStore>();
        builder.Services.AddScoped<ISelectedFolderResolver, SelectedFolderResolver>();
        // Node-local agent definitions. Instructions and descriptions are encrypted at rest; the resolver/service
        // projects a bound definition into runtime-package inputs.
        builder.Services.AddScoped<IAgentDefinitionStore, AgentDefinitionStore>();
        // Node-local playbook actions. Behavior and advisory trigger conditions are encrypted at rest; enabled actions
        // are folded into the agent prompt by the resolver, while the CRUD service owns operator authoring.
        builder.Services.AddScoped<IPlaybookActionStore, PlaybookActionStore>();
        // Node-local MCP registrations. Secret-bearing args/env/description columns are encrypted at rest; the
        // connection manager reads enabled rows and the CRUD service owns registration changes.
        builder.Services.AddScoped<IMcpServerStore, McpServerStore>();
        // Model-type classification store. Persists the digest-keyed detection cache and the operator override, keyed by
        // model name (NOCASE). Unencrypted — model names/digests/capabilities/kinds are not secrets. The classification
        // service reads/writes through it to resolve the effective kind that filters the chat picker. Scoped to match
        // the scoped, DbContext-backed store.
        builder.Services.AddScoped<IModelClassificationStore, ModelClassificationStore>();
        // Feedback-insights read store. Pure analytics over node-local feedback/tool-event rows; it reads only
        // plaintext columns and writes nothing.
        builder.Services.AddScoped<IFeedbackInsightsStore, FeedbackInsightsStore>();
        // Agent-definition application layer: the resolver projects a conversation's bound definition into loopback
        // runtime-package inputs, and the service validates/orchestrates management CRUD.
        builder.Services.AddScoped<IAgentDefinitionResolver, AgentDefinitionResolver>();
        // Default-agent id memoization: resolves the seeded "Default Assistant" id once and caches it for the process
        // lifetime so the mode-off chat send/regenerate hot paths avoid a GetBySeedSlugAsync round-trip per send.
        // Singleton (it owns the cache + a fresh scope per first lookup of the scoped store).
        builder.Services.AddSingleton<IDefaultAgentProvider, DefaultAgentProvider>();
        // Orchestration resolver: compiles an orchestrator definition and topology into the loopback orchestration spec.
        builder.Services.AddScoped<IOrchestrationResolver, OrchestrationResolver>();
        builder.Services.AddScoped<IAgentDefinitionService, AgentDefinitionService>();
        // Starter-pack template catalog: loads the embedded agent-templates.seed.json once (zero runtime egress) and
        // serves the curated personas. Singleton because the catalog is immutable and read-once.
        builder.Services.AddSingleton<IAgentTemplateCatalog, AgentTemplateCatalog>();
        // Starter-pack import service: idempotent, additive import of catalog templates into ordinary chat-persona
        // definitions through the forge-proof seeded store path. Scoped to match the scoped, DbContext-backed store.
        builder.Services.AddScoped<IAgentTemplateImportService, AgentTemplateImportService>();
        // Playbook action service: validates manual authoring, owns agent existence checks, and delegates
        // persistence/versioning to the store. The resolver folds enabled actions into the prompt.
        builder.Services.AddScoped<IPlaybookActionService, PlaybookActionService>();
        // Feedback-insights service: shapes raw aggregates into the operator read model, including derived down-rate,
        // single-sample guard flags, and privacy-capped exemplars.
        builder.Services.AddScoped<IFeedbackInsightsService, FeedbackInsightsService>();
        // Analysis model options. Defaults to the node-local chat model so feedback comments are never sent to the
        // cloud chat client by fallback.
        builder.Services.AddOptions<PlaybookAnalysisOptions>()
               .Bind(builder.Configuration.GetSection(PlaybookAnalysisOptions.Section))
               .PostConfigure(analysisOptions =>
               {
                   if (string.IsNullOrWhiteSpace(analysisOptions.ModelName))
                   {
                       analysisOptions.ModelName = builder.Configuration.GetValue<string>("Ollama:ChatModel")
                                                   ?? builder.Configuration.GetValue<string>("Agent:LocalChat:DefaultModel")
                                                   ?? string.Empty;
                   }
               });
        // Analysis agent: proposes suggested actions from feedback aggregates using a node-local model only. Singleton
        // because it holds no scoped state and receives a fresh per-run chat client.
        builder.Services.AddSingleton<IPlaybookAnalysisAgent, OllamaPlaybookAnalysisAgent>();
        // Analysis orchestration: gates on the occurrence threshold, validates proposal evidence, dedupes, and writes
        // suggested actions for human review.
        builder.Services.AddScoped<IPlaybookAnalysisService, PlaybookAnalysisService>();
        // Golden conversation store. Free-text input turns, assertions, and rubrics are encrypted at rest; the eval
        // runner reads enabled rows and the CRUD service owns manual authoring.
        builder.Services.AddScoped<IGoldenConversationStore, GoldenConversationStore>();
        // Golden conversation CRUD service: validates manual authoring and ownership-guards deletes.
        builder.Services.AddScoped<IGoldenConversationService, GoldenConversationService>();
        // Eval model options. Defaults to the node-local chat model so golden text and agent output stay on-node.
        builder.Services.AddOptions<PlaybookEvalOptions>()
               .Bind(builder.Configuration.GetSection(PlaybookEvalOptions.Section))
               .PostConfigure(evalOptions =>
               {
                   if (string.IsNullOrWhiteSpace(evalOptions.ModelName))
                   {
                       evalOptions.ModelName = builder.Configuration.GetValue<string>("Ollama:ChatModel")
                                               ?? builder.Configuration.GetValue<string>("Agent:LocalChat:DefaultModel")
                                               ?? string.Empty;
                   }
               });
        // Eval judge: deterministic assertion path plus node-local judge path. Singleton because it holds no scoped
        // state and receives the per-run node-local client as a parameter.
        builder.Services.AddSingleton<IPlaybookEvalJudge, OllamaPlaybookEvalJudge>();
        // Eval orchestration: re-runs the real agent loop over the golden set, scores candidate-vs-baseline output, and
        // persists the plaintext EvalResult consumed by the promotion gate.
        builder.Services.AddScoped<IPlaybookEvalService, PlaybookEvalService>();
        // Golden harvest read boundary: reconstructs harvest candidates from an agent's thumbs-up assistant turns
        // (plaintext thumbs-up scan via parameterized raw ADO, decrypted turn content via NodeChatDbContext). Scoped to
        // match the scoped, DbContext-backed store.
        builder.Services.AddScoped<IGoldenHarvestSourceStore, GoldenHarvestSourceStore>();
        // Golden harvest options: server-side caps on candidates persisted per run and most-recent thumbs-up sources
        // scanned. No model name — harvest is deterministic and invokes no LLM, so nothing is defaulted at composition.
        builder.Services.AddOptions<GoldenHarvestOptions>()
               .Bind(builder.Configuration.GetSection(GoldenHarvestOptions.Section));
        // Golden harvest orchestration: deterministically scans thumbs-up sources, dedups against already-harvested
        // messages, and stages each fresh candidate inert via the golden CRUD service (same validation/caps/encryption).
        // Scoped to match the scoped stores/service it composes.
        builder.Services.AddScoped<IGoldenHarvestService, GoldenHarvestService>();
        // Scheduler persistence stores. Job definitions, run history, and per-run event payloads are node-local, scoped
        // to the DbContext, and encrypted at rest where the store exposes JSON payload fields.
        builder.Services.AddScoped<IScheduledJobDefinitionStore, ScheduledJobDefinitionStore>();
        builder.Services.AddScoped<IScheduledJobRunStore, ScheduledJobRunStore>();
        builder.Services.AddScoped<IScheduledJobRunEventStore, ScheduledJobRunEventStore>();
        // Model-fit llmfit persistence stores. The approved-image registry is code-seeded and operator-toggled.
        // Snapshots carry sanitized-by-default summaries; the encrypted raw output, stderr and diagnostics are exposed only
        // on the explicit operator-diagnostics read. Recommendation and benchmark rows are normalized snapshot projections.
        // Scoped to match the scoped, DbContext-backed stores.
        builder.Services.AddScoped<IApprovedUtilityImageStore, ApprovedUtilityImageStore>();
        builder.Services.AddScoped<IModelFitSnapshotStore, ModelFitSnapshotStore>();
        builder.Services.AddScoped<IModelFitRecommendationStore, ModelFitRecommendationStore>();
        builder.Services.AddScoped<IModelFitBenchmarkStore, ModelFitBenchmarkStore>();
        // Model-fit utility runner and guards. The runner is a thin HostAgent gRPC client; selection follows
        // the AgentHome Sandbox Provider config key so that the local container value selects the gRPC runner while any
        // other value including the default fake selects the deterministic fake. The resolver is the reusable
        // approved image guard the scheduler refresh path calls before a run. The request validator allowlists the intent params.
        // Every boundary carries intent only and never a raw command line. The resolver is Scoped because it depends on
        // the Scoped image store. The runner and request validator are Singletons because the runner holds a long lived
        // gRPC channel and the validator is stateless.
        builder.Services.AddSingleton<IModelFitUtilityRunner>(ModelFitUtilityRunnerSelector.Resolve);
        builder.Services.AddScoped<IApprovedImageResolver, ApprovedImageResolver>();
        builder.Services.AddSingleton<ModelFitRequestValidator>();
        // Model-fit refresh service: the single non-bypass path that runs the approved llmfit recommend image,
        // tolerantly parses the JSON and replaces the cached recommendation snapshot. Invoked only by the scheduler's
        // ModelRecommendationCheckHandler. Scoped because it depends on the Scoped resolver and DbContext-backed stores.
        builder.Services.AddScoped<IModelFitRefreshService, ModelFitRefreshService>();
        // Model-fit local-API services. The query service is a pure cache reader over the persistence stores (approved
        // images + sanitized snapshot summary + normalized recommendation rows) and takes NO dependency on the runner or
        // refresh service, so a read can never start an llmfit run. The refresh trigger is a template-guarded facade over
        // the scheduler trigger service: it fires only an existing model-recommendation-check definition and never runs
        // llmfit itself. Both are Scoped because they compose the Scoped, DbContext-backed stores / scheduler service.
        builder.Services.AddScoped<IModelFitQueryService, ModelFitQueryService>();
        builder.Services.AddScoped<IModelFitRefreshTrigger, ModelFitRefreshTrigger>();
        // Playbook relevance-retrieval ranker: the resolver/orchestration paths consult it only when an agent's
        // Enabled set exceeds the retrieval threshold and the send carries a non-blank query; below that the full static
        // prepend is used (byte-identical). The lexical ranker (deterministic, model-free, stateless) is registered
        // concretely as the fallback/disabled path; the embedding ranker is the IPlaybookRetrievalRanker — it resolves
        // the concrete lexical for graceful degradation and ranks via the node-local embedding model only when
        // EmbeddingModelName is configured. Both are Singletons (the embedding cache is a long-lived RAM-only store).
        builder.Services.AddSingleton<LexicalPlaybookRetrievalRanker>();
        builder.Services.AddSingleton<IPlaybookRetrievalRanker, EmbeddingPlaybookRetrievalRanker>();
        builder.Services.AddOptions<PlaybookRetrievalOptions>()
               .Bind(builder.Configuration.GetSection(PlaybookRetrievalOptions.Section))
               .PostConfigure(static retrievalOptions =>
               {
                   // Guard against config that would disable the gate nonsensically: a non-positive top-k or threshold
                   // is clamped to the defaults so retrieval, once engaged, always injects at least one action.
                   if (retrievalOptions.RetrievalThreshold < 0)
                   {
                       retrievalOptions.RetrievalThreshold = 0;
                   }

                   if (retrievalOptions.TopK < 1)
                   {
                       retrievalOptions.TopK = 1;
                   }

                   // The embedding cache bound floors at 1 (mirror the MaxEnabledActions clamp) so a misconfigured
                   // non-positive value cannot wedge candidate caching once the embedding ranker engages.
                   if (retrievalOptions.EmbeddingCacheMaxEntries < 1)
                   {
                       retrievalOptions.EmbeddingCacheMaxEntries = 1;
                   }
               });
        // Bounded playbook-store options: floor the enabled-action cap at 1 so a misconfigured non-positive value cannot
        // wedge every promote/manual-enable.
        builder.Services.AddOptions<PlaybookActionOptions>()
               .Bind(builder.Configuration.GetSection(PlaybookActionOptions.Section))
               .PostConfigure(static actionOptions =>
               {
                   if (actionOptions.MaxEnabledActions < 1)
                   {
                       actionOptions.MaxEnabledActions = 1;
                   }
               });
        // Cohort-monitor read store: windowed feedback counts over node-local message_feedback/tool-event rows. Pure
        // analytics, computed on read, and writes nothing.
        builder.Services.AddScoped<IPlaybookMonitorStore, PlaybookMonitorStore>();
        // Cohort-monitor service: classifies enabled actions against the epsilon/sample floor and flags flat/regressed
        // actions for human review. It never auto-disables actions and runs only from the monitor endpoint.
        builder.Services.AddScoped<IPlaybookMonitorService, PlaybookMonitorService>();
        builder.Services.AddOptions<PlaybookMonitorOptions>()
               .Bind(builder.Configuration.GetSection(PlaybookMonitorOptions.Section))
               .PostConfigure(static monitorOptions =>
               {
                   // Clamp to safe bounds: a negative epsilon would invert the dead-band, a non-positive sample floor
                   // would let a single after-enable vote draw a verdict (and a flag).
                   if (monitorOptions.ImprovementEpsilon < 0d)
                   {
                       monitorOptions.ImprovementEpsilon = 0d;
                   }

                   if (monitorOptions.MinSampleSize < 1)
                   {
                       monitorOptions.MinSampleSize = 1;
                   }
               });
        // workspace copy sensitive-file exclusion policy for the workspace copy (stateless, name-based).
        builder.Services.AddSingleton<ISensitiveFileExclusionService, SensitiveFileExclusionService>();
        builder.Services.AddSingleton<DeadLetterFlushService>();
        builder.Services.AddSingleton<IWorkerShutdownDrainService, WorkerShutdownDrainService>();
        builder.Services.AddSingleton<IOllamaModelService, OllamaModelService>();
        // Model-type classification service: resolves each model's effective kind (override ?? detected) over the
        // classification store, lazily probing /api/show and caching by digest. Scoped because it depends on the
        // scoped, DbContext-backed IModelClassificationStore (a singleton could not consume it); the singleton
        // IOllamaModelService is safe to consume from a scoped service.
        builder.Services.AddScoped<IModelClassificationService, ModelClassificationService>();
        builder.Services.AddSingleton<ILocalChatRuntimePackageBuilder, LocalChatRuntimePackageBuilder>();
        builder.Services.AddSingleton<ILocalToolOfferProvider, LocalToolOfferProvider>();
        // MCP tool extensibility. The connection manager owns the MCP client lifecycle and republishes the dynamic
        // tool snapshot into the registry consumed by offered-tool resolution. The startup connector triggers an
        // initial refresh off the hot path; the manager stays singleton because it owns long-lived connections.
        builder.Services.AddOptions<McpOptions>()
               .Bind(configuration.GetSection(McpOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<McpOptions>, McpOptionsValidator>();
        // Scheduler options: controls Quartz activation, concurrency, history retention, and QRTZ table prefix. The
        // hosted service reads Enabled before starting so a disabled scheduler never fires jobs.
        builder.Services.AddOptions<SchedulerOptions>()
               .Bind(configuration.GetSection(SchedulerOptions.Section))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<SchedulerOptions>, SchedulerOptionsValidator>();
        builder.Services.AddSingleton<IMcpClientFactory, McpClientFactory>();
        builder.Services.AddSingleton<IMcpServerConnectionManager, McpServerConnectionManager>();
        builder.Services.AddHostedService<McpServerStartupConnector>();
        // Model-fit image-reference validator (security boundary): validates that a reference is already in the
        // strict canonical repository:tag@sha256:<64 lowercase hex> form against the approved-repository allowlist, never
        // rewriting an untrusted reference into a trusted one. Stateless → singleton. The startup seeder re-validates every
        // code-defined catalog descriptor through it and skips any whose reference fails, then upserts the rest into the
        // registry (preserving the operator Enabled toggle). Hosted so it runs once off the hot path.
        builder.Services.AddSingleton<ApprovedImageReferenceValidator>();
        builder.Services.AddHostedService<ApprovedUtilityImageSeeder>();
        // MCP registration service: validates transport fields, loopback URL, and unique names, then republishes the
        // live tool snapshot after enabled-set changes.
        builder.Services.AddScoped<IMcpServerService, McpServerService>();
        // ClientLocal run_in_agent_home tool. The handler flag-gates and validates requests before delegating through
        // the AgentHome gateway to the manifest initializer, sandbox provider, and selected-folder resolver. The tool
        // stays off the distributed wire until AgentHome is enabled.
        builder.Services.AddSingleton<IAgentHomeIdentityProvider, AgentHomeIdentityProvider>();
        // Workspace copy service: selected-folder copy with exclusions, symlink-escape guard, byte budget, and git baseline.
        builder.Services.AddSingleton<IAgentHomeWorkspaceService, AgentHomeWorkspaceService>();
        // Patch export service: post-run diff of the workspace-copy baseline with changes.patch, changed-files.json, and budget guard.
        builder.Services.AddSingleton<IAgentHomePatchService, AgentHomePatchService>();
        // Memory-proposal export service: gated collection of agent-written JSONL proposals with schema validation and secret scan.
        builder.Services.AddSingleton<IAgentHomeMemoryProposalService, AgentHomeMemoryProposalService>();
        // Run-scoped JSONL logger. The AgentHome gateway constructs one per run; the logger owns redacted event output.
        builder.Services.AddTransient<IAgentHomeRunLogger, AgentHomeRunLogger>();
        // Host patch-apply service: approval-gated landing of exported changes.patch onto selected host folders.
        builder.Services.AddScoped<INodePatchApplyService, NodePatchApplyService>();
        builder.Services.AddSingleton<IAgentHomeService, AgentHomeService>();
        builder.Services.AddSingleton<IAgentHomeToolGateway, AgentHomeToolGateway>();
        builder.Services.AddSingleton<IClientLocalToolHandler, RunInAgentHomeToolHandler>();
        // Sandbox provider selection. The provider is configuration-bound and resolved once; the default is the
        // deterministic fake, while local-container selects the HostAgent-backed provider.
        builder.Services.AddOptions<SandboxOptions>()
               .Bind(configuration.GetSection(SandboxOptions.SectionName));
        // Local-container provider options. Bound and validated unconditionally; the fail-closed validator matters only
        // when the local-container provider is selected. The provider is a thin gRPC client that reuses HostAgent options.
        builder.Services.AddOptions<LocalContainerOptions>()
               .Bind(configuration.GetSection(LocalContainerOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<LocalContainerOptions>, LocalContainerOptionsValidator>();
        builder.Services.AddSingleton<ISandboxRuntimeProvider>(SandboxProviderSelector.Resolve);
        // AgentHome layout initializer. Materializes the worker-local /agent-home tree idempotently and can run while
        // AgentHome itself is disabled.
        builder.Services.AddOptions<AgentHomeOptions>()
               .Bind(configuration.GetSection(AgentHomeOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<AgentHomeOptions>, AgentHomeOptionsValidator>();
        builder.Services.AddSingleton<IAgentHomeManifestService, AgentHomeManifestService>();
        builder.Services.AddSingleton<NodeChatPersistenceWriter>();
        builder.Services.AddSingleton<INodeChatPersistenceService, NodeChatPersistenceService>();
        builder.Services.AddSingleton<INodeChatInvocationPump, NodeChatInvocationPump>();
        builder.Services.AddSingleton<INodeChatRemotePersistenceCoordinator, NodeChatRemotePersistenceCoordinator>();
        builder.Services.AddSingleton<INodeChatMutationGuard, NodeChatMutationGuard>();
        builder.Services.AddSingleton<INodeChatStreamCancellationRegistry, NodeChatStreamCancellationRegistry>();
        builder.Services.AddSingleton<IInvocationResumeRegistry, InvocationResumeRegistry>();
        builder.Services.AddScoped<INodeChatStreamService, NodeChatStreamService>();
        builder.Services.AddScoped<INodeChatRegenerationService, NodeChatRegenerationService>();
        builder.Services.AddSingleton<NodeChatRestartRecoveryService>();
        builder.Services.AddSingleton<ILocalEmbeddingService, LocalEmbeddingService>();
        builder.Services.AddSingleton<WorkerHubConnection>(sp =>
        {
            var connection = ActivatorUtilities.CreateInstance<WorkerHubConnection>(sp);
            var dispatcher = new Lazy<IWorkerEventDispatcher>(() => sp.GetRequiredService<IWorkerEventDispatcher>());
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("WorkerHubConnectionEventBindings");

            connection.InvocationAssignedReceived += (_, args) =>
                DispatchSafely(dispatcher.Value.DispatchInvocationAssignedV2Async(args.Envelope), logger, nameof(IWorkerEventDispatcher.DispatchInvocationAssignedV2Async));
            connection.ToolCallResultReceived += (_, args) =>
                DispatchSafely(dispatcher.Value.DispatchToolCallResultAsync(args.ToolCallResult), logger, nameof(IWorkerEventDispatcher.DispatchToolCallResultAsync));
            connection.DisconnectRequestedReceived += (_, args) =>
                DispatchSafely(dispatcher.Value.DispatchDisconnectRequestedAsync(args.DisconnectRequest), logger, nameof(IWorkerEventDispatcher.DispatchDisconnectRequestedAsync));
            connection.ApprovalResolvedReceived += (_, args) =>
                DispatchSafely(dispatcher.Value.DispatchApprovalResolvedAsync(args.ApprovalResolution), logger, nameof(IWorkerEventDispatcher.DispatchApprovalResolvedAsync));
            connection.InvocationCancelledReceived += (_, args) =>
                DispatchSafely(dispatcher.Value.DispatchInvocationCancelledAsync(args.Cancellation), logger, nameof(IWorkerEventDispatcher.DispatchInvocationCancelledAsync));

            return connection;
        });
        builder.Services.AddSingleton<IWorkerHubConnection>(sp => sp.GetRequiredService<WorkerHubConnection>());
        builder.Services.AddSingleton<IHubMessageSender>(sp => sp.GetRequiredService<WorkerHubConnection>());
        builder.Services.AddSingleton<ICertPinStore, CertPinStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<NodeChatMigrationRecoveryService>();

        builder.Services.AddDbContext<NodeChatDbContext>((serviceProvider, options) =>
        {
            var connectionString = configuration.GetConnectionString("node-sqlite")
                                   ?? throw new InvalidOperationException("Connection string 'node-sqlite' is required.");

            options.UseSqlite(connectionString)
                   .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                   .AddInterceptors(serviceProvider.GetRequiredService<NodeEncryptionSaveChangesInterceptor>(),
                       serviceProvider.GetRequiredService<NodeEncryptionMaterializationInterceptor>());
        });

        builder.Services.AddDbContext<NodeIdentityDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("node-sqlite")
                                   ?? throw new InvalidOperationException("Connection string 'node-sqlite' is required.");

            options.UseSqlite(connectionString,
                sqlite => sqlite.MigrationsHistoryTable(NodeIdentityDbContext.IdentityMigrationsHistoryTable));
        });

        builder.AddOllamaApiClient("embeddings")
               .AddEmbeddingGenerator();

        builder.Services.AddOllamaLocalModelProvider(_ =>
        {
            var chatConnectionSettings = ResolveChatConnectionSettings(configuration);
            return new OllamaLocalModelProviderRegistration(chatConnectionSettings.Endpoint, chatConnectionSettings.Model);
        });
        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            var credentialStore = sp.GetRequiredService<ICloudCredentialStore>();
            var credentials = credentialStore.LoadAsync().GetAwaiter().GetResult();

            if (credentials is not null
                && string.Equals(credentials.ProviderName, CloudProviderOptions.ProviderAzureFoundry, StringComparison.OrdinalIgnoreCase))
            {
                return sp.GetRequiredService<IAzureFoundryChatClientFactory>().Create(credentials);
            }

            var chatConnectionSettings = ResolveChatConnectionSettings(configuration);
            if (UseLocalModelProvider(configuration))
            {
                return sp.GetRequiredService<ILocalModelProvider>().CreateChatClient(new LocalModelSelection
                {
                    ModelName = chatConnectionSettings.Model,
                    ProviderName = OllamaLocalModelProvider.OllamaProviderName
                });
            }

            return sp.GetRequiredService<IOllamaApiClient>() as IChatClient
                   ?? throw new InvalidOperationException("The configured local Ollama client must implement IChatClient.");
        });

        builder.Services.AddLocalAiAgentRuntime(builder.Configuration);

        return builder;
    }

    private static void DispatchSafely(Task dispatchTask, ILogger logger, string operationName)
    {
        ArgumentNullException.ThrowIfNull(dispatchTask);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        _ = dispatchTask.ContinueWith(static (task, state) =>
            {
                var (continuationLogger, dispatchOperationName) = ((ILogger Logger, string OperationName))state!;

                if (task.IsFaulted)
                {
                    continuationLogger.LogError(task.Exception, "Unhandled worker hub event dispatch failure during {OperationName}.", dispatchOperationName);
                }
            },
            (Logger: logger, OperationName: operationName),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static ChatConnectionSettings ResolveChatConnectionSettings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("chat");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var connectionStringBuilder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };

            if (connectionStringBuilder.TryGetValue("Endpoint", out var endpointValue)
                && endpointValue is string endpoint
                && Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
                && connectionStringBuilder.TryGetValue("Model", out var modelValue)
                && modelValue is string model
                && !string.IsNullOrWhiteSpace(model))
            {
                return new ChatConnectionSettings(endpointUri, model);
            }
        }

        var fallbackEndpoint = configuration.GetValue<string>("Ollama:Endpoint") ?? "http://127.0.0.1:11434";
        var fallbackModel = configuration.GetValue<string>("Ollama:ChatModel")
                            ?? configuration.GetValue<string>("Agent:LocalChat:DefaultModel")
                            ?? throw new InvalidOperationException("Agent:LocalChat:DefaultModel is required.");

        return new ChatConnectionSettings(new Uri(fallbackEndpoint, UriKind.Absolute), fallbackModel);
    }

    private static bool UseLocalModelProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.GetValue<bool>(UseLocalModelProviderConfigurationKey);
    }

    private sealed record ChatConnectionSettings(Uri Endpoint, string Model);
}
