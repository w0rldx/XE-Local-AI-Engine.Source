namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A pipeline verdict. <see cref="Accepted" /> false means the turn could not be turned into a sample at all and is
///     counted as a rejection; a schema-valid turn that failed a later layer is accepted and retained with the
///     <see cref="TrainingSampleLabel.Bad" /> label (decision #9), never discarded.
/// </summary>
public sealed class SampleValidationOutcome
{
    public required bool Accepted { get; init; }

    public required string? RejectionReason { get; init; }

    public required TrainingSampleLabel Label { get; init; }

    public required TrainingSampleContentV1? Content { get; init; }

    public required TrainingSampleValidationV1 Validation { get; init; }
}
