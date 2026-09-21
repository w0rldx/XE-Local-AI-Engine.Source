namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>Refuses a blank repository id before discovery is called.</summary>
public sealed class InspectImageRepositoryRequestValidator : Validator<InspectImageRepositoryRequest>
{
    public InspectImageRepositoryRequestValidator()
    {
        this.AddFirstViolationRule(static request =>
            string.IsNullOrWhiteSpace(request.RepoId) ? "A repository id is required." : null);
    }
}
