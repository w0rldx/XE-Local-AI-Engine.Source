namespace XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Template interface implemented by every scheduled-job handler. Each handler owns exactly one
///     <see cref="TemplateId" /> and is discovered at startup by <see cref="IScheduledJobTemplateRegistry" />.
/// </summary>
public interface IScheduledJobHandler
{
    /// <summary>
    ///     Stable, globally-unique identifier that links this handler to job definitions stored in the database.
    ///     Must match <see cref="Descriptor" />.<see cref="ScheduledJobTemplateDescriptor.TemplateId" />.
    /// </summary>
    string TemplateId { get; }

    /// <summary>
    ///     Metadata describing the template: display name, supported schedule kinds, default policy overrides,
    ///     and capability flags. Exposed by the management API and used by the UI template picker.
    /// </summary>
    ScheduledJobTemplateDescriptor Descriptor { get; }

    /// <summary>
    ///     Performs the work defined by this template for one scheduled invocation. Implementations should respect
    ///     <paramref name="cancellationToken" /> throughout (it is signalled on job interrupt / scheduler shutdown).
    ///     <see cref="OperationCanceledException" /> is allowed to propagate — the dispatcher does not swallow it.
    /// </summary>
    Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken);
}
