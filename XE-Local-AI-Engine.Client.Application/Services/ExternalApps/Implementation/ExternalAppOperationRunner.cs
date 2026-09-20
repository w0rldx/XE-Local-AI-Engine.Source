namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

/// <summary>Which operation a background entry is running. Reported so a cancel can say what it stopped.</summary>
public enum ExternalAppOperationKind
{
    Install = 0,
    Start = 1,
    Stop = 2,
    Restart = 3,
    Update = 4,
    Reset = 5,
    Uninstall = 6
}

/// <summary>Owns the background half of every mutating operation: one entry per instance, each with its own cancellation source and its own dependency-injection scope.</summary>
/// <remarks>
///     The scope is the point: this is a singleton and the instance store holds a scoped database context, so a
///     pipeline using the admitting request's services would write through a context the browser can dispose by
///     navigating away. Each operation opens <c>CreateAsyncScope</c> and resolves from there; nothing from the
///     request crosses the boundary, its cancellation token included. Each source is linked to
///     <see cref="IHostApplicationLifetime.ApplicationStopping" />, so a graceful shutdown cancels every pipeline.
/// </remarks>
internal sealed class ExternalAppOperationRunner
{
    private readonly ConcurrentDictionary<Guid, RunningOperation> _entries = new();
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ExternalAppOperationRunner> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public ExternalAppOperationRunner(IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<ExternalAppOperationRunner> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether the host is shutting down; a user's cancel is the opposite decision.</summary>
    /// <remarks>
    ///     A pipeline cancelled by THIS must leave its row transient and touch no container: applications keep
    ///     serving while the engine is down, and the transient row is the only durable record the boot reconciler
    ///     has of what was interrupted.
    /// </remarks>
    public bool IsShuttingDown => _lifetime.ApplicationStopping.IsCancellationRequested;

    /// <summary>Whether an operation currently holds this instance. Read by the reconciler and the observer.</summary>
    public bool IsRunning(Guid instanceId)
    {
        return _entries.ContainsKey(instanceId);
    }

    /// <summary>Starts <paramref name="pipeline" /> on its own scope, reporting the task it runs on; <see langword="false" /> when this instance already has an entry.</summary>
    /// <remarks>
    ///     Ownership of <paramref name="gateLease" /> transfers on a successful start: it is released in the
    ///     pipeline's <c>finally</c>, which is what keeps a second command answering "in flight" for exactly as long
    ///     as the work runs. A losing call disposes neither the lease nor anything else — its caller still owns both.
    /// </remarks>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "A successful start transfers the CancellationTokenSource to the pipeline's finally; a losing one disposes it here.")]
    public bool TryStart(Guid instanceId,
        ExternalAppOperationKind kind,
        IDisposable gateLease,
        Func<IServiceProvider, CancellationToken, Task> pipeline,
        out Task completion)
    {
        ArgumentNullException.ThrowIfNull(gateLease);
        ArgumentNullException.ThrowIfNull(pipeline);

        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
        var entry = new RunningOperation(source, kind);
        if (!_entries.TryAdd(instanceId, entry))
        {
            source.Dispose();
            completion = Task.CompletedTask;
            return false;
        }

        // CancellationToken.None on purpose: the pipeline's own cancellation is the entry's source, and passing the
        // ambient one would let a caller that never owned this work abandon its finally.
        completion = Task.Run(() => RunAsync(instanceId, entry, gateLease, pipeline), CancellationToken.None);
        return true;
    }

    /// <summary>
    ///     Requests cancellation of the operation on this instance. Returns <see langword="false" /> when nothing is
    ///     running, which is the caller's signal to answer "invalid transition" rather than to accept the command.
    /// </summary>
    public bool Cancel(Guid instanceId)
    {
        if (!_entries.TryGetValue(instanceId, out var entry))
        {
            return false;
        }

        try
        {
            // Forced sync: CancellationTokenSource.CancelAsync runs callbacks on the thread pool and surfaces a failing
            // one as the first inner exception, not the AggregateException the catch below is written against.
#pragma warning disable MA0045 // forced sync: synchronous cancellation contract (see comment above)
            entry.Source.Cancel();
#pragma warning restore MA0045
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The pipeline settled and unregistered between the lookup and the cancel. Nothing to stop.
            return false;
        }
        catch (AggregateException)
        {
            // A registered callback threw. Cancellation was still requested for all of them.
            return true;
        }
    }

    private async Task RunAsync(Guid instanceId,
        RunningOperation entry,
        IDisposable gateLease,
        Func<IServiceProvider, CancellationToken, Task> pipeline)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await pipeline(scope.ServiceProvider, entry.Source.Token);
        }
#pragma warning disable CA1031 // The pipeline is the last frame that can act; anything escaping it must not tear the process down.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // A pipeline settles its own row, so reaching here means the settling itself failed. Nothing above can
            // act on it, and letting it escape an unobserved Task.Run would be a process-level fault.
            _logger.LogError(exception,
                "The {Kind} operation on external application instance {InstanceId} ended with an unhandled failure.",
                entry.Kind,
                instanceId);
        }
        finally
        {
            _ = _entries.TryRemove(instanceId, out _);
            entry.Source.Dispose();
            gateLease.Dispose();
        }
    }

    private sealed class RunningOperation
    {
        public RunningOperation(CancellationTokenSource source, ExternalAppOperationKind kind)
        {
            Source = source;
            Kind = kind;
        }

        public CancellationTokenSource Source { get; }

        public ExternalAppOperationKind Kind { get; }
    }
}
