namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>Bounds on a transcript-row edit: the trimmed text is what gets stored, so the trimmed text is what is measured.</summary>
public sealed class UpdateTranscriptSegmentRequestValidator : Validator<UpdateTranscriptSegmentRequest>
{
    /// <summary>The longest row an edit may store; far above anything one whisper window produces.</summary>
    internal const int MaxTextLength = 8000;

    public UpdateTranscriptSegmentRequestValidator()
    {
        RuleFor(static request => request.Text)
            .Must(static text => text?.Trim().Length is >= 1 and <= MaxTextLength)
            .WithMessage($"The segment text must be 1 to {MaxTextLength} characters after trimming.");

        RuleFor(static request => request.Seq)
            .GreaterThan(0)
            .WithMessage("The segment sequence must be a positive number.");
    }
}
