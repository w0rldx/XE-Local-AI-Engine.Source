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
using XE_Local_AI_Engine.Client.Services.Analysis;
using XE_Local_AI_Engine.Client.Services.Analysis.Implementation;
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
using XE_Local_AI_Engine.Client.Services.Monitoring;
using XE_Local_AI_Engine.Client.Services.Monitoring.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Scheduler;
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

        return builder;
    }
}
