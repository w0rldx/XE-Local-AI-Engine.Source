namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Validators;

using FastEndpoints;
using FluentValidation;

public sealed class AnswerNowNodeChatMessageRequestValidator : Validator<AnswerNowNodeChatMessageRequest>
{
    public AnswerNowNodeChatMessageRequestValidator()
    {
        RuleFor(static request => request.MessageId)
            .NotEmpty();
    }
}
