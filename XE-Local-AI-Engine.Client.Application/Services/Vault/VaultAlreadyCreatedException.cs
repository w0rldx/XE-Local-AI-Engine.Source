namespace XE_Local_AI_Engine.Client.Services.Vault;

/// <summary>
///     <see cref="INodeVault.CreateAsync" /> found the vault already <see cref="VaultState.Unlocked" /> and was not asked
///     to replace it: a concurrent create won, and writing again would invalidate the recovery code that one returned.
/// </summary>
public sealed class VaultAlreadyCreatedException : InvalidOperationException
{
    public VaultAlreadyCreatedException()
        : base("The node vault has already been created.")
    {
    }

    public VaultAlreadyCreatedException(string message)
        : base(message)
    {
    }

    public VaultAlreadyCreatedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
