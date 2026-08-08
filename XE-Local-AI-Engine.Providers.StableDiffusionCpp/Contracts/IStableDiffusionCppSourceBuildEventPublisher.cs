namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public interface IStableDiffusionCppSourceBuildEventPublisher
{
    Task PublishStatusAsync(StableDiffusionCppSourceBuildStatusEvent statusEvent, CancellationToken ct = default);
}

public static class StableDiffusionCppSourceBuildEvents
{
    public const string StatusChanged = "stableDiffusionCppSourceBuild.statusChanged";
}

public sealed record StableDiffusionCppSourceBuildStatusEvent(
    StableDiffusionCppSourceBuildPhase Phase,
    IReadOnlyList<string> AppendedLogLines,
    long AppendedLogStartSequence,
    bool Terminal,
    string? SanitizedError,
    StableDiffusionCppSourceBuildDescriptor? CurrentBuild)
{
    public Guid? BuildId => CurrentBuild?.BuildId;
}
