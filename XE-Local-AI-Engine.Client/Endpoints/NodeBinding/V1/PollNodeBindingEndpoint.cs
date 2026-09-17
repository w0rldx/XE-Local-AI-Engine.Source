namespace XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class PollNodeBindingEndpoint(INodeBindingService nodeBindingService) : Endpoint<PollNodeBindingSessionRequest, PollNodeBindingSessionResponse>
{
    private readonly INodeBindingService _nodeBindingService = nodeBindingService ?? throw new ArgumentNullException(nameof(nodeBindingService));

    public override void Configure()
    {
        Post(LocalApiRoutes.NodeBinding.Poll);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(PollNodeBindingSessionRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _nodeBindingService.PollUntilTerminalAsync(req.ToSession(), ct);
            await Send.OkAsync(result.ToResponse(), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await Send.OkAsync(new PollNodeBindingSessionResponse
            {
                Status = "cancelled",
                IntervalSeconds = req.IntervalSeconds,
                ExpiresAt = req.ExpiresAt
            }, CancellationToken.None);
        }
    }
}
