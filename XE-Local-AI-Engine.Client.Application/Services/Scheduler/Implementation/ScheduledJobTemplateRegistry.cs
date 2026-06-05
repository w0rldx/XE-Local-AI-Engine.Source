namespace XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

/// <summary>
///     Singleton registry built at startup from all <see cref="IScheduledJobHandler" /> implementations
///     discovered via dependency injection. Registered by <c>NodeSchedulerServiceCollectionExtensions</c>
///     .
/// </summary>
/// <remarks>
///     Template IDs are matched case-sensitively. Registration order (as returned by DI) is preserved in
///     <see cref="ListTemplates" /> so that the management UI displays templates in a deterministic order.
/// </remarks>
/// <exception cref="InvalidOperationException">
///     Thrown at construction when two handlers declare the same <see cref="IScheduledJobHandler.TemplateId" />.
///     This is a programming error that must be caught at startup, not silently suppressed at runtime.
/// </exception>
public sealed class ScheduledJobTemplateRegistry : IScheduledJobTemplateRegistry
{
    private readonly FrozenDictionary<string, IScheduledJobHandler> _handlers;
    private readonly IReadOnlyList<ScheduledJobTemplateDescriptor> _descriptors;

    /// <summary>
    ///     Initializes the registry from the set of handlers resolved by the DI container.
    /// </summary>
    /// <param name="handlers">All registered <see cref="IScheduledJobHandler" /> implementations.</param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when two or more handlers share the same <see cref="IScheduledJobHandler.TemplateId" />.
    /// </exception>
    public ScheduledJobTemplateRegistry(IEnumerable<IScheduledJobHandler> handlers)
    {
        var dict = new Dictionary<string, IScheduledJobHandler>(StringComparer.Ordinal);
        var descriptors = new List<ScheduledJobTemplateDescriptor>();

        foreach (var handler in handlers)
        {
            if (!dict.TryAdd(handler.TemplateId, handler))
            {
                throw new InvalidOperationException(
                    $"Duplicate scheduled-job template ID '{handler.TemplateId}' detected. " +
                    $"Each IScheduledJobHandler must declare a unique TemplateId. " +
                    $"Conflicting type: '{handler.GetType().FullName}'.");
            }

            descriptors.Add(handler.Descriptor);
        }

        _handlers = dict.ToFrozenDictionary(StringComparer.Ordinal);
        _descriptors = descriptors.AsReadOnly();
    }

    /// <inheritdoc />
    public IReadOnlyList<ScheduledJobTemplateDescriptor> ListTemplates() => _descriptors;

    /// <inheritdoc />
    public ScheduledJobTemplateDescriptor? GetTemplate(string templateId) =>
        _handlers.TryGetValue(templateId, out var handler) ? handler.Descriptor : null;

    /// <inheritdoc />
    public bool TryGetHandler(string templateId, [NotNullWhen(true)] out IScheduledJobHandler? handler) =>
        _handlers.TryGetValue(templateId, out handler);
}
