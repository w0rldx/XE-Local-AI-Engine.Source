namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
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
        // Node-local playbook actions. Behavior and advisory trigger conditions are encrypted at rest; enabled actions
        // are folded into the agent prompt by the resolver, while the CRUD service owns operator authoring.
        builder.Services.AddScoped<IPlaybookActionStore, PlaybookActionStore>();
        // Append-only agent execution telemetry (adaptive memory diagnostics). Metadata only — no message content — so
        // rows are unencrypted; the run path writes latency/token/success rows linked to the chat message by id.
        builder.Services.AddScoped<IAgentExecutionLogStore, AgentExecutionLogStore>();
        // Node-local MCP registrations. Secret-bearing args/env/description columns are encrypted at rest; the
        // connection manager reads enabled rows and the CRUD service owns registration changes.
        builder.Services.AddScoped<IMcpServerStore, McpServerStore>();
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
        // Feedback-insights service: shapes raw aggregates into the operator read model, including derived down-rate,
        // single-sample guard flags, and privacy-capped exemplars.
        builder.Services.AddScoped<IFeedbackInsightsService, FeedbackInsightsService>();

        return builder;
    }
}
