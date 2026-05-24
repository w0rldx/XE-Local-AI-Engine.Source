namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

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
            .InclusiveBetween(1, 100)
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
