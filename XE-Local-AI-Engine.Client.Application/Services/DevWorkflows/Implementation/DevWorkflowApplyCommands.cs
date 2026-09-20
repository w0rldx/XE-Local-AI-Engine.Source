namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;

/// <summary>The integration half of the tool lane: the run's implemented tasks through Dev Mode's apply gate.</summary>
/// <remarks>
///     It adds NO apply mechanics — the call it makes is the one the Dev Mode apply endpoint makes,
///     <see cref="IDevelopmentManagementService.ApplyAsync" />, so the evidence chain is the one already there. What
///     is new is only WHEN it runs. Sequential, and it stops at the first refusal.
///     See docs/wiki/25-dev-workflows.md ("The integration half").
/// </remarks>
internal sealed class DevWorkflowApplyCommands
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevelopmentStore _development;
    private readonly DevWorkflowGraphCache _graphs;
    private readonly IDevelopmentManagementService _management;
    private readonly IDevWorkflowStore _store;
    private readonly TimeProvider _timeProvider;

    public DevWorkflowApplyCommands(IDevWorkflowStore store,
        IDevelopmentStore development,
        IDevelopmentManagementService management,
        DevWorkflowGraphCache graphs,
        TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _development = development ?? throw new ArgumentNullException(nameof(development));
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Applies every task this run implemented, in order, answering as a validation pass would.</summary>
    /// <remarks>
    ///     The lane above is the same lane, and only the tick writes rows. What counts as one "command" here is one
    ///     task's apply, and the report artifact names them one by one.
    ///     See docs/wiki/25-dev-workflows.md ("The integration half").
    /// </remarks>
    public async Task<DevWorkflowToolRun> RunAsync(DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);

        if (nodeRun.DevelopmentProjectId is not { } projectId)
        {
            // Run start refuses a graph with repository nodes on a work item that names no project, so this is a row
            // materialized before such a node existed rather than an ordinary miss.
            return Result(nodeRun,
                applied: [],
                DevWorkflowFailureClasses.Configuration,
                $"Node run '{nodeRun.NodeKey}' applies approved patches but names no development project to apply them to.");
        }

        var implementations = await ImplementedAsync(run, nodeRun, projectId, cancellationToken);
        var applied = new List<AppliedTask>();
        for (var index = 0; index < implementations.Count; index++)
        {
            var implementation = implementations[index];
            if (cancellationToken.IsCancellationRequested)
            {
                // A stop between two patches, answered as a RESULT rather than by letting the token throw: one patch
                // may already be in the repository, and only this path writes the report that says which.
                return Result(nodeRun,
                    [.. applied, .. implementations.Skip(index).Select(Unattempted)],
                    DevWorkflowFailureClasses.Cancelled,
                    $"The run was cancelled after {applied.Count(static entry => entry.Outcome != AppliedOutcomes.AlreadyApplied)} of "
                    + $"{implementations.Count} approved patches had been offered to the Development apply gate.");
            }

            var taskId = implementation.DevelopmentTaskId!.Value;
            var task = await _development.GetTaskAsync(taskId, cancellationToken);
            var title = Sanitized(task);
            if (task.Status == DevelopmentTaskStatus.Completed)
            {
                // Already applied, by this node before a restart or by an operator in the Dev Mode view. The same answer
                // arriving earlier, and re-applying it is exactly what the task's own state exists to prevent.
                applied.Add(new AppliedTask { NodeKey = implementation.NodeKey, TaskId = taskId, Title = title, Outcome = AppliedOutcomes.AlreadyApplied, Detail = null });
                continue;
            }

            var (entry, failureClass) = await ApplyOneAsync(projectId, implementation, task, title, run, nodeRun, cancellationToken);
            applied.Add(entry);
            if (failureClass is not null)
            {
                return Result(nodeRun, applied, failureClass, entry.Detail);
            }
        }

        // No tasks is a PASS, not a refusal: a decomposition may legitimately answer that no follow-up work is needed,
        // and the report says so rather than leaving a reader to infer it from an empty list.
        return Result(nodeRun, applied, failureClass: null, detail: null);
    }

    /// <summary>One task through the gate, and the failure class if the gate refused it.</summary>
    /// <remarks>
    ///     An <c>ApplyBlocked</c> result is the gate declining on evidence rather than an error: the host repository
    ///     is not at the exact approved base, which is what the SECOND patch of one fan-out finds.
    ///     See docs/wiki/25-dev-workflows.md ("The integration half").
    /// </remarks>
    private async Task<(AppliedTask Entry, string? FailureClass)> ApplyOneAsync(Guid projectId,
        DevWorkflowNodeRunSnapshot implementation,
        DevelopmentTaskSnapshot task,
        string title,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        // Attempt-keyed, so a retry ASKS again: applying twice is prevented by the Completed short-circuit above and
        // by Dev Mode's idempotent arm, neither of which needs a constant attempt.
        var operationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, $"apply-{task.Id:N}");
        try
        {
            // On behalf of THIS run, which is what gets past the ownership guard the same method enforces against
            // every other caller: the gate that authorised this apply is a node of this run.
            var result = await _management.ApplyAsync(projectId, task.Id, operationId, run.Id, cancellationToken);
            if (!string.Equals(result.Phase, DevelopmentOperationPhases.ApplyBlocked, StringComparison.Ordinal))
            {
                return (new AppliedTask { NodeKey = implementation.NodeKey, TaskId = task.Id, Title = title, Outcome = AppliedOutcomes.Applied, Detail = null }, null);
            }

            var blocked = $"The Development apply gate declined '{title}': the repository is not at the exact base the approved patch was reviewed against.";
            return (new AppliedTask { NodeKey = implementation.NodeKey, TaskId = task.Id, Title = title, Outcome = AppliedOutcomes.Blocked, Detail = blocked }, DevWorkflowFailureClasses.Policy);
        }
        catch (Exception exception) when (exception is DevelopmentInvalidTransitionException or DevelopmentWorkspaceSecurityException)
        {
            // The evidence chain refused, and asking again answers none of those, so this goes straight to a human. A
            // task already stood down gets the lane's sentence in front of Dev Mode's precondition complaint.
            return (Refusal(implementation, task, title, exception, StoodDown(task, title)), DevWorkflowFailureClasses.Policy);
        }
        catch (Exception exception) when (exception is DevelopmentRepositoryStateConflictException or KeyNotFoundException)
        {
            // The node cannot run AS CONFIGURED: a repository that needs reconnecting, or a row that is gone.
            return (Refusal(implementation, task, title, exception), DevWorkflowFailureClasses.Configuration);
        }
        catch (DevelopmentConcurrencyException exception)
        {
            // Something else was writing this project's ledger. Transient, and retryable once — the apply either landed,
            // in which case the next attempt finds the task Completed and says so, or it did not happen at all.
            return (Refusal(implementation, task, title, exception), DevWorkflowFailureClasses.Internal);
        }
    }

    /// <summary>The schema's own bound on <c>terminal_reason</c> (<c>DevWorkflowNodeRunConfiguration</c>).</summary>
    private const int MaxTerminalReason = 1024;

    private static AppliedTask Refusal(DevWorkflowNodeRunSnapshot implementation,
        DevelopmentTaskSnapshot task,
        string title,
        Exception exception,
        string? lead = null)
    {
        var sanitized = DevWorkflowToolCommands.Sanitized(exception);
        return new AppliedTask
        {
            NodeKey = implementation.NodeKey,
            TaskId = task.Id,
            Title = title,
            Outcome = AppliedOutcomes.Refused,
            Detail = lead is null ? sanitized : $"{lead} Development answered: {sanitized}"
        };
    }

    /// <summary>The lane's own account of the one refusal a RETRY produces, in front of Dev Mode's.</summary>
    /// <remarks>
    ///     Dev Mode's answer on the next attempt is about a PRECONDITION rather than about what happened. The cause is
    ///     READ off the task rather than assumed, and sanitized again here, a second reader being a second exposure.
    ///     See docs/wiki/25-dev-workflows.md ("The integration half").
    /// </remarks>
    private static string? StoodDown(DevelopmentTaskSnapshot task, string title)
    {
        if (task.Status != DevelopmentTaskStatus.Blocked)
        {
            return null;
        }

        var reason = SanitizedReason(task.BlockedReason) ?? "Development recorded no reason";
        return $"'{title}' is stood down in Development ({reason}), so this attempt could not offer its patch at all: a "
               + "blocked task is no longer awaiting apply, and retrying this node does not return it there.";
    }

    /// <summary>One stored reason, fit to be read again. A reason the sanitizer refuses is dropped rather than escaping.</summary>
    private static string? SanitizedReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        try
        {
            return DevelopmentArtifactSanitizer.SanitizeText(reason).TrimEnd('.');
        }
        catch (DevelopmentWorkspaceSecurityException)
        {
            return null;
        }
    }

    /// <summary>A task the sequence never reached, because it was cancelled first.</summary>
    /// <remarks>
    ///     Named rather than left out: a report listing two of four tasks would read as a run that implemented two. It
    ///     carries no title, reading one being a store round-trip on an already-cancelled token, and the node key and
    ///     task id identify the row anyway.
    /// </remarks>
    private static AppliedTask Unattempted(DevWorkflowNodeRunSnapshot implementation) =>
        new()
        {
            NodeKey = implementation.NodeKey,
            TaskId = implementation.DevelopmentTaskId!.Value,
            Title = null,
            Outcome = AppliedOutcomes.Cancelled,
            Detail = "The run was cancelled before this patch was offered to the Development apply gate."
        };

    /// <summary>One task title, fit to be stored on a row and rendered on a wire.</summary>
    /// <remarks>
    ///     A title is MODEL text arriving through a task package, and it reaches an operator in the terminal reason
    ///     and in every report entry, so it gets the same answer the lane's exception messages get. One the sanitizer
    ///     REFUSES is replaced by the task's own id, which says which task the gate answered for.
    /// </remarks>
    private static string Sanitized(DevelopmentTaskSnapshot task)
    {
        try
        {
            return DevelopmentArtifactSanitizer.SanitizeText(task.Title);
        }
        catch (DevelopmentWorkspaceSecurityException)
        {
            return $"the task {task.Id:D}";
        }
    }

    /// <summary>The tasks THIS node integrates: every node run upstream of it that named a task and succeeded.</summary>
    /// <remarks>
    ///     In materialization order. Bound to the node runs rather than the project's task list, and to this node's
    ///     own ANCESTRY rather than to the run, resolved over the run's pinned graph — the revision the tick routed
    ///     on, so a materialization's clones are in it and "upstream" is the set the approval covers.
    ///     See docs/wiki/25-dev-workflows.md ("The integration half").
    /// </remarks>
    private async Task<IReadOnlyList<DevWorkflowNodeRunSnapshot>> ImplementedAsync(DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot applyNodeRun,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var upstream = _graphs.Resolve(run).Ancestors(applyNodeRun.NodeKey);
        return
        [
            .. (await _store.ListNodeRunsAsync(run.Id, cancellationToken))
               .Where(nodeRun => nodeRun.DevelopmentTaskId is not null
                                 && nodeRun.DevelopmentProjectId == projectId
                                 && nodeRun.Status == DevWorkflowNodeRunStatus.Succeeded
                                 && upstream.Contains(nodeRun.NodeKey))
               .OrderBy(static nodeRun => nodeRun.MaterializationIndex ?? 0)
               .ThenBy(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal)
               .DistinctBy(static nodeRun => nodeRun.DevelopmentTaskId!.Value)
        ];
    }

    /// <summary>What the node run answers with, and the report an operator reads to see which patches landed.</summary>
    /// <remarks>
    ///     The detail becomes the row's <c>terminal_reason</c>, bounded at <see cref="MaxTerminalReason" />. The
    ///     refusals composed here are additive and SQLite enforces no declared length, so it is capped in the ONE
    ///     place every detail passes through, lead kept and tail cut.
    /// </remarks>
    private DevWorkflowToolRun Result(DevWorkflowNodeRunSnapshot nodeRun, IReadOnlyList<AppliedTask> applied, string? failureClass, string? detail)
    {
        detail = detail is { Length: > MaxTerminalReason } overlong ? $"{overlong[..(MaxTerminalReason - 1)]}…" : detail;
        var failed = applied.Count(static entry => !string.Equals(entry.Outcome, AppliedOutcomes.Applied, StringComparison.Ordinal)
                                                   && !string.Equals(entry.Outcome, AppliedOutcomes.AlreadyApplied, StringComparison.Ordinal));
        var report = new DevWorkflowApplyReport
        {
            Passed = failureClass is null,
            NodeKey = nodeRun.NodeKey,
            Attempt = nodeRun.Attempt,
            TasksApplied = applied.Count - failed,
            Tasks = applied,
            CompletedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };
        return new DevWorkflowToolRun
        {
            Passed = failureClass is null,
            FailureClass = failureClass,
            FailureCode = null,
            SanitizedReason = detail,
            CommandsRun = applied.Count,
            CommandsFailed = failed,
            TestsPassed = null,
            TestsFailed = null,
            Report = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions),
            SecretPaths = []
        };
    }

    /// <summary>What became of one task at the gate. Lowercase-hyphenated, like every other token this product renders.</summary>
    private static class AppliedOutcomes
    {
        public const string Applied = "applied";
        public const string AlreadyApplied = "already-applied";
        public const string Blocked = "blocked";
        public const string Refused = "refused";

        /// <summary>The sequence was stopped before this task's patch was offered at all.</summary>
        public const string Cancelled = "cancelled";
    }

    private sealed record AppliedTask
    {
        public required string NodeKey { get; init; }

        public required Guid TaskId { get; init; }

        public required string? Title { get; init; }

        public required string Outcome { get; init; }

        public required string? Detail { get; init; }
    }

    /// <summary>The report an apply node leaves: which task each patch belonged to, and what the gate did with it.</summary>
    /// <remarks>
    ///     Deliberately NOT the validation report shape: that document describes commands run against a workspace, and
    ///     filling its command list with task applies would claim evidence it does not have. Written under the
    ///     ordinary <c>Report</c> kind for the same reason.
    /// </remarks>
    private sealed record DevWorkflowApplyReport
    {
        public required bool Passed { get; init; }

        public required string NodeKey { get; init; }

        public required int Attempt { get; init; }

        public required int TasksApplied { get; init; }

        public required IReadOnlyList<AppliedTask> Tasks { get; init; }

        public required long CompletedAtUtc { get; init; }
    }
}
