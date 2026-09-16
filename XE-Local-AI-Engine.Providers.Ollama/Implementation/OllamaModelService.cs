namespace XE_Local_AI_Engine.Providers.Ollama.Implementation;

using System.Runtime.CompilerServices;
using OllamaSharp;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

public sealed class OllamaModelService : IOllamaModelService, IDisposable
{
    private readonly IOllamaApiClient _ollamaClient;
    private readonly SemaphoreSlim _pullSemaphore = new(initialCount: 1, maxCount: 1);

    public OllamaModelService(IOllamaApiClient ollamaClient)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
    }

    public void Dispose()
    {
        _pullSemaphore.Dispose();
    }

    public async Task<IEnumerable<OllamaModelSummary>> ListLocalModelsAsync(CancellationToken ct = default)
    {
        var models = await _ollamaClient.ListLocalModelsAsync(ct).ConfigureAwait(false);

        return models.Select(static model => new OllamaModelSummary(model.ReadModelName(),
                                 model.Digest ?? string.Empty,
                                 model.Size,
                                 // Explicit ctor (MA0132) with the same semantics the endpoint mapper applied per call
                                 // site before this type existed: the daemon reports UTC, so stamp the kind rather than
                                 // letting the local-time conversion shift the instant.
                                 new DateTimeOffset(DateTime.SpecifyKind(model.ModifiedAt, DateTimeKind.Utc)),
                                 model.Details?.Family,
                                 model.Details?.ParameterSize,
                                 model.Details?.QuantizationLevel))
                     .ToArray();
    }

    public async Task<OllamaModelDetails> ShowModelDetailsAsync(string modelName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var response = await _ollamaClient.ShowModelAsync(modelName, ct).ConfigureAwait(false);
        var maxContextTokens = OllamaModelInfoReader.TryGetContextLength(response.Info?.ExtraInfo, out var contextLength)
            ? contextLength
            : (int?)null;

        return new OllamaModelDetails(maxContextTokens,
            response.Capabilities ?? [],
            response.Template,
            response.System,
            response.License);
    }

    public async IAsyncEnumerable<PullProgress> PullModelAsync(string modelName,
        [EnumeratorCancellation]
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        await _pullSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await foreach (var response in _ollamaClient.PullModelAsync(modelName, ct).ConfigureAwait(false))
            {
                if (response is not null)
                {
                    yield return OllamaPullProgressMapper.ToPullProgress(modelName, response);
                }
            }
        }
        finally
        {
            _pullSemaphore.Release();
        }
    }

    public Task DeleteModelAsync(string modelName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _ollamaClient.DeleteModelAsync(modelName, ct);
    }

    public async Task<IReadOnlyList<RunningModelSnapshot>> ListRunningModelsAsync(CancellationToken ct = default)
    {
        var runningModels = await _ollamaClient.ListRunningModelsAsync(ct).ConfigureAwait(false);
        return runningModels
               .Select(RunningModelSnapshotMapper.ToSnapshot)
               .ToArray();
    }

    public Task UnloadModelAsync(string modelName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // keep_alive=0 sets the requested model's expiry timer to zero. Per Ollama's scheduler, an in-flight generation
        // completes before the model is evicted, so this is graceful. Unloading a model the runtime does not currently
        // hold is a harmless no-op, which keeps the eject action idempotent.
        return OllamaModelUnloader.UnloadAsync(_ollamaClient, modelName, ct);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            await _ollamaClient.ListLocalModelsAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsLoopbackModelInstalledAsync(string modelName, CancellationToken ct = default)
    {
        // Uri.IsLoopback is the same fact the composition-time SSRF guard enforces, read without throwing. A remote
        // endpoint makes every Ollama model ineligible. (A disabled runtime never reaches this implementation at all:
        // the gate-off composition root registers UnavailableOllamaModelService, which answers false outright.)
        if (!_ollamaClient.Uri.IsLoopback)
        {
            return false;
        }

        try
        {
            var models = await _ollamaClient.ListLocalModelsAsync(ct).ConfigureAwait(false);
            // ReadModelName, not model.Name: current daemons fill "model" and older ones "name", and the sibling
            // ListLocalModelsAsync already reads it that way. Reading only Name made every model on a current daemon
            // look uninstalled.
            return models.Any(model => string.Equals(model.ReadModelName(), modelName, StringComparison.OrdinalIgnoreCase));
        }
        catch (HttpRequestException)
        {
            // An unreachable daemon is not an error here — it just means no Ollama model is installed as far as we know.
            return false;
        }
    }
}
