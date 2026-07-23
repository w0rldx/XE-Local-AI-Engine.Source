namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class DevelopmentCoordinator(IDevelopmentStore store, IDevelopmentHostApplyPort applyPort) : IDevelopmentCoordinator
{
    private const string StartupInterruptedReason = "The node restarted while the Development attempt was running.";
    private const string StartupValidationRecoveryReason = "The node restarted before deterministic Development validation completed.";
    private const string AmbiguousApplyReason = "The host apply state did not match the approved base or exact approved result.";

    private readonly IDevelopmentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDevelopmentHostApplyPort _applyPort = applyPort ?? throw new ArgumentNullException(nameof(applyPort));

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
            throw new DevelopmentWorkspaceSecurityException("The selected repository does not match the approved apply subject.");
        }

        var repositoryRoot = repository.RepositoryRoot;
        var completed = await _store.FindOperationAsync(subject.ProjectId,
                                        operationId,
                                        DevelopmentOperationPhases.ApplyCompleted,
                                        cancellationToken)
                                    .ConfigureAwait(false);
        if (completed is not null)
        {
            return completed;
        }

        var blocked = await _store.FindOperationAsync(subject.ProjectId,
                                      operationId,
                                      DevelopmentOperationPhases.ApplyBlocked,
                                      cancellationToken)
                                  .ConfigureAwait(false);
        if (blocked is not null)
        {
            return blocked;
        }

        _ = await _store.RecordApplyStartedAsync(operationId, subject, cancellationToken).ConfigureAwait(false);
        var hostState = await _applyPort.InspectAsync(subject, repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (revalidateBeforeHostMutation is not null)
        {
            await revalidateBeforeHostMutation(cancellationToken).ConfigureAwait(false);
        }

        switch (hostState)
        {
            case DevelopmentHostApplyState.UnappliedBaseUnchanged:
                await _applyPort.ApplyAsync(subject, repositoryRoot, cancellationToken).ConfigureAwait(false);
                break;
            case DevelopmentHostApplyState.ExactApprovedResultPresent:
                break;
            case DevelopmentHostApplyState.Ambiguous:
                return await _store.BlockApplyAsync(operationId, subject, AmbiguousApplyReason, cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"Unsupported Development host apply state '{hostState}'.");
        }

        return await _store.CompleteApplyAsync(operationId, subject, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ReconcileStartupAsync(CancellationToken cancellationToken = default)
    {
        var interrupted = await _store.ReconcileRunningAttemptsAsync(StartupInterruptedReason, cancellationToken).ConfigureAwait(false);
        var validations = await _store.ReconcileIncompleteValidationsAsync(StartupValidationRecoveryReason, cancellationToken).ConfigureAwait(false);
        return interrupted + validations;
    }
}
