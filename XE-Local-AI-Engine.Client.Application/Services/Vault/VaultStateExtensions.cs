namespace XE_Local_AI_Engine.Client.Services.Vault;

public static class VaultStateExtensions
{
    /// <summary>Whether a v2 <c>node.key</c> exists, so a password reset must prove the recovery code.</summary>
    public static bool HasWrappedKey(this VaultState state) =>
        state is VaultState.Locked or VaultState.Unlocked;
}
