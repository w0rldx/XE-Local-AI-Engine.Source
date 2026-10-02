namespace XE_Local_AI_Engine.Client.Services.Vault.Implementation;

/// <summary>
///     The vault when the operator secret is in operator custody (env, secrets file, Aspire): nothing to wrap, every
///     call is a no-op. Dev loops, CI, containers and every test fixture run on this.
/// </summary>
public sealed class NullNodeVault : INodeVault
{
    public VaultState State => VaultState.External;

    public Task<VaultChange?> CreateAsync(string password, bool replaceUnlocked, CancellationToken cancellationToken) =>
        Task.FromResult<VaultChange?>(null);

    public Task<VaultChange?> RewrapAsync(string currentPassword, string newPassword, CancellationToken cancellationToken) =>
        Task.FromResult<VaultChange?>(null);

    public Task<VaultChange?> RewrapWithRecoveryAsync(string recoveryCode, string newPassword, CancellationToken cancellationToken) =>
        Task.FromResult<VaultChange?>(null);

    public Task RestoreAsync(VaultChange change, CancellationToken cancellationToken) => Task.CompletedTask;
}
