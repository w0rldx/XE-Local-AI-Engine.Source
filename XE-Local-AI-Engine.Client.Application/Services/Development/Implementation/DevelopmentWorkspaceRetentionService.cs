namespace XE_Local_AI_Engine.Client.Services.Development.Implementation;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Retention sweeper for the per-task Development Mode workspace clones and runtime directories, which nothing
///     else deletes.
/// </summary>
/// <remarks>
///     A startup sweep runs before the periodic loop. A task's two directories go together, and only when the task is
///     <c>Completed</c> or <c>Cancelled</c>, older than the retention age and has no live attempt; a directory with no
///     task row, and a partial first clone, go once past the grace period. Every deletion needs a node-minted name, a
///     path under the data root and no link. A sweep or delete failure is logged, never fatal; the next tick retries.
/// </remarks>
internal sealed partial class DevelopmentWorkspaceRetentionService : BackgroundService
{
    private const string PartialCloneMarker = ".partial-";

    private readonly string _dataDirectoryRoot;
    private readonly ILogger<DevelopmentWorkspaceRetentionService> _logger;
    private readonly DevelopmentWorkspaceRetentionOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public DevelopmentWorkspaceRetentionService(IOptions<DevelopmentWorkspaceRetentionOptions> options,
        INodeDataDirectory dataDirectory,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<DevelopmentWorkspaceRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _dataDirectoryRoot = Path.GetFullPath(dataDirectory.Root);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
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
        var workspacesRoot = Path.Combine(_dataDirectoryRoot, "development", "workspaces");
        var runtimeRoot = Path.Combine(_dataDirectoryRoot, "development", "runtime");

        // A link at or above either root carries every delete through it, and the containment check is lexical.
        if (HasLinkBelowDataRoot(workspacesRoot) || HasLinkBelowDataRoot(runtimeRoot))
        {
            SweepRefusedLinkedRoot(_logger);
            return;
        }

        var pairs = new HashSet<TaskKey>();
        var partials = new List<PartialClone>();
        Collect(workspacesRoot, pairs, partials);
        Collect(runtimeRoot, pairs, partials);
        if (pairs.Count == 0 && partials.Count == 0)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var tasks = new Dictionary<TaskKey, DevelopmentTaskSnapshot>();
        foreach (var projectId in (await store.ListProjectsAsync(cancellationToken)).Select(static project => project.Id))
        {
            foreach (var task in await store.ListTasksAsync(projectId, cancellationToken))
            {
                tasks[new TaskKey(projectId, task.Id)] = task;
            }
        }

        var now = _timeProvider.GetUtcNow();
        var removed = new List<Guid>();
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] paths = [PairPath(workspacesRoot, pair), PairPath(runtimeRoot, pair)];
            var reclaimable = tasks.TryGetValue(pair, out var task)
                ? await IsFinishedAndIdleAsync(store, task, now, cancellationToken)
                : IsPastGrace(paths, now);

            if (!reclaimable)
            {
                continue;
            }

            // Both deletes are attempted even when the first fails; the next tick retries whatever is left.
            var workspaceGone = TryDelete(paths[0], pair.TaskId);
            var runtimeGone = TryDelete(paths[1], pair.TaskId);
            if (workspaceGone && runtimeGone)
            {
                removed.Add(pair.TaskId);
            }
        }

        // A non-terminal task's own next prepare removes its partial clones, and one may be cloning right now.
        foreach (var partial in partials)
        {
            if (IsPastGrace([partial.Path], now)
                && (!tasks.TryGetValue(partial.Key, out var owner) || IsTerminal(owner.Status)))
            {
                _ = TryDelete(partial.Path, partial.Key.TaskId);
            }
        }

        if (removed.Count > 0)
        {
            SweepCompleted(_logger, removed.Count, string.Join(separator: ',', removed));
        }
    }

    private async Task<bool> IsFinishedAndIdleAsync(IDevelopmentStore store, DevelopmentTaskSnapshot task, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!IsTerminal(task.Status) || now - DateTimeOffset.FromUnixTimeMilliseconds(task.UpdatedAtUtc) < _options.RetentionAge)
        {
            return false;
        }

        var attempts = await store.ListAttemptsAsync(task.Id, cancellationToken);
        return !attempts.Any(static attempt => attempt.Status is DevelopmentAttemptStatus.Running or DevelopmentAttemptStatus.Pending);
    }

    // The only states with no outgoing edge in DevelopmentStore.LegalTransitions; Blocked can still be retried.
    private static bool IsTerminal(DevelopmentTaskStatus status) =>
        status is DevelopmentTaskStatus.Completed or DevelopmentTaskStatus.Cancelled;

    private bool IsPastGrace(IEnumerable<string> paths, DateTimeOffset now) =>
        paths.Where(Directory.Exists)
             .All(path => now - new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero) >= _options.OrphanGrace);

    /// <summary>
    ///     Finds the <c>&lt;project&gt;/&lt;task&gt;</c> directories and partial clones under one root. Anything with a
    ///     name the node did not mint, or behind a link, is never collected.
    /// </summary>
    private void Collect(string root, HashSet<TaskKey> pairs, List<PartialClone> partials)
    {
        if (!Directory.Exists(root) || IsLink(root))
        {
            return;
        }

        foreach (var projectPath in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(projectPath), "N", out var projectId) || IsLink(projectPath))
            {
                continue;
            }

            foreach (var taskPath in Directory.EnumerateDirectories(projectPath))
            {
                if (IsLink(taskPath) || !IsUnderDataRoot(taskPath))
                {
                    continue;
                }

                var name = Path.GetFileName(taskPath);
                if (Guid.TryParseExact(name, "N", out var taskId))
                {
                    pairs.Add(new TaskKey(projectId, taskId));
                    continue;
                }

                var marker = name.IndexOf(PartialCloneMarker, StringComparison.Ordinal);
                if (marker > 0
                    && Guid.TryParseExact(name[..marker], "N", out taskId)
                    && Guid.TryParseExact(name[(marker + PartialCloneMarker.Length)..], "N", out _))
                {
                    partials.Add(new PartialClone(new TaskKey(projectId, taskId), taskPath));
                }
            }
        }
    }

    private bool TryDelete(string path, Guid taskId)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        // Re-checked here: the counterpart directory in the other tree was never walked, so its gates are unproven.
        if (IsLink(path) || IsLink(Path.GetDirectoryName(path)!) || !IsUnderDataRoot(path))
        {
            return false;
        }

        try
        {
            StandaloneGitClone.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DeleteFailed(_logger, taskId, exception);
            return false;
        }
    }

    private static string PairPath(string root, TaskKey pair) =>
        Path.Combine(root, pair.ProjectId.ToString("N"), pair.TaskId.ToString("N"));

    /// <summary>Whether any directory between the data root (exclusive) and <paramref name="path" /> (inclusive) is a link.</summary>
    private bool HasLinkBelowDataRoot(string path)
    {
        var current = _dataDirectoryRoot;
        foreach (var component in Path.GetRelativePath(_dataDirectoryRoot, path)
                                      .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (IsLink(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLink(string path)
    {
        var info = new DirectoryInfo(path);
        // A missing path reports every attribute bit set, so the reparse bit counts only for one that exists.
        return info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    private bool IsUnderDataRoot(string path) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(_dataDirectoryRoot) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct TaskKey(Guid ProjectId, Guid TaskId);

    private readonly record struct PartialClone(TaskKey Key, string Path);

    [LoggerMessage(EventId = 4830, Level = LogLevel.Information,
        Message = "Development workspace retention removed the workspace of {DeletedCount} task(s): {TaskIds}.")]
    private static partial void SweepCompleted(ILogger logger, int deletedCount, string taskIds);

    [LoggerMessage(EventId = 4831, Level = LogLevel.Warning, Message = "Development workspace retention could not delete a directory of task {TaskId}.")]
    private static partial void DeleteFailed(ILogger logger, Guid taskId, Exception exception);

    [LoggerMessage(EventId = 4833, Level = LogLevel.Warning,
        Message = "Development workspace retention skipped the sweep: a directory on the path to the workspaces or runtime root is a link.")]
    private static partial void SweepRefusedLinkedRoot(ILogger logger);

    [LoggerMessage(EventId = 4832, Level = LogLevel.Warning, Message = "Development workspace retention sweep failed.")]
    private static partial void SweepFailed(ILogger logger, Exception exception);
}
