namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Validators;

using FastEndpoints;
using FluentValidation;

public sealed class ListGraphWorkflowConversationRunsRequestValidator : Validator<ListGraphWorkflowConversationRunsRequest>
{
    public ListGraphWorkflowConversationRunsRequestValidator()
    {
        RuleFor(static request => request.ConversationId).NotEmpty();
        RuleFor(static request => request.Limit)
            .InclusiveBetween(1, GraphWorkflowRequestLimits.MaxRunPageSize)
            .WithMessage($"A run page holds between 1 and {GraphWorkflowRequestLimits.MaxRunPageSize} runs.");
    }
}
