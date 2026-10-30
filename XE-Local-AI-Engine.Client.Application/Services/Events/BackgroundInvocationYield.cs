namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Runs post-turn background model work only once the node's invocation slot has stayed free for an idle window,
///     and gives the slot back when a turn takes it mid-call.
/// </summary>
/// <remarks>
///     A take cancels the work, which reruns from the start at the next idle window, as often as it takes: a yielded job
///     is never dropped. Only the post-turn workers may use it; a path a turn awaits while holding the slot would wait
///     for itself. Every slot holder counts (chat, graph, scheduler, integrations, benchmarks), as background is lowest.
/// </remarks>
internal sealed class BackgroundInvocationYield
{
    /// <summary>How long the slot must stay free before background work starts.</summary>
    internal static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(2);

    private readonly IWorkerEventDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleWindow;
    private readonly ILogger<BackgroundInvocationYield> _logger;

    public BackgroundInvocationYield(IWorkerEventDispatcher dispatcher, TimeProvider timeProvider, ILogger<BackgroundInvocationYield> logger)
        : this(dispatcher, timeProvider, logger, IdleWindow)
    {
    }

    /// <summary>A yield over a different window; tests that do not exercise the wait pass <see cref="TimeSpan.Zero" />.</summary>
    internal BackgroundInvocationYield(IWorkerEventDispatcher dispatcher, TimeProvider timeProvider, ILogger<BackgroundInvocationYield> logger, TimeSpan idleWindow)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idleWindow = idleWindow;
    }

    /// <summary>Runs <paramref name="work" /> at the next idle window, rerunning it after every yield until it completes or throws.</summary>
    /// <param name="workName">A content-free label for the yield log line.</param>
    /// <param name="cancellationToken">Shutdown: cancels the wait and the work without a rerun.</param>
    public async Task RunWhenIdleAsync(string workName, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var completed = false;
        while (!completed)
        {
            var slotChanged = await WaitForIdleWindowAsync(cancellationToken);
            completed = await RunUntilSlotChangesAsync(workName, work, slotChanged, cancellationToken);
        }
    }

    // True when the work completed; false when a slot change cancelled it and it must rerun.
    private async Task<bool> RunUntilSlotChangesAsync(string workName, Func<CancellationToken, Task> work, Task slotChanged, CancellationToken cancellationToken)
    {
        using var yieldSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = work(yieldSource.Token);
        if (await Task.WhenAny(running, slotChanged) == running)
        {
            await running;
            return true;
        }

        await yieldSource.CancelAsync();
        try
        {
            await running;
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Whatever the cancelled call threw is the yield's doing, not a failure: no fault is recorded, the job reruns.
            _logger.LogInformation("Background {Work} yielded the invocation slot to a turn; it reruns at the next idle window.", workName);
            return false;
        }
    }

    // Returns the slot-change task armed when the window closed, so a take after that point still cancels the work.
    private async Task<Task> WaitForIdleWindowAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var slotChanged = _dispatcher.SlotChanged;
            if (_dispatcher.SlotHolder is null && await StaysUnchangedForWindowAsync(slotChanged, cancellationToken))
            {
                return slotChanged;
            }

            await slotChanged.WaitAsync(cancellationToken);
        }
    }

    private async Task<bool> StaysUnchangedForWindowAsync(Task slotChanged, CancellationToken cancellationToken)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var idle = Task.Delay(_idleWindow, _timeProvider, window.Token);
        if (await Task.WhenAny(idle, slotChanged) == idle)
        {
            await idle;
            return true;
        }

        // The slot was taken (or taken and freed) inside the window: stop its timer.
        await window.CancelAsync();
        return false;
    }
}
