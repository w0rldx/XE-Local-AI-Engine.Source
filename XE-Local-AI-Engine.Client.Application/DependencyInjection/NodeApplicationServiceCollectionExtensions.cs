namespace XE_Local_AI_Engine.Client.DependencyInjection;

using XE_Local_AI_Engine.Client.DependencyInjection.Modules;

/// <summary>
///     Registers the node-local application services, persistence boundaries, model providers, and host-agent clients.
/// </summary>
/// <remarks>
///     The per-feature registrations live in the <c>AddNode*</c> module extensions under
///     <c>DependencyInjection/Modules</c>; the order this orchestrator invokes them in is load-bearing, because hosted
///     services start in registration order. See docs/wiki/02-project-layout.md ("Composition root: the AddNode* modules").
/// </remarks>
public static class NodeApplicationServiceCollectionExtensions
{
    public static IHostApplicationBuilder AddNodeApplication(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.AddNodeCoreOptions(configuration);
        builder.AddNodeAuth(configuration);
        builder.AddNodeInvocation(configuration);
        builder.AddNodeWorkspaceAndAgents(configuration);
        builder.AddNodeAnalysis(configuration);
        builder.AddNodeAdaptiveMemory(configuration);
        builder.AddNodeDrafting(configuration);
        builder.AddNodeEval(configuration);
        builder.AddNodeGoldenHarvest(configuration);
        builder.AddNodeSchedulingStores(configuration);
        builder.AddNodeModelFit(configuration);
        builder.AddNodeBenchmarks();
        builder.AddNodeTrainingDatasets();
        builder.AddNodeCapacity(configuration);
        builder.AddNodeMcpAgentRuns(configuration);
        builder.AddNodePlaybookRetrievalAndMonitoring(configuration);
        builder.AddNodeWorkerInfrastructure(configuration);
        builder.AddNodeModelCapabilitiesAndMcp(configuration);
        builder.AddNodeAgentHome(configuration);
        builder.AddNodeCoder(configuration);
        // Sandboxed run_python compute tool. After AddNodeAgentHome, which registers the agent-role sandbox provider
        // and the owner/node identity its jail is keyed on.
        builder.AddNodeCompute(configuration);
        builder.AddNodeDocumentIngestion(configuration);
        builder.AddNodeKnowledgeBase(configuration);
        builder.AddNodeChat(configuration);
        builder.AddNodeChatStreamBudget(configuration);

        // After AddNodeChat: hosted services start in registration order, so the chat restart recovery terminalizes
        // rows orphaned by a crash before the work-session reconciler collapses those sessions to Interrupted.
        builder.AddNodeWorkSessions(configuration);
        builder.AddNodeDevelopment(configuration);

        // After both, for hosted-service START order — NOT because the dependency is one-way: a DevTask node run drives a
        // Development task and vice versa, so Development's reconciler must terminalize its orphans before this dispatcher admits runs.
        builder.AddNodeDevWorkflows(configuration);

        // Immediately after: the graph-workflow runtime is the same shape one slice later, and its hosted services
        // must start behind the ones whose orphaned rows they would otherwise adopt mid-repair.
        builder.AddNodeGraphWorkflows(configuration);

        // After AddNodeDevWorkflows for the same hosted-service START order reason: this module's coordinator must start
        // after chat restart recovery has terminalized crash-orphaned rows, so it never adopts an execution chat is repairing.
        builder.AddNodeIntegrations(configuration);
        // Development Mode container sandbox (ADR 0004). After AddNodeDevelopment so it reads as what it is: a
        // Development Mode concern, not an AgentHome one.
        builder.AddNodeContainerSandbox(configuration);

        // Immediately after AddNodeContainerSandbox, which registers the daemon-attestation singleton the resolver takes; the
        // feature chain is ContainerSandbox -> ContainerRuntime -> ExternalAppsCatalog -> ExternalApps, none of it on the startup path.
        builder.AddNodeContainerRuntime(configuration);
        builder.AddNodeExternalAppsCatalog(configuration);

        // Last of the four, and the only one with hosted services: the boot reconciler and the state observer take
        // the resolver the previous module registered and the catalog the one before it did.
        builder.AddNodeExternalApps(configuration);

        // BEFORE AddNodeModelRuntime, which registers the external multiplexer provider only when an IExternalProviderRegistry
        // is already in the collection — a registration-time check, so a later registry ships a node on which no ext model routes.
        builder.AddNodeExternalProviders();
        builder.AddNodeModelRuntime(configuration);

        // Runs after AddNodeModelRuntime: the image model store reuses the Hugging Face download client that
        // AddHuggingFaceGgufStore (invoked there) registers.
        builder.AddNodeImages(configuration);

        // Same ordering reason as AddNodeImages above: the whisper weight store reuses the Hugging Face download
        // client AddNodeModelRuntime registers.
        builder.AddNodeTranscription(configuration);

        // Same ordering reason as AddNodeImages above: the base-checkpoint store reuses the Hugging Face download
        // client AddNodeModelRuntime registers.
        builder.AddNodeTrainingRuntime();

        // After the runtime module (run store + process spawner) and after the llama.cpp module, whose supervisor
        // provides the runtime-mutation lease the run queue acquires before every claim.
        builder.AddNodeTrainingRuns();

        return builder;
    }
}
