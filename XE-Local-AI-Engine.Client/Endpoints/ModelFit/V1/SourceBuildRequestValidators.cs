namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed class StartLlamaCppSourceBuildRequestValidator : Validator<StartLlamaCppSourceBuildRequest>
{
    public StartLlamaCppSourceBuildRequestValidator()
    {
        RuleFor(static request => request)
            .Must(BeValid)
            .WithMessage("The source-build request is invalid. Custom builds require a canonical public GitHub HTTPS repository and explicit risk acknowledgement; commits must be full 40-character SHAs.");
    }

    private static bool BeValid(StartLlamaCppSourceBuildRequest request)
    {
        try
        {
            _ = LlamaCppSourceBuildRequestValidation.Normalize(request.ToContract());
            return true;
        }
        catch (LlamaRuntimeException)
        {
            return false;
        }
    }
}

public sealed class GetLlamaCppSourceBuildPrerequisitesRequestValidator : Validator<GetLlamaCppSourceBuildPrerequisitesRequest>
{
    public GetLlamaCppSourceBuildPrerequisitesRequestValidator()
    {
        RuleFor(static request => request.Backend)
            .Must(Enum.IsDefined)
            .WithMessage("Backend must be cpu, vulkan, or cuda.");
    }
}
