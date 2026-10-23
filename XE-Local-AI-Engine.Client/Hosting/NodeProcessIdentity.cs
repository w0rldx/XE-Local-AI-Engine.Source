namespace XE_Local_AI_Engine.Client.Hosting;

/// <summary>
///     One id per engine process, so a client can tell a restarted engine from the one it was talking to, even when
///     the restart finished between two probes or the new process is the vault-unlock host.
/// </summary>
public static class NodeProcessIdentity
{
    /// <summary>A GUID in "N" format, created once when this process first reads it.</summary>
    public static string Generation { get; } = Guid.NewGuid().ToString("N");
}
