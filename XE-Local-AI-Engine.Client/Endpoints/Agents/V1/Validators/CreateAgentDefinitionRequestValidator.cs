namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>Bounds the echoed AI-drafting provenance block, which is operator input like any other field.</summary>
public sealed class CreateAgentDefinitionRequestValidator : Validator<CreateAgentDefinitionRequest>
{
    public CreateAgentDefinitionRequestValidator()
    {
        this.AddFirstViolationRule(static request => GenerationProvenance.Validate(request.GenerationMetadata));
    }
}
