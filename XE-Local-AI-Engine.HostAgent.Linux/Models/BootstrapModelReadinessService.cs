namespace XE_Local_AI_Engine.HostAgent.Linux.Models;

using System.Runtime.CompilerServices;
using OllamaSharp;
using OllamaSharp.Models;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.HostAgent.Linux.Lifecycle;

/// <summary>
///     Application service for bootstrap model readiness behavior.
/// </summary>
public sealed class BootstrapModelReadinessService : IDisposable
{
    public const string DefaultBootstrapModel = "qwen3:0.6b";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<BootstrapModelReadinessService> _logger;
    private readonly IOllamaApiClient _ollamaClient;
    private readonly HostAgentRuntimeOptions _runtimeOptions;
    private readonly TimeProvider _timeProvider;

    public BootstrapModelReadinessService(HostAgentRuntimeOptions runtimeOptions,
        IOllamaApiClient ollamaClient,
        TimeProvider timeProvider,
        ILogger<BootstrapModelReadinessService> logger)
    {
        _runtimeOptions = runtimeOptions ?? throw new ArgumentNullException(nameof(runtimeOptions));
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsReady { get; private set; }

    public string BootstrapModel => _runtimeOptions.Manifest?.Models.BootstrapModel ?? DefaultBootstrapModel;

    public void Dispose()
    {
        _gate.Dispose();
    }

    public BootstrapModelReadinessSnapshot GetSnapshot()
    {
        return CreateSnapshot(IsReady,
            IsReady ? ["bootstrap-model-ready"] : ["bootstrap-model-not-ready"]);
    }

    public async Task<BootstrapModelReadinessSnapshot> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (IsReady)
        {
            return CreateSnapshot(true, ["bootstrap-model-ready"]);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady)
            {
                return CreateSnapshot(true, ["bootstrap-model-ready"]);
            }

            var modelName = BootstrapModel;
            if (!await _ollamaClient.IsRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                return CreateSnapshot(false, ["ollama-not-running"]);
            }

            if (await IsModelAvailableAsync(modelName, cancellationToken).ConfigureAwait(false))
            {
                IsReady = true;
                return CreateSnapshot(true, [$"bootstrap-model-present:{modelName}"]);
            }

            _logger.LogInformation("Pulling bootstrap model {BootstrapModel} before WorkerHub startup is allowed.", modelName);
            var lastPullStatus = "started";
            await foreach (var progress in PullModelAsync(modelName, cancellationToken).ConfigureAwait(false))
            {
                lastPullStatus = string.IsNullOrWhiteSpace(progress.Status) ? lastPullStatus : progress.Status;
            }

            if (!await IsModelAvailableAsync(modelName, cancellationToken).ConfigureAwait(false))
            {
                return CreateSnapshot(false, [$"bootstrap-model-unavailable-after-pull:{modelName}", $"last-pull-status:{lastPullStatus}"]);
            }

            IsReady = true;
            return CreateSnapshot(true, [$"bootstrap-model-pulled:{modelName}", $"last-pull-status:{lastPullStatus}"]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Bootstrap model readiness check failed.");
            return CreateSnapshot(false, [exception.Message]);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<PullProgress> PullModelAsync(string modelName,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        await foreach (var response in _ollamaClient.PullModelAsync(modelName, cancellationToken).ConfigureAwait(false))
        {
            if (response is null)
            {
                continue;
            }

            yield return new PullProgress
            {
                ModelName = modelName,
                Status = response.Status ?? string.Empty,
                CompletedBytes = response.Completed,
                TotalBytes = response.Total
            };
        }
    }

    private async Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken)
    {
        var localModels = await _ollamaClient.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);
        return localModels.Any(model => string.Equals(ReadModelName(model), modelName, StringComparison.OrdinalIgnoreCase));
    }

    private BootstrapModelReadinessSnapshot CreateSnapshot(bool isReady, IReadOnlyList<string> diagnostics)
    {
        return new BootstrapModelReadinessSnapshot(isReady, BootstrapModel, _timeProvider.GetUtcNow(), diagnostics);
    }

    private static string? ReadModelName(Model model)
    {
        return !string.IsNullOrWhiteSpace(model.ModelName) ? model.ModelName : model.Name;
    }
}

/// <summary>
///     Value object carrying bootstrap model readiness snapshot data.
/// </summary>
public sealed record BootstrapModelReadinessSnapshot(
    bool IsReady,
    string ModelName,
    DateTimeOffset ObservedAt,
    IReadOnlyList<string> Diagnostics);
