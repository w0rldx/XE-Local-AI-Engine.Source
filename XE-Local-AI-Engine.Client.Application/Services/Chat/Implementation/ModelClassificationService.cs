namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Resolves effective model kinds over the classification store, lazily probing Ollama's <c>/api/show</c>
///     capabilities and caching them by content digest. The effective kind is <c>override ?? detected</c> (defaulting
///     to <see cref="ModelKind.Unknown" />); detection failures are swallowed so a list never fails because the
///     daemon is offline.
/// </summary>
internal sealed class ModelClassificationService(
    IModelClassificationStore store,
    IOllamaModelService ollamaModelService,
    ILogger<ModelClassificationService> logger) : IModelClassificationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IModelClassificationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IOllamaModelService _ollamaModelService = ollamaModelService ?? throw new ArgumentNullException(nameof(ollamaModelService));
    private readonly ILogger<ModelClassificationService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<IReadOnlyDictionary<string, ModelClassificationResult>> ClassifyAsync(
        IEnumerable<(string ModelName, string? Digest)> models,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);

        // Keyed case-insensitively to match the store's NOCASE model_name collation. If the input lists the same name
        // more than once (differing only in case), the first occurrence wins and later duplicates are skipped.
        var results = new Dictionary<string, ModelClassificationResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var (modelName, digest) in models)
        {
            if (string.IsNullOrWhiteSpace(modelName) || results.ContainsKey(modelName))
            {
                continue;
            }

            var record = await ResolveAsync(modelName, digest, cancellationToken).ConfigureAwait(false);
            results[modelName] = ToResult(record);
        }

        return results;
    }

    public async Task<ModelClassificationResult> SetOverrideAsync(string modelName, ModelKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var record = await _store.SetOverrideAsync(modelName, kind, cancellationToken).ConfigureAwait(false);
        return ToResult(record);
    }

    public async Task<ModelClassificationResult> ResetOverrideAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // Clear the override and return the cleared row's effective kind (now DetectedKind, defaulting to Unknown). We do
        // NOT eagerly probe here: an override-only row carries a null Digest, so probing now would cache Digest=null and
        // the next list (which knows the real live digest) would see a mismatch and immediately re-probe — a redundant
        // double probe. Detection happens lazily on the next ClassifyAsync with the real digest, mirroring the override
        // (PUT) nuance where the React client invalidates the list rather than trusting the mutation response.
        var cleared = await _store.SetOverrideAsync(modelName, overrideKind: null, cancellationToken).ConfigureAwait(false);
        return ToResult(cleared);
    }

    /// <summary>
    ///     Loads the cached classification and lazily (re)detects when it is missing, stale (the supplied digest differs
    ///     from the cached one) or still unprobed (Unknown detected kind with no cached capabilities). A cache hit issues
    ///     no <c>/api/show</c> call.
    /// </summary>
    private async Task<ModelClassificationRecord> ResolveAsync(string modelName, string? digest, CancellationToken cancellationToken)
    {
        var record = await _store.GetByNameAsync(modelName, cancellationToken).ConfigureAwait(false);

        if (!NeedsDetection(record, digest))
        {
            return record!;
        }

        var detected = await TryDetectAsync(modelName, digest, cancellationToken).ConfigureAwait(false);

        // On a detection failure fall back to the cached record (preserving any override) or a synthetic Unknown so the
        // model still appears in the list — it just cannot be classified until the daemon is reachable again.
        return detected ?? record ?? UnknownRecord(modelName, digest);
    }

    private static bool NeedsDetection(ModelClassificationRecord? record, string? digest)
    {
        if (record is null)
        {
            return true;
        }

        // A re-pull changes the content digest, invalidating the cached detection.
        if (!string.Equals(record.Digest, digest, StringComparison.Ordinal))
        {
            return true;
        }

        // A row that exists but was never successfully probed (Unknown with no cached capabilities) is worth retrying —
        // e.g. an override-only row, or a prior probe that ran while the daemon was offline.
        return record.DetectedKind == ModelKind.Unknown && string.IsNullOrEmpty(record.DetectedCapabilitiesJson);
    }

    /// <summary>
    ///     Probes <c>/api/show</c>, classifies the capabilities and upserts the detection cache. Returns <c>null</c>
    ///     (never throws) when the probe fails so callers can fall back to the cached record.
    /// </summary>
    private async Task<ModelClassificationRecord?> TryDetectAsync(string modelName, string? digest, CancellationToken cancellationToken)
    {
        try
        {
            var details = await _ollamaModelService.ShowModelDetailsAsync(modelName, cancellationToken).ConfigureAwait(false);
            var capabilities = details.Capabilities;
            var detectedKind = ModelKindDetector.FromCapabilities(capabilities, modelName);
            var capabilitiesJson = capabilities.Count > 0
                ? JsonSerializer.Serialize(capabilities, SerializerOptions)
                : null;

            return await _store.UpsertDetectedAsync(modelName, digest, detectedKind, capabilitiesJson, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Detection is best-effort: an offline or old daemon must not break the model list. Log and fall back.
            _logger.LogDebug(exception, "Model kind detection failed for {ModelName}; falling back to cached classification.", modelName);
            return null;
        }
    }

    private ModelClassificationResult ToResult(ModelClassificationRecord record)
    {
        var effectiveKind = record.OverrideKind ?? record.DetectedKind;
        return new ModelClassificationResult(
            record.ModelName,
            effectiveKind,
            record.DetectedKind,
            DeserializeCapabilities(record.DetectedCapabilitiesJson),
            record.OverrideKind is not null);
    }

    private IReadOnlyList<string> DeserializeCapabilities(string? capabilitiesJson)
    {
        if (string.IsNullOrEmpty(capabilitiesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(capabilitiesJson, SerializerOptions) ?? [];
        }
        catch (JsonException exception)
        {
            _logger.LogDebug(exception, "Stored capabilities JSON could not be deserialized; treating as empty.");
            return [];
        }
    }

    private static ModelClassificationRecord UnknownRecord(string modelName, string? digest)
    {
        return new ModelClassificationRecord(
            modelName,
            digest,
            ModelKind.Unknown,
            DetectedCapabilitiesJson: null,
            OverrideKind: null,
            DetectedAtUtc: null,
            UpdatedAtUtc: 0);
    }
}
