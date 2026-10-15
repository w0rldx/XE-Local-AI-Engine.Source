namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Validators;

using FastEndpoints;
using FluentValidation;

public sealed class InvokeGraphWorkflowIntentRequestValidator : Validator<InvokeGraphWorkflowIntentRequest>
{
    /// <summary>The longest utterance the endpoint reads; a spoken command is a sentence, not a document.</summary>
    public const int MaxUtteranceLength = 2000;

    public InvokeGraphWorkflowIntentRequestValidator()
    {
        RuleFor(static request => request.Utterance)
            .Must(static utterance => !string.IsNullOrWhiteSpace(utterance))
            .WithMessage("An intent needs an utterance.")
            .MaximumLength(MaxUtteranceLength);

        RuleFor(static request => request.RequestId).NotEmpty().WithMessage("An intent needs a caller-minted request id.");

        RuleFor(static request => request.UtteredAt).NotEqual(default(DateTimeOffset)).WithMessage("An intent needs the time it was uttered, as an ISO 8601 timestamp.");
    }
}
