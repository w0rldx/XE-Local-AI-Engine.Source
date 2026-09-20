namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Configuration-bound selection of the Development Mode <see cref="IDevelopmentSandboxRuntimeProvider" />, bound from the
///     <c>Development:Sandbox</c> section and resolved once at startup, so a change requires a restart.
/// </summary>
/// <remarks>
///     Separate from <see cref="SandboxOptions" /> rather than a second key inside it, because the two select for different features and
///     only this one may name a container provider (ADR 0004): one shared key could not express "Development on docker, AgentHome on
///     process", and that per-feature split is the whole point.
/// </remarks>
public sealed class DevelopmentSandboxOptions
{
    public const string SectionName = "Development:Sandbox";

    /// <summary>
    ///     Backend name (<c>fake</c>, <c>process</c> or <c>docker</c>) the Development Mode candidate set is narrowed to; unset inherits
    ///     the AgentHome key's constraint, and an unknown name is rejected at startup rather than at first resolution.
    /// </summary>
    /// <remarks>
    ///     A CONSTRAINT rather than a name, with one consequence the AgentHome key does not carry: naming <c>docker</c> is also read as
    ///     the node DECLARING that Development Mode needs an engine-approved image toolchain, which the key always meant.
    ///     <c>Development:ContainerSandbox:Image</c> declares the same need without it. The two must agree — an image set while this key
    ///     names a backend that cannot supply one fails closed at startup with the unmet axis, never runs on the host toolchain. The
    ///     AgentHome inheritance applies only while no image toolchain is declared.
    /// </remarks>
    public string? Provider { get; set; }

    /// <summary>
    ///     Whether Development Mode's AGENT-FACING sandbox may run at all on a backend that cannot deny egress. Off by default.
    /// </summary>
    /// <remarks>
    ///     Off, the attempt asks for <see cref="SandboxNetworkPolicy.None" /> wherever the backend advertises it and still runs where it
    ///     cannot; set, such a node refuses to prepare the workspace with <see cref="SandboxCapabilityNotSupportedException" /> naming this
    ///     key. The WARM-RESTORE sandbox is exempt by design and this key does not reach it: that short-lived sandbox FILLS the package
    ///     cache from the base commit, so denying its egress would populate nothing and turn every later <c>--no-restore</c> build into a
    ///     confusing failure. Separate from <see cref="SandboxOptions.RequireEgressDenial" />, with identical semantics.
    /// </remarks>
    public bool RequireEgressDenial { get; set; }
}
