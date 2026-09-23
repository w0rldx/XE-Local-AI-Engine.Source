namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Checks what the data annotations on <see cref="GraphWorkflowOptions" /> cannot: the semantic floor under each
///     budget, and the one relation that holds between two of them.
/// </summary>
/// <remarks>
///     A floor is not a range bound. <c>MaxNodesPerDefinition</c> of 1 passes <c>[Range(1, …)]</c> and still admits no graph, because every
///     graph carries a Start and an End; a <c>DispatchIntervalMilliseconds</c> of 1 is a legal positive integer and a sweep that spends its
///     time opening scopes. Both belong at startup, not at the first run where the operator meets them once per node run. The cross-option
///     relation is <c>MaxNodeRunsPerRun</c> against <c>MaxNodesPerDefinition</c>: a run that cannot instantiate the definition it started
///     from fails halfway through a graph the same node let the operator save.
/// </remarks>
public sealed class GraphWorkflowOptionsValidator : IValidateOptions<GraphWorkflowOptions>
{
    private const int EventReplayCeiling = 1000;

    public ValidateOptionsResult Validate(string? name, GraphWorkflowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        (string Name, int Value, int Floor)[] floors =
        [
            (nameof(options.MaxNodesPerDefinition), options.MaxNodesPerDefinition, 2),
            (nameof(options.MaxNodeRunsPerRun), options.MaxNodeRunsPerRun, 2),
            (nameof(options.MaxTotalAttempts), options.MaxTotalAttempts, 1),
            (nameof(options.DefaultNodeTimeoutSeconds), options.DefaultNodeTimeoutSeconds, 1),
            (nameof(options.MaxOutputJsonBytes), options.MaxOutputJsonBytes, 1024),
            (nameof(options.DispatchIntervalMilliseconds), options.DispatchIntervalMilliseconds, 100),
            (nameof(options.MaxConcurrentRuns), options.MaxConcurrentRuns, 1),
            (nameof(options.MaxRunInputBytes), options.MaxRunInputBytes, 1024),
            (nameof(options.EventReplayLimit), options.EventReplayLimit, 1),
            (nameof(options.MaxSteersPerNode), options.MaxSteersPerNode, 1)
        ];

        var failures = new List<string>();
        foreach (var (memberName, value, floor) in floors)
        {
            if (value < floor)
            {
                failures.Add($"{GraphWorkflowOptions.Section}:{memberName} is {value}, below its floor of {floor}.");
            }
        }

        if (options.MaxNodeRunsPerRun < options.MaxNodesPerDefinition)
        {
            failures.Add($"{GraphWorkflowOptions.Section}:MaxNodeRunsPerRun ({options.MaxNodeRunsPerRun}) is below MaxNodesPerDefinition "
                         + $"({options.MaxNodesPerDefinition}), so a run could not instantiate a definition this node accepts.");
        }

        // EventReplayLimit is the one budget with a ceiling as well as a floor: one replay is one response body.
        if (options.EventReplayLimit > EventReplayCeiling)
        {
            failures.Add($"{GraphWorkflowOptions.Section}:EventReplayLimit is {options.EventReplayLimit}, above the ceiling of {EventReplayCeiling}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
