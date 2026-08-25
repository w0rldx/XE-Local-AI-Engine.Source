namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Where the compilers, SDKs and interpreters a workload runs against come from. The axis ADR 0004 identified as
///     the reason Development Mode needs a container at all — "No confinement mechanism supplies a toolchain. A
///     container image does" — and the one axis the capability vocabulary was missing when ADR 0007 was written.
///     <para>
///         It is not a synonym for isolation strength. A workload that names <see cref="EngineApprovedImage" /> is
///         saying it cannot run against whatever the operator happens to have installed, not that it wants a stronger
///         boundary; a workload that names <see cref="HostToolchain" /> is saying the opposite, and can therefore never
///         resolve to a backend that only supplies an image.
///     </para>
/// </summary>
public enum SandboxToolchainSource
{
    /// <summary>
    ///     The host's own toolchain, as the engine's user sees it. Everything AgentHome, Coder, work sessions and
    ///     <c>run_python</c> execute is either a fixed host binary or an engine-provisioned interpreter, so this is
    ///     what they declare.
    /// </summary>
    HostToolchain = 0,

    /// <summary>
    ///     A digest-pinned, operator-approved image the engine names (<c>Development:ContainerSandbox:Image</c>).
    ///     Declaring it is the ONLY way a workload can reach a container backend, which is how ADR 0004 §1's narrowing
    ///     survives the move from an absent <c>implements</c> clause to a selected backend: the permission is bounded
    ///     by a declared need rather than by a feature name, and creating a new one is a source change in a reviewed
    ///     file.
    /// </summary>
    EngineApprovedImage = 1
}
