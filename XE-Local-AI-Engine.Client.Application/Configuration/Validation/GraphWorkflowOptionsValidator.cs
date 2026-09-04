namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Checks what the data annotations on <see cref="GraphWorkflowOptions" /> cannot: the semantic floor under each
///     budget, and the one relation that holds between two of them.
///     <para>
///         A floor is not a range bound. <c>MaxNodesPerDefinition = 1</c> passes <c>[Range(1, …)]</c> and still admits
///         no graph, because every graph carries a Start and an End; <c>DispatchIntervalMilliseconds = 1</c> is a legal
///         positive integer and a sweep that spends its time opening scopes. Both belong here, at startup, rather than
///         at the first run — which is where the operator would otherwise meet them, once per node run.
///     </para>
///     <para>
///         The cross-option relation is <c>MaxNodeRunsPerRun</c> against <c>MaxNodesPerDefinition</c>: a run that
///         cannot instantiate the definition it started from would fail halfway through a graph the same node let the
///         operator save. The ceiling is <c>EventReplayLimit</c>, bounded because one replay is one response body.
///     </para>
/// </summary>
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
            (nameof(options.EventReplayLimit), options.EventReplayLimit, 1)
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

        if (options.EventReplayLimit > EventReplayCeiling)
        {
            failures.Add($"{GraphWorkflowOptions.Section}:EventReplayLimit is {options.EventReplayLimit}, above the ceiling of {EventReplayCeiling}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
