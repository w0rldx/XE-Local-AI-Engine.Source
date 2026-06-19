namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     Server-push hub for Open Canvas (Preview) run events. Clients connect, drive runs through the REST endpoints, and
///     receive node/run events (each carrying its runId) broadcast via <see cref="PreviewWorkflowEventPublisher" />.
///     <see cref="OnDisconnectedAsync" /> cancels every run owned by the disconnecting connection so an abandoned tab
///     does not keep a run burning compute. <see cref="Subscribe" /> opts a connection into a per-run group for scoped
///     delivery. Protected with the same Operator policy as the other local hubs.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class PreviewWorkflowHub(IPreviewWorkflowExecutionService executionService) : Hub
{
    private readonly IPreviewWorkflowExecutionService _executionService =
        executionService ?? throw new ArgumentNullException(nameof(executionService));

    /// <summary>Returns the SignalR group name for a run's scoped delivery.</summary>
    public static string RunGroup(Guid runId)
    {
        return $"preview-run-{runId:N}";
    }

    /// <summary>Opts this connection into the per-run group so it receives only that run's scoped events.</summary>
    public Task Subscribe(Guid runId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, RunGroup(runId));
    }

    /// <summary>Removes this connection from a run's group (e.g. when the canvas closes a run view).</summary>
    public Task Unsubscribe(Guid runId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, RunGroup(runId));
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _executionService.CancelRunsForConnectionAsync(Context.ConnectionId).ConfigureAwait(false);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }
}
