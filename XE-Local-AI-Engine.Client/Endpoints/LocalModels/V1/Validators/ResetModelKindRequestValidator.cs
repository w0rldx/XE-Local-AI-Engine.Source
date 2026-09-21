namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Validators;

using FastEndpoints;

/// <summary>
///     The model-name grammar for the classification-override reset, which carries nothing else to check.
/// </summary>
public sealed class ResetModelKindRequestValidator : Validator<ResetModelKindRequest>
{
    public ResetModelKindRequestValidator()
    {
        this.AddRouteBoundModelNameRule(static request => request.ModelName);
    }
}
