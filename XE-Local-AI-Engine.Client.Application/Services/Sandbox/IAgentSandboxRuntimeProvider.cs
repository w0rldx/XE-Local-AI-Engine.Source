namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The sandbox an <b>AgentHome</b> run executes in — and, by attachment, the one Coder reads through.
///     <para>
///         Adds no members. It names the ROLE, not the backend: under ADR 0007 it is registered as a factory that
///         resolves <see cref="SandboxWorkloads.AgentHome" /> through <c>SandboxProviderSelector</c>, so what a
///         consumer of this interface receives is whichever registered backend can honour that declaration with the
///         least additional privilege. The bare <see cref="ISandboxRuntimeProvider" /> is still deliberately NOT
///         registered in DI, so there is no "whichever provider happened to win" service left to inject by accident.
///     </para>
///     <para>
///         What stops a container requirement from silently spreading is no longer this interface alone. ADR 0007
///         Decision 4 replaces the single absent <c>implements</c> clause with three mechanisms: the AgentHome
///         declaration names <see cref="SandboxToolchainSource.HostToolchain" />, which no container backend supplies,
///         so minimal-satisfying resolution can never reach one; the declaration's isolation floor has no default, so
///         a new consumer cannot inherit the weakest posture by saying nothing; and
///         <c>SandboxSubstrateSelectionArchitectureTests</c> enumerates every declaration and asserts the exact set of
///         backends allowed to serve it. That last one is an honest reduction — a compile error cannot be skipped and
///         a test can — so <c>DockerSandboxRuntimeProvider</c> also still does not implement this interface, which
///         keeps the old compile error standing behind the new checks rather than in place of them.
///     </para>
///     <para>
///         The implementations that can serve this role are the deterministic fake and the jailed process provider,
///         both of which also serve the Development role — so when both roles resolve the same provider name they
///         resolve the SAME DI singleton instance. That sharing is load-bearing: Coder attaches to AgentHome's live
///         sandbox by attach key through <see cref="ISandboxRuntimeProvider.ConnectAsync" />, and the process provider
///         allocates its jail root once per instance.
///     </para>
/// </summary>
public interface IAgentSandboxRuntimeProvider : ISandboxRuntimeProvider
{
    /// <summary>
    ///     Replaces a sandbox directory with a known-empty directory. Implementations must reject escapes and links;
    ///     returning successfully is the proof that no file from a prior AgentHome selection remains below the path.
    /// </summary>
    Task ResetDirectoryAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default);
}
