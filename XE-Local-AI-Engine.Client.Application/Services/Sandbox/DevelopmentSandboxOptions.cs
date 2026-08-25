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
}
