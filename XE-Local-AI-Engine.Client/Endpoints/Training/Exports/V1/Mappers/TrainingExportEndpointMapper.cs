namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Export;

internal static class TrainingExportEndpointMapper
{
    public static TrainingArtifactResponse ToResponse(this TrainingArtifactRecord record) =>
        new()
        {
            Id = record.Id,
            RunId = record.RunId,
            Kind = record.Kind.ToString(),
            // The staged path is a server-side location; only its file name is useful to an operator, and publishing
            // the rest would leak the node's data directory layout into the browser.
            FileName = Path.GetFileName(record.Path),
            Sha256 = record.Sha256,
            SizeBytes = record.SizeBytes,
            SmokeState = record.SmokeState.ToString(),
            SmokeReason = record.SmokeReason,
            CommittedModelName = record.CommittedModelName,
            QualityComparisonId = record.QualityComparisonId,
            QualityOutcome = ArtifactQualityService.ReadDecision(record)?.Outcome.ToString(),
            DiscardedAtUtc = record.DiscardedAtUtc,
            DiscardReason = record.DiscardReason,
            DiscardCleanupPending = record.DiscardCleanupPending,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };
}
