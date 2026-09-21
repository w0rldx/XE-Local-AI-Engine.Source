namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>Caps every draft field before the drafting service acquires the node's single draft slot.</summary>
/// <remarks>
///     The two name/description caps are this surface's own; the rest are shared with the agent draft route. Refusing
///     here rather than in the handler keeps an oversized or hostile request from ever reaching the slot.
/// </remarks>
public sealed class DraftSkillRequestValidator : Validator<DraftSkillRequest>
{
    private const int MaxExistingDescriptionLength = 1024;
    private const int MaxExistingNameLength = 64;

    public DraftSkillRequestValidator()
    {
        this.AddFirstViolationRule(static request => DraftEndpointSupport.ValidateRequest(request.ModelName,
            request.Brief,
            request.ExistingName,
            request.ExistingDescription,
            request.ExistingContent,
            MaxExistingNameLength,
            MaxExistingDescriptionLength));
    }
}
