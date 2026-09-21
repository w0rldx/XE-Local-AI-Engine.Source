namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Validators;

using FastEndpoints;

/// <summary>
///     The model-name grammar for the launch-argument read and clear, which share one request type.
/// </summary>
public sealed class GetModelLaunchArgumentsRequestValidator : Validator<GetModelLaunchArgumentsRequest>
{
    public GetModelLaunchArgumentsRequestValidator()
    {
        this.AddRouteBoundModelNameRule(static request => request.ModelName);
    }
}
