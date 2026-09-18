namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class DevelopmentCoordinator : IDevelopmentCoordinator
{
    private const string StartupInterruptedReason = "The node restarted while the Development attempt was running.";
    private const string StartupValidationRecoveryReason = "The node restarted before deterministic Development validation completed.";
    private const string AmbiguousApplyReason = "The host apply state did not match the approved base or exact approved result.";

    private readonly IDevelopmentStore _store;
    private readonly IDevelopmentHostApplyPort _applyPort;

    public DevelopmentCoordinator(IDevelopmentStore store, IDevelopmentHostApplyPort applyPort)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(applyPort);
        _store = store;
        _applyPort = applyPort;
    }

    public Task<DevelopmentOperationResult> CreateProjectAsync(DevelopmentCreateProjectCommand command, CancellationToken cancellationToken = default) =>
        _store.CreateProjectAsync(command, cancellationToken);

    public Task<DevelopmentOperationResult> StartAttemptAsync(DevelopmentStartAttemptCommand command, CancellationToken cancellationToken = default) =>
        _store.StartAttemptAsync(command, cancellationToken);

    public Task<DevelopmentOperationResult> TerminalizeAttemptAsync(DevelopmentTerminalizeAttemptCommand command, CancellationToken cancellationToken = default) =>
        _store.TerminalizeAttemptAsync(command, cancellationToken);

    public Task<DevelopmentOperationResult> TransitionTaskAsync(DevelopmentTransitionTaskCommand command, CancellationToken cancellationToken = default) =>
        _store.TransitionTaskAsync(command, cancellationToken);

    public Task<DevelopmentOperationResult> AttachArtifactAsync(DevelopmentAttachArtifactCommand command, CancellationToken cancellationToken = default) =>
        _store.AttachArtifactAsync(command, cancellationToken);

    public Task<DevelopmentOperationResult> ApplyAsync(Guid operationId,
        DevelopmentApprovedApplySubject subject,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default) =>
        ApplyCoreAsync(operationId, subject, repository, revalidateBeforeHostMutation: null, cancellationToken);

    public Task<DevelopmentOperationResult> ApplyRevalidatedAsync(Guid operationId,
        DevelopmentApprovedApplySubject subject,
        DevelopmentRepositoryBinding repository,
        Func<CancellationToken, Task> revalidateBeforeHostMutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revalidateBeforeHostMutation);
        return ApplyCoreAsync(operationId, subject, repository, revalidateBeforeHostMutation, cancellationToken);
    }

    private async Task<DevelopmentOperationResult> ApplyCoreAsync(Guid operationId,
        DevelopmentApprovedApplySubject subject,
        DevelopmentRepositoryBinding repository,
        Func<CancellationToken, Task>? revalidateBeforeHostMutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(repository);
        if (repository.ProjectId != subject.ProjectId
            || !string.Equals(repository.RepositoryIdentityHash, subject.RepositoryIdentityHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new DevelopmentRepositoryStateConflictException("The selected repository does not match the approved apply subject.");
        }

        var repositoryRoot = repository.RepositoryRoot;
        var completed = await _store.FindOperationAsync(subject.ProjectId,
                                        operationId,
                                        DevelopmentOperationPhases.ApplyCompleted,
                                        cancellationToken);
        if (completed is not null)
        {
            return completed;
        }

        var blocked = await _store.FindOperationAsync(subject.ProjectId,
                                      operationId,
                                      DevelopmentOperationPhases.ApplyBlocked,
                                      cancellationToken);
        if (blocked is not null)
        {
            return blocked;
        }

        _ = await _store.RecordApplyStartedAsync(operationId, subject, cancellationToken);
        var hostState = await _applyPort.InspectAsync(subject, repositoryRoot, cancellationToken);
        if (revalidateBeforeHostMutation is not null)
        {
            await revalidateBeforeHostMutation(cancellationToken);
        }

        switch (hostState)
        {
            case DevelopmentHostApplyState.UnappliedBaseUnchanged:
                await _applyPort.ApplyAsync(subject, repositoryRoot, cancellationToken);
                break;
            case DevelopmentHostApplyState.ExactApprovedResultPresent:
                break;
            case DevelopmentHostApplyState.Ambiguous:
                return await _store.BlockApplyAsync(operationId, subject, AmbiguousApplyReason, cancellationToken);
            default:
                throw new InvalidOperationException($"Unsupported Development host apply state '{hostState}'.");
        }

        return await _store.CompleteApplyAsync(operationId, subject, cancellationToken);
    }

    public async Task<int> ReconcileStartupAsync(CancellationToken cancellationToken = default)
    {
        var interrupted = await _store.ReconcileRunningAttemptsAsync(StartupInterruptedReason, cancellationToken);
        var validations = await _store.ReconcileIncompleteValidationsAsync(StartupValidationRecoveryReason, cancellationToken);
        return interrupted + validations;
    }
}
