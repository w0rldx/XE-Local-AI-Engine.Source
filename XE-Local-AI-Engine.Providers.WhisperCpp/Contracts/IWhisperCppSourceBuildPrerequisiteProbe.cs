namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Bounded, non-interactive probes for the toolchain a managed whisper.cpp build needs.
/// </summary>
/// <remarks>
///     It exists so a refusal can say <em>which</em> tool is missing. Without it the operator is told only that "one
///     or more prerequisites are missing", with nothing to act on.
/// </remarks>
public interface IWhisperCppSourceBuildPrerequisiteProbe
{
    Task<SourceBuildPrerequisiteReport> ProbeAsync(WhisperBackend backend, CancellationToken ct);
}
