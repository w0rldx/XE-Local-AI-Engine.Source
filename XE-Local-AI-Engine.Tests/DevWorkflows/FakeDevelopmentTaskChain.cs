namespace XE_Local_AI_Engine.Tests.DevWorkflows;

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
    private readonly List<Guid> _cancelled = [];
    private readonly IServiceScopeFactory _scopes;
    private int _failuresOwed;
    private bool _holdNext;

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
    ///     Makes the next action start a real coder attempt and leave it RUNNING, so a test can look at a node run
    ///     while the chain is genuinely working — which is the only state a drain has anything to ask about.
    /// </summary>
    public void HoldNextAttempt()
    {
        lock (_gate)
        {
            _holdNext = true;
        }
    }

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
        lock (_gate)
        {
            _actions.Add(task.Status.ToString());
            fail = _failuresOwed > 0;
            if (fail)
            {
                _failuresOwed--;
            }

            hold = !fail && _holdNext;
            _holdNext = false;
        }

        if (fail || hold)
        {
            return await StartAnAttemptAsync(store, projectId, task, fail ? DevelopmentAttemptStatus.Failed : null, cancellationToken).ConfigureAwait(false);
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
    ///     A real attempt row, landed on <paramref name="terminal" /> or left running, so the node run reads it off the
    ///     same rows the real runner would have written.
    /// </summary>
    private static async Task<DevelopmentNextActionResult> StartAnAttemptAsync(IDevelopmentStore store,
        Guid projectId,
        DevelopmentTaskSnapshot task,
        DevelopmentAttemptStatus? terminal,
        CancellationToken cancellationToken)
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
                                   "The scripted coder attempt failed."),
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

    public Task<DevelopmentOperationResult> ApplyAsync(Guid projectId, Guid taskId, Guid operationId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DevelopmentProjectAggregate> ReconnectRepositoryAsync(Guid projectId,
        Guid selectedFolderId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
