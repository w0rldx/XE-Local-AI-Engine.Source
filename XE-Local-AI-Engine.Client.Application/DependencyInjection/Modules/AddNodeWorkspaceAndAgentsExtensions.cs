namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using System.Net.Http;
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
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

internal static class AddNodeWorkspaceAndAgentsExtensions
{
    public static IHostApplicationBuilder AddNodeWorkspaceAndAgents(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Selected-folder store plus safe resolver. The host path is encrypted at rest; the model-facing surface sees
        // only opaque folder ids and aliases. Registration stays available while AgentHome is disabled because the
        // workspace-copy and AgentHome gateway paths both depend on it.
        builder.Services.AddScoped<INodeSelectedFolderStore, NodeSelectedFolderStore>();
        builder.Services.AddScoped<ISelectedFolderResolver, SelectedFolderResolver>();
        builder.Services.AddScoped<IWorkspaceRevocationService, WorkspaceRevocationService>();
        // Node-local agent definitions. Instructions and descriptions are encrypted at rest; the resolver/service
        // projects a bound definition into runtime-package inputs.
        builder.Services.AddScoped<IAgentDefinitionStore, AgentDefinitionStore>();
        // Node-local canvas (Open Canvas preview) workflows. The serialized graph (carrying agent instructions and Start
        // text) is encrypted at rest; the store owns id/version/timestamp stamping and optimistic-concurrency updates.
        builder.Services.AddScoped<ICanvasWorkflowStore, CanvasWorkflowStore>();
        // Node-local agent skill library. Skill description and SKILL.md body are encrypted at rest; the resolver loads
        // an agent's enabled, assigned skills and the factory attaches them via MAF progressive disclosure, while the
        // CRUD service owns operator authoring. Scoped to match the scoped, DbContext-backed store.
        builder.Services.AddScoped<IAgentSkillStore, AgentSkillStore>();
        // Node-local user-defined custom tool library. The model-facing description and the kind-specific config (which
        // carries the secret header/env values) are encrypted at rest; the store owns id/version/timestamp stamping and
        // the content-affecting version-bump rule, while the CRUD service owns operator authoring and validation.
        builder.Services.AddScoped<ICustomToolStore, CustomToolStore>();
        // Custom-tool executors + catalog (P2 SECURITY CORE). The catalog reads the store live per turn (no cache) and
        // hands the resolver an executable already floored in ApprovalRequiredAIFunction. HttpFetch goes out through a
        // dedicated named client whose SocketsHttpHandler pins the connection to an SSRF-validated address and refuses
        // redirects; Command runs on the host under a scrubbed environment, wall-clock timeout, tree-kill, output cap,
        // and a process-wide concurrency ceiling.
        builder.Services.AddHttpClient(HttpFetchExecutor.HttpClientName)
               .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
               {
                   AllowAutoRedirect = false,
                   UseCookies = false,
                   ConnectCallback = CustomToolSsrfGuard.CreatePinnedConnectCallback(),
                   // Short pooled lifetime so a pinned validated address is not reused indefinitely across DNS changes.
                   PooledConnectionLifetime = TimeSpan.FromMinutes(1)
               });
        builder.Services.AddSingleton(static _ => new CustomToolConcurrencyLimiter());
        builder.Services.AddScoped<ICustomToolExecutor, HttpFetchExecutor>();
        builder.Services.AddScoped<ICustomToolExecutor, HostProcessExecutor>();
        builder.Services.AddScoped<ICustomToolCatalog, CustomToolCatalog>();
        // Operator-facing CRUD + author-time validation over the store. Reuses the P2 guards so what it accepts is
        // exactly what the executors will run; masks secret header/env values on the read path.
        builder.Services.AddScoped<ICustomToolService, CustomToolService>();
        // Node-local playbook actions. Behavior and advisory trigger conditions are encrypted at rest; enabled actions
        // are folded into the agent prompt by the resolver, while the CRUD service owns operator authoring.
        builder.Services.AddScoped<IPlaybookActionStore, PlaybookActionStore>();
        // Append-only agent execution telemetry (adaptive memory diagnostics). Metadata only — no message content — so
        // rows are unencrypted; the run path writes latency/token/success rows linked to the chat message by id.
        builder.Services.AddScoped<IAgentExecutionLogStore, AgentExecutionLogStore>();
        // Node-local MCP registrations. Secret-bearing args/env/description columns are encrypted at rest; the
        // connection manager reads enabled rows and the CRUD service owns registration changes.
        builder.Services.AddScoped<IMcpServerStore, McpServerStore>();
        builder.Services.AddScoped<ISlashCommandStore, SlashCommandStore>();
        builder.Services.AddScoped<ISlashCommandService, SlashCommandService>();
        // The single INBOUND-MCP bearer credential (opposite direction to the registrations above): the key an external
        // MCP client presents to this node's own MCP server endpoint. Material is encrypted at rest.
        builder.Services.AddScoped<IMcpServerApiKeyStore, McpServerApiKeyStore>();
        // Model-type classification store. Persists the digest-keyed detection cache and the operator override, keyed by
        // model name (NOCASE). Unencrypted — model names/digests/capabilities/kinds are not secrets. The classification
        // service reads/writes through it to resolve the effective kind that filters the chat picker. Scoped to match
        // the scoped, DbContext-backed store.
        builder.Services.AddScoped<IModelClassificationStore, ModelClassificationStore>();
        // Per-model→provider routing map. Resolves which local runtime (llamacpp / ollama) serves a given model so the
        // model-routing chat client and the preview/embeddings resolvers dispatch correctly and resume-safe across node
        // restarts. Unencrypted — model names and provider keys are not secrets. Scoped to match the
        // scoped, DbContext-backed store; the singleton resolver reads it through a fresh scope per lookup.
        builder.Services.AddScoped<IModelProviderMapStore, ModelProviderMapStore>();
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
        // Agent skill service: validates skill content (MAF-safe Name, NOCASE-unique, length caps) and delegates
        // persistence/versioning to the store. The resolver resolves an agent's assigned skills into the runtime package
        // for MAF progressive disclosure.
        builder.Services.AddScoped<IAgentSkillService, AgentSkillService>();
        // Third-party skill import. Its named HttpClient disables automatic redirects so the github.com → codeload
        // hop can be re-validated against the host allowlist by hand; following redirects blindly would let a
        // compromised response choose the host. The in-memory cache holds the short-lived, single-use preview payload
        // that phase 2 persists verbatim — phase 2 must never re-fetch, or the operator approves one payload and the
        // node stores another. AddMemoryCache is idempotent (TryAdd), so registering it here keeps this module
        // self-contained regardless of module order.
        builder.Services.AddMemoryCache();
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
