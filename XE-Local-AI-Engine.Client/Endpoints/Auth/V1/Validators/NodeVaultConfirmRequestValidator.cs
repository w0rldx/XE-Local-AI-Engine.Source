namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1.Validators;

using FastEndpoints;
using FluentValidation;

public sealed class NodeVaultConfirmRequestValidator : Validator<NodeVaultConfirmRequest>
{
    public NodeVaultConfirmRequestValidator()
    {
        RuleFor(static request => request.Password)
            .NotEmpty()
            .MaximumLength(256);
    }
}
