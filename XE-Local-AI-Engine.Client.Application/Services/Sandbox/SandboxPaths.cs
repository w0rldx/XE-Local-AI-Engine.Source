namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The stable on-disk locations the process sandbox owns. These are shared by the provider (which creates a fresh
///     per-instance jail container under <see cref="ContainerRoot" />) and the orphan reaper (which sweeps
///     <see cref="MarkersRoot" /> and refuses to delete anything outside <see cref="ContainerRoot" />). Centralised so
///     the reaper's ownership check and the provider's jail creation can never disagree about what "ours" means.
/// </summary>
public static class SandboxPaths
{
    /// <summary>The directory name, under the system temp path, that contains every jail this product creates.</summary>
    public const string ContainerDirectoryName = "xe-agent-home-sandboxes";

    /// <summary>
    ///     The stable root under which all per-instance jail containers live. Stable across restarts (unlike the
    ///     per-instance GUID directory beneath it), which is what makes an ownership check possible after a crash.
    /// </summary>
    public static string ContainerRoot => Path.Combine(Path.GetTempPath(), ContainerDirectoryName);

    /// <summary>
    ///     Where per-process markers are written. A sibling of the per-instance jail containers so that a provider
    ///     disposing its own container root does not take the markers with it.
    /// </summary>
    public static string MarkersRoot => Path.Combine(ContainerRoot, "markers");
}
