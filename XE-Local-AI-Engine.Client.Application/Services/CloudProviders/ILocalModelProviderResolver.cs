namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Routes a model to the local runtime that serves it (Lane A plan §7.5). Two lookups compose the route:
///     <c>ModelName → ProviderName</c> over the persisted per-model→provider map (with a configured default for
///     unmapped models, §6.1), then <c>ProviderName → <see cref="ILocalModelProvider" /></c> over the registered
///     provider set (llama-server + the optional Ollama provider, decision #14).
/// </summary>
/// <remarks>
///     Singleton-safe: the provider set is captured once, and the per-model map is read through a fresh scope on each
///     call so the singleton model-routing client / preview resolver can consume this without a scope-lifetime
///     mismatch. Provider keys are matched case-insensitively.
/// </remarks>
public interface ILocalModelProviderResolver
{
    /// <summary>
    ///     The hard cap on concurrently-loaded <c>(model, role)</c> processes (decision #18). The preview-workflow
    ///     resolver reads this to reject-at-start when a graph's distinct-model count would exceed it (plan §7.6).
    /// </summary>
    int MaxLoadedProcesses { get; }

    /// <summary>
    ///     Resolves the provider key that serves <paramref name="modelName" />: the persisted map entry when present,
    ///     otherwise the configured default provider (§6.1). Never returns <c>null</c>.
    /// </summary>
    Task<string> ResolveProviderNameForModelAsync(string modelName, CancellationToken cancellationToken = default);

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
}
