namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;

/// <summary>
///     Startup/options validator for worker node options settings.
/// </summary>
public sealed class WorkerNodeOptionsValidator : IValidateOptions<WorkerNodeOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkerNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(string.IsNullOrWhiteSpace(options.NodeName), "WorkerNode:NodeName is required.")
                               .AppendIf(options.MaxResponseSizeMb is < 1 or > 100, "WorkerNode:MaxResponseSizeMb must be between 1 and 100.")
                               .AppendIf(string.IsNullOrWhiteSpace(options.DeadLetterQueuePath), "WorkerNode:DeadLetterQueuePath is required.")
                               .AppendIf(options.MaxPendingToolCallAgeMinutes is < 1 or > 60, "WorkerNode:MaxPendingToolCallAgeMinutes must be between 1 and 60.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
