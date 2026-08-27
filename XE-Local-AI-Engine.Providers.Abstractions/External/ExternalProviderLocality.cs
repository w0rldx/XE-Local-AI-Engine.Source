namespace XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The OPERATOR-DECLARED trust locality of an external OpenAI-compatible connection. Never inferred from the base
///     URL: a self-hosted llama-server can sit behind a public hostname, and a hosted API can sit behind a LAN proxy,
///     so only the operator can say whether a prompt sent there leaves the trust boundary.
/// </summary>
/// <remarks>
///     Consumers treat an <em>unresolvable</em> locality (a malformed id, a deleted connection, an unreadable store) as
///     <see cref="Cloud" /> and not-routable — the fail-closed posture. This enum therefore carries only the two
///     POSITIVE declarations; the third, unresolved state lives in the policy layer that queries the registry.
/// </remarks>
public enum ExternalProviderLocality
{
    /// <summary>Self-hosted on the node or its trusted network; receives full local-parity tool/knowledge privileges.</summary>
    Local = 0,

    /// <summary>A hosted endpoint outside the trust boundary; gated exactly like the built-in cloud providers.</summary>
    Cloud = 1
}
