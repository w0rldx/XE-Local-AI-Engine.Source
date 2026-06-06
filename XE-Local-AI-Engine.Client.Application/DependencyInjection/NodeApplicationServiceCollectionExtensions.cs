namespace XE_Local_AI_Engine.Client;

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
        builder.AddNodeHostAgentAndInvocation(configuration);
        builder.AddNodeWorkspaceAndAgents(configuration);
        builder.AddNodeAnalysis(configuration);
        builder.AddNodeEval(configuration);
        builder.AddNodeGoldenHarvest(configuration);
        builder.AddNodeSchedulingStores(configuration);
        builder.AddNodeModelFit(configuration);
        builder.AddNodePlaybookRetrievalAndMonitoring(configuration);
        builder.AddNodeWorkerInfrastructure(configuration);
        builder.AddNodeModelCapabilitiesAndMcp(configuration);
        builder.AddNodeAgentHome(configuration);
        builder.AddNodePreviewWorkflows(configuration);
        builder.AddNodeChat(configuration);
        builder.AddNodeModelRuntime(configuration);

        return builder;
    }
}
