namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     How strongly a sandbox is separated from the host filesystem. Provider-neutral, and DEFAULT-OFF: an existing
///     caller that names nothing keeps exactly the behaviour it had before the isolated mode existed.
/// </summary>
public enum SandboxIsolationMode
{
    /// <summary>
    ///     The historical posture: a supervised child in a working-directory jail on the host filesystem, contained by
    ///     process group, cgroup ceilings and (where the host allows it) an empty network namespace, but able to READ
    ///     everything the engine's own user can read. AgentHome, Coder and Development Mode all run here.
    /// </summary>
    None = 0,

    /// <summary>
    ///     A mount namespace in which the host filesystem is not present at all: a read-only system tree, an invented
    ///     minimal <c>/etc</c>, explicitly named read-only trees, and one writable directory. A provider that does not
    ///     advertise <see cref="SandboxProviderCapabilities.SupportsFilesystemIsolation" /> REJECTS this fail-closed
    ///     rather than serving a weaker sandbox — the point of asking for it is to be told when it is not there.
    ///     <para>
    ///         On a CREATE REQUEST this names that whole contract, mechanism included, which is why a container
    ///         provider refuses it despite having the boundary: it implements no <see cref="SandboxCreateRequest.ReadOnlyTrees" /> binding,
    ///         no synthetic <c>/etc</c> and no jail-backed <c>/tmp</c>. As a
    ///         <see cref="SandboxRequirements.IsolationFloor" /> it names only the PROPERTY — the host filesystem
    ///         absent from the sandbox's view — and is checked against
    ///         <see cref="SandboxProviderCapabilities.SupportsHostFilesystemBoundary" /> instead.
    ///     </para>
    /// </summary>
    Filesystem = 1
}
