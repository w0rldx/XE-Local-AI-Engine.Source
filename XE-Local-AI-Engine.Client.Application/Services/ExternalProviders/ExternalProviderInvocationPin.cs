namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Resolves the external bindings a logical invocation is authorized against, for its caller to pin.
/// </summary>
/// <remarks>
///     <para>
///         A turn decides ONCE — before its first send — which tools the active model may be offered, and that decision
///         reads the connection's declared locality. A multi-round tool loop then sends several times. Between two of
///         those sends the operator can edit the connection, and without a pin the later sends simply follow the new
///         configuration while still carrying the tools (and the node-local data in their results) that only the OLD
///         declaration earned. The pins are read here, where the invocation's models are resolved, and verified in the
///         transport on every send.
///     </para>
///     <para>
///         WHY this only RESOLVES and never opens the scope: the ambient set is an <see cref="AsyncLocal{T}" />, and an
///         <c>async</c> method's writes to one are invisible to its caller — the caller's execution context is restored
///         when the call returns. A helper that awaited a registry read and then seeded the scope itself therefore
///         seeded nothing at all, and every send it was meant to cover ran unpinned. So the async half (the registry
///         read) lives here and the caller opens <see cref="ExternalProviderBindingPinScope.Begin" /> SYNCHRONOUSLY, in
///         the frame the pinned sends actually run in.
///     </para>
///     <para>
///         Failing to read a binding is deliberately NOT an error. An <c>ext:</c> id that does not resolve is already
///         fail-closed twice over — the tool gate classifies it Unresolved and withholds, and the transport refuses the
///         send outright — so the honest thing here is to return nothing and let those gates speak.
///     </para>
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
    ///     The pins for every external id among <paramref name="modelIds" />. For a fan-out whose branches each run
    ///     their own model — an orchestration's participants, a spawned sub-agent — where every branch needs its own
    ///     pin but they share one async flow, so one scope has to carry them all.
    /// </summary>
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
                binding = await registry.TryResolveBindingAsync(modelId, cancellationToken).ConfigureAwait(false);
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
