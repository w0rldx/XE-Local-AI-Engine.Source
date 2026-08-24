namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Collapses the sessions a crashed or restarted host left mid-flight to <c>Interrupted</c>, exactly once, at
///     startup. Registered after the chat module so orphaned chat rows are terminalized first: a session that resumes
///     must not find its conversation still holding a half-written turn.
/// </summary>
public sealed class WorkSessionStartupReconciler(IServiceScopeFactory scopeFactory,
    IOptions<WorkSessionOptions> options,
    ILogger<WorkSessionStartupReconciler> logger) : IHostedService
{
    private const string InterruptedReason = "The host restarted while the work session was in flight.";

    private readonly ILogger<WorkSessionStartupReconciler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly WorkSessionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            // The services stay registered when the feature is off, so the guard is here rather than in the container.
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        var reconciled = await store.ReconcileRunningSessionsAsync(InterruptedReason, cancellationToken).ConfigureAwait(false);
        if (reconciled > 0)
        {
            _logger.LogInformation("Reconciled {Count} in-flight work session(s) to Interrupted after host startup.", reconciled);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
