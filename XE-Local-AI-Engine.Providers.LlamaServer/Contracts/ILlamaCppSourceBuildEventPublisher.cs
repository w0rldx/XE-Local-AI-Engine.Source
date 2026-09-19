namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public interface ILlamaCppSourceBuildEventPublisher
{
    Task PublishStatusAsync(LlamaCppSourceBuildStatusHubEvent statusEvent, CancellationToken cancellationToken = default);
}

public static class LlamaCppSourceBuildHubEvents
{
    public const string StatusChanged = "llamaCppSourceBuild.statusChanged";
}

public sealed class LlamaCppSourceBuildStatusHubEvent
{
    public required string Phase { get; init; }

    public required IReadOnlyList<string> AppendedLogLines { get; init; }

    public required long AppendedLogStartSequence { get; init; }

    public required bool Terminal { get; init; }

    public required string? SanitizedError { get; init; }

    public required LlamaCppSourceBuildDescriptor? CurrentBuild { get; init; }

    public Guid? BuildId => CurrentBuild?.BuildId;
}
