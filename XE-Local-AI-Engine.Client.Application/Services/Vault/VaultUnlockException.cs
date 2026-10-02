namespace XE_Local_AI_Engine.Client.Services.Vault;

/// <summary>
///     A wrap did not authenticate: the password or recovery code is wrong, or the vault file was tampered with. The
///     two are deliberately indistinguishable.
/// </summary>
public sealed class VaultUnlockException : Exception
{
    public VaultUnlockException()
        : base("The password or recovery code does not unlock the node vault.")
    {
    }

    public VaultUnlockException(string message)
        : base(message)
    {
    }

    public VaultUnlockException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
