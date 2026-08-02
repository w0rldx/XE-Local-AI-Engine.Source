namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Default <see cref="IToolCapableModelRegistrar" /> over <see cref="IGgufModelStore" /> +
///     <see cref="INodeSettingsStore" />. Reads the template-detected <see cref="LocalModelDescriptor.IsToolCapable" />
///     from the installed-model descriptors and unions the capable names into the persisted allow-list.
/// </summary>
/// <remarks>
///     Matching is <see cref="StringComparison.OrdinalIgnoreCase" /> when deciding whether a name is ALREADY present, so
///     a case variant is not appended twice — but the name is stored exactly as the descriptor reports it, because the
///     gate compares with <see cref="StringComparison.Ordinal" />. Storing the descriptor's own casing is what makes the
///     stored entry match the model id the chat path actually presents.
/// </remarks>
internal sealed class ToolCapableModelRegistrar : IToolCapableModelRegistrar
{
    private readonly IGgufModelStore _ggufModelStore;
    private readonly ILogger<ToolCapableModelRegistrar> _logger;
    private readonly INodeSettingsStore _settingsStore;

    public ToolCapableModelRegistrar(IGgufModelStore ggufModelStore,
        INodeSettingsStore settingsStore,
        ILogger<ToolCapableModelRegistrar> logger)
    {
        _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> RegisterIfToolCapableAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        var installed = await _ggufModelStore.ListInstalledModelsAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = installed.FirstOrDefault(model =>
            string.Equals(model.ModelName, modelName, StringComparison.OrdinalIgnoreCase));

        if (descriptor is not { IsToolCapable: true })
        {
            return false;
        }

        var added = await AddAsync([descriptor.ModelName], cancellationToken).ConfigureAwait(false);
        if (added > 0)
        {
            _logger.LogInformation("Model {ModelName} advertises tool calling in its chat template; added it to the tool-capable model list.",
                descriptor.ModelName);
        }

        return added > 0;
    }

    public async Task<int> BackfillInstalledAsync(CancellationToken cancellationToken = default)
    {
        var installed = await _ggufModelStore.ListInstalledModelsAsync(cancellationToken).ConfigureAwait(false);
        var capable = installed.Where(model => model.IsToolCapable)
                               .Select(model => model.ModelName)
                               .Where(name => !string.IsNullOrWhiteSpace(name))
                               .ToArray();

        if (capable.Length == 0)
        {
            return 0;
        }

        var added = await AddAsync(capable, cancellationToken).ConfigureAwait(false);
        if (added > 0)
        {
            _logger.LogInformation("Added {Count} installed tool-capable model(s) to the tool-capable model list.", added);
        }

        return added;
    }

    /// <summary>
    ///     Unions <paramref name="modelNames" /> into the stored allow-list and saves only when something changed.
    ///     Returns the number of names added.
    /// </summary>
    /// <remarks>
    ///     The no-change early return matters: <c>SaveAsync</c> invalidates and re-primes the settings cache and is
    ///     reached on every completed download and every startup, so writing an identical list would churn the cache (and
    ///     the file) for nothing.
    /// </remarks>
    private async Task<int> AddAsync(IReadOnlyList<string> modelNames, CancellationToken cancellationToken)
    {
        var stored = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = stored.ToolCapableModels ?? [];

        // HashSet.Add doubles as the "was it already there" test, so this both de-dupes against the stored list and
        // de-dupes the incoming batch against itself in one pass.
        var present = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var fresh = modelNames.Where(present.Add).ToArray();
        if (fresh.Length == 0)
        {
            return 0;
        }

        var merged = new List<string>(existing);
        merged.AddRange(fresh);

        await _settingsStore.SaveAsync(stored with
        {
            ToolCapableModels = merged
        }, cancellationToken).ConfigureAwait(false);
        return fresh.Length;
    }
}
