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
                  .Must(static status => DevWorkflowTokenRules.IsNamed<DevWorkflowWorkItemStatus>(status))
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

public sealed class CreateDevWorkflowRuleSetRequestValidator : Validator<CreateDevWorkflowRuleSetRequest>
{
    public CreateDevWorkflowRuleSetRequestValidator()
    {
        RuleFor(static request => request.Name)
            .NotEmpty()
            .WithMessage("A rule set needs a name.")
            .MaximumLength(DevWorkflowRequestLimits.MaxRuleSetNameLength)
            .WithMessage($"The name is longer than the {DevWorkflowRequestLimits.MaxRuleSetNameLength}-character limit.");

        RuleFor(static request => request.Description)
            .MaximumLength(DevWorkflowRequestLimits.MaxRuleSetDescriptionLength)
            .WithMessage($"The description is longer than the {DevWorkflowRequestLimits.MaxRuleSetDescriptionLength}-character limit.");

        RuleFor(static request => request.Body)
            .NotEmpty()
            .WithMessage("A rule set needs a body — the markdown that gets injected is the whole of it.")
            .MaximumLength(DevWorkflowRequestLimits.MaxRuleSetBodyLength)
            .WithMessage($"The body is longer than the {DevWorkflowRequestLimits.MaxRuleSetBodyLength}-character limit.");

        RuleFor(static request => request.Scope).Must(HasOnlyKnownNodeTypes).WithMessage(UnknownNodeTypeMessage);
    }

    /// <summary>
    ///     The node-type axis is a CLOSED token set. A token nothing parses could only ever match nothing, silently —
    ///     the same trap that got <c>languages</c> and <c>taskTypes</c> dropped — so it is refused at the door.
    /// </summary>
    /// <summary>
    ///     Matched against the NAMES, not through <c>Enum.TryParse</c>: that also accepts the underlying numbers, so
    ///     "3" and "-1" would be stored verbatim and then never match anything at resolution time — precisely the
    ///     silent-no-op trap this check exists to close.
    /// </summary>
    internal static bool HasOnlyKnownNodeTypes(DevWorkflowRuleScope? scope) =>
        scope?.NodeTypes is not { } nodeTypes || nodeTypes.All(DevWorkflowTokenRules.IsNamed<DevWorkflowNodeType>);

    internal static string UnknownNodeTypeMessage { get; } = $"scope.nodeTypes must contain only {string.Join(", ", Enum.GetNames<DevWorkflowNodeType>())}.";
}

public sealed class UpdateDevWorkflowRuleSetRequestValidator : Validator<UpdateDevWorkflowRuleSetRequest>
{
    public UpdateDevWorkflowRuleSetRequestValidator()
    {
        RuleFor(static request => request.RuleSetId).NotEmpty();

        // The version the edit was made against. Without it a PUT is a last-writer-wins overwrite of whatever landed
        // in between, which is the one thing optimistic concurrency exists to refuse.
        RuleFor(static request => request.Version).GreaterThan(0).WithMessage("A rule set update must carry the version it was edited from.");

        RuleFor(static request => request.Name)
            .NotEmpty()
            .WithMessage("A rule set needs a name.")
            .MaximumLength(DevWorkflowRequestLimits.MaxRuleSetNameLength)
            .WithMessage($"The name is longer than the {DevWorkflowRequestLimits.MaxRuleSetNameLength}-character limit.");

        RuleFor(static request => request.Description)
            .MaximumLength(DevWorkflowRequestLimits.MaxRuleSetDescriptionLength)
            .WithMessage($"The description is longer than the {DevWorkflowRequestLimits.MaxRuleSetDescriptionLength}-character limit.");

        RuleFor(static request => request.Body)
            .NotEmpty()
            .WithMessage("A rule set needs a body — the markdown that gets injected is the whole of it.")
            .MaximumLength(DevWorkflowRequestLimits.MaxRuleSetBodyLength)
            .WithMessage($"The body is longer than the {DevWorkflowRequestLimits.MaxRuleSetBodyLength}-character limit.");

        RuleFor(static request => request.Scope)
            .Must(CreateDevWorkflowRuleSetRequestValidator.HasOnlyKnownNodeTypes)
            .WithMessage(CreateDevWorkflowRuleSetRequestValidator.UnknownNodeTypeMessage);
    }
}

public sealed class ListDevWorkflowRunsRequestValidator : Validator<ListDevWorkflowRunsRequest>
{
    public ListDevWorkflowRunsRequestValidator()
    {
        When(static request => request.Status is not null,
            () => RuleFor(static request => request.Status)
                  .Must(static status => DevWorkflowTokenRules.IsNamed<DevWorkflowRunStatus>(status))
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
            .Must(static decision => DevWorkflowTokenRules.IsNamed<DevWorkflowDecisionKind>(decision))
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

/// <summary>
///     The one spelling of "is this a member of that enum". Matched against the NAMES, never through
///     <c>Enum.TryParse</c>: that also accepts the underlying numbers, so <c>"3"</c> and <c>"-1"</c> would pass the
///     door and then be read by the endpoint behind it as a member nobody named — a filter that matches nothing, or,
///     on the decision axis, an intervention the operator never asked for.
/// </summary>
internal static class DevWorkflowTokenRules
{
    public static bool IsNamed<TEnum>(string? raw)
        where TEnum : struct, Enum =>
        raw is not null && Enum.GetNames<TEnum>().Contains(raw, StringComparer.OrdinalIgnoreCase);
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

    public const int MaxRuleSetNameLength = 255;

    public const int MaxRuleSetDescriptionLength = 1024;

    /// <summary>
    ///     A rule set body, bounded by what an objective can actually carry. Deliberately well under
    ///     <c>DevWorkflowAgentExecutor.MaxObjectiveCharacters</c> (7000), because a body accepted here that the
    ///     objective then has to cut is policy the operator believed was in force and the agent never fully read. A
    ///     document longer than this wants splitting into scoped rule sets, which is the whole point of the scope.
    /// </summary>
    public const int MaxRuleSetBodyLength = 4096;

    public const int MaxRunPageSize = 200;
    public const int MaxEventPageSize = 500;
}
