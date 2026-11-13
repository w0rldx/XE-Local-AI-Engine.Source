namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>Stops the sandbox command one tool call of a running turn is executing, without stopping the turn.</summary>
/// <remarks>
///     202 once the stop was requested; the tool then returns its normal "not completed" result to the model. 404 when
///     that call runs no stoppable command here (finished, never started, or a sandbox that cannot cancel one command).
/// </remarks>
public sealed class StopNodeChatToolCommandEndpoint : Endpoint<StopNodeChatToolCommandRequest>
{
    private readonly ToolCommandStopRegistry _stops;

    public StopNodeChatToolCommandEndpoint(ToolCommandStopRegistry stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        _stops = stops;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.StopToolCommand);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status202Accepted)
                                             .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(StopNodeChatToolCommandRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (await _stops.TryStopAsync(req.MessageId, req.ToolCallId, ct))
        {
            await Send.ResultAsync(TypedResults.Accepted((string?)null));
            return;
        }

        await Send.NotFoundAsync(ct);
    }
}
