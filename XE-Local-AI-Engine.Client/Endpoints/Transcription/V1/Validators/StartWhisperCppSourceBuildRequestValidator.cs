namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     Rejects a source-build request the provider's own normalizer would refuse, so the operator gets a 400 with a
///     reason instead of a 409 carrying an exception message.
/// </summary>
/// <remarks>
///     The rule is expressed by round-tripping through
///     <see cref="WhisperCppSourceBuildRequestValidation.Normalize" /> rather than restated here: a second copy of
///     "what a valid build request looks like" would drift from the one the build itself enforces.
/// </remarks>
public sealed class StartWhisperCppSourceBuildRequestValidator : Validator<StartWhisperCppSourceBuildRequest>
{
    public StartWhisperCppSourceBuildRequestValidator()
    {
        RuleFor(static request => request)
            .Must(BeValid)
            .WithMessage("The source-build request is invalid. Custom builds require a canonical public GitHub HTTPS "
                         + "repository and explicit risk acknowledgement; commits must be full 40-character SHAs.");
    }

    private static bool BeValid(StartWhisperCppSourceBuildRequest request)
    {
        try
        {
            _ = WhisperCppSourceBuildRequestValidation.Normalize(request.ToContract());
            return true;
        }
        catch (WhisperRuntimeException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            // An out-of-range enum never reaches the normalizer's own checks; it is still a bad request, not a 500.
            return false;
        }
    }
}
