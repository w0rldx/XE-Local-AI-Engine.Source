namespace XE_Local_AI_Engine.Client.Hosting.Vault;

/// <summary>The admin password reset a recovery-code unlock hands to the real host; all three parts or none.</summary>
internal sealed class VaultRecoveryReset
{
    /// <summary>The new admin password.</summary>
    public required string Password { get; init; }

    /// <summary>The recovery code that proved the reset; the reset verifies it again against the file.</summary>
    public required string RecoveryCode { get; init; }

    /// <summary>The rotated code the pre-host already showed; the reset wraps the vault's recovery slot under it.</summary>
    public required string NewRecoveryCode { get; init; }
}
