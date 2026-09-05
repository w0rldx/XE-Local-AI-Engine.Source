namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Shape validation only: lengths, enum parsing, non-empty ids and query ranges. Whether the agent exists and its
///     model can call a tool needs a resolved model and stays in the service — a second capability gate here would be a
///     second thing to keep in step.
///     <para>
///         The two length ceilings mirror the service's. They are duplicated deliberately: this layer rejects an
///         over-long title before a conversation is created for it, and the service still enforces its own for callers
///         that are not this API.
///     </para>
/// </summary>
public sealed class CreateWorkSessionRequestValidator : Validator<CreateWorkSessionRequest>
{
    public CreateWorkSessionRequestValidator()
    {
        RuleFor(static request => request.Title)
            .NotEmpty()
            .WithMessage("A work session needs a title.")
            .MaximumLength(WorkSessionRequestLimits.MaxTitleLength)
            .WithMessage($"The title is longer than the {WorkSessionRequestLimits.MaxTitleLength}-character limit.");

        RuleFor(static request => request.Objective)
            .NotEmpty()
            .WithMessage("A work session needs an objective.")
            .MaximumLength(WorkSessionRequestLimits.MaxObjectiveLength)
            .WithMessage($"The objective is longer than the {WorkSessionRequestLimits.MaxObjectiveLength}-character limit.");

        // Development is a real enum member but no work-session execution path supports it. Refuse it at the wire,
        // where the caller can be told why, rather than accepting a session that cannot run.
        RuleFor(static request => request.Kind)
            .Must(static kind => Enum.TryParse<AgentWorkSessionKind>(kind, ignoreCase: true, out var parsed)
                                 && parsed is AgentWorkSessionKind.General or AgentWorkSessionKind.Research)
            .WithMessage("Kind must be General or Research.");

        RuleFor(static request => request.AgentDefinitionId).NotEmpty().WithMessage("A work session needs an agent.");
    }
}

public sealed class UpdateWorkSessionRequestValidator : Validator<UpdateWorkSessionRequest>
{
    public UpdateWorkSessionRequestValidator()
    {
        RuleFor(static request => request.SessionId).NotEmpty();

        // Omitted means unchanged, so only a PRESENT value is bounded. Blank-but-present is a caller mistake, not a
        // request to clear a title the session cannot do without.
        // Chained `.When(...)`, never the block `When(pred, () => ...)` form: only the chained one sets the
        // per-component condition FastEndpoints' schema processor reads, and with the block form it saw an
        // unconditional NotEmpty and emitted this OPTIONAL member as required on the wire. The rules are unchanged —
        // a condition at the end of a chain covers every validator before it (ApplyConditionTo.AllValidators default).
        RuleFor(static request => request.Title)
            .NotEmpty()
            .WithMessage("A work session needs a title.")
            .MaximumLength(WorkSessionRequestLimits.MaxTitleLength)
            .WithMessage($"The title is longer than the {WorkSessionRequestLimits.MaxTitleLength}-character limit.")
            .When(static request => request.Title is not null);

        RuleFor(static request => request.Objective)
            .NotEmpty()
            .WithMessage("A work session needs an objective.")
            .MaximumLength(WorkSessionRequestLimits.MaxObjectiveLength)
            .WithMessage($"The objective is longer than the {WorkSessionRequestLimits.MaxObjectiveLength}-character limit.")
            .When(static request => request.Objective is not null);

        RuleFor(static request => request.AgentDefinitionId)
            .NotEmpty()
            .WithMessage("A work session needs an agent.")
            .When(static request => request.AgentDefinitionId is not null);
    }
}

public sealed class WorkSessionFeedRequestValidator : Validator<WorkSessionFeedRequest>
{
    public WorkSessionFeedRequestValidator() =>
        RuleFor(static request => request.SinceSeq).GreaterThanOrEqualTo(0).WithMessage("sinceSeq cannot be negative.");
}

public sealed class WorkSessionEventFeedRequestValidator : Validator<WorkSessionEventFeedRequest>
{
    public WorkSessionEventFeedRequestValidator()
    {
        RuleFor(static request => request.SinceSeq).GreaterThanOrEqualTo(0).WithMessage("sinceSeq cannot be negative.");
        RuleFor(static request => request.Limit)
            .InclusiveBetween(1, WorkSessionRequestLimits.MaxEventPageSize)
            .WithMessage($"limit must be between 1 and {WorkSessionRequestLimits.MaxEventPageSize}.");
    }
}

internal static class WorkSessionRequestLimits
{
    public const int MaxTitleLength = 200;
    public const int MaxObjectiveLength = 8000;
    public const int MaxEventPageSize = 500;
}
