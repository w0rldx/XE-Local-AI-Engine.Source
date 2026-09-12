namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>Projects the model catalogue and its download statuses onto the wire DTOs.</summary>
internal static class TranscriptionModelMapper
{
    public static TranscriptionModelListResponse ToResponse(this TranscriptionModelCatalogView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new TranscriptionModelListResponse
        {
            Models = [.. view.Models.Select(static model => model.ToResponse())],
            SelectedModelId = view.SelectedModelId,
            RecommendedModelId = view.RecommendedModelId
        };
    }

    public static TranscriptionModelResponse ToResponse(this TranscriptionModelView model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new TranscriptionModelResponse
        {
            Id = model.Entry.Id,
            Tier = model.Entry.Tier.ToString(),
            SizeBytes = model.Entry.SizeBytes,
            ApproximateVramBytes = model.Entry.ApproximateVramBytes,
            ApproximateRamBytes = model.Entry.ApproximateRamBytes,
            EnglishOnly = model.Entry.EnglishOnly,
            Installed = model.Installed,
            Download = model.Download?.ToResponse()
        };
    }

    public static TranscriptionModelDownloadStatusResponse ToResponse(this WhisperModelDownloadStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new TranscriptionModelDownloadStatusResponse
        {
            Phase = status.Phase switch
            {
                WhisperModelDownloadPhase.Running => TranscriptionModelDownloadPhaseDto.Running,
                WhisperModelDownloadPhase.Completed => TranscriptionModelDownloadPhaseDto.Completed,
                WhisperModelDownloadPhase.Cancelled => TranscriptionModelDownloadPhaseDto.Cancelled,
                WhisperModelDownloadPhase.Failed => TranscriptionModelDownloadPhaseDto.Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(status), status.Phase, "Unknown download phase.")
            },
            CompletedBytes = status.CompletedBytes,
            TotalBytes = status.TotalBytes,
            PartIndex = status.PartIndex,
            PartCount = status.PartCount,
            SanitizedError = status.SanitizedError
        };
    }
}
