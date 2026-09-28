namespace XE_Local_AI_Engine.Client.Services.Development;

internal interface IDevelopmentCoderAttemptRunner
{
    Task<DevelopmentCoderAttemptResult> RunAsync(Guid attemptId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default);
}
