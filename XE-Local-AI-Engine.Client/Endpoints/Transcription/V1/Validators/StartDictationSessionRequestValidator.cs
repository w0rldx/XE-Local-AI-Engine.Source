namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     The dictation language follows the session language override's rule: absent, or a 2-8 character code.
/// </summary>
public sealed class StartDictationSessionRequestValidator : Validator<StartDictationSessionRequest>
{
    public StartDictationSessionRequestValidator()
    {
        // Declared on the REQUEST, never the property, as CreateTranscriptionSessionRequestValidator does.
        RuleFor(static request => request)
            .Must(static request => request.Language is null || request.Language.Trim().Length is >= 2 and <= 8)
            .WithMessage("Name the dictation language as a 2-8 character code, or leave it out to detect it.");
    }
}
