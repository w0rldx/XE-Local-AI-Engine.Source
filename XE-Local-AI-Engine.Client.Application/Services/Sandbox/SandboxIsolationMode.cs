namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     How strongly a sandbox is separated from the host filesystem. Provider-neutral, and DEFAULT-OFF: an existing
///     caller that names nothing keeps exactly the behaviour it had before the isolated mode existed.
/// </summary>
public enum SandboxIsolationMode
{
    /// <summary>
    ///     A supervised child in a working-directory jail on the host filesystem, contained by process group, cgroup ceilings and, where
    ///     the host allows it, an empty network namespace.
    /// </summary>
    /// <remarks>
    ///     The child can READ everything the engine's own user can read. AgentHome, Coder and Development Mode all run here.
    /// </remarks>
    None = 0,

    /// <summary>
    ///     A mount namespace in which the host filesystem is not present at all: a read-only system tree, an invented minimal <c>/etc</c>,
    ///     explicitly named read-only trees, and one writable directory.
    /// </summary>
    /// <remarks>
    ///     A provider not advertising <c>SupportsFilesystemIsolation</c> REJECTS this fail-closed rather than serving a weaker sandbox —
    ///     the point of asking is to be told when it is not there. On a CREATE REQUEST it names that whole contract, mechanism included,
    ///     which is why a container provider refuses it despite having the boundary: it binds no
    ///     <see cref="SandboxCreateRequest.ReadOnlyTrees" />, invents no <c>/etc</c> and backs no <c>/tmp</c> with the jail. As a
    ///     <see cref="SandboxRequirements.IsolationFloor" /> it names only the PROPERTY, checked against <c>SupportsHostFilesystemBoundary</c>.
    /// </remarks>
    Filesystem = 1
}
