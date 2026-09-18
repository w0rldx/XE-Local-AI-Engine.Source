namespace XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class StartNodeBindingEndpoint : EndpointWithoutRequest<NodeBindingSessionResponse>
{
    private readonly INodeBindingService _nodeBindingService;

    public StartNodeBindingEndpoint(INodeBindingService nodeBindingService)
    {
        ArgumentNullException.ThrowIfNull(nodeBindingService);
        _nodeBindingService = nodeBindingService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.NodeBinding.Start);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var session = await _nodeBindingService.StartBindingAsync(ct);
        await Send.OkAsync(session.ToResponse(), ct);
    }
}
