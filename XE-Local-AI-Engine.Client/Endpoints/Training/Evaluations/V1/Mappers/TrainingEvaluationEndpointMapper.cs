namespace XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal static class TrainingEvaluationEndpointMapper
{
    public static EvaluationResponse ToResponse(this TrainingEvaluationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new EvaluationResponse
        {
            Id = record.Id,
            TrainingRunId = record.TrainingRunId,
            ComparisonId = record.ComparisonId,
            ModelName = record.ModelName,
            ModelContentFingerprint = record.ModelContentFingerprint,
            DatasetId = record.DatasetId,
            DatasetContentFingerprint = record.DatasetContentFingerprint,
            Status = record.Status.ToString(),
            WorkStatus = record.WorkStatus?.ToString(),
            TotalCount = record.TotalCount,
            ScoredCount = record.ScoredCount,
            PassedCount = record.PassedCount,
            // Projected to a list rather than a map: the generated TypeScript client renders a named row type, and the
            // per-kind table wants a stable order anyway.
            PerKind = TrainingEvaluationResults.ReadTally(record.PerKindJson)
                                               .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                                               .Select(entry => new EvaluationKindTallyResponse
                                               {
                                                   Kind = entry.Key,
                                                   Total = entry.Value.Total,
                                                   Passed = entry.Value.Passed
                                               })
                                               .ToArray(),
            ErrorMessage = record.ErrorMessage,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };
    }
}
