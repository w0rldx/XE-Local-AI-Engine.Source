namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>Stable SignalR client-method name for llama.cpp source-build status pushes.</summary>
public static class LlamaCppSourceBuildHubEvents
{
    public const string StatusChanged = "llamaCppSourceBuild.statusChanged";
}

/// <summary>
///     Stable SignalR wire shape for a source-build status push.
/// </summary>
/// <remarks>
///     Provider contracts intentionally remain transport-agnostic; this projection keeps their CLR enums from leaking
///     as numeric or Pascal-cased values and can absorb further descriptor fields without changing the provider event
///     contract.
/// </remarks>
internal sealed class LlamaCppSourceBuildStatusHubMessage
{
    public required string Phase { get; init; }
    public required IReadOnlyList<string> AppendedLogLines { get; init; }
    public required long AppendedLogStartSequence { get; init; }
    public required bool Terminal { get; init; }
    public string? SanitizedError { get; init; }
    public LlamaCppSourceBuildDescriptorHubMessage? CurrentBuild { get; init; }
}

internal sealed class LlamaCppSourceBuildDescriptorHubMessage
{
    public required Guid BuildId { get; init; }
    public required string Backend { get; init; }
    public required string Source { get; init; }
    public required string Repository { get; init; }
    public required string RevisionMode { get; init; }
    public string? RequestedCommit { get; init; }
    public string? ResolvedCommit { get; init; }
}
