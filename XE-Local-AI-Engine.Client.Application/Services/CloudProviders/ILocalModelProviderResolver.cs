namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Client.Services.Models;

/// <summary>
///     Routes a model to the local runtime that serves it. Two lookups compose the route:
///     <c>ModelName → ProviderName</c> over the persisted per-model→provider map (with a configured default for
///     unmapped models), then <c>ProviderName → <see cref="ILocalModelProvider" /></c> over the registered
///     provider set (llama-server + the optional Ollama provider).
/// </summary>
/// <remarks>
///     Singleton-safe: the provider set is captured once, and the per-model map is read through a fresh scope on each
///     call so the singleton model-routing client / preview resolver can consume this without a scope-lifetime
///     mismatch. Provider keys are matched case-insensitively.
/// </remarks>
public interface ILocalModelProviderResolver
{
    /// <summary>
    ///     The hard cap on concurrently-loaded <c>(model, role)</c> processes. The preview-workflow
    ///     resolver reads this to reject-at-start when a graph's distinct-model count would exceed it.
    /// </summary>
    int MaxLoadedProcesses { get; }

    /// <summary>
    ///     The provider that unmapped models route to (the configured default — <c>ollama</c> by configuration). Consumers that
    ///     operate provider-wide rather than per-model (for example a node health/inventory snapshot) resolve through
    ///     this so they keep a single, deterministic default provider under the multi-provider registration instead of
    ///     binding to whichever <see cref="ILocalModelProvider" /> happened to be registered last.
    /// </summary>
    ILocalModelProvider DefaultProvider { get; }

    /// <summary>
    ///     Resolves the provider key that serves <paramref name="modelName" />: the persisted map entry when present,
    ///     otherwise the configured default provider. Never returns <c>null</c>.
    /// </summary>
    Task<string> ResolveProviderNameForModelAsync(string modelName, CancellationToken cancellationToken = default);

    /// <summary>Resolves through an already-held installed-model composite or map-only read lease without nesting.</summary>
    Task<string> ResolveProviderNameForModelAsync(string modelName,
        IModelProviderMapReadLease existingLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existingLease);
        return ResolveProviderNameForModelAsync(modelName, cancellationToken);
    }

    /// <summary>
    ///     Resolves the registered <see cref="ILocalModelProvider" /> whose <see cref="ILocalModelProvider.ProviderName" />
    ///     equals <paramref name="providerName" /> (case-insensitive).
    /// </summary>
    /// <exception cref="InvalidOperationException">No registered provider matches <paramref name="providerName" />.</exception>
    ILocalModelProvider ResolveProvider(string providerName);

    /// <summary>
    ///     Convenience composition of <see cref="ResolveProviderNameForModelAsync" /> + <see cref="ResolveProvider" />:
    ///     resolves the provider that serves <paramref name="modelName" /> in one call.
    /// </summary>
    Task<ILocalModelProvider> ResolveProviderForModelAsync(string modelName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Drops the short-TTL <c>ModelName → ProviderName</c> cache the resolver keeps (AUD4-16). Callers that mutate the
    ///     persisted per-model→provider map (a GGUF download mapping to <c>llamacpp</c>, the Ollama backfill mapping to
    ///     <c>ollama</c>) invoke this after a successful write so a subsequent lookup observes the new row immediately
    ///     rather than after the TTL. Cheap and idempotent; a no-op when nothing is cached.
    /// </summary>
    void InvalidateModelProviderMap();
}
