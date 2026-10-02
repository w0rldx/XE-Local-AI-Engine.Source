namespace XE_Local_AI_Engine.Client.Services.Vault;

/// <summary>Where the node's operator secret lives and whether this process holds it unwrapped.</summary>
public enum VaultState
{
    /// <summary>The secret comes from env, a secrets file or Aspire: operator custody, the vault is not involved.</summary>
    External,

    /// <summary>No v2 <c>node.key</c> yet (fresh install or legacy key): the first password wraps it.</summary>
    Pending,

    /// <summary>A v2 <c>node.key</c> exists and this process does not hold the unwrapped secret.</summary>
    Locked,

    /// <summary>A v2 <c>node.key</c> exists and the host was started with the unwrapped secret.</summary>
    Unlocked
}

/// <summary>
///     The persisted desktop <c>node.key</c> as a password- and recovery-code-wrapped vault (ADR 0018).
/// </summary>
/// <remarks>
///     The vault passphrase IS the admin login password, so every password write in <c>NodeAuthService</c> writes the
///     vault first and compensates with <see cref="RestoreAsync" /> when its own later step fails. Every mutating call
///     returns <c>null</c> when it changed nothing (state <see cref="VaultState.External" />, or no v2 file to rewrap).
/// </remarks>
public interface INodeVault
{
    VaultState State { get; }

    /// <summary>
    ///     Wraps the current operator secret under <paramref name="password" /> and a fresh recovery code, replacing a
    ///     missing or legacy <c>node.key</c>.
    /// </summary>
    /// <remarks>
    ///     The state is checked atomically and a throw writes nothing. From <see cref="VaultState.Unlocked" /> it re-wraps
    ///     the same secret only when <paramref name="replaceUnlocked" /> is true (setup after an identity reset), else
    ///     throws <see cref="VaultAlreadyCreatedException" /> (a concurrent create won). <see cref="VaultState.Locked" />
    ///     throws <see cref="InvalidOperationException" />.
    /// </remarks>
    Task<VaultChange?> CreateAsync(string password, bool replaceUnlocked, CancellationToken cancellationToken);

    /// <summary>
    ///     Re-wraps the password slot. Throws <see cref="VaultUnlockException" /> when <paramref name="currentPassword" />
    ///     does not unlock the file, leaving it untouched.
    /// </summary>
    Task<VaultChange?> RewrapAsync(string currentPassword, string newPassword, CancellationToken cancellationToken);

    /// <summary>
    ///     Re-wraps the password slot after proving the recovery code. Throws <see cref="VaultUnlockException" /> on a
    ///     malformed or wrong code, leaving the file untouched.
    /// </summary>
    Task<VaultChange?> RewrapWithRecoveryAsync(string recoveryCode, string newPassword, CancellationToken cancellationToken);

    /// <summary>Puts back the file bytes and state from before <paramref name="change" />.</summary>
    Task RestoreAsync(VaultChange change, CancellationToken cancellationToken);
}
