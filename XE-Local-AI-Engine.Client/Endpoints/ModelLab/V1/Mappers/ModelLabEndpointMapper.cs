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
            MetadataPresent = result.MetadataPresent,
            CheckpointHiddenSize = result.CheckpointHiddenSize,
            CheckpointBlockCount = result.CheckpointBlockCount,
            GgufEmbeddingLength = result.GgufEmbeddingLength,
            GgufBlockCount = result.GgufBlockCount,
            SteerableBlockCount = result.GgufSteerableBlockCount,
            ModelType = result.ModelType,
            CheckpointLayerTypes = result.CheckpointLayerTypes,
            AttentionSupported = result.AttentionSupported,
            AttentionRefusalMessage = result.AttentionRefusalMessage,
            AttentionRefusal = result.AttentionRefusal,
            TraceRefusalMessage = result.TraceRefusalMessage,
            TraceRefusal = result.TraceRefusal,
            DefaultSteeringLayer = result.DefaultSteeringLayer,
            LicenseConfirmationText = result.LicenseConfirmationText,
            Refusal = result.Refusal,
            RefusalMessage = result.RefusalMessage
        };

    /// <summary><paramref name="detail" /> adds the result and the note, which the list leaves out.</summary>
    public static ModelLabExperimentResponse ToResponse(ModelLabExperimentRecord record, bool detail) =>
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
            Result = detail && record.ResultJson is { } json ? ModelLabTraceSpec.ReadResultOrNull(json) : null,
            Notes = detail ? record.Notes : null,
            IsPinned = record.IsPinned,
            AppVersion = record.AppVersion,
            CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(record.CreatedAtUtc),
            UpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(record.UpdatedAtUtc)
        };

    public static ModelLabPromptTokensResponse ToResponse(ModelLabPromptTokens tokens) =>
        new()
        {
            RenderedPrompt = tokens.RenderedPrompt,
            Tokens =
            [
                .. tokens.Tokens.Select(static token => new ModelLabPromptTokenResponse
                {
                    Id = token.Id,
                    Piece = token.Piece,
                    Special = token.Special
                })
            ],
            TokenCount = tokens.TokenCount,
            Truncated = tokens.Truncated
        };
}
