namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Configuration-bound selection of the Development Mode <see cref="IDevelopmentSandboxRuntimeProvider" />, bound
///     from the <c>Development:Sandbox</c> section. Resolved once at startup as a singleton, so a change requires a
///     restart — the same posture as <see cref="SandboxOptions" />.
///     <para>
///         Separate from <see cref="SandboxOptions" /> rather than a second key inside it, because the two select for
///         different features and only this one may name a container provider (ADR 0004). One shared key could not
///         express "Development on docker, AgentHome on process", which is the whole point of decision D2.
///     </para>
/// </summary>
public sealed class DevelopmentSandboxOptions
{
    public const string SectionName = "Development:Sandbox";

    /// <summary>
    ///     Selected provider name (<c>fake</c>, <c>process</c>, or <c>docker</c>). Null/blank means "unset", which
    ///     resolves to whatever the AgentHome role resolved — deliberately, so introducing this option changes nothing
    ///     on a node that does not set it. An unknown name is rejected at startup by
    ///     <c>DevelopmentSandboxOptionsValidator</c>, not at first resolution.
    /// </summary>
    public string? Provider { get; set; }
}
