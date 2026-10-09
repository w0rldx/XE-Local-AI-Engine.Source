namespace XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The <see cref="IScheduledJobManagementService" /> a node with <c>Scheduler:Enabled=false</c> registers in place of
///     the Quartz-backed one.
/// </summary>
/// <remarks>
///     The Scheduler endpoints stay discovered, and FastEndpoints activates every endpoint at startup, so a missing
///     registration stops the host. Every member refuses with <see cref="ScheduledJobValidationException" />, which the
///     host maps to 400, the same shape work sessions use when they are off.
/// </remarks>
internal sealed class DisabledScheduledJobManagementService : IScheduledJobManagementService
{
    internal const string DisabledMessage = "The scheduler is disabled on this node.";

    public IReadOnlyList<ScheduledJobTemplateDescriptor> ListTemplatesAsync() =>
        throw Disabled();

    public Task<IReadOnlyList<ScheduledJobDefinitionRecord>> ListJobsAsync(bool includeDeleted = false, CancellationToken cancellationToken = default) =>
        throw Disabled();

    public Task<ScheduledJobDefinitionRecord?> GetJobAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw Disabled();

    public Task<ScheduledJobDefinitionRecord> CreateJobAsync(ScheduledJobManagementInput input, CancellationToken cancellationToken = default) =>
        throw Disabled();

    public Task<ScheduledJobDefinitionRecord?> UpdateJobAsync(Guid id, ScheduledJobManagementInput input, CancellationToken cancellationToken = default) =>
        throw Disabled();

    public Task<ScheduledJobDefinitionRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default) =>
        throw Disabled();

    public Task<bool> DeleteJobAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw Disabled();

    public Task<Guid> TriggerNowAsync(Guid id,
        IReadOnlyDictionary<string, string>? parameterOverrides = null,
        CancellationToken cancellationToken = default) =>
        throw Disabled();

    public Task<int> ReconcileDurableJobsAsync(CancellationToken cancellationToken = default) =>
        throw Disabled();

    public Task<IReadOnlyList<ScheduledJobRunRecord>> ListRunsAsync(ScheduledRunStatus? status = null,
        long? fromUtc = null,
        long? toUtc = null,
        Guid? scheduledJobId = null,
        CancellationToken cancellationToken = default) =>
        throw Disabled();

    public Task<ScheduledJobRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        throw Disabled();

    public Task<RunCancellationOutcome> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        throw Disabled();

    private static ScheduledJobValidationException Disabled() =>
        new(DisabledMessage);
}
