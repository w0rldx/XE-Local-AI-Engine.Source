namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     The integration half of the tool lane: an <c>Apply</c> Tool node hands the tasks this run implemented to Dev
///     Mode's own hash-locked apply gate, one after another, and reports what that gate made of each.
///     <para>
///         It adds NO apply mechanics. The call it makes is the call the Dev Mode apply endpoint makes —
///         <see cref="IDevelopmentManagementService.ApplyAsync" /> onto <c>DevelopmentApplyService</c> and the
///         coordinator's revalidated apply — so the evidence chain (independently approved subject, exact patch and
///         manifest digests, host state inspected before and after) is the one that was already there. What is new is
///         only WHEN it runs: downstream of a human gate the graph is required to place in front of it (Y3), never on
///         the implementation node's own success.
///     </para>
///     <para>
///         <b>Sequential, and it stops at the first refusal.</b> The gate's single-writer discipline is per TASK, so N
///         tasks are N calls; and once one has been refused the repository is not in the state the next patch was
///         approved against, so continuing would be applying patches to a tree nobody judged.
///     </para>
///     <para>
///         Each task's apply is keyed on the run, the node and the TASK — never on the attempt — so a re-attempt of this
///         node finds the recorded result of an apply that already landed instead of asking a task that has moved on to
///         apply again.
///     </para>
/// </summary>
internal sealed class DevWorkflowApplyCommands
{
    /// <summary>
    ///     The attempt an apply is keyed under. Not a real attempt number, for the same reason a materialized child's
    ///     task creation is not: the act belongs to the node for the life of the run, and a second attempt must find the
    ///     first one's answer rather than re-run it against a repository it already changed.
    /// </summary>
    private const int ApplyAttempt = 0;

    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevelopmentStore _development;
    private readonly IDevelopmentManagementService _management;
    private readonly IDevWorkflowStore _store;
    private readonly TimeProvider _timeProvider;

    public DevWorkflowApplyCommands(IDevWorkflowStore store,
        IDevelopmentStore development,
        IDevelopmentManagementService management,
        TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _development = development ?? throw new ArgumentNullException(nameof(development));
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    ///     Applies every task this run implemented, in order, and answers with the same value a validation pass answers
    ///     with — because the lane above is the same lane, and only the tick writes rows.
    ///     <para>
    ///         What this counts as a "command" is one task's apply: the counts are what a conditional edge routes on and
    ///         what a fix-loop objective quotes, and for this node the unit of work is a task rather than a shell
    ///         command. The report artifact names them one by one.
    ///     </para>
    /// </summary>
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

        var applied = new List<AppliedTask>();
        foreach (var implementation in await ImplementedAsync(run.Id, projectId, cancellationToken).ConfigureAwait(false))
        {
            var taskId = implementation.DevelopmentTaskId!.Value;
            var task = await _development.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (task.Status == DevelopmentTaskStatus.Completed)
            {
                // Already applied, by this node before a restart or by an operator in the Dev Mode view. The same answer
                // arriving earlier, and re-applying it is exactly what the operation key exists to prevent.
                applied.Add(new AppliedTask(implementation.NodeKey, taskId, task.Title, AppliedOutcomes.AlreadyApplied, Detail: null));
                continue;
            }

            var (entry, failureClass) = await ApplyOneAsync(projectId, implementation, task, run, nodeRun, cancellationToken).ConfigureAwait(false);
            applied.Add(entry);
            if (failureClass is not null)
            {
                return Result(nodeRun, applied, failureClass, entry.Detail);
            }
        }

        // No tasks is a PASS, not a refusal: a decomposition may legitimately answer that no follow-up work is needed,
        // and a run that implemented nothing has nothing to integrate. The report says so rather than leaving a reader
        // to infer it from an empty list.
        return Result(nodeRun, applied, failureClass: null, detail: null);
    }

    /// <summary>
    ///     One task through the gate, and the failure class if the gate refused it.
    ///     <para>
    ///         A <c>ApplyBlocked</c> result is the gate declining on evidence rather than an error: the host repository
    ///         is not at the exact approved base — which is what the SECOND patch of one fan-out finds, because the
    ///         first one's applied change is sitting in the tree it was approved against. Concurrent-patch merge is
    ///         named as v2 in the plan (§5.6.3), and this is where that boundary shows up at runtime, legibly, instead
    ///         of as a patch applied onto a tree nobody judged.
    ///     </para>
    /// </summary>
    private async Task<(AppliedTask Entry, string? FailureClass)> ApplyOneAsync(Guid projectId,
        DevWorkflowNodeRunSnapshot implementation,
        DevelopmentTaskSnapshot task,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        var operationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, ApplyAttempt, $"apply-{task.Id:N}");
        try
        {
            var result = await _management.ApplyAsync(projectId, task.Id, operationId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(result.Phase, DevelopmentOperationPhases.ApplyBlocked, StringComparison.Ordinal))
            {
                return (new AppliedTask(implementation.NodeKey, task.Id, task.Title, AppliedOutcomes.Applied, Detail: null), null);
            }

            var blocked = $"The Development apply gate declined '{task.Title}': the repository is not at the exact base the approved patch was reviewed against.";
            return (new AppliedTask(implementation.NodeKey, task.Id, task.Title, AppliedOutcomes.Blocked, blocked), DevWorkflowFailureClasses.Policy);
        }
        catch (Exception exception) when (exception is DevelopmentInvalidTransitionException or DevelopmentWorkspaceSecurityException)
        {
            // The evidence chain refused: the approved subject is no longer the current one, the review is not the one
            // that approved it, or the repository's trust has lapsed. None of them is answered differently by asking
            // again, so this is the class that goes straight to a human.
            return (Refusal(implementation, task, exception), DevWorkflowFailureClasses.Policy);
        }
        catch (Exception exception) when (exception is DevelopmentRepositoryStateConflictException or KeyNotFoundException)
        {
            // The node cannot run AS CONFIGURED: a repository that needs reconnecting, or a row that is gone.
            return (Refusal(implementation, task, exception), DevWorkflowFailureClasses.Configuration);
        }
        catch (DevelopmentConcurrencyException exception)
        {
            // Something else was writing this project's ledger. Transient, and retryable once — the apply either landed
            // under its own key, in which case the next attempt reads the recorded result, or it did not happen at all.
            return (Refusal(implementation, task, exception), DevWorkflowFailureClasses.Internal);
        }
    }

    private static AppliedTask Refusal(DevWorkflowNodeRunSnapshot implementation, DevelopmentTaskSnapshot task, Exception exception) =>
        new(implementation.NodeKey, task.Id, task.Title, AppliedOutcomes.Refused, DevWorkflowToolCommands.Sanitized(exception));

    /// <summary>
    ///     The tasks this run implemented: every node run that named one and succeeded at it, in materialization order.
    ///     <para>
    ///         Bound to the node runs rather than to the project's task list, because the project also holds the
    ///         operator's own task and whatever earlier runs left there — and the run may only integrate what IT
    ///         implemented. A node run names its task exactly when the implementation lane bound it, the materialized
    ///         children of one decomposition carry their 1-based index, and a re-attempt keeps the same pointer, so
    ///         distinct task ids in index order is the whole enumeration.
    ///     </para>
    /// </summary>
    private async Task<IReadOnlyList<DevWorkflowNodeRunSnapshot>> ImplementedAsync(Guid runId, Guid projectId, CancellationToken cancellationToken) =>
    [
        .. (await _store.ListNodeRunsAsync(runId, cancellationToken).ConfigureAwait(false))
        .Where(nodeRun => nodeRun.DevelopmentTaskId is not null
                          && nodeRun.DevelopmentProjectId == projectId
                          && nodeRun.Status == DevWorkflowNodeRunStatus.Succeeded)
        .OrderBy(static nodeRun => nodeRun.MaterializationIndex ?? 0)
        .ThenBy(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal)
        .DistinctBy(static nodeRun => nodeRun.DevelopmentTaskId!.Value)
    ];

    /// <summary>What the node run answers with, and the report an operator reads to see which patches landed.</summary>
    private DevWorkflowToolRun Result(DevWorkflowNodeRunSnapshot nodeRun, IReadOnlyList<AppliedTask> applied, string? failureClass, string? detail)
    {
        var failed = applied.Count(static entry => !string.Equals(entry.Outcome, AppliedOutcomes.Applied, StringComparison.Ordinal)
                                                   && !string.Equals(entry.Outcome, AppliedOutcomes.AlreadyApplied, StringComparison.Ordinal));
        var report = new DevWorkflowApplyReport(failureClass is null,
            nodeRun.NodeKey,
            nodeRun.Attempt,
            applied.Count - failed,
            applied,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        return new DevWorkflowToolRun(failureClass is null,
            failureClass,
            FailureCode: null,
            detail,
            applied.Count,
            failed,
            TestsPassed: null,
            TestsFailed: null,
            JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions),
            []);
    }

    /// <summary>What became of one task at the gate. Lowercase-hyphenated, like every other token this product renders.</summary>
    private static class AppliedOutcomes
    {
        public const string Applied = "applied";
        public const string AlreadyApplied = "already-applied";
        public const string Blocked = "blocked";
        public const string Refused = "refused";
    }

    private sealed record AppliedTask(string NodeKey, Guid TaskId, string Title, string Outcome, string? Detail);

    /// <summary>
    ///     The report an apply node leaves behind: which task each patch belonged to, and what the gate did with it.
    ///     <para>
    ///         Deliberately NOT the validation report shape. That document describes commands run against a workspace,
    ///         and filling its command list with task applies would be a report claiming evidence it does not have. It
    ///         is written under the ordinary <c>Report</c> kind for the same reason.
    ///     </para>
    /// </summary>
    private sealed record DevWorkflowApplyReport(
        bool Passed,
        string NodeKey,
        int Attempt,
        int TasksApplied,
        IReadOnlyList<AppliedTask> Tasks,
        long CompletedAtUtc);
}
