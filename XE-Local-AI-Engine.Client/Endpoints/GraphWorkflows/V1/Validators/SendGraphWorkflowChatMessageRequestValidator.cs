namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     Shape validation only. The size caps, the definition's kind, the conversation's kind and whether attachments are
///     accepted are the chat service's answers, because they are options and state rather than request shape.
/// </summary>
public sealed class SendGraphWorkflowChatMessageRequestValidator : Validator<SendGraphWorkflowChatMessageRequest>
{
    /// <summary>More chips than any composer draws. The run input budget bounds the references far below the body cap anyway.</summary>
    private const int MaxAttachments = 32;

    public SendGraphWorkflowChatMessageRequestValidator()
    {
        RuleFor(static request => request.ConversationId).NotEmpty();
        RuleFor(static request => request.DefinitionId).NotEmpty().WithMessage("A workflow chat message names the Chat workflow it runs.");

        // The empty Guid is not an idempotency key: every caller that forgot to mint one would send the same one.
        RuleFor(static request => request.RequestId).NotEmpty().WithMessage("A workflow chat message needs a caller-minted request id.");
        RuleFor(static request => request.Content).NotEmpty().WithMessage("A workflow chat message needs content.");
        RuleFor(static request => request.AttachmentFileIds)
            .Must(static ids => ids is null || ids.Count <= MaxAttachments)
            .WithMessage($"A workflow chat message carries at most {MaxAttachments} attachments.");
    }
}
