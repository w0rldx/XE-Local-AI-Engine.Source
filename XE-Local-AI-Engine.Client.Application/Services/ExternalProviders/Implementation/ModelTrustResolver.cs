namespace XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Default <see cref="IModelTrustResolver" />: external ids resolve through the registry, everything else through
///     the cloud factory's own routing snapshot — the same source the send path routes from, so classification and
///     routing cannot diverge.
/// </summary>
/// <remarks>
///     Every failure mode of the external lookup collapses to <see cref="ModelTrustLocality.Unresolved" />, which the
///     gates treat as cloud. That is the point: a corrupt store, a connection deleted between the gate and the send, or
///     an id someone hand-edited into a saved agent must all withhold node-local data rather than assume the benign
///     case.
/// </remarks>
public sealed class ModelTrustResolver : IModelTrustResolver
{
    private readonly IActiveCloudChatClientFactory _cloudFactory;
    private readonly ILogger<ModelTrustResolver> _logger;
    private readonly IExternalProviderRegistry _registry;
    private readonly IExternalProviderRegistryCache _registryCache;

    public ModelTrustResolver(IExternalProviderRegistry registry,
        IExternalProviderRegistryCache registryCache,
        IActiveCloudChatClientFactory cloudFactory,
        ILogger<ModelTrustResolver> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _registryCache = registryCache ?? throw new ArgumentNullException(nameof(registryCache));
        _cloudFactory = cloudFactory ?? throw new ArgumentNullException(nameof(cloudFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ModelTrustLocality> ResolveAsync(string? modelId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            // No model to classify. Node-local is the right answer, not a fail-closed one: a model-less turn has no
            // egress to gate, and reporting cloud here would withhold tools from every turn that has not selected yet.
            return ModelTrustLocality.Local;
        }

        if (!ExternalModelId.HasExternalScheme(modelId))
        {
            return _cloudFactory.IsCloudProviderSelected(modelId) ? ModelTrustLocality.Cloud : ModelTrustLocality.Local;
        }

        var registration = await TryResolveExternalCoreAsync(modelId, cancellationToken).ConfigureAwait(false);
        return ToLocality(registration);
    }

    /// <inheritdoc />
    public async Task<ExternalProviderModelRegistration?> TryResolveExternalAsync(string? modelId, CancellationToken cancellationToken = default)
    {
        return ExternalModelId.HasExternalScheme(modelId)
            ? await TryResolveExternalCoreAsync(modelId, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <inheritdoc />
    public ModelTrustLocality? ClassifyExternalCached(string? modelId)
    {
        if (!ExternalModelId.HasExternalScheme(modelId))
        {
            return null;
        }

        // A cold cache is NOT "not registered". The startup reconciliation pass primes the snapshot, so this branch is
        // the pre-boot window — during which withholding is the only safe answer.
        return _registryCache.TryClassifyCached(modelId, out var registration)
            ? ToLocality(registration)
            : ModelTrustLocality.Unresolved;
    }

    private static ModelTrustLocality ToLocality(ExternalProviderModelRegistration? registration)
    {
        if (registration is null)
        {
            return ModelTrustLocality.Unresolved;
        }

        return registration.Connection.Locality == ExternalProviderLocality.Local
            ? ModelTrustLocality.Local
            : ModelTrustLocality.Cloud;
    }

    private async Task<ExternalProviderModelRegistration?> TryResolveExternalCoreAsync(string modelId, CancellationToken cancellationToken)
    {
        try
        {
            return await _registry.TryResolveAsync(modelId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Swallowed to Unresolved rather than propagated: the gates that call this must reach a decision, and the
            // decision a failure earns is "withhold". Warning, not Debug — an unreadable external store is a real
            // configuration fault the operator has to see, unlike a routine miss.
            _logger.LogWarning(exception, "External provider trust for '{ModelId}' could not be resolved; failing closed to cloud locality.", modelId);
            return null;
        }
    }
}
