namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class DevelopmentCoordinator(IDevelopmentStore store, IDevelopmentHostApplyPort applyPort) : IDevelopmentCoordinator
{
    private const string StartupInterruptedReason = "The node restarted while the Development attempt was running.";
    private const string AmbiguousApplyReason = "The host apply state did not match the approved base or exact approved result.";

    private readonly IDevelopmentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IDevelopmentHostApplyPort _applyPort = applyPort ?? throw new ArgumentNullException(nameof(applyPort));

    public Task<DevelopmentOperationResult> CreateProjectAsync(DevelopmentCreateProjectCommand command, CancellationToken cancellationToken = default)
        => _store.CreateProjectAsync(command, cancellationToken);

    public Task<DevelopmentOperationResult> StartAttemptAsync(DevelopmentStartAttemptCommand command, CancellationToken cancellationToken = default)
        => _store.StartAttemptAsync(command, cancellationToken);

    public Task<DevelopmentOperationResult> TerminalizeAttemptAsync(DevelopmentTerminalizeAttemptCommand command, CancellationToken cancellationToken = default)
        => _store.TerminalizeAttemptAsync(command, cancellationToken);

    public Task<DevelopmentOperationResult> TransitionTaskAsync(DevelopmentTransitionTaskCommand command, CancellationToken cancellationToken = default)
        => _store.TransitionTaskAsync(command, cancellationToken);

    public Task<DevelopmentOperationResult> AttachArtifactAsync(DevelopmentAttachArtifactCommand command, CancellationToken cancellationToken = default)
        => _store.AttachArtifactAsync(command, cancellationToken);

    public async Task<DevelopmentOperationResult> ApplyAsync(Guid operationId,
        DevelopmentApprovedApplySubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
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
        var hostState = await _applyPort.InspectAsync(subject, cancellationToken).ConfigureAwait(false);
        switch (hostState)
        {
            case DevelopmentHostApplyState.UnappliedBaseUnchanged:
                await _applyPort.ApplyAsync(subject, cancellationToken).ConfigureAwait(false);
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

    public Task<int> ReconcileStartupAsync(CancellationToken cancellationToken = default)
        => _store.ReconcileRunningAttemptsAsync(StartupInterruptedReason, cancellationToken);
}

