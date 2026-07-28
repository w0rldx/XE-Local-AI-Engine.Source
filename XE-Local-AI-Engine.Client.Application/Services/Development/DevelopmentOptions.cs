namespace XE_Local_AI_Engine.Client.Services.Development;

using System.ComponentModel.DataAnnotations;

public sealed class DevelopmentOptions
{
    public const string Section = "Development";

    public bool Enabled { get; init; } = true;

    [Range(1, 256 * 1024 * 1024)]
    public int MaxArtifactBytes { get; init; } = 16 * 1024 * 1024;

    [Range(1, 24 * 60 * 60)]
    public int MaxAttemptDurationSeconds { get; init; } = 30 * 60;

    [Range(1, 1024)]
    public int MaxToolCalls { get; init; } = 64;

    [Range(1, 10_000)]
    public int MaxChangedFiles { get; init; } = 256;

    [Range(1, 16 * 1024 * 1024)]
    public int MaxFileWriteBytes { get; init; } = 1024 * 1024;

    [Range(1, 64 * 1024 * 1024)]
    public int MaxPatchBytes { get; init; } = 8 * 1024 * 1024;

    [Range(1, 16 * 1024 * 1024)]
    public int MaxCommandOutputBytes { get; init; } = 256 * 1024;

    [Range(1, 1_000_000)]
    public int MaxOutputTokens { get; init; } = 32_768;

    [Range(1, 1024)]
    public int LiveChannelCapacity { get; init; } = 64;

    [Range(1, 16_384)]
    public int MaxLiveTextCharacters { get; init; } = 4096;

    [Range(2, 100)]
    public int RepeatedToolWarningThreshold { get; init; } = 3;

    [Range(2, 100)]
    public int RepeatedCommandFailureWarningThreshold { get; init; } = 3;

    [Range(1, 24 * 60 * 60)]
    public int NoProgressWarningSeconds { get; init; } = 120;

    [Range(1, 100)]
    public int SubjectOscillationWarningThreshold { get; init; } = 1;

    [Range(1, 99)]
    public int ApproachingLimitPercent { get; init; } = 80;

    [Range(1, 99)]
    public int ContextHeadroomWarningPercent { get; init; } = 20;

    [Range(2, 100)]
    public int RepeatedReviewFindingWarningThreshold { get; init; } = 2;

    [Range(1, 100)]
    public int PlanningWithoutProgressWarningThreshold { get; init; } = 3;

    /// <summary>
    ///     The deterministic validation command profile, in dependency order. Every id must be in the code-owned
    ///     catalog (<see cref="DevelopmentCommandIds" />), and validation passes only when all of them complete with
    ///     exit code 0.
    ///     <para>
    ///         This default shipped as <c>[GitDiffCheck]</c> alone, which made the gate a whitespace check: an
    ///         attempt could reach <c>InReview</c> having never compiled the code and never run a test. Restore,
    ///         build and test are what make the gate mean anything — do not shrink this back to keep a fixture fast.
    ///         A fixture that cannot afford the full profile overrides it explicitly instead.
    ///     </para>
    ///     <para>
    ///         The dotnet commands name <c>XE-Local-AI-Engine.slnx</c> exactly (the Solution constant in
    ///         <c>DevelopmentWorkspaceTools</c>), so on a foreign registered repository they fail because that
    ///         solution is not there. That loud failure is intended and is strictly better than the silent false
    ///         pass it replaces; making the profile repo-agnostic is tracked separately.
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> ValidationCommandIds { get; init; } =
    [
        DevelopmentCommandIds.GitDiffCheck,
        DevelopmentCommandIds.DotnetRestore,
        DevelopmentCommandIds.DotnetBuildRelease,
        DevelopmentCommandIds.DotnetTestRelease
    ];
}
