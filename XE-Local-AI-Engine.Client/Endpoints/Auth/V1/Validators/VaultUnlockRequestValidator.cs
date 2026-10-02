namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>Validates the locked pre-host's <c>auth/vault/unlock</c> body.</summary>
public sealed class VaultUnlockRequestValidator : Validator<VaultUnlockRequest>
{
    public VaultUnlockRequestValidator()
    {
        RuleFor(static request => request.Password)
            .NotEmpty()
            .MaximumLength(256);
    }
}
