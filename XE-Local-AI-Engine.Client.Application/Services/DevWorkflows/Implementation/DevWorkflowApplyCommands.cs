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
///         Each task's apply is keyed on the run, the node, the ATTEMPT and the task — the ordinary key every other
///         thing this lane writes uses. Re-applying is prevented on the task's own state rather than on the key, which
///         is what makes the operator's retry work: a node standing Blocked because the gate declined a patch is
///         retryable by hand, and a key that ignored the attempt would hand that retry the recorded refusal without
///         asking the repository anything.
///     </para>
/// </summary>
internal sealed class DevWorkflowApplyCommands
{
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

        var implementations = await ImplementedAsync(run.Id, projectId, cancellationToken).ConfigureAwait(false);
        var applied = new List<AppliedTask>();
        for (var index = 0; index < implementations.Count; index++)
        {
            var implementation = implementations[index];
            if (cancellationToken.IsCancellationRequested)
            {
                // A stop that arrived between two patches. Answered as a RESULT rather than by letting the token throw:
                // one patch may already be in the repository, and the sentence saying which is on a report the throwing
                // path never writes — the poll reads cancellation off the task and settles the row without asking this
                // for evidence. What did not happen is named too, because a report listing two of four tasks reads as a
                // run that had two.
                return Result(nodeRun,
                    [.. applied, .. implementations.Skip(index).Select(Unattempted)],
                    DevWorkflowFailureClasses.Cancelled,
                    $"The run was cancelled after {applied.Count(static entry => entry.Outcome != AppliedOutcomes.AlreadyApplied)} of "
                    + $"{implementations.Count} approved patches had been offered to the Development apply gate.");
            }

            var taskId = implementation.DevelopmentTaskId!.Value;
            var task = await _development.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            var title = Sanitized(task);
            if (task.Status == DevelopmentTaskStatus.Completed)
            {
                // Already applied, by this node before a restart or by an operator in the Dev Mode view. The same answer
                // arriving earlier, and re-applying it is exactly what the task's own state exists to prevent.
                applied.Add(new AppliedTask(implementation.NodeKey, taskId, title, AppliedOutcomes.AlreadyApplied, Detail: null));
                continue;
            }

            var (entry, failureClass) = await ApplyOneAsync(projectId, implementation, task, title, run, nodeRun, cancellationToken).ConfigureAwait(false);
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
        string title,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        // Attempt-keyed, so a retry ASKS again. Applying twice is prevented by two things that do not need a constant
        // attempt: the Completed short-circuit above, which is the state a landed apply leaves the task in, and Dev
        // Mode's own idempotent arm, which recognises an exact approved result already present in the repository.
        var operationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, $"apply-{task.Id:N}");
        try
        {
            var result = await _management.ApplyAsync(projectId, task.Id, operationId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(result.Phase, DevelopmentOperationPhases.ApplyBlocked, StringComparison.Ordinal))
            {
                return (new AppliedTask(implementation.NodeKey, task.Id, title, AppliedOutcomes.Applied, Detail: null), null);
            }

            var blocked = $"The Development apply gate declined '{title}': the repository is not at the exact base the approved patch was reviewed against.";
            return (new AppliedTask(implementation.NodeKey, task.Id, title, AppliedOutcomes.Blocked, blocked), DevWorkflowFailureClasses.Policy);
        }
        catch (Exception exception) when (exception is DevelopmentInvalidTransitionException or DevelopmentWorkspaceSecurityException)
        {
            // The evidence chain refused: the approved subject is no longer the current one, the review is not the one
            // that approved it, or the repository's trust has lapsed. None of them is answered differently by asking
            // again, so this is the class that goes straight to a human. A task the gate has already stood down gets the
            // lane's own sentence in front of Dev Mode's, which by then is about a precondition rather than a cause.
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
        return new AppliedTask(implementation.NodeKey,
            task.Id,
            title,
            AppliedOutcomes.Refused,
            lead is null ? sanitized : $"{lead} Development answered: {sanitized}");
    }

    /// <summary>
    ///     The lane's own account of the one refusal a RETRY produces, in front of Dev Mode's.
    ///     <para>
    ///         A patch the gate declined at the base check stands its task down, and Dev Mode's answer on the next
    ///         attempt is then about a PRECONDITION rather than about what happened: "patch preview requires an
    ///         independently approved task awaiting explicit apply" is true, names no cause, and reads to an operator
    ///         who just pressed Retry as a different and less serious problem than the one they were retrying. The
    ///         sanitized original is kept after it, because it is what the Development view says about the same task.
    ///     </para>
    ///     <para>
    ///         The cause is READ off the task rather than assumed: a declined apply is the usual way one of these gets
    ///         blocked, but not the only one, and Development already recorded which. That reason went through the same
    ///         sanitizer on its way in and gets it again here, because a second reader is a second exposure.
    ///     </para>
    /// </summary>
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

    /// <summary>
    ///     A task the sequence never reached, because it was cancelled first.
    ///     <para>
    ///         Named rather than left out: the report is the operator's account of what this node did with the run's
    ///         patches, and one that listed two of four tasks would read as a run that implemented two. It carries no
    ///         title, because reading one is a store round-trip on a token that has already been cancelled — the node
    ///         key and the task id are what identify the row anyway.
    ///     </para>
    /// </summary>
    private static AppliedTask Unattempted(DevWorkflowNodeRunSnapshot implementation) =>
        new(implementation.NodeKey,
            implementation.DevelopmentTaskId!.Value,
            Title: null,
            AppliedOutcomes.Cancelled,
            "The run was cancelled before this patch was offered to the Development apply gate.");

    /// <summary>
    ///     One task title, fit to be stored on a row and rendered on a wire. A title is what the decomposing agent
    ///     called the slice — MODEL text, arriving through a task package — and it reaches an operator both in this
    ///     node's terminal reason and in every report entry, which is the same exposure the lane's exception messages
    ///     have and gets the same answer.
    ///     <para>
    ///         A title the sanitizer REFUSES — one carrying credential-like material it will not redact — is replaced by
    ///         the task's own id rather than allowed to escape as a second exception, because the entry is about which
    ///         task the gate answered for and the id says that.
    ///     </para>
    /// </summary>
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

    /// <summary>
    ///     What the node run answers with, and the report an operator reads to see which patches landed.
    ///     <para>
    ///         The detail becomes the row's <c>terminal_reason</c>, which the schema bounds at
    ///         <see cref="MaxTerminalReason" /> — and the composed refusals here are additive: a lead sentence, a model
    ///         title, a stored blocked reason and an exception message. SQLite does not enforce a declared length, so an
    ///         over-long one would break the contract silently and only bite on a provider that does. Capped in the ONE
    ///         place every detail passes through, with the lead kept and the tail cut.
    ///     </para>
    /// </summary>
    private DevWorkflowToolRun Result(DevWorkflowNodeRunSnapshot nodeRun, IReadOnlyList<AppliedTask> applied, string? failureClass, string? detail)
    {
        detail = detail is { Length: > MaxTerminalReason } overlong ? $"{overlong[..(MaxTerminalReason - 1)]}…" : detail;
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

        /// <summary>The sequence was stopped before this task's patch was offered at all.</summary>
        public const string Cancelled = "cancelled";
    }

    private sealed record AppliedTask(string NodeKey, Guid TaskId, string? Title, string Outcome, string? Detail);

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
