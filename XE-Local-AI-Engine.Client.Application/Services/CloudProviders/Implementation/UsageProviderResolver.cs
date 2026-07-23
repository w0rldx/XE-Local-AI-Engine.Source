namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Default <see cref="IUsageProviderResolver" />: a cloud-first, local-fallback classification composed from the two
///     existing routing seams — <see cref="IActiveCloudChatClientFactory.ResolveActiveCloudProviderName" /> (codex/azure)
///     and <see cref="ILocalModelProviderResolver.ResolveProviderNameForModelAsync" /> (llamacpp/ollama) — folded through
///     <see cref="UsageProviderClassifier" />. Correctness of terminalization outranks attribution accuracy, so every
///     failure path swallows to <see cref="AgentUsageProviders.Unknown" /> and the local lookup is bounded by a short
///     timeout so a dead runtime probe can never stall the write.
/// </summary>
internal sealed class UsageProviderResolver : IUsageProviderResolver
{
    // Bounds the local per-model→provider lookup (a scoped SQLite read) so a stalled probe can never block terminalization.
    private static readonly TimeSpan ResolutionTimeout = TimeSpan.FromSeconds(2);

    private readonly IActiveCloudChatClientFactory _cloudFactory;
    private readonly ILogger<UsageProviderResolver> _logger;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly TimeProvider _timeProvider;

    public UsageProviderResolver(IActiveCloudChatClientFactory cloudFactory,
        ILocalModelProviderResolver providerResolver,
        TimeProvider timeProvider,
        ILogger<UsageProviderResolver> logger)
    {
        _cloudFactory = cloudFactory ?? throw new ArgumentNullException(nameof(cloudFactory));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> ResolveAsync(string? modelName, CancellationToken cancellationToken = default)
    {
        // No model to attribute (interrupted / no-model / platform-thin envelope) → unknown, WITHOUT consulting the
        // factory (whose node-default selection would otherwise mislabel a model-less turn as the signed-in cloud provider).
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return AgentUsageProviders.Unknown;
        }

        // Cloud takes precedence: a turn that reached a selected cloud provider is attributed there. Snapshot-cached and
        // synchronous, but guarded anyway — attribution must never throw out of terminalization.
        string? cloudProviderName;
        try
        {
            cloudProviderName = _cloudFactory.ResolveActiveCloudProviderName(modelName);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Cloud provider resolution failed for usage attribution; treating the turn as non-cloud.");
            cloudProviderName = null;
        }

        if (!string.IsNullOrWhiteSpace(cloudProviderName))
        {
            return UsageProviderClassifier.Classify(cloudProviderName, localProviderName: null);
        }

        // Local: which runtime serves the model (llama.cpp vs Ollama). Bounded so a stalled scoped read can never hang
        // terminalization; any failure/timeout degrades to unknown.
        string? localProviderName;
        try
        {
            using var timeoutSource = new CancellationTokenSource(ResolutionTimeout, _timeProvider);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            localProviderName = await _providerResolver.ResolveProviderNameForModelAsync(modelName, linkedSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Local provider resolution failed or timed out for usage attribution; recording the turn as unknown.");
            localProviderName = null;
        }

        return UsageProviderClassifier.Classify(cloudProviderName: null, localProviderName);
    }
}
