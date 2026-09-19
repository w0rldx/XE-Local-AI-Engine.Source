namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Represents capability reporter: the Ollama runtime/model preflight, driven through the
///     <see cref="ModelCapabilityProber" /> behind the <see cref="ICapabilityReporter" /> facade.
/// </summary>
internal sealed class CapabilityReporter : ICapabilityReporter
{
    private readonly ILogger<CapabilityReporter> _logger;
    private readonly ModelCapabilityProber _prober;

    // Read LIVE, never captured. See ResolveDefaultModelAsync.
    private readonly INodeRuntimeSettings _runtimeSettings;

    public CapabilityReporter(ModelCapabilityProber prober,
        INodeRuntimeSettings runtimeSettings,
        ILogger<CapabilityReporter> logger)
    {
        _prober = prober ?? throw new ArgumentNullException(nameof(prober));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     The effective default chat model, resolved LIVE through <see cref="INodeRuntimeSettings" /> on every preflight.
    /// </summary>
    /// <remarks>
    ///     This used to be captured in the constructor straight from configuration:
    ///     <c>configuration.GetValue&lt;string&gt;("Agent:LocalChat:DefaultModel") ?? configuration.GetValue&lt;string&gt;("Ollama:ChatModel")</c>.
    ///     That broke the precedence contract stated on <see cref="INodeRuntimeSettings" /> itself — "consumers must read
    ///     migrated values through this surface, never via the appsettings binding of a migrated field, which is the seed
    ///     only". Because <c>Agent:LocalChat:DefaultModel</c> IS seeded in <c>appsettings.json</c>, the first branch always
    ///     won and the operator's stored default model was never consulted at all — not merely stale until restart, but
    ///     ignored permanently. The node then reported its fallback capability against a model the operator had moved away
    ///     from (and which, on a fresh node, is not installed).
    ///     <see cref="INodeRuntimeSettings.GetDefaultModelNameAsync" /> applies <c>stored &gt; seed</c> with that same
    ///     appsettings key as the seed, so this is behaviour-preserving when nothing is stored and correct when something is.
    /// </remarks>
    private Task<string> ResolveDefaultModelAsync(CancellationToken cancellationToken) =>
        _runtimeSettings.GetDefaultModelNameAsync(cancellationToken);

    public async Task<bool> VerifyOllamaAndModelAsync(string? modelName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!await _prober.IsRuntimeReachableAsync(cancellationToken))
            {
                _logger.LogWarning("Ollama is not reachable during capability preflight.");
                return false;
            }
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug(exception, "Ollama preflight: local endpoint not reachable (expected in desktop mode without an Ollama daemon).");
            return false;
        }

        var installedModels = await _prober.GetInstalledModelNamesAsync(cancellationToken);
        if (installedModels.Count == 0)
        {
            _logger.LogWarning("Ollama is reachable but no local models are installed.");
            return false;
        }

        var defaultModel = await ResolveDefaultModelAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(modelName))
        {
            return installedModels.Contains(defaultModel, StringComparer.OrdinalIgnoreCase);
        }

        if (installedModels.Contains(modelName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var canFallback = installedModels.Contains(defaultModel, StringComparer.OrdinalIgnoreCase);
        if (canFallback)
        {
            _logger.LogWarning("Requested model '{RequestedModel}' not available, using fallback '{FallbackModel}'.",
                modelName,
                defaultModel);
        }
        else
        {
            _logger.LogWarning("Requested model '{RequestedModel}' is unavailable and fallback model '{FallbackModel}' is not installed.",
                modelName,
                defaultModel);
        }

        return canFallback;
    }
}
