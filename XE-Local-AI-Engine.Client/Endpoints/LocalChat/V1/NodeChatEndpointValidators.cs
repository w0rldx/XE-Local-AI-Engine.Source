namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     Startup/options validator for create node chat conversation request settings.
/// </summary>
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

/// <summary>
///     Startup/options validator for list node chat conversations request settings.
/// </summary>
public sealed class ListNodeChatConversationsRequestValidator : Validator<ListNodeChatConversationsRequest>
{
    public ListNodeChatConversationsRequestValidator()
    {
        RuleFor(static request => request.Limit)
            .InclusiveBetween(1, 100)
            .When(static request => request.Limit.HasValue);
    }
}

/// <summary>
///     Startup/options validator for get node chat conversation request settings.
/// </summary>
public sealed class GetNodeChatConversationRequestValidator : Validator<GetNodeChatConversationRequest>
{
    public GetNodeChatConversationRequestValidator()
    {
        RuleFor(static request => request.ConversationId)
            .NotEmpty();
    }
}

/// <summary>
///     Startup/options validator for delete node chat conversation request settings.
/// </summary>
public sealed class DeleteNodeChatConversationRequestValidator : Validator<DeleteNodeChatConversationRequest>
{
    public DeleteNodeChatConversationRequestValidator()
    {
        RuleFor(static request => request.ConversationId)
            .NotEmpty();
    }
}

/// <summary>
///     Startup/options validator for set node chat selected path request settings.
/// </summary>
public sealed class SetNodeChatSelectedPathRequestValidator : Validator<SetNodeChatSelectedPathRequest>
{
    public SetNodeChatSelectedPathRequestValidator()
    {
        RuleFor(static request => request.ConversationId)
            .NotEmpty();
    }
}

/// <summary>
///     Startup/options validator for cancel node chat message request settings.
/// </summary>
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
