namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     Rejects a download or cancel for an id that is not in the catalogue, at the boundary — so the coordinator's
///     own <c>ArgumentException</c> can never surface as a 500 for what is an ordinary client mistake.
/// </summary>
public sealed class TranscriptionModelDownloadRequestValidator : Validator<TranscriptionModelDownloadRequest>
{
    public TranscriptionModelDownloadRequestValidator()
    {
        RuleFor(request => request.ModelId)
            .NotEmpty()
            .WithMessage("A transcription model id is required.")
            .Must(static modelId => WhisperModelCatalog.Find(modelId) is not null)
            .WithMessage("The requested transcription model is not in the catalogue.");
    }
}
