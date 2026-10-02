namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>Validates the locked pre-host's <c>auth/vault/unlock-recovery</c> body; the new password follows setup's policy.</summary>
public sealed class VaultRecoveryUnlockRequestValidator : Validator<VaultRecoveryUnlockRequest>
{
    public VaultRecoveryUnlockRequestValidator()
    {
        // 8 groups of 5 plus separators is 47 characters; the bound only rejects junk, the parser decides validity.
        RuleFor(static request => request.RecoveryCode)
            .NotEmpty()
            .MaximumLength(128);

        RuleFor(static request => request.NewPassword)
            .NotEmpty()
            .MinimumLength(12)
            .MaximumLength(256);
    }
}
