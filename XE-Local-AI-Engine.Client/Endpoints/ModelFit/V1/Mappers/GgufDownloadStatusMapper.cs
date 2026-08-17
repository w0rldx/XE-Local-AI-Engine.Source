namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.ModelFit;

internal static class GgufDownloadStatusMapper
{
    public static GgufDownloadStatusResponse Map(GgufDownloadStatus status) =>
        new()
        {
            OperationId = status.OperationId,
            OperationKind = status.OperationKind,
            ModelName = status.ModelName,
            Phase = status.Phase.ToString(),
            CompletedBytes = status.CompletedBytes,
            TotalBytes = status.TotalBytes,
            SanitizedError = status.SanitizedError,
            ErrorCode = status.ErrorCode,
            StartedAtUtc = status.StartedAtUtc,
            UpdatedAtUtc = status.UpdatedAtUtc
        };
}
