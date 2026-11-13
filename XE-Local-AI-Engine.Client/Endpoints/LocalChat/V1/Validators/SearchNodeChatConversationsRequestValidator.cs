namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Validators;

using FastEndpoints;
using FluentValidation;

public sealed class SearchNodeChatConversationsRequestValidator : Validator<SearchNodeChatConversationsRequest>
{
    public SearchNodeChatConversationsRequestValidator()
    {
        RuleFor(static request => request.Query)
            .Must(static query => !string.IsNullOrWhiteSpace(query))
            .WithMessage("A search needs at least one non-blank term.")
            .MaximumLength(200);

        RuleFor(static request => request.Limit)
            .InclusiveBetween(from: 1, to: 100)
            .When(static request => request.Limit.HasValue);
    }
}
