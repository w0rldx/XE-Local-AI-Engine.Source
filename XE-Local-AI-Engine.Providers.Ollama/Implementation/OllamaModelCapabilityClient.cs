namespace XE_Local_AI_Engine.Providers.Ollama.Implementation;

using OllamaSharp;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Ollama implementation of <see cref="IModelCapabilityClient" />: thin pass-throughs over
///     <see cref="IOllamaApiClient" /> that normalize raw OllamaSharp responses into provider-neutral snapshots.
/// </summary>
/// <remarks>
///     Methods intentionally do not catch transport failures, so an unreachable endpoint surfaces the same
///     <see cref="System.Net.Http.HttpRequestException" /> the application capability reporter already classifies.
/// </remarks>
public sealed class OllamaModelCapabilityClient : IModelCapabilityClient
{
    private readonly IOllamaApiClient _ollamaClient;

    /// <summary>Creates a capability client over the configured Ollama API client.</summary>
    public OllamaModelCapabilityClient(IOllamaApiClient ollamaClient)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
    }

    /// <inheritdoc />
    public Task<bool> IsRuntimeReachableAsync(CancellationToken ct)
    {
        return _ollamaClient.IsRunningAsync(ct);
    }

    /// <inheritdoc />
    public async Task<string?> GetRuntimeVersionAsync(CancellationToken ct)
    {
        return await _ollamaClient.GetVersionAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledModelEntry>> ListInstalledModelsAsync(CancellationToken ct)
    {
        var models = await _ollamaClient.ListLocalModelsAsync(ct).ConfigureAwait(false);
        return models.Select(model => new InstalledModelEntry(model.Name, model.Digest)).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunningModelSnapshot>> ListRunningModelsAsync(CancellationToken ct)
    {
        var runningModels = await _ollamaClient.ListRunningModelsAsync(ct).ConfigureAwait(false);
        return runningModels
               .Select(RunningModelSnapshotMapper.ToSnapshot)
               .ToArray();
    }

    /// <inheritdoc />
    public async Task<ModelCapabilityDetail> GetModelDetailAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var response = await _ollamaClient.ShowModelAsync(modelName, ct).ConfigureAwait(false);
        var maxContextTokens = OllamaModelInfoReader.TryGetContextLength(response.Info?.ExtraInfo, out var contextLength)
            ? contextLength
            : (int?)null;

        return new ModelCapabilityDetail(maxContextTokens);
    }
}
