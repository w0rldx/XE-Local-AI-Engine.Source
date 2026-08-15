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
        builder.AddNodePreviewWorkflows(configuration);
        builder.AddNodeDocumentIngestion(configuration);
        builder.AddNodeKnowledgeBase(configuration);
        builder.AddNodeChat(configuration);
        builder.AddNodeChatStreamBudget(configuration);
        builder.AddNodeDevelopment(configuration);
        // Development Mode container sandbox (ADR 0004). After AddNodeDevelopment so it reads as what it is: a
        // Development Mode concern, not an AgentHome one.
        builder.AddNodeContainerSandbox(configuration);
        builder.AddNodeModelRuntime(configuration);

        // Runs after AddNodeModelRuntime: the image model store reuses the Hugging Face download client that
        // AddHuggingFaceGgufStore (invoked there) registers.
        builder.AddNodeImages(configuration);

        return builder;
    }
}
