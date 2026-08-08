namespace XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1.Mappers;
using XE_Local_AI_Engine.Client.Models.NodeBinding;
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
            var result = await _nodeBindingService.PollUntilTerminalAsync(req.ToSession(), ct).ConfigureAwait(false);
            await Send.OkAsync(result.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await Send.OkAsync(new PollNodeBindingSessionResponse
            {
                Status = "cancelled",
                IntervalSeconds = req.IntervalSeconds,
                ExpiresAt = req.ExpiresAt
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (NodeBindingException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
