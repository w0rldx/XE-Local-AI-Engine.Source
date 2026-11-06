namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Validators;

/// <summary>The model-name grammar for the architecture descriptor read.</summary>
public sealed class GetModelLabArchitectureRequestValidator : Validator<GetModelLabArchitectureRequest>
{
    public GetModelLabArchitectureRequestValidator()
    {
        this.AddRouteBoundModelNameRule(static request => request.ModelName);
    }
}
