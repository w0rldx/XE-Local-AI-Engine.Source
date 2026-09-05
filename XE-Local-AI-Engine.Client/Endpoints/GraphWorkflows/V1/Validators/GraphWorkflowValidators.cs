namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Persistence.Entities;

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

/// <summary>
///     Shape validation only, as above. Whether the definition is startable — its version, its node count, its input
///     size — is the run service's answer, because those are the runtime's options and its pinned graph.
/// </summary>
public sealed class StartGraphWorkflowRunRequestValidator : Validator<StartGraphWorkflowRunRequest>
{
    public StartGraphWorkflowRunRequestValidator()
    {
        RuleFor(static request => request.DefinitionId).NotEmpty();

        // The empty Guid is not an idempotency key: every caller that forgot to mint one would send the same one, and
        // the second start would answer with the first caller's run.
        RuleFor(static request => request.RequestId).NotEmpty().WithMessage("A graph workflow run needs a caller-minted request id.");

        // Omitted skips the check; present, it is a real version, and version 0 never existed.
        RuleFor(static request => request.DefinitionVersion)
            .GreaterThan(0)
            .WithMessage("A definition version to start against must be positive.")
            .When(static request => request.DefinitionVersion is not null);
    }
}

public sealed class ListGraphWorkflowRunsRequestValidator : Validator<ListGraphWorkflowRunsRequest>
{
    public ListGraphWorkflowRunsRequestValidator()
    {
        RuleFor(static request => request.Limit)
            .InclusiveBetween(1, GraphWorkflowRequestLimits.MaxRunPageSize)
            .WithMessage($"A run page holds between 1 and {GraphWorkflowRequestLimits.MaxRunPageSize} runs.");

        // Refused here rather than in the endpoint so an unknown token answers 400 with the name it did not recognise,
        // instead of the endpoint's Enum.Parse throwing its way to a 500.
        RuleFor(static request => request.Status)
            .Must(static status => status is not null && Enum.GetNames<GraphWorkflowRunStatus>().Contains(status, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"status must be one of {string.Join(", ", Enum.GetNames<GraphWorkflowRunStatus>())}.")
            .When(static request => request.Status is not null);
    }
}

public sealed class GraphWorkflowNodeRunRequestValidator : Validator<GraphWorkflowNodeRunRequest>
{
    public GraphWorkflowNodeRunRequestValidator()
    {
        RuleFor(static request => request.RunId).NotEmpty();
        RuleFor(static request => request.NodeKey).NotEmpty().WithMessage("A node run is read by its node key.");
    }
}

public sealed class GraphWorkflowRunEventFeedRequestValidator : Validator<GraphWorkflowRunEventFeedRequest>
{
    public GraphWorkflowRunEventFeedRequestValidator()
    {
        RuleFor(static request => request.RunId).NotEmpty();

        // 0 asks for everything; a negative watermark is not a smaller ask, it is a caller bug.
        RuleFor(static request => request.AfterSeq).GreaterThanOrEqualTo(0).WithMessage("An event watermark cannot be negative.");
    }
}

internal static class GraphWorkflowRequestLimits
{
    public const int MaxNameLength = 200;

    public const int MaxDescriptionLength = 1024;

    /// <summary>
    ///     The largest run page. Deliberately not <c>EventReplayLimit</c> or any other option: this bounds a LIST of
    ///     runs, which no option describes, and a page is a client concern rather than a runtime budget.
    /// </summary>
    public const int MaxRunPageSize = 200;
}
