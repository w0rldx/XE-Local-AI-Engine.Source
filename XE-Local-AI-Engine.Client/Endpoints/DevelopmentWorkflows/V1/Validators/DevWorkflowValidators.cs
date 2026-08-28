namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Shape validation only: lengths, enum parsing, non-empty ids and query ranges. Whether a graph is ROUTABLE is
///     the runtime's parser's answer, not a second opinion here — it is the same parser run start uses, and a copy of
///     its rules in a validator would be a copy that drifts.
/// </summary>
public sealed class CreateDevWorkflowWorkItemRequestValidator : Validator<CreateDevWorkflowWorkItemRequest>
{
    public CreateDevWorkflowWorkItemRequestValidator()
    {
        RuleFor(static request => request.Title)
            .NotEmpty()
            .WithMessage("A work item needs a title.")
            .MaximumLength(DevWorkflowRequestLimits.MaxTitleLength)
            .WithMessage($"The title is longer than the {DevWorkflowRequestLimits.MaxTitleLength}-character limit.");

        RuleFor(static request => request.Request)
            .NotEmpty()
            .WithMessage("A work item needs a request — what is being asked for.")
            .MaximumLength(DevWorkflowRequestLimits.MaxRequestLength)
            .WithMessage($"The request is longer than the {DevWorkflowRequestLimits.MaxRequestLength}-character limit.");
    }
}

public sealed class UpdateDevWorkflowWorkItemRequestValidator : Validator<UpdateDevWorkflowWorkItemRequest>
{
    public UpdateDevWorkflowWorkItemRequestValidator()
    {
        RuleFor(static request => request.WorkItemId).NotEmpty();

        // Omitted means unchanged, so only a PRESENT value is bounded. Blank-but-present is a caller mistake, not a
        // request to clear a title the work item cannot do without.
        When(static request => request.Title is not null,
            () => RuleFor(static request => request.Title)
                  .NotEmpty()
                  .WithMessage("A work item needs a title.")
                  .MaximumLength(DevWorkflowRequestLimits.MaxTitleLength)
                  .WithMessage($"The title is longer than the {DevWorkflowRequestLimits.MaxTitleLength}-character limit."));

        When(static request => request.Request is not null,
            () => RuleFor(static request => request.Request)
                  .NotEmpty()
                  .WithMessage("A work item needs a request — what is being asked for.")
                  .MaximumLength(DevWorkflowRequestLimits.MaxRequestLength)
                  .WithMessage($"The request is longer than the {DevWorkflowRequestLimits.MaxRequestLength}-character limit."));
    }
}

public sealed class ListDevWorkflowWorkItemsRequestValidator : Validator<ListDevWorkflowWorkItemsRequest>
{
    public ListDevWorkflowWorkItemsRequestValidator() =>
        When(static request => request.Status is not null,
            () => RuleFor(static request => request.Status)
                  .Must(static status => Enum.TryParse<DevWorkflowWorkItemStatus>(status, ignoreCase: true, out _))
                  .WithMessage($"status must be one of {string.Join(", ", Enum.GetNames<DevWorkflowWorkItemStatus>())}."));
}

public sealed class CreateDevWorkflowDefinitionRequestValidator : Validator<CreateDevWorkflowDefinitionRequest>
{
    public CreateDevWorkflowDefinitionRequestValidator()
    {
        RuleFor(static request => request.Name)
            .NotEmpty()
            .WithMessage("A workflow definition needs a name.")
            .MaximumLength(DevWorkflowRequestLimits.MaxTitleLength)
            .WithMessage($"The name is longer than the {DevWorkflowRequestLimits.MaxTitleLength}-character limit.");

        RuleFor(static request => request.Graph).NotNull().WithMessage("A workflow definition needs a graph.");
    }
}

public sealed class UpdateDevWorkflowDefinitionRequestValidator : Validator<UpdateDevWorkflowDefinitionRequest>
{
    public UpdateDevWorkflowDefinitionRequestValidator()
    {
        RuleFor(static request => request.DefinitionId).NotEmpty();

        // The version the edit was made against. Without it a PUT is a last-writer-wins overwrite of whatever landed
        // in between, which is the one thing optimistic concurrency exists to refuse.
        RuleFor(static request => request.Version).GreaterThan(0).WithMessage("A definition update must carry the version it was edited from.");

        When(static request => request.Name is not null,
            () => RuleFor(static request => request.Name)
                  .NotEmpty()
                  .WithMessage("A workflow definition needs a name.")
                  .MaximumLength(DevWorkflowRequestLimits.MaxTitleLength)
                  .WithMessage($"The name is longer than the {DevWorkflowRequestLimits.MaxTitleLength}-character limit."));
    }
}

public sealed class ListDevWorkflowRunsRequestValidator : Validator<ListDevWorkflowRunsRequest>
{
    public ListDevWorkflowRunsRequestValidator()
    {
        When(static request => request.Status is not null,
            () => RuleFor(static request => request.Status)
                  .Must(static status => Enum.TryParse<DevWorkflowRunStatus>(status, ignoreCase: true, out _))
                  .WithMessage($"status must be one of {string.Join(", ", Enum.GetNames<DevWorkflowRunStatus>())}."));

        RuleFor(static request => request.Limit)
            .InclusiveBetween(1, DevWorkflowRequestLimits.MaxRunPageSize)
            .WithMessage($"limit must be between 1 and {DevWorkflowRequestLimits.MaxRunPageSize}.");
    }
}

public sealed class StartDevWorkflowRunRequestValidator : Validator<StartDevWorkflowRunRequest>
{
    public StartDevWorkflowRunRequestValidator()
    {
        RuleFor(static request => request.WorkItemId).NotEmpty();
        RuleFor(static request => request.DefinitionId).NotEmpty().WithMessage("A run needs the definition to run.");
        RuleFor(static request => request.OperationId).NotEmpty().WithMessage("A run start needs an operation id, so a replayed request cannot start a second run.");

        When(static request => request.InputsJson is not null,
            () => RuleFor(static request => request.InputsJson)
                  .MaximumLength(DevWorkflowRequestLimits.MaxInputsLength)
                  .WithMessage($"inputsJson is longer than the {DevWorkflowRequestLimits.MaxInputsLength}-character limit."));
    }
}

public sealed class DevWorkflowRunActionRequestValidator : Validator<DevWorkflowRunActionRequest>
{
    public DevWorkflowRunActionRequestValidator()
    {
        RuleFor(static request => request.RunId).NotEmpty();
        RuleFor(static request => request.OperationId).NotEmpty().WithMessage("A run command needs an operation id, so a replayed request cannot act twice.");
    }
}

public sealed class DevWorkflowRunEventFeedRequestValidator : Validator<DevWorkflowRunEventFeedRequest>
{
    public DevWorkflowRunEventFeedRequestValidator()
    {
        RuleFor(static request => request.SinceSeq).GreaterThanOrEqualTo(0).WithMessage("sinceSeq cannot be negative.");
        RuleFor(static request => request.Limit)
            .InclusiveBetween(1, DevWorkflowRequestLimits.MaxEventPageSize)
            .WithMessage($"limit must be between 1 and {DevWorkflowRequestLimits.MaxEventPageSize}.");
    }
}

public sealed class DevWorkflowArtifactFeedRequestValidator : Validator<DevWorkflowArtifactFeedRequest>
{
    public DevWorkflowArtifactFeedRequestValidator() =>
        RuleFor(static request => request.SinceSeq).GreaterThanOrEqualTo(0).WithMessage("sinceSeq cannot be negative.");
}

public sealed class DevWorkflowDecisionRequestValidator : Validator<DevWorkflowDecisionRequest>
{
    public DevWorkflowDecisionRequestValidator()
    {
        RuleFor(static request => request.RunId).NotEmpty();
        RuleFor(static request => request.NodeRunId).NotEmpty();
        RuleFor(static request => request.OperationId)
            .NotEmpty()
            .WithMessage("A decision needs an operation id: it is what makes a double-submitted answer one decision rather than two.");

        // Parsed, not merely non-empty: the six kinds are one enum, and whether THIS node run can take the one named
        // is the runtime's answer, given later as a conflict.
        RuleFor(static request => request.Decision)
            .Must(static decision => Enum.TryParse<DevWorkflowDecisionKind>(decision, ignoreCase: true, out _))
            .WithMessage($"decision must be one of {string.Join(", ", Enum.GetNames<DevWorkflowDecisionKind>())}.");

        When(static request => request.Comment is not null,
            () => RuleFor(static request => request.Comment)
                  .MaximumLength(DevWorkflowRequestLimits.MaxCommentLength)
                  .WithMessage($"The comment is longer than the {DevWorkflowRequestLimits.MaxCommentLength}-character limit."));

        When(static request => request.PayloadJson is not null,
            () => RuleFor(static request => request.PayloadJson)
                  .MaximumLength(DevWorkflowRequestLimits.MaxPayloadLength)
                  .WithMessage($"payloadJson is longer than the {DevWorkflowRequestLimits.MaxPayloadLength}-character limit."));
    }
}

internal static class DevWorkflowRequestLimits
{
    public const int MaxTitleLength = 200;

    /// <summary>The request becomes the first agent's objective, so it is bounded like one.</summary>
    public const int MaxRequestLength = 8000;

    /// <summary>A structured seed document, not prose — larger than an objective, far below a payload.</summary>
    public const int MaxInputsLength = 32_768;

    public const int MaxCommentLength = 8000;

    /// <summary>A gate payload can be a whole edited plan, so it gets the node's follow-up message ceiling.</summary>
    public const int MaxPayloadLength = 262_144;

    public const int MaxRunPageSize = 200;
    public const int MaxEventPageSize = 500;
}
