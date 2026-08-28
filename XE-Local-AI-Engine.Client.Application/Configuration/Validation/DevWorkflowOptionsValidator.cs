namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Checks the one cross-section relation the workflow runtime depends on: an agent node IS a work session, so a node
///     with development workflows on and work sessions off would accept a run, dispatch its first agent node, and fail
///     it with "work sessions are disabled on this node" — once per node-run, at run time, where the operator sees the
///     symptom and not the switch. Startup is where that belongs.
///     <para>
///         Only the enable flags relate across the sections. The budgets inside <see cref="DevWorkflowOptions" /> are
///         independent of each other — <c>MaxTotalAttempts</c> bounds retries rather than first attempts, so it is
///         deliberately smaller than <c>MaxNodeRunsPerRun</c> and their data-annotation ranges are the whole check.
///     </para>
/// </summary>
public sealed class DevWorkflowOptionsValidator : IValidateOptions<DevWorkflowOptions>
{
    private readonly IOptions<WorkSessionOptions> _workSessions;

    public DevWorkflowOptionsValidator(IOptions<WorkSessionOptions> workSessions) =>
        _workSessions = workSessions ?? throw new ArgumentNullException(nameof(workSessions));

    public ValidateOptionsResult Validate(string? name, DevWorkflowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return !options.Enabled || _workSessions.Value.Enabled
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("DevWorkflows:Enabled is true but WorkSessions:Enabled is false. Every workflow agent node runs as a work "
                                         + "session, so each one would fail at dispatch. Enable WorkSessions, or turn DevWorkflows off.");
    }
}
