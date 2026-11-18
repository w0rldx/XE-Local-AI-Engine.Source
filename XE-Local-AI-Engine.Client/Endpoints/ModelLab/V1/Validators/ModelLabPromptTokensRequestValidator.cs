namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Validators;

using System.Text;
using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Validators;

/// <summary>The model-name grammar, and the same per-message size cap the Next token run itself passed in <c>LocalChatHub.StreamDirectInference</c>.</summary>
public sealed class ModelLabPromptTokensRequestValidator : Validator<ModelLabPromptTokensRequest>
{
    public ModelLabPromptTokensRequestValidator()
    {
        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .WithMessage("Choose the model that ran the prompt.");

        this.AddBodyBoundModelNameRule(static request => request.ModelName);

        RuleFor(static request => request.Prompt)
            .Must(static prompt => !string.IsNullOrWhiteSpace(prompt))
            .WithMessage("Enter the prompt that ran.");

        RuleFor(static request => request.Prompt).Custom(WithinMessageSizeCap);
        RuleFor(static request => request.SystemPrompt).Custom(WithinMessageSizeCap);
    }

    private void WithinMessageSizeCap(string? content, FluentValidation.ValidationContext<ModelLabPromptTokensRequest> context)
    {
        var maxSizeKb = Resolve<IOptions<Configuration.SecurityOptions>>().Value.MaxMessageSizeKb;
        if (content is not null && Encoding.UTF8.GetByteCount(content) > maxSizeKb * 1024)
        {
            context.AddFailure($"A prompt can be at most {maxSizeKb} KiB.");
        }
    }
}
