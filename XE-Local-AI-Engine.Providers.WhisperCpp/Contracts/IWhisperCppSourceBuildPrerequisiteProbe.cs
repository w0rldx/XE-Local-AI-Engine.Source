namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>One checklist row the operator sees before starting a source build.</summary>
/// <param name="Key">Stable identifier the SPA keys on (<c>os-is-linux</c>, <c>cmake</c>, <c>free-disk</c>, …).</param>
/// <param name="Satisfied">Whether this prerequisite is met.</param>
/// <param name="Detail">A short, display-safe explanation — usually the tool's own first version line.</param>
public sealed record WhisperCppSourceBuildPrerequisiteItem(string Key, bool Satisfied, string Detail);

/// <summary>The whole checklist, plus the one flag that decides whether a build may start.</summary>
public sealed record WhisperCppSourceBuildPrerequisiteReport(
    bool CanBuild,
    IReadOnlyList<WhisperCppSourceBuildPrerequisiteItem> Items);

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
