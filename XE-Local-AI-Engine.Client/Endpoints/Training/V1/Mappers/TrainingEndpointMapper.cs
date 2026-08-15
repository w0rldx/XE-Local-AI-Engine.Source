namespace XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

internal static class TrainingEndpointMapper
{
    public static TrainingDefinitionResponse ToResponse(this TrainingDefinitionRecord record) =>
        new()
        {
            Id = record.Id,
            Name = record.Name,
            Kind = record.Kind,
            Body = TrainingEndpointSupport.Read<DatasetDefinitionBodyV1>(record.DefinitionJson) ?? new DatasetDefinitionBodyV1(),
            DefinitionVersion = record.DefinitionVersion,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };

    public static TrainingDatasetResponse ToResponse(this TrainingDatasetRecord record) =>
        new()
        {
            Id = record.Id,
            DefinitionId = record.DefinitionId,
            DefinitionVersion = record.DefinitionVersion,
            Name = record.Name,
            Status = record.Status,
            Revision = record.Revision,
            ContentFingerprint = record.ContentFingerprint,
            TotalSampleCount = record.TotalSampleCount,
            GoodSampleCount = record.GoodSampleCount,
            BadSampleCount = record.BadSampleCount,
            RejectedSampleCount = record.RejectedSampleCount,
            DuplicateSampleCount = record.DuplicateSampleCount,
            WorkStatus = record.WorkStatus,
            WorkErrorMessage = record.WorkErrorMessage,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };

    public static TrainingSampleResponse ToResponse(this TrainingSampleRecord record) =>
        new()
        {
            Id = record.Id,
            DatasetId = record.DatasetId,
            Sequence = record.Sequence,
            Kind = record.Kind,
            Label = record.Label,
            ReviewState = record.ReviewState,
            Provenance = record.Provenance,
            SourceHash = record.SourceHash,
            Content = TrainingEndpointSupport.Read<TrainingSampleContentV1>(record.ContentJson) ?? new TrainingSampleContentV1(),
            Validation = TrainingEndpointSupport.Read<TrainingSampleValidationV1>(record.ValidationJson),
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };

    public static ToolMockResponse ToResponse(this ToolMockRecord record) =>
        new()
        {
            Id = record.Id,
            ToolName = record.ToolName,
            Body = TrainingEndpointSupport.Read<ToolMockBodyV1>(record.MockJson) ?? new ToolMockBodyV1(),
            Verification = TrainingEndpointSupport.Read<ToolMockVerificationV1>(record.VerificationJson),
            VerificationState = record.VerificationState,
            Enabled = record.Enabled,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };
}
