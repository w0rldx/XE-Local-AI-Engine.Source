namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>Caps every draft field before the drafting service acquires the node's single draft slot.</summary>
public sealed class DraftImagePromptRequestValidator : Validator<DraftImagePromptRequest>
{
    // The image form's own prompt caps.
    private const int MaxExistingPromptLength = 2000;

    public DraftImagePromptRequestValidator()
    {
        this.AddFirstViolationRule(static request =>
        {
            if (request.ExistingPrompt is { Length: > MaxExistingPromptLength })
            {
                return $"Existing prompt must be at most {MaxExistingPromptLength} characters.";
            }

            if (request.ExistingNegativePrompt is { Length: > MaxExistingPromptLength })
            {
                return $"Existing negative prompt must be at most {MaxExistingPromptLength} characters.";
            }

            return DraftEndpointSupport.ValidateRequest(request.ModelName,
                request.Brief,
                existingName: null,
                existingDescription: null,
                existingContent: null,
                maxExistingNameLength: 0,
                maxExistingDescriptionLength: 0);
        });
    }
}
