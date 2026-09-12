namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>Rejects a backend outside the enum before the probe spawns anything.</summary>
public sealed class GetWhisperCppSourceBuildPrerequisitesRequestValidator
    : Validator<GetWhisperCppSourceBuildPrerequisitesRequest>
{
    public GetWhisperCppSourceBuildPrerequisitesRequestValidator()
    {
        RuleFor(static request => request.Backend)
            .Must(Enum.IsDefined)
            .WithMessage("Backend must be cpu or cuda.");
    }
}
