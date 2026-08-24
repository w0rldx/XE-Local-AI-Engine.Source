namespace XE_Local_AI_Engine.Client.DependencyInjection;

using XE_Local_AI_Engine.Client.DependencyInjection.Modules;

/// <summary>
///     Registers the node-local application services, persistence boundaries, model providers, and host-agent clients.
///     The per-feature registrations live in the <c>AddNode*</c> module extensions under
///     <c>DependencyInjection/Modules</c>; this orchestrator invokes them in the original registration order.
/// </summary>
public static class NodeApplicationServiceCollectionExtensions
{
    public static IHostApplicationBuilder AddNodeApplication(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.AddNodeCoreOptions(configuration);
        builder.AddNodeAuthAndConnection(configuration);
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
        builder.AddNodePreviewWorkflows(configuration);
        builder.AddNodeDocumentIngestion(configuration);
        builder.AddNodeKnowledgeBase(configuration);
        builder.AddNodeChat(configuration);
        builder.AddNodeChatStreamBudget(configuration);

        // After AddNodeChat: hosted services start in registration order, so the chat restart recovery terminalizes
        // rows orphaned by a crash before the work-session reconciler collapses those sessions to Interrupted.
        builder.AddNodeWorkSessions(configuration);
        builder.AddNodeDevelopment(configuration);
        // Development Mode container sandbox (ADR 0004). After AddNodeDevelopment so it reads as what it is: a
        // Development Mode concern, not an AgentHome one.
        builder.AddNodeContainerSandbox(configuration);
        builder.AddNodeModelRuntime(configuration);

        // Runs after AddNodeModelRuntime: the image model store reuses the Hugging Face download client that
        // AddHuggingFaceGgufStore (invoked there) registers.
        builder.AddNodeImages(configuration);

        // Same ordering reason as AddNodeImages above: the base-checkpoint store reuses the Hugging Face download
        // client AddNodeModelRuntime registers.
        builder.AddNodeTrainingRuntime();

        // After the runtime module (run store + process spawner) and after the llama.cpp module, whose supervisor
        // provides the runtime-mutation lease the run queue acquires before every claim.
        builder.AddNodeTrainingRuns();

        return builder;
    }
}
