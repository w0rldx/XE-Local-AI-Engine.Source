namespace XE_Local_AI_Engine.Client.Services.Development;

using System.ComponentModel.DataAnnotations;

public sealed class DevelopmentOptions
{
    public const string Section = "Development";

    public bool Enabled { get; init; }

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

    public IReadOnlyList<string> ValidationCommandIds { get; init; } =
    [
        DevelopmentCommandIds.GitDiffCheck
    ];
}
