namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Validators;

using FastEndpoints;
using FluentValidation;

public sealed class CreateNodeChatConversationRequestValidator : Validator<CreateNodeChatConversationRequest>
{
    public CreateNodeChatConversationRequestValidator()
    {
        RuleFor(static request => request.Title)
            .MaximumLength(200);

        RuleFor(static request => request.UserId)
            .MaximumLength(128);
    }
}

public sealed class ListNodeChatConversationsRequestValidator : Validator<ListNodeChatConversationsRequest>
{
    public ListNodeChatConversationsRequestValidator()
    {
        RuleFor(static request => request.Limit)
            .InclusiveBetween(from: 1, to: 100)
            .When(static request => request.Limit.HasValue);
    }
}

public sealed class GetNodeChatConversationRequestValidator : Validator<GetNodeChatConversationRequest>
{
    public GetNodeChatConversationRequestValidator()
    {
        RuleFor(static request => request.ConversationId)
            .NotEmpty();
    }
}

public sealed class DeleteNodeChatConversationRequestValidator : Validator<DeleteNodeChatConversationRequest>
{
    public DeleteNodeChatConversationRequestValidator()
    {
        RuleFor(static request => request.ConversationId)
            .NotEmpty();
    }
}

public sealed class SetNodeChatSelectedPathRequestValidator : Validator<SetNodeChatSelectedPathRequest>
{
    public SetNodeChatSelectedPathRequestValidator()
    {
        RuleFor(static request => request.ConversationId)
            .NotEmpty();
    }
}

public sealed class CancelNodeChatMessageRequestValidator : Validator<CancelNodeChatMessageRequest>
{
    public CancelNodeChatMessageRequestValidator()
    {
        RuleFor(static request => request.ConversationId)
            .NotEmpty();
        RuleFor(static request => request.MessageId)
            .NotEmpty();
        RuleFor(static request => request.RequestId)
            .NotEmpty();
    }
}

public sealed class ResolveToolApprovalRequestValidator : Validator<ResolveToolApprovalRequest>
{
    public ResolveToolApprovalRequestValidator()
    {
        RuleFor(static request => request.RequestId)
            .NotEmpty();
    }
}

public sealed class ResolveUserQuestionRequestValidator : Validator<ResolveUserQuestionRequest>
{
    public ResolveUserQuestionRequestValidator()
    {
        RuleFor(static request => request.RequestId)
            .NotEmpty();

        RuleFor(static request => request.Answers)
            .NotEmpty();

        RuleForEach(static request => request.Answers)
            .ChildRules(static answer =>
            {
                answer.RuleFor(static a => a.Question)
                      .NotEmpty();

                // An answer must actually answer: either at least one selected option or free text from the "Other"
                // row. Neither would park the turn on an empty result the model cannot branch on, so reject it here
                // rather than feeding a content-free answer into the run.
                answer.RuleFor(static a => a)
                      .Must(static a => a.Selected?.Any(static selected => !string.IsNullOrWhiteSpace(selected)) == true
                                        || !string.IsNullOrWhiteSpace(a.Other))
                      .WithMessage("An answer must carry at least one selected option or free text.");
            });
    }
}
