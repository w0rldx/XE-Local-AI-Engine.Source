namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Push channel for source-build progress. The provider registers a no-op implementation as its floor; a host that
///     has a hub substitutes one that forwards.
/// </summary>
public interface IWhisperCppSourceBuildEventPublisher
{
    Task PublishStatusAsync(WhisperCppSourceBuildStatusEvent statusEvent, CancellationToken ct = default);
}

/// <summary>Event names this lane publishes under.</summary>
public static class WhisperCppSourceBuildEvents
{
    public const string StatusChanged = "whisperCppSourceBuild.statusChanged";
}

/// <summary>
///     One change in the build's state: the phase now, and only the log lines appended since the last event.
/// </summary>
/// <param name="AppendedLogLines">The new lines, never the whole retained tail.</param>
/// <param name="AppendedLogStartSequence">Sequence number of the first appended line, so a subscriber can spot a gap.</param>
public sealed record WhisperCppSourceBuildStatusEvent(
    WhisperCppSourceBuildPhase Phase,
    IReadOnlyList<string> AppendedLogLines,
    long AppendedLogStartSequence,
    bool Terminal,
    string? SanitizedError,
    WhisperCppSourceBuildDescriptor? CurrentBuild)
{
    public Guid? BuildId => CurrentBuild?.BuildId;
}
