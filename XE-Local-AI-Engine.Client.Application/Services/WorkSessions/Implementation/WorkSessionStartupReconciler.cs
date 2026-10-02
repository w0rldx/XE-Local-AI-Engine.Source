namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Collapses the sessions a crashed or restarted host left mid-flight to <c>Interrupted</c>, exactly once, at
///     startup.
/// </summary>
/// <remarks>
///     Registered after the chat module so orphaned chat rows are terminalized first: a session that resumes must not
///     find its conversation still holding a half-written turn.
/// </remarks>
public sealed class WorkSessionStartupReconciler : IHostedService
{
    private const string InterruptedReason = "The host restarted while the work session was in flight.";

    private readonly ILogger<WorkSessionStartupReconciler> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public WorkSessionStartupReconciler(IServiceScopeFactory scopeFactory,
        ILogger<WorkSessionStartupReconciler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Not gated on the feature switch: it is live, so a row a crash stranded while it was off must already be settled when it is turned on.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        var reconciled = await store.ReconcileRunningSessionsAsync(InterruptedReason, cancellationToken);
        if (reconciled > 0)
        {
            _logger.LogInformation("Reconciled {Count} in-flight work session(s) to Interrupted after host startup.", reconciled);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
