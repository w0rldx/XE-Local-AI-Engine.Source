namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>One checklist row the operator sees before starting a source build.</summary>
public sealed record WhisperCppSourceBuildPrerequisiteItem
{
    /// <summary>Stable identifier the SPA keys on (<c>os-is-linux</c>, <c>cmake</c>, <c>free-disk</c>, …).</summary>
    public required string Key { get; init; }

    /// <summary>Whether this prerequisite is met.</summary>
    public required bool Satisfied { get; init; }

    /// <summary>A short, display-safe explanation — usually the tool's own first version line.</summary>
    public required string Detail { get; init; }
}

/// <summary>The whole checklist, plus the one flag that decides whether a build may start.</summary>
public sealed class WhisperCppSourceBuildPrerequisiteReport
{
    public required bool CanBuild { get; init; }

    public required IReadOnlyList<WhisperCppSourceBuildPrerequisiteItem> Items { get; init; }
}

/// <summary>
///     Bounded, non-interactive probes for the toolchain a managed whisper.cpp build needs.
/// </summary>
/// <remarks>
///     It exists so a refusal can say <em>which</em> tool is missing. Without it the operator is told only that "one
///     or more prerequisites are missing", with nothing to act on.
/// </remarks>
public interface IWhisperCppSourceBuildPrerequisiteProbe
{
    Task<WhisperCppSourceBuildPrerequisiteReport> ProbeAsync(WhisperBackend backend, CancellationToken ct);
}
