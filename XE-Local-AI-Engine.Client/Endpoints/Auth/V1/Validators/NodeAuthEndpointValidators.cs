namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1.Validators;

using FastEndpoints;
using FluentValidation;

public sealed class NodeSetupRequestValidator : Validator<NodeSetupRequest>
{
    public NodeSetupRequestValidator()
    {
        RuleFor(static request => request.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(256);

        RuleFor(static request => request.Password)
            .NotEmpty()
            .MinimumLength(12)
            .MaximumLength(256);
    }
}

public sealed class NodeLoginRequestValidator : Validator<NodeLoginRequest>
{
    public NodeLoginRequestValidator()
    {
        RuleFor(static request => request.Email)
            .EmailAddress()
            .MaximumLength(256)
            .When(static request => !string.IsNullOrWhiteSpace(request.Email));

        RuleFor(static request => request.Password)
            .NotEmpty()
            .MaximumLength(256);
    }
}

public sealed class NodeChangePasswordRequestValidator : Validator<NodeChangePasswordRequest>
{
    public NodeChangePasswordRequestValidator()
    {
        RuleFor(static request => request.CurrentPassword)
            .NotEmpty()
            .MaximumLength(256);

        RuleFor(static request => request.NewPassword)
            .NotEmpty()
            .MinimumLength(12)
            .MaximumLength(256);
    }
}
