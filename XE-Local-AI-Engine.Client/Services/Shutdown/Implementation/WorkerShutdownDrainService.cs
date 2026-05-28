namespace XE_Local_AI_Engine.Client.Services.Shutdown.Implementation;

using XE_Local_AI_Engine.Client.Services.Shutdown;

using System.Diagnostics;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;

public sealed class WorkerShutdownDrainService : IWorkerShutdownDrainService
{
    private readonly DeadLetterFlushService _deadLetterFlushService;
    private readonly IInvocationRunner _invocationRunner;
    private readonly ILogger<WorkerShutdownDrainService> _logger;
    private readonly WorkerShutdownDrainOptions _options;
    private readonly IWorkerEventDispatcher _workerEventDispatcher;
    private readonly IWorkerHubConnection _workerHubConnection;

    public WorkerShutdownDrainService(IWorkerEventDispatcher workerEventDispatcher,
        IInvocationRunner invocationRunner,
        DeadLetterFlushService deadLetterFlushService,
        IWorkerHubConnection workerHubConnection,
        IOptions<WorkerShutdownDrainOptions> options,
        ILogger<WorkerShutdownDrainService> logger)
    {
        _workerEventDispatcher = workerEventDispatcher ?? throw new ArgumentNullException(nameof(workerEventDispatcher));
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _deadLetterFlushService = deadLetterFlushService ?? throw new ArgumentNullException(nameof(deadLetterFlushService));
        _workerHubConnection = workerHubConnection ?? throw new ArgumentNullException(nameof(workerHubConnection));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WorkerShutdownDrainResult> DrainAsync(CancellationToken cancellationToken = default)
    {
        var elapsed = Stopwatch.StartNew();
        var diagnostics = new List<string>();
        var stopAcceptingCompleted = false;
        var activeInvocationsDrained = false;
        var deadLetterFlushCompleted = false;
        var workerHubDisconnected = false;
        var drainTimeout = GetDrainTimeout();

        _logger.LogInformation("Worker shutdown drain starting. Sequence: {DrainSequence}. Active invocation ceiling: {DrainTimeoutSeconds}s.",
            WorkerShutdownDrainOptions.DrainSequence,
            drainTimeout.TotalSeconds);

        try
        {
            _workerEventDispatcher.StopAcceptingRemoteInvocations();
            stopAcceptingCompleted = true;
            diagnostics.Add("stop-accepting-remote-invocations:completed");
            _logger.LogInformation("Worker shutdown drain stopped accepting new remote invocations.");
        }
        catch (Exception exception)
        {
            diagnostics.Add("stop-accepting-remote-invocations:failed");
            _logger.LogWarning("Worker shutdown drain could not stop accepting new remote invocations. Exception type: {ExceptionType}.",
                exception.GetType().Name);
        }

        try
        {
            var activeCountAtStart = _invocationRunner.ActiveInvocationCount;
            activeInvocationsDrained = await _invocationRunner
                                             .DrainActiveInvocationsAsync(drainTimeout, cancellationToken)
                                             .ConfigureAwait(false);

            diagnostics.Add(activeInvocationsDrained
                ? "await-active-invocations:completed"
                : "await-active-invocations:timed-out");

            if (activeInvocationsDrained)
            {
                _logger.LogInformation("Worker shutdown drain completed active invocation wait. Active invocations at start: {ActiveInvocationCount}.",
                    activeCountAtStart);
            }
            else
            {
                _logger.LogWarning(
                    "Worker shutdown drain timed out after {DrainTimeoutSeconds}s while waiting for active invocations. Active invocations at start: {ActiveInvocationCount}; remaining: {RemainingInvocationCount}.",
                    drainTimeout.TotalSeconds,
                    activeCountAtStart,
                    _invocationRunner.ActiveInvocationCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            diagnostics.Add("await-active-invocations:cancelled");
            _logger.LogWarning("Worker shutdown drain active invocation wait was cancelled.");
        }
        catch (Exception exception)
        {
            diagnostics.Add("await-active-invocations:failed");
            _logger.LogWarning("Worker shutdown drain active invocation wait failed. Exception type: {ExceptionType}.",
                exception.GetType().Name);
        }

        try
        {
            await _deadLetterFlushService.FlushAsync(cancellationToken).ConfigureAwait(false);
            deadLetterFlushCompleted = true;
            diagnostics.Add("flush-dead-letter-outbox:completed");
            _logger.LogInformation("Worker shutdown drain flushed the dead-letter outbox path.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            diagnostics.Add("flush-dead-letter-outbox:cancelled");
            _logger.LogWarning("Worker shutdown drain dead-letter flush was cancelled.");
        }
        catch (Exception exception)
        {
            diagnostics.Add("flush-dead-letter-outbox:failed");
            _logger.LogWarning("Worker shutdown drain dead-letter flush failed. Exception type: {ExceptionType}.",
                exception.GetType().Name);
        }

        try
        {
            await _workerHubConnection.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            workerHubDisconnected = true;
            diagnostics.Add("disconnect-worker-hub:completed");
            _logger.LogInformation("Worker shutdown drain disconnected WorkerHub.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            diagnostics.Add("disconnect-worker-hub:cancelled");
            _logger.LogWarning("Worker shutdown drain WorkerHub disconnect was cancelled.");
        }
        catch (Exception exception)
        {
            diagnostics.Add("disconnect-worker-hub:failed");
            _logger.LogWarning("Worker shutdown drain WorkerHub disconnect failed. Exception type: {ExceptionType}.",
                exception.GetType().Name);
        }

        elapsed.Stop();

        var result = new WorkerShutdownDrainResult(stopAcceptingCompleted,
            activeInvocationsDrained,
            deadLetterFlushCompleted,
            workerHubDisconnected,
            elapsed.Elapsed,
            diagnostics);

        if (result.Succeeded)
        {
            _logger.LogInformation("Worker shutdown drain completed successfully in {ElapsedMilliseconds}ms.",
                result.Elapsed.TotalMilliseconds);
        }
        else
        {
            _logger.LogWarning("Worker shutdown drain completed with incomplete steps in {ElapsedMilliseconds}ms. Diagnostics: {Diagnostics}.",
                result.Elapsed.TotalMilliseconds,
                result.Diagnostics);
        }

        return result;
    }

    private TimeSpan GetDrainTimeout()
    {
        if (_options.DrainTimeout > TimeSpan.Zero)
        {
            return _options.DrainTimeout;
        }

        _logger.LogWarning("Worker shutdown drain timeout configuration was invalid. Falling back to {DefaultDrainTimeoutSeconds}s.",
            WorkerShutdownDrainOptions.DefaultDrainTimeout.TotalSeconds);
        return WorkerShutdownDrainOptions.DefaultDrainTimeout;
    }
}
