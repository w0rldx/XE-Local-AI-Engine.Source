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
///     Represents node application service collection extensions.
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
        // selected-folder selected-folder store plus the safe resolver. The store persists folders in node SQLite with the
        // host path encrypted at rest by the node encryption interceptors. The resolver owns alias normalization, host
        // path validation, and exposes only the opaque id and alias to the model. Registration stays reachable even
        // when AgentHome is disabled; workspace copy (workspace copy) and the tool (AgentHome gateway) consume it.
        builder.Services.AddScoped<INodeSelectedFolderStore, NodeSelectedFolderStore>();
        builder.Services.AddScoped<ISelectedFolderResolver, SelectedFolderResolver>();
        // Loop P3 agent-definition store. Persists node-local agent definitions with Instructions/Description
        // encrypted at rest by the node encryption interceptors; the application-layer resolver/service (Lane 2/3)
        // consumes it to project a bound definition into the existing runtime package envelope.
        builder.Services.AddScoped<IAgentDefinitionStore, AgentDefinitionStore>();
        // Playbook P1 action store. Persists node-local playbook actions bound to an agent definition with the injected
        // Behavior + the advisory TriggerCondition encrypted at rest by the node encryption interceptors; the resolver
        // (Lane 2) folds enabled actions into the agent's system prompt, and the CRUD service (Lane 3) orchestrates
        // authoring. Scoped to match the scoped, DbContext-backed store.
        builder.Services.AddScoped<IPlaybookActionStore, PlaybookActionStore>();
        // Loop P4 MCP server registration store. Persists node-local MCP server registrations with the secret-bearing
        // args/env/description columns encrypted at rest by the node encryption interceptors; the connection manager
        // (Lane 2) reads only enabled rows, and the CRUD service (Lane 3) orchestrates registration. Scoped to match
        // the scoped, DbContext-backed store.
        builder.Services.AddScoped<IMcpServerStore, McpServerStore>();
        // Model-type classification store. Persists the digest-keyed detection cache and the operator override, keyed by
        // model name (NOCASE). Unencrypted — model names/digests/capabilities/kinds are not secrets. The classification
        // service reads/writes through it to resolve the effective kind that filters the chat picker. Scoped to match
        // the scoped, DbContext-backed store.
        builder.Services.AddScoped<IModelClassificationStore, ModelClassificationStore>();
        // Playbook P2 feedback-insights read store. Read-only aggregate over the node-local message_feedback rows
        // (joined to conversations.agent_definition_id + tool_events) — pure analytics, touches only plaintext
        // columns, writes nothing. Scoped to match the scoped, DbContext-backed store.
        builder.Services.AddScoped<IFeedbackInsightsStore, FeedbackInsightsStore>();
        // Loop P3 application layer over the store: the resolver projects a conversation's bound definition into the
        // loopback runtime-package inputs (consumed by the stream/regeneration paths), and the service validates +
        // orchestrates CRUD for the management endpoints. Both are scoped to match the scoped, DbContext-backed store.
        builder.Services.AddScoped<IAgentDefinitionResolver, AgentDefinitionResolver>();
        // Loop P5 orchestration resolver: a sibling of IAgentDefinitionResolver that compiles a Kind=Orchestrator
        // definition + its topology into the loopback orchestration spec (consumed by the stream/regeneration paths).
        // Scoped to match the scoped, DbContext-backed store it reads participants from.
        builder.Services.AddScoped<IOrchestrationResolver, OrchestrationResolver>();
        builder.Services.AddScoped<IAgentDefinitionService, AgentDefinitionService>();
        // Playbook P1 application service: validates manual playbook authoring (Behavior required, owning agent must
        // exist, P1-only Enabled/Disabled + Manual) and delegates persistence/versioning to the store. Scoped to match
        // the scoped, DbContext-backed stores it composes. The resolver below folds the enabled actions into the prompt.
        builder.Services.AddScoped<IPlaybookActionService, PlaybookActionService>();
        // Playbook P2 feedback-insights application service: shapes the raw store aggregate into the operator read
        // model (derived down-rate, the never-act-on-n=1 threshold flag, privacy-capped/truncated exemplars).
        // Read-only analytics; scoped to match the scoped, DbContext-backed store it reads.
        builder.Services.AddScoped<IFeedbackInsightsService, FeedbackInsightsService>();
        // Playbook P3 analysis options: the node-local model used to read feedback comments. Defaults to the node's
        // configured chat model when unset, so analysis never silently picks the cloud chat client (the comments stay
        // on-node — §7 privacy).
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
        // Playbook P3 analysis agent (the AI surface): proposes Suggested actions from the P2 aggregate using a
        // node-local model only. Singleton — it holds no scoped state and resolves a fresh per-run chat client.
        builder.Services.AddSingleton<IPlaybookAnalysisAgent, OllamaPlaybookAnalysisAgent>();
        // Playbook P3 analysis orchestration: reads the P2 aggregate, gates on the occurrence threshold, validates
        // each proposal's evidence, dedupes, and writes Suggested actions for human review. Scoped to match the
        // scoped services it composes.
        builder.Services.AddScoped<IPlaybookAnalysisService, PlaybookAnalysisService>();
        // Playbook P4 golden conversation store. Persists node-local golden cases bound to an agent definition with the
        // InputTurns/Assertion/Rubric free text encrypted at rest by the node encryption interceptors; the eval runner
        // reads enabled rows, and the CRUD service (below) orchestrates manual authoring. Scoped to match the scoped,
        // DbContext-backed store.
        builder.Services.AddScoped<IGoldenConversationStore, GoldenConversationStore>();
        // Playbook P4 golden CRUD service: validates manual authoring (non-blank Title, existing owning agent, non-empty
        // InputTurns, at least one of {Assertion, Rubric}) and ownership-guards delete. Scoped to match the scoped store.
        builder.Services.AddScoped<IGoldenConversationService, GoldenConversationService>();
        // Playbook P4 eval options: the node-local model used to re-run the agent loop + score judge-path cases. Defaults
        // to the node's configured chat model when unset, so the eval never silently picks the cloud chat client (golden
        // text + agent output stay on-node — §9 privacy).
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
        // Playbook P4 eval judge (the scoring surface): deterministic assertion path + node-local judge path. Singleton
        // like the analysis agent — it holds no scoped state and receives the per-run node-local client as a parameter.
        builder.Services.AddSingleton<IPlaybookEvalJudge, OllamaPlaybookEvalJudge>();
        // Playbook P4 eval orchestration (the gate's evidence): re-runs the real agent loop over the golden set with the
        // candidate vs baseline prompt, scores each case, and persists the plaintext EvalResult the promote gate reads.
        // Scoped to match the scoped stores/services it composes. The .AI.Agent runner is registered in the agent runtime.
        builder.Services.AddScoped<IPlaybookEvalService, PlaybookEvalService>();
        // Golden harvest read boundary: reconstructs harvest candidates from an agent's thumbs-up assistant turns
        // (plaintext thumbs-up scan via parameterized raw ADO, decrypted turn content via NodeChatDbContext). Scoped to
        // match the scoped, DbContext-backed store.
        builder.Services.AddScoped<IGoldenHarvestSourceStore, GoldenHarvestSourceStore>();
        // Golden harvest options: server-side caps on candidates persisted per run and most-recent thumbs-up sources
        // scanned. No model name — harvest invokes no LLM (deterministic, D1), so nothing is defaulted at composition.
        builder.Services.AddOptions<GoldenHarvestOptions>()
               .Bind(builder.Configuration.GetSection(GoldenHarvestOptions.Section));
        // Golden harvest orchestration (deterministic, no model — D1): scans thumbs-up sources, dedups against already-
        // harvested messages, and stages each fresh candidate inert via the golden CRUD service (same validation/caps/
        // encryption). Scoped to match the scoped stores/service it composes.
        builder.Services.AddScoped<IGoldenHarvestService, GoldenHarvestService>();
        // Marker 1 scheduler persistence stores. Persist node-local job definitions, run history, and per-run events
        // with ParameterJson/DetailsJson/DataJson encrypted at rest by the node encryption interceptors. Scoped to
        // match the scoped, DbContext-backed stores that compose them. No Quartz NuGet package until Marker 2.
        builder.Services.AddScoped<IScheduledJobDefinitionStore, ScheduledJobDefinitionStore>();
        builder.Services.AddScoped<IScheduledJobRunStore, ScheduledJobRunStore>();
        builder.Services.AddScoped<IScheduledJobRunEventStore, ScheduledJobRunEventStore>();
        // Marker 1 model-fit llmfit persistence stores. The approved-image registry is code-seeded and operator-toggled.
        // Snapshots carry sanitized-by-default summaries; the encrypted raw output, stderr and diagnostics are exposed only
        // on the explicit operator-diagnostics read. Recommendation and benchmark rows are normalized snapshot projections.
        // Scoped to match the scoped, DbContext-backed stores. No endpoints, HostAgent or React until later markers.
        builder.Services.AddScoped<IApprovedUtilityImageStore, ApprovedUtilityImageStore>();
        builder.Services.AddScoped<IModelFitSnapshotStore, ModelFitSnapshotStore>();
        builder.Services.AddScoped<IModelFitRecommendationStore, ModelFitRecommendationStore>();
        builder.Services.AddScoped<IModelFitBenchmarkStore, ModelFitBenchmarkStore>();
        // Marker 2 model-fit utility runner and guards. The runner is a thin HostAgent gRPC client; selection follows
        // the AgentHome Sandbox Provider config key so that the local container value selects the gRPC runner while any
        // other value including the default fake selects the deterministic fake. The resolver is the reusable
        // approved image guard that Marker 3 calls before a run. The request validator allowlists the intent params.
        // Every boundary carries intent only and never a raw command line. The resolver is Scoped because it depends on
        // the Scoped image store. The runner and request validator are Singletons because the runner holds a long lived
        // gRPC channel and the validator is stateless. No endpoints, scheduler handler or React until later markers.
        builder.Services.AddSingleton<IModelFitUtilityRunner>(ModelFitUtilityRunnerSelector.Resolve);
        builder.Services.AddScoped<IApprovedImageResolver, ApprovedImageResolver>();
        builder.Services.AddSingleton<ModelFitRequestValidator>();
        // Marker 3 model-fit refresh service: the single non-bypass path that runs the approved llmfit recommend image,
        // tolerantly parses the JSON and replaces the cached recommendation snapshot. Invoked only by the scheduler's
        // ModelRecommendationCheckHandler. Scoped because it depends on the Scoped resolver and DbContext-backed stores.
        builder.Services.AddScoped<IModelFitRefreshService, ModelFitRefreshService>();
        // Marker 4 model-fit local-API services. The query service is a pure cache reader over the M1 stores (approved
        // images + sanitized snapshot summary + normalized recommendation rows) and takes NO dependency on the runner or
        // refresh service, so a read can never start an llmfit run. The refresh trigger is a template-guarded facade over
        // the scheduler trigger service: it fires only an existing model-recommendation-check definition and never runs
        // llmfit itself. Both are Scoped because they compose the Scoped, DbContext-backed stores / scheduler service.
        builder.Services.AddScoped<IModelFitQueryService, ModelFitQueryService>();
        builder.Services.AddScoped<IModelFitRefreshTrigger, ModelFitRefreshTrigger>();
        // Playbook P5 relevance-retrieval ranker: the resolver/orchestration paths consult it only when an agent's
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
        // Playbook P5 bounded-store options: the hard cap on Enabled actions per agent. PostConfigure floors the cap at
        // 1 so a misconfigured non-positive value cannot wedge every promote/manual-enable.
        builder.Services.AddOptions<PlaybookActionOptions>()
               .Bind(builder.Configuration.GetSection(PlaybookActionOptions.Section))
               .PostConfigure(static actionOptions =>
               {
                   if (actionOptions.MaxEnabledActions < 1)
                   {
                       actionOptions.MaxEnabledActions = 1;
                   }
               });
        // Playbook P5 cohort-monitor read store: windowed feedback counts over the node-local message_feedback rows
        // (joined to conversations.agent_definition_id + tool_events for the facet) — pure analytics, computed on read,
        // writes nothing. Scoped to match the scoped, DbContext-backed store, like FeedbackInsightsStore.
        builder.Services.AddScoped<IPlaybookMonitorStore, PlaybookMonitorStore>();
        // Playbook P5 cohort-monitor application service: classifies each Enabled action's before/after down-rate against
        // the epsilon + minimum-sample floor and flags Flat/Regressed for human review (never auto-disable). Invoked off
        // the hot path by the monitor endpoint only. Scoped to match the scoped store it composes.
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
        // Loop P4 MCP tool extensibility. The connection manager owns the MCP client lifecycle and republishes the
        // dynamic tool snapshot into the MCP tool registry that the offer provider reads, where that registry is wired
        // in AddLocalAiAgentRuntime. The startup connector triggers an initial refresh off the hot path. Both are
        // singletons because the manager holds long-lived connections, and the CRUD service refreshes after any
        // change to the enabled set.
        builder.Services.AddOptions<McpOptions>()
               .Bind(configuration.GetSection(McpOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<McpOptions>, McpOptionsValidator>();
        // Marker 1 scheduler options. Controls whether the Quartz scheduler is active, concurrency, history retention,
        // and the embedded QRTZ table prefix. Validated on start; the Quartz hosted service (Marker 2) reads Enabled
        // before starting so a disabled scheduler never fires jobs.
        builder.Services.AddOptions<SchedulerOptions>()
               .Bind(configuration.GetSection(SchedulerOptions.Section))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<SchedulerOptions>, SchedulerOptionsValidator>();
        builder.Services.AddSingleton<IMcpClientFactory, McpClientFactory>();
        builder.Services.AddSingleton<IMcpServerConnectionManager, McpServerConnectionManager>();
        builder.Services.AddHostedService<McpServerStartupConnector>();
        // Marker 1 model-fit image-reference validator (security boundary): validates that a reference is already in the
        // strict canonical repository:tag@sha256:<64 lowercase hex> form against the approved-repository allowlist, never
        // rewriting an untrusted reference into a trusted one. Stateless → singleton. The startup seeder re-validates every
        // code-defined catalog descriptor through it and skips any whose reference fails, then upserts the rest into the
        // registry (preserving the operator Enabled toggle). Hosted so it runs once off the hot path.
        builder.Services.AddSingleton<ApprovedImageReferenceValidator>();
        builder.Services.AddHostedService<ApprovedUtilityImageSeeder>();
        // Lane 3 application layer over the store: validates registrations (transport-specific fields, loopback URL,
        // unique Name) and re-publishes the live tool snapshot via the connection manager after any change to the
        // enabled set. Scoped to match the scoped, DbContext-backed store.
        builder.Services.AddScoped<IMcpServerService, McpServerService>();
        // ClientLocal tool run_in_agent_home. AgentHome gateway replaces the pending placeholder with the real
        // fake-backed gateway: the handler still flag-gates and §7-validates, then delegates through the gateway to
        // IAgentHomeService, which drives the manifest initializer, the sandbox provider (the fake by default), and
        // the selected-folder resolver. The tool stays off the distributed wire (server seed inactive) until the AgentHome gateway is enabled.
        builder.Services.AddSingleton<IAgentHomeIdentityProvider, AgentHomeIdentityProvider>();
        // workspace copy workspace copy: PrepareAsync delegates the selected-folder copy (exclusions, symlink-escape guard,
        // byte budget, git baseline) to this stateless service.
        builder.Services.AddSingleton<IAgentHomeWorkspaceService, AgentHomeWorkspaceService>();
        // patch export patch export: RunAsync delegates the post-run diff of the workspace copy baseline (changes.patch +
        // changed-files.json, MaxPatchBytes budget) to this stateless service.
        builder.Services.AddSingleton<IAgentHomePatchService, AgentHomePatchService>();
        // memory-proposal export memory proposal export: RunAsync delegates the gated collect of the agent-written JSONL proposals
        // (schema validation + secret scan) to this stateless service.
        builder.Services.AddSingleton<IAgentHomeMemoryProposalService, AgentHomeMemoryProposalService>();
        // run logger run-scoped JSONL logger. the run gateway (AgentHome gateway) constructs one per run via the factory; base
        // JSONL logging + redaction contract is the MVP scope. OTel meters and list-runs endpoint are deferred.
        builder.Services.AddTransient<IAgentHomeRunLogger, AgentHomeRunLogger>();
        // host patch apply host patch apply: a user-driven, approval-gated action that lands exported changes.patch
        // onto the host selected folders. Scoped because it depends on the Scoped ISelectedFolderResolver.
        builder.Services.AddScoped<INodePatchApplyService, NodePatchApplyService>();
        builder.Services.AddSingleton<IAgentHomeService, AgentHomeService>();
        builder.Services.AddSingleton<IAgentHomeToolGateway, AgentHomeToolGateway>();
        builder.Services.AddSingleton<IClientLocalToolHandler, RunInAgentHomeToolHandler>();
        // sandbox provider abstraction sandbox provider abstraction. Selection is configuration-bound and restart-required (resolved once
        // as a singleton). The MVP default is the deterministic fake; local-container sandbox adds "local-container".
        builder.Services.AddOptions<SandboxOptions>()
               .Bind(configuration.GetSection(SandboxOptions.SectionName));
        // local-container sandbox local-container provider options. Bound + validated unconditionally; the validator is
        // fail-closed, but the running default stays the fake (D8), so invalid LocalContainer config only matters once
        // the "local-container" provider is selected. The provider is a thin gRPC client and reuses HostAgentClientOptions.
        builder.Services.AddOptions<LocalContainerOptions>()
               .Bind(configuration.GetSection(LocalContainerOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<LocalContainerOptions>, LocalContainerOptionsValidator>();
        builder.Services.AddSingleton<ISandboxRuntimeProvider>(SandboxProviderSelector.Resolve);
        // layout initializer AgentHome layout initializer. Materializes the worker-local /agent-home tree (idempotent,
        // self-healing, owner-mismatch-recovering). Wired but unreached in production until AgentHome gateway swaps the
        // pending gateway for a fake-backed one; the layout itself can initialize while AgentHome:Enabled=false.
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
