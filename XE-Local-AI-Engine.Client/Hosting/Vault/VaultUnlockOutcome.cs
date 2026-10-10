namespace XE_Local_AI_Engine.Client.Hosting.Vault;

/// <summary>What unlocking a v2 <c>node.key</c> produced, handed from the pre-host (or a CLI unlock) to the real host.</summary>
internal sealed class VaultUnlockOutcome
{
    /// <summary>The unwrapped operator secret. The receiver injects it and zeroes this buffer.</summary>
    public required byte[] MasterKey { get; init; }

    /// <summary>The URL the pre-host actually bound, so the real host re-binds the same origin; null for a CLI unlock.</summary>
    public string? BoundUrl { get; init; }

    /// <summary>
    ///     Set by a recovery-code unlock: the real host resets the admin password (Identity and the vault's password
    ///     wrap, via <c>INodeAuthService.ResetAdminPasswordAsync</c>) to this value right after migrations.
    /// </summary>
    public string? ResetPassword { get; init; }

    /// <summary>The recovery code that proved the reset; that call verifies it again against the file.</summary>
    public string? ResetRecoveryCode { get; init; }

    /// <summary>The rotated recovery code the pre-host already showed; the reset wraps the vault's recovery slot under it.</summary>
    public string? ResetNewRecoveryCode { get; init; }

    /// <summary>Set by a pre-host password unlock: the real host registers it so <c>auth/refresh</c> can trade the <c>node_ut</c> cookie once.</summary>
    public VaultUnlockTicket? UnlockTicket { get; init; }
}
