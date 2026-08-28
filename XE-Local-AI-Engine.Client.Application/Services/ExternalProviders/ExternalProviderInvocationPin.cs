namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Pins the external binding a logical invocation is authorized against, for the duration of that invocation.
/// </summary>
/// <remarks>
///     <para>
///         A turn decides ONCE — before its first send — which tools the active model may be offered, and that decision
///         reads the connection's declared locality. A multi-round tool loop then sends several times. Between two of
///         those sends the operator can edit the connection, and without a pin the later sends simply follow the new
///         configuration while still carrying the tools (and the node-local data in their results) that only the OLD
///         declaration earned. The pin is read here, where the turn's model is resolved, and verified in the transport
///         on every send.
///     </para>
///     <para>
///         Failing to read a binding is deliberately NOT an error here. An <c>ext:</c> id that does not resolve is
///         already fail-closed twice over — the tool gate classifies it Unresolved and withholds, and the transport
///         refuses the send outright — so the honest thing for a diagnostic scope is to seed nothing and let those
///         gates speak.
///     </para>
/// </remarks>
public static class ExternalProviderInvocationPin
{
    /// <summary>
    ///     Seeds the ambient pin for <paramref name="modelId" />, or returns <see langword="null" /> when there is
    ///     nothing to pin (a node-local or cloud model, or an external id that does not resolve). The result is safe to
    ///     <c>using</c> either way.
    /// </summary>
    public static async Task<IDisposable?> BeginAsync(IExternalProviderRegistry registry,
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (!ExternalModelId.HasExternalScheme(modelId))
        {
            return null;
        }

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
            return null;
        }

        return binding is null
            ? null
            : ExternalProviderBindingPinScope.Begin(new ExternalProviderBindingPin(binding.Registration.ModelId,
                binding.Generation,
                binding.Locality,
                binding.Origin));
    }
}
