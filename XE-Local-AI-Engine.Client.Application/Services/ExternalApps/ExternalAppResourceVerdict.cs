namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Whether this machine can take one more application, with the figures the decision was made on.
/// </summary>
/// <remarks>
///     In bytes rather than mebibytes, so the API layer never converts and the two sides cannot disagree about
///     which unit a number is in. The figures ship even on a pass, because the install dialog shows what it
///     measured rather than only what it concluded.
/// </remarks>
public sealed class ExternalAppResourceVerdict
{
    public required bool Satisfied { get; init; }

    public required ExternalAppFailureCategory? FailureCategory { get; init; }

    public required long RequiredMemoryBytes { get; init; }

    public required long AvailableMemoryBytes { get; init; }

    public required long RequiredDiskBytes { get; init; }

    public required long AvailableDiskBytes { get; init; }

    public required string Message { get; init; }
}
