namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The sandbox an <b>AgentHome</b> run executes in — and, by attachment, the one Coder reads through.
///     <para>
///         Adds no members. It exists to make provider selection <em>per feature</em> a property of
///         the type system rather than of a registration someone has to keep correct: a feature that asks for this
///         interface is structurally unable to receive the Development Mode role's provider, and vice versa. The bare
///         <see cref="ISandboxRuntimeProvider" /> is deliberately NOT registered in DI, so there is no "whichever
///         provider happened to win" service left to inject by accident.
///     </para>
///     <para>
///         Concretely, this is what stops a container requirement from silently spreading. ADR 0004 permits Docker for
///         Development Mode execution only; <c>DockerSandboxRuntimeProvider</c> therefore implements the Development
///         role and NOT this one, which makes registering it for AgentHome a compile error rather than a review catch.
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
