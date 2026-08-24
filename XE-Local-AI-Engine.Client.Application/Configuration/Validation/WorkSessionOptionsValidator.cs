namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Machine-checks the one cross-section relation the work-session docs assert: a park must expire before the node
///     expires the pending tool call it is waiting on, or the park times out against a call the node has already given
///     up on and the session is checkpointed off a prompt nobody can answer any more.
/// </summary>
public sealed class WorkSessionOptionsValidator : IValidateOptions<WorkSessionOptions>
{
    private readonly IOptions<WorkerNodeOptions> _workerNode;

    public WorkSessionOptionsValidator(IOptions<WorkerNodeOptions> workerNode) =>
        _workerNode = workerNode ?? throw new ArgumentNullException(nameof(workerNode));

    public ValidateOptionsResult Validate(string? name, WorkSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var pendingToolCallAgeMinutes = _workerNode.Value.MaxPendingToolCallAgeMinutes;
        var pendingToolCallAgeSeconds = pendingToolCallAgeMinutes * 60;
        return options.MaxParkedSeconds < pendingToolCallAgeSeconds
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"WorkSessions:MaxParkedSeconds ({options.MaxParkedSeconds}) must stay under WorkerNode:MaxPendingToolCallAgeMinutes "
                + $"({pendingToolCallAgeMinutes} minutes = {pendingToolCallAgeSeconds} seconds), so a park expires before the node "
                + "expires the tool call it is parked on.");
    }
}
