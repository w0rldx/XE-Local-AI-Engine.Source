namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Caching.Memory;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Automation;
using XE_Local_AI_Engine.Client.Services.Automation.Implementation;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;
using XE_Local_AI_Engine.Client.Services.Insights;
using XE_Local_AI_Engine.Client.Services.Insights.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

internal static class AddNodeWorkspaceAndAgentsExtensions
{
    public static IHostApplicationBuilder AddNodeWorkspaceAndAgents(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Selected-folder store plus safe resolver: the host path is encrypted at rest and the model-facing surface sees only opaque
        // folder ids and aliases. Registered even while AgentHome is disabled, because workspace copy and the gateway both need it.
        builder.Services.AddScoped<INodeSelectedFolderStore, NodeSelectedFolderStore>();
        builder.Services.AddScoped<ISelectedFolderResolver, SelectedFolderResolver>();
        builder.Services.AddScoped<IWorkspaceRevocationService, WorkspaceRevocationService>();
        // Node-local agent definitions. Instructions and descriptions are encrypted at rest; the resolver/service
        // projects a bound definition into runtime-package inputs.
        builder.Services.AddScoped<IAgentDefinitionStore, AgentDefinitionStore>();
        // Node-local agent skill library. Skill description and SKILL.md body are encrypted at rest; the resolver loads an agent's
        // enabled skills for the factory to attach via MAF progressive disclosure, the CRUD service owns authoring. Scoped like the store.
        builder.Services.AddScoped<IAgentSkillStore, AgentSkillStore>();
        // Node-local custom tool library. The model-facing description and the kind-specific config (which carries the secret header
        // and env values) are encrypted at rest; the store owns id/version/timestamp stamping and the content-affecting version bump.
        builder.Services.AddScoped<ICustomToolStore, CustomToolStore>();
        // Custom-tool executors + catalog: the catalog reads the store live per turn (no cache) and hands the resolver an executable
        // already floored in ApprovalRequiredAIFunction. HttpFetch pins an SSRF-validated address and refuses redirects; Command runs scrubbed, timed, tree-killed, capped and throttled.
        builder.Services.AddHttpClient(HttpFetchExecutor.HttpClientName)
               .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
               {
                   AllowAutoRedirect = false,
                   UseCookies = false,
                   // Never route through an ambient/system HTTP proxy: the ConnectCallback would validate and dial the PROXY endpoint
                   // while the proxy resolves the hostname, letting a name it maps to a private address bypass the SSRF denylist.
                   UseProxy = false,
                   ConnectCallback = CustomToolSsrfGuard.CreatePinnedConnectCallback(),
                   // Short pooled lifetime so a pinned validated address is not reused indefinitely across DNS changes.
                   PooledConnectionLifetime = TimeSpan.FromMinutes(1)
               });
        builder.Services.AddSingleton(static _ => new CustomToolConcurrencyLimiter());
        // Executors + catalog are SINGLETON: the invocation stack consuming the catalog is singleton, so a scoped catalog would be a
        // captive dependency (ValidateOnBuild fails it); CustomToolCatalog reads its scoped store through a fresh scope per call instead.
        builder.Services.AddSingleton<ICustomToolExecutor, HttpFetchExecutor>();
        builder.Services.AddSingleton<ICustomToolExecutor, HostProcessExecutor>();
        builder.Services.AddSingleton<ICustomToolCatalog, CustomToolCatalog>();
        // Operator-facing CRUD + author-time validation over the store. Reuses the execution-time guards so what it accepts is
        // exactly what the executors will run; masks secret header/env values on the read path.
        builder.Services.AddScoped<ICustomToolService, CustomToolService>();
        // Node-local playbook actions. Behavior and advisory trigger conditions are encrypted at rest; enabled actions
        // are folded into the agent prompt by the resolver, while the CRUD service owns operator authoring.
        builder.Services.AddScoped<IPlaybookActionStore, PlaybookActionStore>();
        // Append-only agent execution telemetry (adaptive memory diagnostics). Metadata only — no message content — so
        // rows are unencrypted; the run path writes latency/token/success rows linked to the chat message by id.
        builder.Services.AddScoped<IAgentExecutionLogStore, AgentExecutionLogStore>();
        // The Agents diagnostics/usage endpoints' only path to that store. Scoped, matching the store it wraps.
        builder.Services.AddScoped<AgentExecutionLogQueryService>();
        // Node-local MCP registrations. Secret-bearing args/env/description columns are encrypted at rest; the
        // connection manager reads enabled rows and the CRUD service owns registration changes.
        builder.Services.AddScoped<IMcpServerStore, McpServerStore>();
        builder.Services.AddScoped<ISlashCommandStore, SlashCommandStore>();
        builder.Services.AddScoped<ISlashCommandService, SlashCommandService>();
        // The single INBOUND-MCP bearer credential (opposite direction to the registrations above): the key an external
        // MCP client presents to this node's own MCP server endpoint. Material is encrypted at rest.
        builder.Services.AddScoped<IMcpServerApiKeyStore, McpServerApiKeyStore>();
        // The single INBOUND model-proxy bearer credential — the key an external OpenAI-compatible tool presents to this
        // node's raw-model proxy. Separate from the MCP key above; material is encrypted at rest on the same terms.
        builder.Services.AddScoped<ILocalModelProxyApiKeyStore, LocalModelProxyApiKeyStore>();
        // Model-type classification store: the digest-keyed detection cache and the operator override, keyed by model name (NOCASE)
        // and unencrypted, since names, digests and kinds are not secrets. The service resolves the picker's effective kind through it.
        builder.Services.AddScoped<IModelClassificationStore, ModelClassificationStore>();
        // Per-model→provider routing map: which local runtime (llamacpp / ollama) serves a model, so chat routing and the preview and
        // embeddings resolvers dispatch correctly across restarts. Unencrypted; Scoped, and the singleton resolver reads it per lookup.
        builder.Services.AddScoped<IModelProviderMapStore, ModelProviderMapStore>();
        builder.Services.AddSingleton<KeyedCompositeLockDomain>();
        builder.Services.AddSingleton<IModelProviderMapLeaseCoordinator, ModelProviderMapLeaseCoordinator>();
        builder.Services.AddScoped<IInstalledModelSnapshotCoordinator>(static services =>
            new InstalledModelSnapshotCoordinator(services.GetRequiredService<KeyedCompositeLockDomain>(),
                services.GetRequiredService<IInstalledGgufSnapshotStore>(),
                services.GetRequiredService<ICoordinatedModelProviderMapStore>()));
        builder.Services.AddScoped<ICoordinatedModelProviderMapStore>(static services =>
            new CoordinatedModelProviderMapStore(services.GetRequiredService<IModelProviderMapStore>()));
        builder.Services.AddScoped<LocalModelDeletionCoordinator>();
        builder.Services.AddScoped<ILocalModelDeletionCoordinator>(static services =>
            services.GetRequiredService<LocalModelDeletionCoordinator>());
        builder.Services.AddScoped<ILocalModelDeletionJournalReconciler>(static services =>
            services.GetRequiredService<LocalModelDeletionCoordinator>());
        builder.Services.AddScoped<ILocalModelAdministrationService, LocalModelAdministrationService>();
        // Two-runtime unload fan-out for the eject route: every llama-server role, then the gated Ollama eviction. Singleton like the
        // other lifecycle coordinators, since everything it consumes (process supervisor, Ollama model service, config) is one too.
        builder.Services.AddSingleton<IModelUnloadCoordinator, ModelUnloadCoordinator>();
        builder.Services.AddSingleton<DefaultModelSelectionPolicy>();
        builder.Services.AddHostedService<LocalModelDeletionStartupReconciler>();
        builder.Services.AddScoped<GgufAcquisitionStateProbe>();
        builder.Services.AddSingleton<GgufAcquisitionIdentityResolver>();
        builder.Services.AddScoped<IGgufAcquisitionPreflight, GgufAcquisitionPreflight>();
        builder.Services.AddScoped<IOllamaProviderMapBackfillCoordinator, OllamaProviderMapBackfillCoordinator>();
        builder.Services.AddScoped<IModelLaunchArgumentsStore, ModelLaunchArgumentsStore>();
        // The LocalModels launch-argument endpoints' only path to that store. Scoped, matching the store it wraps.
        builder.Services.AddScoped<ModelLaunchArgumentsService>();
        // Feedback-insights read store. Pure analytics over node-local feedback/tool-event rows; it reads only
        // plaintext columns and writes nothing.
        builder.Services.AddScoped<IFeedbackInsightsStore, FeedbackInsightsStore>();
        // Agent-definition application layer: the resolver projects a conversation's bound definition into loopback
        // runtime-package inputs, and the service validates/orchestrates management CRUD.
        builder.Services.AddScoped<IAgentDefinitionResolver, AgentDefinitionResolver>();
        // Default-agent id memoization: resolves the seeded "Default Assistant" id once and caches it for the process lifetime, so the
        // mode-off chat hot paths avoid a GetBySeedSlugAsync per send. Singleton: it owns the cache and takes a scope for the first lookup.
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
        // Agent skill service: validates skill content (MAF-safe Name, NOCASE-unique, length caps) and delegates persistence and
        // versioning to the store; the resolver turns an agent's assigned skills into the runtime package for progressive disclosure.
        builder.Services.AddScoped<IAgentSkillService, AgentSkillService>();
        // AddMemoryCache is idempotent (TryAdd), so registering it here keeps this module self-contained whatever the module order.
        builder.Services.AddMemoryCache();
        // Third-party skill import: its named client disables redirects, so the github.com → codeload hop is re-validated against the
        // host allowlist by hand, and the cache holds the single-use preview payload phase 2 persists verbatim, never re-fetching it.
        builder.Services.AddHttpClient(GitHubSkillArchiveDownloader.HttpClientName)
               .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
               {
                   AllowAutoRedirect = false
               });

        // Guard limits are bound from configuration so an operator can tighten them without a rebuild. The defaults
        // admit a real collection repository; see SkillImportOptions for which of them actually bound memory.
        var skillImportOptions = new SkillImportOptions();
        configuration.GetSection(SkillImportOptions.SectionName).Bind(skillImportOptions);
        builder.Services.AddSingleton(skillImportOptions);

        builder.Services.AddScoped<ISkillImportService>(static sp => new SkillImportService(sp.GetRequiredService<IAgentSkillStore>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubSkillArchiveDownloader.HttpClientName),
            sp.GetRequiredService<SkillImportOptions>()));
        // Feedback-insights service: shapes raw aggregates into the operator read model, including derived down-rate,
        // single-sample guard flags, and privacy-capped exemplars.
        builder.Services.AddScoped<IFeedbackInsightsService, FeedbackInsightsService>();

        return builder;
    }
}
