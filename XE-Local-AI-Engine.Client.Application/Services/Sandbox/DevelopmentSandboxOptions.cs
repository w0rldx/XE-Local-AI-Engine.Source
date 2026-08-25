namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Configuration-bound selection of the Development Mode <see cref="IDevelopmentSandboxRuntimeProvider" />, bound
///     from the <c>Development:Sandbox</c> section. Resolved once at startup as a singleton, so a change requires a
///     restart — the same posture as <see cref="SandboxOptions" />.
///     <para>
///         Separate from <see cref="SandboxOptions" /> rather than a second key inside it, because the two select for
///         different features and only this one may name a container provider (ADR 0004). One shared key could not
///         express "Development on docker, AgentHome on process" — that per-feature split is the whole point.
///     </para>
/// </summary>
public sealed class DevelopmentSandboxOptions
{
    public const string SectionName = "Development:Sandbox";

    /// <summary>
    ///     Backend name (<c>fake</c>, <c>process</c>, or <c>docker</c>) the Development Mode candidate set is narrowed
    ///     to. Null/blank means "unset", which inherits the AgentHome key's constraint — deliberately, so introducing
    ///     this option changed nothing on a node that does not set it. An unknown name is rejected at startup by
    ///     <c>DevelopmentSandboxOptionsValidator</c>, not at first resolution.
    ///     <para>
    ///         <b>Meaning change, ADR 0007.</b> This key used to NAME the provider Development Mode ran on. It is now a
    ///         CONSTRAINT, and it carries one extra consequence the AgentHome key does not: naming an image-backed
    ///         backend (<c>docker</c>) is also read as the node DECLARING that Development Mode needs an
    ///         engine-approved image toolchain, which is what the key always meant. Setting
    ///         <c>Development:ContainerSandbox:Image</c> declares the same need without this key. The two together
    ///         must agree — an image configured while this key names a backend that cannot supply one fails closed at
    ///         startup with the unmet axis named, rather than silently running Development Mode on the host toolchain
    ///         the operator was trying to get away from. The inheritance from the AgentHome key applies only while no
    ///         image toolchain is declared, for the same reason.
    ///     </para>
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    ///     Whether Development Mode's AGENT-FACING sandbox may run at all on a backend that cannot deny egress. Off by
    ///     default — the 2026-08-25 ruling's Option B, under which the attempt asks for
    ///     <see cref="SandboxNetworkPolicy.None" /> wherever the backend advertises it and still runs where it cannot,
    ///     because the shipped configuration resolves a Windows node (and any Linux node whose <c>unshare</c> probe
    ///     failed) to the process backend. Setting it makes denial a precondition on this node: such a node refuses to
    ///     prepare the workspace with <see cref="SandboxCapabilityNotSupportedException" /> naming this key rather than
    ///     running the attempt with the host's network.
    ///     <para>
    ///         <b>The warm-restore sandbox is exempt by design, and this key does not reach it.</b> That short-lived
    ///         second sandbox is the one that FILLS the package cache from the base commit, so denying its egress would
    ///         populate nothing and turn every later <c>--no-restore</c> build into a confusing failure. Its content is
    ///         the operator's own base commit and the agent has written nothing when it runs — see
    ///         <c>DevelopmentWorkspaceProvider.EnsureWarmRestoreAsync</c> for the clean-tracked-tree gate that keeps
    ///         that true.
    ///     </para>
    ///     <para>
    ///         Separate from <see cref="SandboxOptions.RequireEgressDenial" /> for the reason the two <c>Provider</c>
    ///         keys are separate: the two select for different features and a node may reasonably require denial for one
    ///         and not the other. The semantics are identical, and <see cref="SandboxEgressPolicy" /> is the one place
    ///         either is acted on.
    ///     </para>
    /// </summary>
    public bool RequireEgressDenial { get; set; }
}
