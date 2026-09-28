namespace XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IKnowledgeModelPrewarmer" />: ensures the configured reranker and the llama.cpp embedder are
///     running, on the host stopping token, one warm at a time.
/// </summary>
/// <remarks>
///     Capacity admission happens inside the supervisor. After a failed or refused warm, requests are ignored for
///     <see cref="RetryCooldown" /> so repeated searches do not re-run the admission's VRAM probe; a successful warm
///     sets no cooldown, since re-ensuring a running process is a cheap reuse that also refreshes its idle timestamp.
/// </remarks>
public sealed partial class KnowledgeModelPrewarmer : IKnowledgeModelPrewarmer
{
    /// <summary>Stable EventId of the Warning logged when a companion warm fails or is refused.</summary>
    public const int WarmFailedEventId = 4902;

    internal static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);

    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IEmbeddingModelResolver _embeddingModelResolver;
    private readonly KnowledgeBaseOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KnowledgeModelPrewarmer> _logger;
    private readonly Lock _gate = new();
    private Task _inflight = Task.CompletedTask;
    private long? _cooldownStartedAt;

    public KnowledgeModelPrewarmer(ILlamaServerProcessSupervisor supervisor,
        ILocalModelProviderResolver providerResolver,
        IEmbeddingModelResolver embeddingModelResolver,
        IOptions<KnowledgeBaseOptions> options,
        IHostApplicationLifetime lifetime,
        TimeProvider timeProvider,
        ILogger<KnowledgeModelPrewarmer> logger)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _embeddingModelResolver = embeddingModelResolver ?? throw new ArgumentNullException(nameof(embeddingModelResolver));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RequestWarm()
    {
        _ = StartWarm();
    }

    /// <summary>Starts a warm unless one is running or the cooldown holds; returns the warm in flight, for tests.</summary>
    internal Task StartWarm()
    {
        lock (_gate)
        {
            if (!_inflight.IsCompleted)
            {
                return _inflight;
            }

            if (_cooldownStartedAt is { } started && _timeProvider.GetElapsedTime(started) < RetryCooldown)
            {
                return _inflight;
            }

            _cooldownStartedAt = null;

            // Task.Run so no part of the warm, not even its synchronous prefix, runs on the caller's thread.
            _inflight = Task.Run(() => WarmAsync(_lifetime.ApplicationStopping), CancellationToken.None);
            return _inflight;
        }
    }

    private async Task WarmAsync(CancellationToken cancellationToken)
    {
        var failed = false;
        if (!string.IsNullOrWhiteSpace(_options.RerankerModelName))
        {
            failed |= !await TryEnsureAsync(_options.RerankerModelName, ModelRole.Reranker, cancellationToken);
        }

        var embedder = await TryResolveLlamaCppEmbedderAsync(cancellationToken);
        if (embedder is not null)
        {
            failed |= !await TryEnsureAsync(embedder, ModelRole.Embedding, cancellationToken);
        }

        if (failed)
        {
            lock (_gate)
            {
                _cooldownStartedAt = _timeProvider.GetTimestamp();
            }
        }
    }

    // The embedder warms only on the llama.cpp provider and only under a confidently resolved installed name: the name
    // the search and ingestion lanes embed with, so the warm process is the one they reuse.
    private async Task<string?> TryResolveLlamaCppEmbedderAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_options.EmbeddingProviderName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var provider = _providerResolver.ResolveProvider(_options.EmbeddingProviderName);
            var resolution = await _embeddingModelResolver.ResolveAsync(provider, cancellationToken);
            return resolution.IsConfident ? resolution.Name : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WarmFailed(_logger, _options.EmbeddingModelName, ModelRole.Embedding, exception.GetType().Name);
            return null;
        }
    }

    private async Task<bool> TryEnsureAsync(string modelName, ModelRole role, CancellationToken cancellationToken)
    {
        try
        {
            await _supervisor.EnsureRunningAsync(modelName, role, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown: nothing to report and no cooldown worth setting.
            return true;
        }
        catch (Exception exception)
        {
            WarmFailed(_logger, modelName, role, exception.GetType().Name);
            return false;
        }
    }

    [LoggerMessage(EventId = WarmFailedEventId, Level = LogLevel.Warning,
        Message = "Knowledge companion warm for {ModelName} ({Role}) failed or was refused; searches use fusion order until it runs. Exception type: {ExceptionType}.")]
    private static partial void WarmFailed(ILogger logger, string modelName, ModelRole role, string exceptionType);
}
