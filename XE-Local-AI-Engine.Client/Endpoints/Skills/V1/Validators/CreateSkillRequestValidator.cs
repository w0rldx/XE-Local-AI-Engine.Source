namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>Bounds the echoed AI-drafting provenance block, which is operator input like any other field.</summary>
public sealed class CreateSkillRequestValidator : Validator<CreateSkillRequest>
{
    public CreateSkillRequestValidator()
    {
        this.AddFirstViolationRule(static request => GenerationProvenance.Validate(request.GenerationMetadata));
    }
}
