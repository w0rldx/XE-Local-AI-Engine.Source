namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>The share file's format and version, then the same shape rules create applies.</summary>
public sealed class ImportGraphWorkflowDefinitionRequestValidator : Validator<ImportGraphWorkflowDefinitionRequest>
{
    public ImportGraphWorkflowDefinitionRequestValidator()
    {
        RuleFor(static request => request.Format)
            .Equal(GraphWorkflowShareFile.Format, StringComparer.Ordinal)
            .WithMessage($"This is not a graph workflow file: its format must be '{GraphWorkflowShareFile.Format}'.");

        RuleFor(static request => request.FormatVersion)
            .Equal(GraphWorkflowShareFile.FormatVersion)
            .WithMessage($"This node reads graph workflow files of format version {GraphWorkflowShareFile.FormatVersion} only.");

        RuleFor(static request => request.Name)
            .NotEmpty()
            .WithMessage("A graph workflow definition needs a name.")
            .MaximumLength(GraphWorkflowRequestLimits.MaxNameLength)
            .WithMessage($"The name is longer than the {GraphWorkflowRequestLimits.MaxNameLength}-character limit.");

        RuleFor(static request => request.Description)
            .MaximumLength(GraphWorkflowRequestLimits.MaxDescriptionLength)
            .WithMessage($"The description is longer than the {GraphWorkflowRequestLimits.MaxDescriptionLength}-character limit.");

        RuleFor(static request => request.Graph).NotNull().WithMessage("A graph workflow definition needs a graph.");
    }
}
