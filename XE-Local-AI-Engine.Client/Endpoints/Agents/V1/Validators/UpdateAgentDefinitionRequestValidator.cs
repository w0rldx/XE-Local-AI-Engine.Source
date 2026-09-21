namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>Bounds the echoed AI-drafting provenance block, which is operator input like any other field.</summary>
/// <remarks>
///     The refusal precedes the record lookup, as the handler's own check did, so an oversized block on an unknown
///     definition id still answers the shape error rather than a 404.
/// </remarks>
public sealed class UpdateAgentDefinitionRequestValidator : Validator<UpdateAgentDefinitionRequest>
{
    public UpdateAgentDefinitionRequestValidator()
    {
        this.AddFirstViolationRule(static request => GenerationProvenance.Validate(request.GenerationMetadata));
    }
}
