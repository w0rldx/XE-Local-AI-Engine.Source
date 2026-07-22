namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public enum DevelopmentHostApplyState
{
    UnappliedBaseUnchanged,
    ExactApprovedResultPresent,
    Ambiguous
}

public interface IDevelopmentHostApplyPort
{
    Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default);
    Task ApplyAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default);
}
