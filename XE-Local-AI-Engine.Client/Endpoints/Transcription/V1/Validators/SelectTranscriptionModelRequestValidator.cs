namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     Validates a model selection. A blank or absent id is VALID and clears the override, so the node falls back to
///     the hardware recommendation — that is a real request, not a missing field. A non-blank id must resolve in the
///     catalogue.
/// </summary>
public sealed class SelectTranscriptionModelRequestValidator : Validator<SelectTranscriptionModelRequest>
{
    public SelectTranscriptionModelRequestValidator()
    {
        RuleFor(request => request.ModelId)
            .Must(static modelId => string.IsNullOrWhiteSpace(modelId) || WhisperModelCatalog.Find(modelId) is not null)
            .WithMessage("The requested transcription model is not in the catalogue.");
    }
}
