namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Validators;

using FastEndpoints;

/// <summary>The model-name grammar for the GGUF inspection read.</summary>
public sealed class GetModelGgufInspectionRequestValidator : Validator<GetModelGgufInspectionRequest>
{
    public GetModelGgufInspectionRequestValidator()
    {
        this.AddRouteBoundModelNameRule(static request => request.ModelName);
    }
}
