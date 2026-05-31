namespace XE_Local_AI_Engine.Client.Endpoints.Invocations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     FastEndpoints handler for the get invocation monitor local API operation.
/// </summary>
public sealed class GetInvocationMonitorEndpoint(
    IWorkerEventDispatcher eventDispatcher,
    IInvocationHistory invocationHistory) : EndpointWithoutRequest<InvocationMonitorResponse>
{
    private readonly IWorkerEventDispatcher _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
    private readonly IInvocationHistory _invocationHistory = invocationHistory ?? throw new ArgumentNullException(nameof(invocationHistory));

    public override void Configure()
    {
        Get(LocalApiRoutes.Invocations.Monitor);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var response = InvocationMonitorResponseMapper.ToResponse(_eventDispatcher.CurrentInvocation,
            _invocationHistory.Snapshot(),
            _invocationHistory.Capacity);

        await Send.OkAsync(response, ct).ConfigureAwait(false);
    }
}
