namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>Stable SignalR client-method name for managed image-runtime source-build status pushes.</summary>
public static class StableDiffusionCppSourceBuildHubEvents
{
    public const string StatusChanged = "stableDiffusionCppSourceBuild.statusChanged";
}

/// <summary>Stable SignalR wire shape for an image-runtime source-build status push.</summary>
/// <remarks>
///     The provider event stays transport-agnostic; this projection spells its enums as the lowerCamelCase strings the
///     React hook validates, the same values the REST source-build status carries.
/// </remarks>
internal sealed class StableDiffusionCppSourceBuildStatusHubMessage
{
    public required string Phase { get; init; }
    public required IReadOnlyList<string> AppendedLogLines { get; init; }
    public required long AppendedLogStartSequence { get; init; }
    public required bool Terminal { get; init; }
    public string? SanitizedError { get; init; }
    public StableDiffusionCppSourceBuildDescriptorHubMessage? CurrentBuild { get; init; }
}

internal sealed class StableDiffusionCppSourceBuildDescriptorHubMessage
{
    public required Guid BuildId { get; init; }
    public required string Backend { get; init; }
    public required string Source { get; init; }
    public required string Repository { get; init; }
    public required string RevisionMode { get; init; }
    public string? RequestedCommit { get; init; }
    public string? ResolvedCommit { get; init; }
}
