namespace XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1.Mappers;
using XE_Local_AI_Engine.Client.Models.NodeBinding;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class StartNodeBindingEndpoint(INodeBindingService nodeBindingService) : EndpointWithoutRequest<NodeBindingSessionResponse>
{
    private readonly INodeBindingService _nodeBindingService = nodeBindingService ?? throw new ArgumentNullException(nameof(nodeBindingService));

    public override void Configure()
    {
        Post(LocalApiRoutes.NodeBinding.Start);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            var session = await _nodeBindingService.StartBindingAsync(ct).ConfigureAwait(false);
            await Send.OkAsync(session.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (NodeBindingException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
