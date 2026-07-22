namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public interface ILlamaCppSourceBuildEventPublisher
{
    Task PublishStatusAsync(LlamaCppSourceBuildStatusHubEvent statusEvent, CancellationToken cancellationToken = default);
}

public static class LlamaCppSourceBuildHubEvents
{
    public const string StatusChanged = "llamaCppSourceBuild.statusChanged";
}

public sealed record LlamaCppSourceBuildStatusHubEvent(
    string Phase,
    IReadOnlyList<string> AppendedLogLines,
    bool Terminal,
    string? SanitizedError,
    LlamaCppSourceBuildDescriptor? CurrentBuild)
{
    public Guid? BuildId => CurrentBuild?.BuildId;
}
