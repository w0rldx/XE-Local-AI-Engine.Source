namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Mappers;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.ModelLab;

internal static class ModelLabEndpointMapper
{
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

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
            LicenseConfirmationText = result.LicenseConfirmationText,
            Refusal = result.Refusal,
            RefusalMessage = result.RefusalMessage
        };

    public static ModelLabExperimentResponse ToResponse(ModelLabExperimentRecord record, bool includeResult) =>
        new()
        {
            Id = record.Id,
            Status = record.Status,
            RepoId = record.RepoId,
            Revision = record.Revision,
            InstalledModelName = record.InstalledModelName,
            Prompt = record.Prompt,
            ErrorMessage = record.ErrorMessage,
            TraceByteCount = record.TraceByteCount,
            Result = includeResult && record.ResultJson is { } json ? ReadResult(json) : null,
            CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(record.CreatedAtUtc),
            UpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(record.UpdatedAtUtc)
        };

    private static ModelLabTraceResult? ReadResult(ReadOnlyMemory<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize<ModelLabTraceResult>(Encoding.UTF8.GetString(json.Span), ResultJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
