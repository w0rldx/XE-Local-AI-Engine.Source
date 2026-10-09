namespace XE_Local_AI_Engine.Client.Services.Vault;

/// <summary>What a vault write replaced, so the caller can undo it when its own later step fails.</summary>
public sealed class VaultChange
{
    /// <summary>The new recovery code, set by <see cref="INodeVault.CreateAsync" /> and a recovery reset; shown once.</summary>
    public string? RecoveryCode { get; init; }

    /// <summary>The previous <c>node.key</c> bytes, or <c>null</c> when there was no file.</summary>
    public required ReadOnlyMemory<byte>? PreviousFile { get; init; }

    public required VaultState PreviousState { get; init; }
}
