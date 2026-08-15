namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One file (or directory) a run produced, staged under the run's own directory. Staged is inert: an artifact is
///     only visible to the rest of the app once <see cref="CommittedModelName" /> is set by registry promotion.
/// </summary>
internal sealed record class TrainingArtifact
{
    public Guid Id { get; set; }

    /// <summary>Owning run. Real FK to <c>training_runs.id</c>, restricted delete; indexed.</summary>
    public Guid RunId { get; set; }

    public TrainingArtifactKind Kind { get; set; }

    /// <summary>
    ///     Where the artifact is staged, under the run's own directory. Plaintext (structural — the export, smoke and
    ///     promotion steps all address the artifact by path), same posture as the image lane's parts manifest paths.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>SHA-256 hex over the staged bytes. Null until the export step has finished writing and hashed it.</summary>
    public string? Sha256 { get; set; }

    public long SizeBytes { get; set; }

    public TrainingArtifactSmokeState SmokeState { get; set; }

    /// <summary>Why the smoke load failed, or why it was skipped. Plaintext, bounded, operator-facing.</summary>
    public string? SmokeReason { get; set; }

    /// <summary>The registry name this artifact was promoted under. Null while the artifact is still staged.</summary>
    public string? CommittedModelName { get; set; }

    public long Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
