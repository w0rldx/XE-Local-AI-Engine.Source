namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface IDevelopmentCoordinator
{
    Task<DevelopmentOperationResult> CreateProjectAsync(DevelopmentCreateProjectCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> StartAttemptAsync(DevelopmentStartAttemptCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> TerminalizeAttemptAsync(DevelopmentTerminalizeAttemptCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> TransitionTaskAsync(DevelopmentTransitionTaskCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> AttachArtifactAsync(DevelopmentAttachArtifactCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> ApplyAsync(Guid operationId, DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default);
    Task<int> ReconcileStartupAsync(CancellationToken cancellationToken = default);
}

