namespace XE_Local_AI_Engine.Client.Hosting.Vault;

/// <summary>What the vault pre-host needs from the real host's builder, so both serve the same origin and assets.</summary>
internal sealed class VaultUnlockHostOptions
{
    public required string DataDirectory { get; init; }

    public required string BindUrl { get; init; }

    public required string ContentRootPath { get; init; }

    public required string? WebRootPath { get; init; }

    public required string EnvironmentName { get; init; }

    public required string Version { get; init; }

    public required bool SuppressBrowser { get; init; }

    public required TextWriter StandardOutput { get; init; }

    /// <summary>Completes when the owning desktop shell's pipe closes; the pre-host then stops without unlocking.</summary>
    public Task? ParentLost { get; init; }
}
