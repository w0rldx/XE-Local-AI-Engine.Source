namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Resolves the external bindings a logical invocation is authorized against, for its caller to pin.
/// </summary>
/// <remarks>
///     A turn decides ONCE, before its first send, which tools the model may be offered, from the connection's declared locality; a tool
///     loop then sends many times, so the pins are read here and verified in the transport on every send. This only RESOLVES them: an
///     <see cref="AsyncLocal{T}" /> written inside an <c>async</c> method is invisible to its caller, so the caller opens
///     <see cref="ExternalProviderBindingPinScope.Begin" /> SYNCHRONOUSLY, in the frame the pinned sends run in. Failing to read a
///     binding is NOT an error: an unresolved <c>ext:</c> id is fail-closed twice already — the tool gate withholds, the transport refuses.
/// </remarks>
public static class ExternalProviderInvocationPin
{
    /// <summary>
    ///     The pin for <paramref name="modelId" />, or an empty list when there is nothing to pin (a node-local or
    ///     cloud model, or an external id that does not resolve). Open the scope with
    ///     <see cref="ExternalProviderBindingPinScope.Begin(IReadOnlyList{ExternalProviderBindingPin})" />.
    /// </summary>
    public static Task<IReadOnlyList<ExternalProviderBindingPin>> ResolveAsync(IExternalProviderRegistry registry,
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        return ResolveAsync(registry, [modelId], cancellationToken);
    }

    /// <summary>
    ///     The pins for every external id among <paramref name="modelIds" />.
    /// </summary>
    /// <remarks>
    ///     For a fan-out whose branches each run their own model — an orchestration's participants, a spawned
    ///     sub-agent: every branch needs its own pin, but they share one async flow, so one scope has to carry them all.
    /// </remarks>
    public static async Task<IReadOnlyList<ExternalProviderBindingPin>> ResolveAsync(IExternalProviderRegistry registry,
        IEnumerable<string?> modelIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(modelIds);

        List<ExternalProviderBindingPin>? pins = null;
        var candidates = modelIds.Where(static modelId => ExternalModelId.HasExternalScheme(modelId))
                                 .Select(static modelId => modelId!)
                                 .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var modelId in candidates)
        {
            ExternalProviderBinding? binding;
            try
            {
                binding = await registry.TryResolveBindingAsync(modelId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                continue;
            }

            if (binding is not null)
            {
                (pins ??= []).Add(new ExternalProviderBindingPin(binding.Registration.ModelId,
                    binding.Generation,
                    binding.Locality,
                    binding.BaseAddress));
            }
        }

        return pins ?? (IReadOnlyList<ExternalProviderBindingPin>)[];
    }
}
