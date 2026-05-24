namespace XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class CancelNodeBindingEndpoint(INodeBindingService nodeBindingService) : EndpointWithoutRequest<CancelNodeBindingResponse>
{
    private readonly INodeBindingService _nodeBindingService = nodeBindingService ?? throw new ArgumentNullException(nameof(nodeBindingService));

    public override void Configure()
    {
        Post(LocalApiRoutes.NodeBinding.Cancel);
        Policies(LocalOperatorAuthorization.OperatorPolicy);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await _nodeBindingService.CancelAsync().ConfigureAwait(false);
        await Send.OkAsync(new CancelNodeBindingResponse { Cancelled = true }, ct).ConfigureAwait(false);
    }
}
