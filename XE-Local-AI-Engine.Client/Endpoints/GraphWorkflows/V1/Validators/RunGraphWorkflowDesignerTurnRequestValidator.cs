namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Designer;

/// <summary>Caps every turn member before the designer takes the node's single draft slot.</summary>
public sealed class RunGraphWorkflowDesignerTurnRequestValidator : Validator<RunGraphWorkflowDesignerTurnRequest>
{
    public const int MaxMessages = 50;

    private const int MaxNodeKeyLength = 64;

    internal static readonly string[] Intents = ["auto", "ask", "change"];

    internal static readonly string[] Roles = ["user", "assistant"];

    public RunGraphWorkflowDesignerTurnRequestValidator()
    {
        RuleFor(static request => request.ModelName)
            .Must(static model => !string.IsNullOrWhiteSpace(model)).WithMessage("A model is required.")
            .MaximumLength(DraftEndpointSupport.MaxModelNameLength);

        RuleFor(static request => request.Messages)
            .Must(static messages => messages is { Count: > 0 and <= MaxMessages })
            .WithMessage($"A turn carries between 1 and {MaxMessages} messages; start a new chat to go on.")
            .Must(static messages => messages is [.., { } last] && string.Equals(last.Role, "user", StringComparison.OrdinalIgnoreCase))
            .WithMessage("The last message must be yours.");

        RuleForEach(static request => request.Messages).NotNull().WithMessage("A message cannot be null.");

        RuleForEach(static request => request.Messages).ChildRules(static message =>
        {
            message.RuleFor(static entry => entry.Role)
                   .Must(static role => Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
                   .WithMessage("A message role is 'user' or 'assistant'.");
            message.RuleFor(static entry => entry.Text)
                   .Must(static text => !string.IsNullOrWhiteSpace(text)).WithMessage("A message needs text.")
                   .MaximumLength(GraphWorkflowDesignerTurnRequest.MaxMessageLength);
        });

        RuleFor(static request => request.SelectedNodeKey).MaximumLength(MaxNodeKeyLength);

        RuleFor(static request => request.Intent)
            .Must(static intent => intent is null || Intents.Contains(intent, StringComparer.OrdinalIgnoreCase))
            .WithMessage("The intent is 'auto', 'ask' or 'change'.");
    }
}
