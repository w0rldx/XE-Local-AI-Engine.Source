namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class SetNodeChatConversationPlainRequestValidator : Validator<SetNodeChatConversationPlainRequest>
{
    public SetNodeChatConversationPlainRequestValidator()
    {
        RuleFor(static request => request.ConversationId)
            .NotEmpty();

        RuleFor(static request => request.PlainSystemPrompt)
            .MaximumLength(NodeChatSetConversationPlainRequest.MaxPlainSystemPromptLength);
    }
}
