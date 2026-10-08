namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>Refuses a blank or malformed repository id before discovery is called.</summary>
public sealed class InspectImageRepositoryRequestValidator : Validator<InspectImageRepositoryRequest>
{
    public InspectImageRepositoryRequestValidator()
    {
        this.AddFirstViolationRule(static request => Check(request.RepoId));
    }

    private static string? Check(string? repoId)
    {
        if (string.IsNullOrWhiteSpace(repoId))
        {
            return "A repository id is required.";
        }

        return HuggingFaceRepoId.IsValid(repoId.Trim()) ? null : HuggingFaceRepoId.InvalidMessage;
    }
}
