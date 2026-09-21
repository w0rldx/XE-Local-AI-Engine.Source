namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Retention sweeper for the on-disk AgentHome run directories, which nothing else ever deletes.
/// </summary>
/// <remarks>
///     A startup sweep runs before the periodic loop, so a node down past a run's window does not keep it until the
///     first tick. Three limits apply oldest-first — age, run count, total bytes — and every deletion passes the same
///     four gates: the directory name is one the node minted, the path resolves under the runs root, the directory is
///     not a link, and the execution lease is not held. A sweep failure is logged and never stops the service.
/// </remarks>
internal sealed partial class AgentHomeRunRetentionService : BackgroundService
{
    /// <summary>
    ///     How recently a run may have started and still be left alone regardless of every limit.
    /// </summary>
    /// <remarks>
    ///     Belt and braces behind the lease check: a just-finished run's <c>events.jsonl</c> can still be mid-append
    ///     when the walk observes it (the run logger holds no lock a sweep could take), and the mistakes are not
    ///     symmetric — deleting a live run destroys work irrecoverably, while keeping one for another interval costs
    ///     bytes the next sweep reclaims.
    /// </remarks>
    private static readonly TimeSpan DeletionGrace = TimeSpan.FromMinutes(15);

    /// <summary>Ceiling on entries one run's size walk visits, so a pathological tree cannot stall the sweep.</summary>
    private const int MaxMeasuredEntriesPerRun = 20000;

    private readonly string _dataDirectoryRoot;
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly IAgentHomeExecutionLeaseManager _leaseManager;
    private readonly ILogger<AgentHomeRunRetentionService> _logger;
    private readonly AgentHomeOptions _agentHomeOptions;
    private readonly AgentHomeRunRetentionOptions _options;
    private readonly TimeProvider _timeProvider;

    public AgentHomeRunRetentionService(IOptions<AgentHomeRunRetentionOptions> options,
        IOptions<AgentHomeOptions> agentHomeOptions,
        INodeDataDirectory dataDirectory,
        IAgentHomeIdentityProvider identityProvider,
        IAgentHomeExecutionLeaseManager leaseManager,
        TimeProvider timeProvider,
        ILogger<AgentHomeRunRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(agentHomeOptions);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _agentHomeOptions = agentHomeOptions.Value ?? throw new ArgumentNullException(nameof(agentHomeOptions));
        _dataDirectoryRoot = dataDirectory.Root;
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _leaseManager = leaseManager ?? throw new ArgumentNullException(nameof(leaseManager));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_agentHomeOptions.Enabled)
        {
            // Retention off, or the feature that writes the runs is off: nothing to sweep either way.
            return;
        }

        try
        {
            await SweepAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            SweepFailed(_logger, exception);
        }

        using var timer = new PeriodicTimer(_options.SweepInterval, _timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                SweepFailed(_logger, exception);
            }
        }
    }

    /// <summary>One sweep, exposed to the tests so a sweep can be observed without driving the whole service loop.</summary>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        var runsRoot = AgentHomeRunPaths.ResolveRunsRoot(_agentHomeOptions, _dataDirectoryRoot);
        if (!Directory.Exists(runsRoot))
        {
            return;
        }

        // Identity first: without it there is no lease key, so there is no way to tell an in-flight run from a
        // finished one. Fail closed — a sweep that cannot ask is a sweep that deletes nothing.
        var identity = await _identityProvider.GetAsync(cancellationToken);
        var leaseKey = new AgentHomeExecutionLeaseKey(identity.OwnerUserId, identity.NodeId);

        var now = _timeProvider.GetUtcNow();
        var runs = Classify(runsRoot, out var unclassified);
        if (unclassified > 0)
        {
            UnclassifiedRuns(_logger, unclassified);
        }

        if (runs.Count == 0)
        {
            return;
        }

        // Oldest first: every limit evicts from this end. The last entry is the newest run — never evicted, whatever
        // any limit says — and anything inside the grace window is skipped as the loop reaches it.
        runs.Sort(static (left, right) => left.StartedAt.CompareTo(right.StartedAt));
        var totalBytes = runs.Sum(static run => run.SizeBytes);
        var remainingRuns = runs.Count;

        var ageCutoff = _options.RetentionDays > 0 ? now.AddDays(-_options.RetentionDays) : (DateTimeOffset?)null;
        var byAge = 0;
        var byCount = 0;
        var byBytes = 0;
        var bytesReclaimed = 0L;
        var removed = new List<string>();

        for (var index = 0; index < runs.Count - 1; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = runs[index];

            // A negative age is a clock that moved backwards, and it lands inside the grace window for free.
            if (now - candidate.StartedAt < DeletionGrace)
            {
                continue;
            }

            var reason = ClassifyReason(candidate, remainingRuns, totalBytes, ageCutoff);
            if (reason is null)
            {
                continue;
            }

            // Re-read the lease immediately before the delete, never once for the whole sweep: a run that started
            // while the walk was measuring must stop the sweep here rather than after its directory is gone.
            if (_leaseManager.IsHeld(leaseKey))
            {
                SweepYieldedToRun(_logger, removed.Count);
                break;
            }

            if (!TryDelete(candidate))
            {
                continue;
            }

            totalBytes -= candidate.SizeBytes;
            bytesReclaimed += candidate.SizeBytes;
            remainingRuns--;
            removed.Add(candidate.RunId);
            switch (reason)
            {
                case DeletionReason.Age:
                    byAge++;
                    break;
                case DeletionReason.Count:
                    byCount++;
                    break;
                default:
                    byBytes++;
                    break;
            }
        }

        if (removed.Count > 0)
        {
            // Run ids are node-minted (`run-{unixMs}-{counter}`) and carry nothing of the model's, so naming them is
            // what makes the line an audit trail; the host path they sat at never appears.
            SweepCompleted(_logger, removed.Count, byAge, byCount, byBytes, bytesReclaimed, string.Join(separator: ',', removed));
        }
    }

    /// <summary>Which limit takes this run, or <see langword="null" /> when none does.</summary>
    private DeletionReason? ClassifyReason(RunCandidate candidate, int remainingRuns, long totalBytes, DateTimeOffset? ageCutoff)
    {
        if (ageCutoff is { } cutoff && candidate.StartedAt < cutoff)
        {
            return DeletionReason.Age;
        }

        // The count and byte limits measure what is still on disk, so each earlier deletion counts towards them and
        // the cap is reached exactly rather than overshot.
        if (_options.MaxRuns > 0 && remainingRuns > _options.MaxRuns)
        {
            return DeletionReason.Count;
        }

        return _options.MaxTotalBytes > 0 && totalBytes > _options.MaxTotalBytes ? DeletionReason.Bytes : null;
    }

    /// <summary>
    ///     The run directories this sweep may reason about, with the ones it must not classify counted out.
    /// </summary>
    private static List<RunCandidate> Classify(string runsRoot, out int unclassified)
    {
        var runs = new List<RunCandidate>();
        unclassified = 0;

        foreach (var path in Directory.EnumerateDirectories(runsRoot))
        {
            var name = Path.GetFileName(path);
            if (AgentHomeRunPaths.TryParseStartedAt(name) is not { } startedAt)
            {
                // A name the node did not mint is never deleted: the sweep cannot say how old it is, and a directory
                // it cannot classify is one it must leave alone.
                unclassified++;
                continue;
            }

            // Lexical containment, then the link check: the first keeps a "..", a rooted name or a trailing-separator
            // root out, and the second keeps the sweep from ever treating a link as the tree it points at.
            if (!PathContainment.IsUnderRoot(path, runsRoot) || AgentHomeRunPaths.IsLink(path))
            {
                unclassified++;
                continue;
            }

            runs.Add(new RunCandidate(path, name, startedAt, AgentHomeRunPaths.MeasureBytes(path, MaxMeasuredEntriesPerRun)));
        }

        return runs;
    }

    private bool TryDelete(RunCandidate candidate)
    {
        try
        {
            // Recursive delete never follows a link out of the tree — it removes the link itself — and the directory
            // the sweep was handed is already proven not to be one.
            Directory.Delete(candidate.Path, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // One unreadable run never aborts the sweep; the next tick tries again.
            DeleteFailed(_logger, candidate.RunId, exception);
            return false;
        }
    }

    private enum DeletionReason
    {
        Age,
        Count,
        Bytes
    }

    private readonly record struct RunCandidate(string Path, string RunId, DateTimeOffset StartedAt, long SizeBytes);

    [LoggerMessage(EventId = 4810, Level = LogLevel.Information,
        Message = "AgentHome run retention removed {DeletedCount} run(s) — {ByAge} past the age limit, {ByCount} over the run cap, {ByBytes} over the byte cap — reclaiming {BytesReclaimed} byte(s): {RunIds}.")]
    private static partial void SweepCompleted(ILogger logger, int deletedCount, int byAge, int byCount, int byBytes, long bytesReclaimed, string runIds);

    [LoggerMessage(EventId = 4811, Level = LogLevel.Debug,
        Message = "AgentHome run retention left {UnclassifiedCount} directory(ies) alone: the name is not one the node minted, or the entry is a link.")]
    private static partial void UnclassifiedRuns(ILogger logger, int unclassifiedCount);

    [LoggerMessage(EventId = 4812, Level = LogLevel.Debug,
        Message = "AgentHome run retention stopped after {DeletedCount} deletion(s): a run took the execution lease.")]
    private static partial void SweepYieldedToRun(ILogger logger, int deletedCount);

    [LoggerMessage(EventId = 4813, Level = LogLevel.Debug, Message = "AgentHome run retention could not delete run {RunId}.")]
    private static partial void DeleteFailed(ILogger logger, string runId, Exception exception);

    [LoggerMessage(EventId = 4814, Level = LogLevel.Warning, Message = "AgentHome run retention sweep failed.")]
    private static partial void SweepFailed(ILogger logger, Exception exception);
}
