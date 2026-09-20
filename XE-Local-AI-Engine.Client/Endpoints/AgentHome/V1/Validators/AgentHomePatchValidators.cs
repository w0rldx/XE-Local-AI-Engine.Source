namespace XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     Shape rules for the run id and the preview hash. The authoritative check stays in the service, where the path
///     is composed; this one answers a malformed id with a 400 at the edge instead of a rejection string.
/// </summary>
internal static class AgentHomePatchRequestRules
{
    /// <summary>
    ///     The run id shape <c>AgentHomeService.CreateRunId</c> produces and
    ///     <c>NodePatchApplyService</c>'s own regex accepts: alphanumeric first character, then alphanumerics,
    ///     underscores and hyphens. No separator of any kind, so nothing here can compose a path.
    /// </summary>
    public const string RunIdPattern = "^[A-Za-z0-9][A-Za-z0-9_-]*$";

    /// <summary>
    ///     The 64 hex characters a SHA-256 is rendered as, in EITHER case: the service compares case-insensitively,
    ///     and the edge must accept what the comparison accepts. The response DTO still emits lowercase only.
    /// </summary>
    public const string Sha256Pattern = "^[0-9a-fA-F]{64}$";

    public const int MaxRunIdLength = 128;
}

public sealed class AgentHomePatchPreviewRequestValidator : Validator<AgentHomePatchPreviewRequest>
{
    public AgentHomePatchPreviewRequestValidator()
    {
        RuleFor(static request => request.RunId)
            .NotEmpty()
            .WithMessage("A patch preview needs a run id.")
            .MaximumLength(AgentHomePatchRequestRules.MaxRunIdLength)
            .WithMessage("The run id is longer than the limit.")
            .Matches(AgentHomePatchRequestRules.RunIdPattern)
            .WithMessage("The run id is not a valid identifier.");
    }
}

public sealed class AgentHomePatchApplyRequestValidator : Validator<AgentHomePatchApplyRequest>
{
    public AgentHomePatchApplyRequestValidator()
    {
        RuleFor(static request => request.RunId)
            .NotEmpty()
            .WithMessage("A patch apply needs a run id.")
            .MaximumLength(AgentHomePatchRequestRules.MaxRunIdLength)
            .WithMessage("The run id is longer than the limit.")
            .Matches(AgentHomePatchRequestRules.RunIdPattern)
            .WithMessage("The run id is not a valid identifier.");

        RuleFor(static request => request.PatchSha256)
            .Matches(AgentHomePatchRequestRules.Sha256Pattern)
            .WithMessage("A patch apply must carry the patch hash the preview reported.");
    }
}
