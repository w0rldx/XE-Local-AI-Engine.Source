namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.AI.Contracts.Events;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Loopback responder for a pending MCP tool-approval request (UX-01). In desktop/local mode there is no worker hub
///     to carry the operator's decision, so the browser posts it here. The handler feeds the decision into the same
///     <see cref="IWorkerEventDispatcher.DispatchApprovalResolvedAsync" /> entry point the platform hub uses, which
///     resolves the runner's pending approval and releases the waiting turn. Keyed only by the approval request id (the
///     runner's opaque per-approval key), so it works with no platform connection and needs no conversation context.
/// </summary>
public sealed class ResolveToolApprovalEndpoint(IWorkerEventDispatcher eventDispatcher)
    : Endpoint<ResolveToolApprovalRequest, ResolveToolApprovalResponse>
{
    private readonly IWorkerEventDispatcher _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.ResolveApproval);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ResolveToolApprovalRequest req, CancellationToken ct)
    {
        // DispatchApprovalResolvedAsync is idempotent/safe when no approval is pending for this id (it logs a warning and
        // no-ops), so a duplicate or stale decision never faults the turn — it just does nothing.
        await _eventDispatcher.DispatchApprovalResolvedAsync(new ApprovalResolvedEvent(req.RequestId, req.Approved)).ConfigureAwait(false);

        await Send.OkAsync(new ResolveToolApprovalResponse
        {
            RequestId = req.RequestId,
            Approved = req.Approved
        }, ct).ConfigureAwait(false);
    }
}
