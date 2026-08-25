namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Whether anything a workload writes has to outlive the sandbox that wrote it. Provider-neutral, and expressed as
///     a requirement rather than as a preference: a backend that cannot preserve a trusted host workspace must not be
///     handed a workload whose whole point is the tree it leaves behind.
/// </summary>
public enum SandboxPersistence
{
    /// <summary>
    ///     Nothing survives teardown. Killing the sandbox discards every byte its commands wrote, which is what makes
    ///     <c>run_python</c>'s advertised statelessness real rather than a claim.
    /// </summary>
    Disposable = 0,

    /// <summary>
    ///     The engine-managed workspace root supplied as <see cref="SandboxCreateRequest.TrustedHostWorkspace" /> must
    ///     be confined and preserved across kill/restart. Requires
    ///     <see cref="SandboxProviderCapabilities.SupportsTrustedHostWorkspace" />.
    /// </summary>
    PreservedTrustedHostWorkspace = 1
}
