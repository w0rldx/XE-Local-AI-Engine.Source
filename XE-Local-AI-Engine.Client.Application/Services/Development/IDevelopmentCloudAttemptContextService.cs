namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal interface IDevelopmentCloudAttemptContextService
{
    Task<DevelopmentCloudAttemptContext> CreateAsync(DevelopmentExecutionSnapshot snapshot,
        IReadOnlyList<DevelopmentCloudContextExcerpt> excerpts,
        IReadOnlyList<Guid>? inputArtifactIds = null,
        CancellationToken cancellationToken = default);
}
