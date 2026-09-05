namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     Shape validation only: lengths, required members and non-empty ids. Whether a graph is ROUTABLE — its kinds, its
///     join policies, its condition operators, its reachability — is the runtime's parser's answer, not a second
///     opinion here. It is the same parser run start uses, and a copy of its rules in a validator would be a copy that
///     drifts; worse, a token refused here would answer with one unkeyed sentence where the parser answers with every
///     failure keyed to the node or edge that carries it, which is what the editor draws on.
/// </summary>
public sealed class CreateGraphWorkflowDefinitionRequestValidator : Validator<CreateGraphWorkflowDefinitionRequest>
{
    public CreateGraphWorkflowDefinitionRequestValidator()
    {
        RuleFor(static request => request.Name)
            .NotEmpty()
            .WithMessage("A graph workflow definition needs a name.")
            .MaximumLength(GraphWorkflowRequestLimits.MaxNameLength)
            .WithMessage($"The name is longer than the {GraphWorkflowRequestLimits.MaxNameLength}-character limit.");

        RuleFor(static request => request.Description)
            .MaximumLength(GraphWorkflowRequestLimits.MaxDescriptionLength)
            .WithMessage($"The description is longer than the {GraphWorkflowRequestLimits.MaxDescriptionLength}-character limit.");

        RuleFor(static request => request.Graph).NotNull().WithMessage("A graph workflow definition needs a graph.");
    }
}

public sealed class UpdateGraphWorkflowDefinitionRequestValidator : Validator<UpdateGraphWorkflowDefinitionRequest>
{
    public UpdateGraphWorkflowDefinitionRequestValidator()
    {
        RuleFor(static request => request.DefinitionId).NotEmpty();

        // The version the edit was made against. Without it a PUT is a last-writer-wins overwrite of whatever landed
        // in between, which is the one thing optimistic concurrency exists to refuse.
        RuleFor(static request => request.Version).GreaterThan(0).WithMessage("A definition update must carry the version it was edited from.");

        // Omitted means unchanged, so only a PRESENT value is bounded. Blank-but-present is a caller mistake, not a
        // request to clear a name the definition cannot do without.
        // Chained `.When(...)`, never the block `When(pred, () => ...)` form: only the chained one sets the
        // per-component condition FastEndpoints' schema processor reads, and with the block form it saw an
        // unconditional NotEmpty and emitted this OPTIONAL member as required on the wire. The rules are unchanged —
        // a condition at the end of a chain covers every validator before it (ApplyConditionTo.AllValidators default).
        RuleFor(static request => request.Name)
            .NotEmpty()
            .WithMessage("A graph workflow definition needs a name.")
            .MaximumLength(GraphWorkflowRequestLimits.MaxNameLength)
            .WithMessage($"The name is longer than the {GraphWorkflowRequestLimits.MaxNameLength}-character limit.")
            .When(static request => request.Name is not null);

        RuleFor(static request => request.Description)
            .MaximumLength(GraphWorkflowRequestLimits.MaxDescriptionLength)
            .WithMessage($"The description is longer than the {GraphWorkflowRequestLimits.MaxDescriptionLength}-character limit.");
    }
}

public sealed class ValidateGraphWorkflowDefinitionRequestValidator : Validator<ValidateGraphWorkflowDefinitionRequest>
{
    public ValidateGraphWorkflowDefinitionRequestValidator() =>
        RuleFor(static request => request.Graph).NotNull().WithMessage("A graph workflow definition needs a graph.");
}

internal static class GraphWorkflowRequestLimits
{
    public const int MaxNameLength = 200;

    public const int MaxDescriptionLength = 1024;
}
