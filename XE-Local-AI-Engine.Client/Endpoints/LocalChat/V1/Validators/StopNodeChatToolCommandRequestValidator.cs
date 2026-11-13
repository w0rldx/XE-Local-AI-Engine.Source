namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Validators;

using FastEndpoints;
using FluentValidation;

public sealed class StopNodeChatToolCommandRequestValidator : Validator<StopNodeChatToolCommandRequest>
{
    public StopNodeChatToolCommandRequestValidator()
    {
        RuleFor(static request => request.MessageId)
            .NotEmpty();
        RuleFor(static request => request.ToolCallId)
            .NotEmpty()
            .MaximumLength(256);
    }
}
