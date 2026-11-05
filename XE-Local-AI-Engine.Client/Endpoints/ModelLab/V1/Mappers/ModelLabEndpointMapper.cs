namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.ModelLab;

internal static class ModelLabEndpointMapper
{
    public static ModelLabCheckpointPreflightResponse ToResponse(ModelLabCheckpointPreflight result) =>
        new()
        {
            RepoId = result.RepoId,
            Revision = result.Revision,
            WeightBytes = result.WeightBytes,
            RequiredVramBytes = result.RequiredVramBytes,
            CardVramBytes = result.CardVramBytes,
            License = result.License,
            IsGated = result.IsGated,
            CheckpointHiddenSize = result.CheckpointHiddenSize,
            CheckpointBlockCount = result.CheckpointBlockCount,
            GgufEmbeddingLength = result.GgufEmbeddingLength,
            GgufBlockCount = result.GgufBlockCount,
            SteerableBlockCount = result.GgufSteerableBlockCount,
            CheckpointLayerTypes = result.CheckpointLayerTypes,
            DefaultSteeringLayer = result.DefaultSteeringLayer,
            LicenseConfirmationText = result.LicenseConfirmationText,
            Refusal = result.Refusal,
            RefusalMessage = result.RefusalMessage
        };

    public static ModelLabExperimentResponse ToResponse(ModelLabExperimentRecord record, bool includeResult) =>
        new()
        {
            Id = record.Id,
            Status = record.Status,
            Kind = record.Kind,
            RepoId = record.RepoId,
            Revision = record.Revision,
            InstalledModelName = record.InstalledModelName,
            Prompt = record.Prompt,
            ErrorMessage = record.ErrorMessage,
            TraceByteCount = record.Kind == ModelLabExperimentKind.Trace ? record.TraceByteCount : null,
            Result = includeResult && record.ResultJson is { } json ? ModelLabTraceSpec.ReadResultOrNull(json) : null,
            CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(record.CreatedAtUtc),
            UpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(record.UpdatedAtUtc)
        };
}
