namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal interface IDevelopmentApplyService
{
    Task<DevelopmentPatchPreview> PreviewAsync(Guid taskId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default);

    Task<DevelopmentOperationResult> ApplyAsync(Guid taskId,
        Guid operationId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default);
}
