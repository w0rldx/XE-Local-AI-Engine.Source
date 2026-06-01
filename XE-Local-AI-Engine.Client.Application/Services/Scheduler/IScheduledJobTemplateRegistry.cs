namespace XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Read-only registry of all <see cref="IScheduledJobHandler" /> implementations discovered at startup.
///     Registered as a singleton by <c>NodeSchedulerServiceCollectionExtensions</c> (Marker 2 — Agent A).
/// </summary>
public interface IScheduledJobTemplateRegistry
{
    /// <summary>
    ///     Returns the descriptors of all registered templates in registration order. Used by the management
    ///     API (Marker 3) to populate the template-picker list.
    /// </summary>
    IReadOnlyList<ScheduledJobTemplateDescriptor> ListTemplates();

    /// <summary>
    ///     Retrieves the descriptor for a specific template, or <see langword="null" /> when no handler with
    ///     that <paramref name="templateId" /> is registered.
    /// </summary>
    ScheduledJobTemplateDescriptor? GetTemplate(string templateId);

    /// <summary>
    ///     Attempts to locate the handler for <paramref name="templateId" />.
    /// </summary>
    /// <param name="templateId">The template identifier to look up.</param>
    /// <param name="handler">
    ///     When this method returns <see langword="true" />, contains the registered handler; otherwise
    ///     <see langword="null" />.
    /// </param>
    /// <returns>
    ///     <see langword="true" /> if a handler is registered for <paramref name="templateId" />;
    ///     <see langword="false" /> otherwise.
    /// </returns>
    bool TryGetHandler(string templateId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IScheduledJobHandler? handler);
}
