namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>The <c>job.json</c> handed to <c>train.py</c> — the whole input contract, in one file.</summary>
public sealed record TrainingJobConfigV1
{
    public int ContractVersion { get; init; }

    public Guid RunId { get; init; }

    public string BasePath { get; init; } = string.Empty;

    public string DatasetPath { get; init; } = string.Empty;

    public string WorkDir { get; init; } = string.Empty;

    public string OutputDir { get; init; } = string.Empty;

    /// <summary>Canonical sequences the trainer must skip — the frozen holdout.</summary>
    public IReadOnlyList<int> HoldoutSequences { get; init; } = [];

    public TrainingRunOptionsV1 Options { get; init; } = new();
}
