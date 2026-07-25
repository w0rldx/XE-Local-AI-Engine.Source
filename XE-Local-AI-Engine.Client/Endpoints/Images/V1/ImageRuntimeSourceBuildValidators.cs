namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;

public sealed class StartStableDiffusionCppSourceBuildRequestValidator : Validator<StartStableDiffusionCppSourceBuildRequest>
{
    public StartStableDiffusionCppSourceBuildRequestValidator()
    {
        RuleFor(static request => request)
            .Must(BeValid)
            .WithMessage(
                "The source-build request is invalid. Custom builds require a canonical public GitHub HTTPS repository and explicit risk acknowledgement; commits must be full 40-character SHAs.");
    }

    private static bool BeValid(StartStableDiffusionCppSourceBuildRequest request)
    {
        try
        {
            _ = StableDiffusionCppSourceBuildRequestValidation.Normalize(request.ToContract());
            return true;
        }
        catch (StableDiffusionRuntimeException)
        {
            return false;
        }
    }
}

public sealed class GetStableDiffusionCppSourceBuildPrerequisitesRequestValidator
    : Validator<GetStableDiffusionCppSourceBuildPrerequisitesRequest>
{
    public GetStableDiffusionCppSourceBuildPrerequisitesRequestValidator()
    {
        RuleFor(static request => request.Backend)
            .Must(Enum.IsDefined)
            .WithMessage("Backend must be cpu, vulkan, or cuda.");
    }
}
