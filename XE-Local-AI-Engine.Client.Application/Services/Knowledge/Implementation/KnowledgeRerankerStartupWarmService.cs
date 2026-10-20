namespace XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Requests one knowledge companion warm at node start when a reranker is configured, so the first ambiguous search
///     finds it resident instead of degrading to fusion order.
/// </summary>
/// <remarks>
///     One-shot: later warms come from turns that offer the search tool, from searches (a skipped or cold rerank) and from
///     a saved reranker change. Best-effort: a failed settings read is logged and never stops the host. Details:
///     <c>docs/wiki/15-knowledge-base.md</c> ("Pre-warm").
/// </remarks>
public sealed partial class KnowledgeRerankerStartupWarmService : BackgroundService
{
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IKnowledgeModelPrewarmer _prewarmer;
    private readonly ILogger<KnowledgeRerankerStartupWarmService> _logger;

    public KnowledgeRerankerStartupWarmService(INodeRuntimeSettings runtimeSettings,
        IKnowledgeModelPrewarmer prewarmer,
        ILogger<KnowledgeRerankerStartupWarmService> logger)
    {
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _prewarmer = prewarmer ?? throw new ArgumentNullException(nameof(prewarmer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield first so host startup is never blocked on the settings read.
        await Task.Yield();

        try
        {
            if (!string.IsNullOrWhiteSpace(await _runtimeSettings.GetRerankerModelNameAsync(stoppingToken)))
            {
                _prewarmer.RequestWarm();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down during startup; nothing to warm.
        }
        catch (Exception exception)
        {
            StartupWarmSkipped(_logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The startup reranker warm was skipped because the reranker setting could not be read. Exception type: {ExceptionType}.")]
    private static partial void StartupWarmSkipped(ILogger logger, string exceptionType);
}
