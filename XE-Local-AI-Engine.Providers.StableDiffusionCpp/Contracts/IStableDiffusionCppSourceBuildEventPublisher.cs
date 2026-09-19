namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public interface IStableDiffusionCppSourceBuildEventPublisher
{
    Task PublishStatusAsync(StableDiffusionCppSourceBuildStatusEvent statusEvent, CancellationToken ct = default);
}

public static class StableDiffusionCppSourceBuildEvents
{
    public const string StatusChanged = "stableDiffusionCppSourceBuild.statusChanged";
}

public sealed class StableDiffusionCppSourceBuildStatusEvent
{
    public required StableDiffusionCppSourceBuildPhase Phase { get; init; }

    public required IReadOnlyList<string> AppendedLogLines { get; init; }

    public required long AppendedLogStartSequence { get; init; }

    public required bool Terminal { get; init; }

    public required string? SanitizedError { get; init; }

    public required StableDiffusionCppSourceBuildDescriptor? CurrentBuild { get; init; }

    public Guid? BuildId => CurrentBuild?.BuildId;
}
