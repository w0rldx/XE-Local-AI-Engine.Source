namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     A scripted stand-in for the one thing a <c>DevTask</c> node run asks of Dev Mode: start the task's next action.
///     <para>
///         Everything else stays real — the development project and task are rows in the same database, moved through
///         the same store and the same legal-transition table the coder, validation and review stages move them through.
///         What this replaces is only the part that would need a model, a repository and a sandbox: instead of running
///         an attempt, it walks the task one legal step along the chain.
///     </para>
///     <para>
///         A container singleton, so a test reads its history through the harness. Take a private host, which every
///         test that uses it does anyway to script it.
///     </para>
/// </summary>
internal sealed class FakeDevelopmentTaskChain : IDevelopmentManagementService
{
    /// <summary>The chain, in the order the real stages walk it. The last hop carries the approved subject.</summary>
    private static readonly IReadOnlyDictionary<DevelopmentTaskStatus, DevelopmentTaskStatus> NextStatus =
        new Dictionary<DevelopmentTaskStatus, DevelopmentTaskStatus>
        {
            [DevelopmentTaskStatus.Planned] = DevelopmentTaskStatus.Ready,
            [DevelopmentTaskStatus.Ready] = DevelopmentTaskStatus.InProgress,
            [DevelopmentTaskStatus.InProgress] = DevelopmentTaskStatus.Validation,
            [DevelopmentTaskStatus.Validation] = DevelopmentTaskStatus.InReview,
            [DevelopmentTaskStatus.ChangesRequested] = DevelopmentTaskStatus.InProgress,
            [DevelopmentTaskStatus.InReview] = DevelopmentTaskStatus.AwaitingApply
        };

    private readonly Lock _gate = new();
    private readonly List<string> _actions = [];
    private readonly List<Guid> _offered = [];
    private readonly List<Guid?> _onBehalfOf = [];
    private readonly List<Guid> _cancelled = [];
    private readonly TaskCompletionSource _applyHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _policyRefusalsOwed;
    private readonly TaskCompletionSource _applyReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IServiceScopeFactory _scopes;
    private int? _appliesAllowed;
    private int? _holdAfterApplies;
    private int _failuresOwed;
    private int _holdsOwed;
    private int _validationStallsOwed;
    private bool _advanceOnStall;
    private bool _gateFails;

    public FakeDevelopmentTaskChain(IServiceScopeFactory scopes) =>
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));

    /// <summary>Every next action that was asked for, as the status it was asked in.</summary>
    public IReadOnlyList<string> Actions
    {
        get
        {
            lock (_gate)
            {
                return [.. _actions];
            }
        }
    }

    /// <summary>
    ///     The tasks whose patches were OFFERED to the apply gate, in the order they were — the ones it declined
    ///     included. Named for the ask rather than the outcome because that is what it records, and because a test that
    ///     asks whether a retry reached the repository at all needs the ask, not the answer.
    /// </summary>
    public IReadOnlyList<Guid> Offered
    {
        get
        {
            lock (_gate)
            {
                return [.. _offered];
            }
        }
    }

    /// <summary>
    ///     The workflow run each apply named itself on behalf of, in the same order as <see cref="Offered" />. The real
    ///     service refuses an apply that names no run for a task a live run drives, so a lane that stopped threading its
    ///     run id would refuse its own patches in production while every scripted test still passed.
    /// </summary>
    public IReadOnlyList<Guid?> OnBehalfOf
    {
        get
        {
            lock (_gate)
            {
                return [.. _onBehalfOf];
            }
        }
    }

    /// <summary>Completes once an apply has landed and the chain is holding, for a test that has to arrive mid-sequence.</summary>
    public Task ApplyHeld => _applyHeld.Task;

    /// <summary>The attempts a cancelling run asked to stop.</summary>
    public IReadOnlyList<Guid> CancelledAttempts
    {
        get
        {
            lock (_gate)
            {
                return [.. _cancelled];
            }
        }
    }

    /// <summary>
    ///     Makes the next <paramref name="count" /> actions start a real coder attempt and fail it, which is what a
    ///     model that could not produce a usable patch leaves behind.
    /// </summary>
    public void FailNextAttempts(int count)
    {
        lock (_gate)
        {
            _failuresOwed = count;
        }
    }

    /// <summary>
    ///     Makes the next <paramref name="count" /> attempts fail the way a workspace policy refusal lands: a failed
    ///     attempt row whose terminal reason is the policy's own sentence behind its failure code, exactly as
    ///     <c>DevelopmentCoderAttemptRunner</c> composes it.
    /// </summary>
    public void RefuseNextAttemptsOnPolicy(int count)
    {
        lock (_gate)
        {
            _policyRefusalsOwed = count;
        }
    }

    /// <summary>
    ///     Makes the next <paramref name="count" /> actions start a real coder attempt and leave it RUNNING, so a test
    ///     can look at a node run while the chain is genuinely working — which is the only state a drain has anything to
    ///     ask about, and the only way two siblings can be caught working at the same moment.
    /// </summary>
    public void HoldNextAttempt(int count = 1)
    {
        lock (_gate)
        {
            _holdsOwed = count;
        }
    }

    /// <summary>
    ///     Makes the next <paramref name="count" /> asks made while the task sits in <c>Validation</c> answer the way
    ///     the real management service does: there is no executable next action, because deterministic validation is a
    ///     phase Dev Mode's own supervisor drives with no attempt row at all. It is the window a workflow tick can land
    ///     in and be told "no" about a task that is perfectly healthy.
    /// </summary>
    public void StallInValidation(int count, bool advance = false)
    {
        lock (_gate)
        {
            _validationStallsOwed = count;
            _advanceOnStall = advance;
        }
    }

    /// <summary>
    ///     Makes the deterministic gate FAIL, through the store hops the real runner makes rather than a scripted
    ///     status: <c>StartValidationAsync</c>, then <c>FinalizeValidationAsync</c> at the target
    ///     <see cref="DevelopmentValidationRunner.TargetFor" /> chooses for a failed run. Nothing here decides where
    ///     that leaves the task — the product does — which is what makes a node run's behaviour afterwards a fact
    ///     about the product.
    ///     <para>
    ///         The gate re-uses the succeeded coder attempt already on the task, exactly as the real one does, so a
    ///         second validation of the SAME attempt is visible as a second <c>ValidationStarted</c> row. The rework
    ///         round it asks for is a real RUNNING coder attempt, which is what the management service starts from
    ///         <c>ChangesRequested</c>.
    ///     </para>
    /// </summary>
    public void FailTheDeterministicGate()
    {
        lock (_gate)
        {
            _gateFails = true;
        }
    }

    /// <summary>
    ///     What the gate says when it declines a patch. Settable because it lands on the TASK as its blocked reason, and
    ///     the workflow's retried refusal reads it back — so its LENGTH is part of a contract worth testing.
    /// </summary>
    public string BlockedReason { get; set; } = "The scripted host repository was not at the approved base.";

    public async Task<DevelopmentNextActionResult> StartNextActionAsync(Guid projectId,
        Guid taskId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var task = await store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);

        bool fail;
        bool hold;
        bool refused;
        bool stalled;
        lock (_gate)
        {
            _actions.Add(task.Status.ToString());
            stalled = task.Status == DevelopmentTaskStatus.Validation && _validationStallsOwed > 0;
            if (stalled)
            {
                _validationStallsOwed--;
            }

            refused = !stalled && _policyRefusalsOwed > 0;
            if (refused)
            {
                _policyRefusalsOwed--;
            }

            fail = !stalled && !refused && _failuresOwed > 0;
            if (fail)
            {
                _failuresOwed--;
            }

            hold = !stalled && !fail && !refused && _holdsOwed > 0;
            if (hold)
            {
                _holdsOwed--;
            }
        }

        if (stalled)
        {
            if (_advanceOnStall)
            {
                // The supervisor finishing BETWEEN the ask and the caller's re-read: the ask is still refused, but by
                // the time anyone looks again the task has moved on and its version has bumped.
                _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(taskId,
                                       operationId,
                                       DevelopmentTaskStatus.InReview,
                                       task.Version),
                                   cancellationToken)
                               .ConfigureAwait(false);
            }

            throw new DevelopmentInvalidTransitionException("The Development task has no executable next action in its current state.");
        }

        if (refused)
        {
            return await StartAnAttemptAsync(store,
                    projectId,
                    task,
                    DevelopmentAttemptStatus.Failed,
                    cancellationToken,
                    DevelopmentAttemptEvidenceException.Compose(DevelopmentAttemptFailureCodes.WorkspacePolicyRefused, DevelopmentTestWritePolicy.RefusalSentence))
                .ConfigureAwait(false);
        }

        if (fail || hold)
        {
            return await StartAnAttemptAsync(store, projectId, task, fail ? DevelopmentAttemptStatus.Failed : null, cancellationToken).ConfigureAwait(false);
        }

        bool gateFails;
        lock (_gate)
        {
            gateFails = _gateFails;
        }

        if (gateFails && task.Status == DevelopmentTaskStatus.ChangesRequested)
        {
            return await StartAnAttemptAsync(store, projectId, task, terminal: null, cancellationToken).ConfigureAwait(false);
        }

        if (gateFails && task.Status == DevelopmentTaskStatus.InProgress)
        {
            return await FailTheGateAsync(store, projectId, task, cancellationToken).ConfigureAwait(false);
        }

        if (!NextStatus.TryGetValue(task.Status, out var next))
        {
            throw new DevelopmentInvalidTransitionException("The Development task has no executable next action in its current state.");
        }

        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(taskId,
                               operationId,
                               next,
                               task.Version,
                               ApprovedSubjectHash: next == DevelopmentTaskStatus.AwaitingApply ? "subject" : null),
                           cancellationToken)
                       .ConfigureAwait(false);
        return new DevelopmentNextActionResult("Attempt", projectId, taskId, AttemptId: null, next, DevelopmentAttemptRole.Coder);
    }

    /// <summary>
    ///     One real failing deterministic gate: the succeeded coder attempt it judges, its <c>ValidationStarted</c> and
    ///     <c>ValidationFinalized</c> rows, and its report artifact. The attempt is only created when the task has none
    ///     to judge, so a gate that runs twice runs twice against the SAME attempt — which is the shape the livelock had.
    /// </summary>
    private static async Task<DevelopmentNextActionResult> FailTheGateAsync(IDevelopmentStore store,
        Guid projectId,
        DevelopmentTaskSnapshot task,
        CancellationToken cancellationToken)
    {
        if (task.CurrentReviewRound >= task.MaxReviewRounds)
        {
            // The real StartNextActionAsync stands a task at its budget down before it opens a gate, so the fake must
            // too — otherwise the first multi-round workflow-lane test written against it would pass on a sequence the
            // product cannot produce.
            throw new DevelopmentInvalidTransitionException("The Development task has no executable next action in its current state.");
        }

        var judged = (await store.ListAttemptsAsync(task.Id, cancellationToken).ConfigureAwait(false))
            .LastOrDefault(static attempt => attempt.Role == DevelopmentAttemptRole.Coder && attempt.Status == DevelopmentAttemptStatus.Succeeded);
        if (judged is null)
        {
            var attemptId = Guid.NewGuid();
            var started = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(task.Id,
                                         attemptId,
                                         Guid.NewGuid(),
                                         DevelopmentAttemptRole.Coder,
                                         "scripted-model",
                                         "local",
                                         (await store.GetTaskAsync(task.Id, cancellationToken).ConfigureAwait(false)).Version),
                                     cancellationToken)
                                     .ConfigureAwait(false);
            _ = await store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand(attemptId,
                                   Guid.NewGuid(),
                                   DevelopmentAttemptStatus.Succeeded,
                                   started.Version),
                               cancellationToken)
                           .ConfigureAwait(false);
            judged = (await store.ListAttemptsAsync(task.Id, cancellationToken).ConfigureAwait(false)).Single(attempt => attempt.Id == attemptId);
        }

        var opened = await store.StartValidationAsync(new DevelopmentStartValidationCommand(task.Id,
                                    Guid.NewGuid(),
                                    (await store.GetTaskAsync(task.Id, cancellationToken).ConfigureAwait(false)).Version),
                                cancellationToken)
                                .ConfigureAwait(false);
        _ = await store.FinalizeValidationAsync(new DevelopmentFinalizeValidationCommand(new DevelopmentAttachArtifactCommand(Guid.NewGuid(),
                                   projectId,
                                   task.Id,
                                   judged.Id,
                                   Guid.NewGuid(),
                                   DevelopmentArtifactKind.ValidationReport,
                                   SchemaVersion: 1,
                                   "content-hash",
                                   ByteCount: 2,
                                   ContentJson: Encoding.UTF8.GetBytes("{}")),
                               Guid.NewGuid(),
                               opened.Version,
                               DevelopmentValidationRunner.TargetFor(passed: false),
                               GateFailureReason),
                           cancellationToken)
                       .ConfigureAwait(false);
        return new DevelopmentNextActionResult("Validation", projectId, task.Id, AttemptId: null, DevelopmentTaskStatus.Validation, Role: null);
    }

    /// <summary>What the scripted gate reports, in the shape <c>DevelopmentValidationRunner</c> composes.</summary>
    private const string GateFailureReason =
        "Deterministic validation failed (tests_failed): Command dotnet_test_release_no_build reported 1 failing of 3 executed tests.";

    /// <summary>
    ///     A real attempt row, landed on <paramref name="terminal" /> or left running, so the node run reads it off the
    ///     same rows the real runner would have written.
    /// </summary>
    private static async Task<DevelopmentNextActionResult> StartAnAttemptAsync(IDevelopmentStore store,
        Guid projectId,
        DevelopmentTaskSnapshot task,
        DevelopmentAttemptStatus? terminal,
        CancellationToken cancellationToken,
        string terminalReason = "The scripted coder attempt failed.")
    {
        var ready = task;
        if (task.Status == DevelopmentTaskStatus.Planned)
        {
            var moved = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(task.Id,
                                           Guid.NewGuid(),
                                           DevelopmentTaskStatus.Ready,
                                           task.Version),
                                       cancellationToken)
                                   .ConfigureAwait(false);
            ready = task with
            {
                Status = DevelopmentTaskStatus.Ready,
                Version = moved.Version
            };
        }

        var attemptId = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(ready.Id,
                               attemptId,
                               Guid.NewGuid(),
                               DevelopmentAttemptRole.Coder,
                               "scripted-model",
                               "local",
                               ready.Version),
                           cancellationToken)
                       .ConfigureAwait(false);
        if (terminal is { } landed)
        {
            _ = await store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand(attemptId,
                                   Guid.NewGuid(),
                                   landed,
                                   ExpectedAttemptVersion: 1,
                                   terminalReason),
                               cancellationToken)
                           .ConfigureAwait(false);
        }

        return new DevelopmentNextActionResult("Attempt", projectId, ready.Id, attemptId, DevelopmentTaskStatus.InProgress, DevelopmentAttemptRole.Coder);
    }

    public async Task<bool> CancelAttemptAsync(Guid projectId, Guid taskId, Guid attemptId, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var attempt = (await store.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false)).Single(candidate => candidate.Id == attemptId);
        _ = await store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand(attemptId,
                               Guid.NewGuid(),
                               DevelopmentAttemptStatus.Cancelled,
                               attempt.Version,
                               "The run was cancelled."),
                           cancellationToken)
                       .ConfigureAwait(false);
        lock (_gate)
        {
            _cancelled.Add(attemptId);
        }

        return true;
    }

    public Task<DevelopmentRepositoryReference> RegisterRepositoryAsync(string displayAlias, string hostPath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<DevelopmentRepositoryReference>> ListRepositoriesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DevelopmentProfileDetectionResult> DetectRepositoryProfileAsync(Guid selectedFolderId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<DevelopmentProjectSnapshot>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DevelopmentProjectAggregate> CreateProjectAsync(DevelopmentCreateProjectInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DevelopmentProjectAggregate> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DevelopmentTaskAggregate> GetTaskAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<DevelopmentEventSnapshot>> ListEventsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<DevelopmentArtifactSnapshot>> ListArtifactsAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DevelopmentArtifactContent> ReadArtifactAsync(Guid projectId, Guid taskId, Guid artifactId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DevelopmentPatchPreviewResult> PreviewAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>
    ///     Lets this many applies land and makes every one after them come back BLOCKED — which is what the real gate
    ///     answers when the host repository is not at the exact base the approved patch was reviewed against, and so
    ///     what the SECOND patch of one fan-out meets: the first one's change is sitting in the tree it was approved
    ///     against.
    /// </summary>
    public void AllowApplies(int count)
    {
        lock (_gate)
        {
            _appliesAllowed = count;
        }
    }

    /// <summary>
    ///     Makes the apply that lands <paramref name="count" />th block until <see cref="ReleaseApplies" />, with its
    ///     ledger write already committed — which is the one moment a sequence of applies can be interrupted BETWEEN
    ///     two patches, and so the only way to observe what a cancel arriving there leaves behind.
    /// </summary>
    public void HoldAfterApplies(int count)
    {
        lock (_gate)
        {
            _holdAfterApplies = count;
        }
    }

    /// <summary>Lets a held apply return.</summary>
    public void ReleaseApplies() =>
        _ = _applyReleased.TrySetResult();

    /// <summary>
    ///     The apply, with the host mutation scripted and the LEDGER real: the store's own <c>CompleteApply</c> /
    ///     <c>BlockApply</c> commands run, so the operation key, the task transition and the events are the ones the
    ///     real service writes. What is skipped is <c>DevelopmentApplyService</c>'s evidence verification and the git
    ///     apply itself, both of which need a real repository, a real coder attempt and a real reviewer attempt.
    ///     <para>
    ///         The evidence chain those two would enforce is asserted where it is real, by the Development suite's own
    ///         apply tests, which this phase left untouched.
    ///     </para>
    /// </summary>
    public async Task<DevelopmentOperationResult> ApplyAsync(Guid projectId,
        Guid taskId,
        Guid operationId,
        Guid? onBehalfOfWorkflowRunId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var task = await store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task.ProjectId != projectId)
        {
            throw new KeyNotFoundException("The Development task does not belong to the project.");
        }

        // The real service's own first two lines: an apply already recorded under this key answers with what it did
        // rather than doing it again. Kept here so a replayed node attempt is as idempotent through the fake as it is
        // through the service.
        if (await store.FindOperationAsync(projectId, operationId, DevelopmentOperationPhases.ApplyCompleted, cancellationToken).ConfigureAwait(false) is { } completed)
        {
            return completed;
        }

        if (await store.FindOperationAsync(projectId, operationId, DevelopmentOperationPhases.ApplyBlocked, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        lock (_gate)
        {
            // Recorded before the precondition, because this ledger counts the ASK: a retry that reaches the gate and is
            // refused by it still reached the gate, and that is the difference between a real second ask and a memoized
            // answer.
            _offered.Add(taskId);

            // Recorded, not judged: the ownership guard is the real service's. What matters here is that the lane names
            // its run at all, because nothing else scripted would notice if it stopped.
            _onBehalfOf.Add(onBehalfOfWorkflowRunId);
        }

        // The real service's precondition, kept because it is what a RETRY meets: an apply reads the approved subject off
        // a task awaiting apply, and a task the gate has already declined is not one — so the second attempt is answered
        // about the precondition rather than about the refusal that caused it.
        if (task.Status != DevelopmentTaskStatus.AwaitingApply)
        {
            throw new DevelopmentInvalidTransitionException("Patch preview requires an independently approved task awaiting explicit apply.");
        }

        bool blocked;
        bool hold;
        lock (_gate)
        {
            blocked = _appliesAllowed is { } allowed && _offered.Count > allowed;
            hold = !blocked && _holdAfterApplies == _offered.Count;
        }

        var subject = new DevelopmentApprovedApplySubject(projectId,
            taskId,
            task.Version,
            "0000000000000000000000000000000000000000",
            "patch-hash",
            "manifest-hash",
            "result-hash",
            string.Concat(projectId.ToString("N"), "/", Guid.Empty.ToString("N")),
            string.Concat(projectId.ToString("N"), "/", Guid.Empty.ToString("N")),
            SubjectHash: task.ApprovedSubjectHash ?? string.Empty);
        if (blocked)
        {
            return await store.BlockApplyAsync(operationId, subject, BlockedReason, cancellationToken).ConfigureAwait(false);
        }

        var result = await store.CompleteApplyAsync(operationId, subject, cancellationToken).ConfigureAwait(false);
        if (hold)
        {
            // Bounded, and NOT on the caller's token: the point is to be holding when the cancel arrives, so observing
            // it here would swallow the very checkpoint under test. The timeout only turns a broken test into a failure
            // instead of a hang.
            _ = _applyHeld.TrySetResult();
            await _applyReleased.Task.WaitAsync(TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }

    public Task<DevelopmentProjectAggregate> ReconnectRepositoryAsync(Guid projectId,
        Guid selectedFolderId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
