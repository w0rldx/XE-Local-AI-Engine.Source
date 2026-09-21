namespace XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     Shape rule for the run id the per-run routes take from the path.
/// </summary>
/// <remarks>
///     The same pattern the patch family uses, reused rather than restated: a third spelling of "what a run id looks
///     like" is a divergence nothing would catch. The authoritative check still lives in the services, where the path
///     is composed; this one answers a malformed id with a 400 at the edge instead of letting it reach a filesystem
///     call at all.
/// </remarks>
public sealed class AgentHomeRunByIdRequestValidator : Validator<AgentHomeRunByIdRequest>
{
    public AgentHomeRunByIdRequestValidator()
    {
        RuleFor(static request => request.RunId)
            .NotEmpty()
            .WithMessage("A run route needs a run id.")
            .MaximumLength(AgentHomePatchRequestRules.MaxRunIdLength)
            .WithMessage("The run id is longer than the limit.")
            .Matches(AgentHomePatchRequestRules.RunIdPattern)
            .WithMessage("The run id is not a valid identifier.");
    }
}
