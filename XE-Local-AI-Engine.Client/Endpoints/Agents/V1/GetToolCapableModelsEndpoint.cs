namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Exposes the node's tool-capable model ids (<see cref="AgentHomeOptions.ToolCapableModels" />). The agent
///     management UI is the only consumer: it warns when a definition pins a model that is not tool-capable, so tool
///     selection can be disabled rather than silently no-op at runtime.
/// </summary>
public sealed class GetToolCapableModelsEndpoint(IOptions<AgentHomeOptions> agentHomeOptions)
    : EndpointWithoutRequest<ToolCapableModelsResponse>
{
    private readonly IOptions<AgentHomeOptions> _agentHomeOptions = agentHomeOptions ?? throw new ArgumentNullException(nameof(agentHomeOptions));

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.ToolCapableModels);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(new ToolCapableModelsResponse
            {
                Models = [.. _agentHomeOptions.Value.ToolCapableModels ?? []]
            },
            ct).ConfigureAwait(false);
    }
}
