namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The sandbox an <b>AgentHome</b> run executes in — and, by attachment, the one Coder reads through. Adds no members: it names the
///     ROLE, not the backend.
/// </summary>
/// <remarks>
///     Registered as a factory resolving <see cref="SandboxWorkloads.AgentHome" /> through <c>SandboxProviderSelector</c>, so a consumer
///     gets the least-privileged backend that honours that declaration; the bare <see cref="ISandboxRuntimeProvider" /> is deliberately NOT
///     in DI. A container backend is unreachable: the declaration names <c>HostToolchain</c>, which none supplies, and
///     <c>DockerSandboxRuntimeProvider</c> does not implement this interface either. A provider serving both this role and the Development
///     role resolves as the SAME DI singleton — load-bearing, since Coder attaches to AgentHome's sandbox by key.
/// </remarks>
public interface IAgentSandboxRuntimeProvider : ISandboxRuntimeProvider
{
    /// <summary>Replaces a sandbox directory with a known-empty directory.</summary>
    /// <remarks>
    ///     Implementations must reject escapes and links; returning successfully is the proof that no file from a prior AgentHome selection
    ///     remains below the path.
    /// </remarks>
    Task ResetDirectoryAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default);
}
