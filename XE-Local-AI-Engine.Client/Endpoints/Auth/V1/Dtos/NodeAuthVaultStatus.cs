namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

using XE_Local_AI_Engine.Client.Services.Vault;

/// <summary>The wire values of <see cref="NodeAuthStatusResponse.Vault" />.</summary>
public static class NodeAuthVaultStatus
{
    public const string Pending = "pending";
    public const string Locked = "locked";
    public const string Unlocked = "unlocked";

    public static string From(VaultState state) => state switch
    {
        VaultState.Pending => Pending,
        VaultState.Locked => Locked,
        _ => Unlocked
    };
}
